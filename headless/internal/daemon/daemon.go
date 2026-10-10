// Package daemon implements colitud: it owns the account session (tokens,
// device), runs sing-box (the only process that touches the TUN device and
// the routes) and answers the colitu CLI over a unix socket.
package daemon

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"os"
	"path/filepath"
	"sort"
	"sync"
	"time"

	"github.com/colitu/colitu-linux/headless/internal/api"
	"github.com/colitu/colitu-linux/headless/internal/proto"
	"github.com/colitu/colitu-linux/headless/internal/singbox"
	"github.com/colitu/colitu-linux/headless/internal/state"
	"github.com/colitu/colitu-linux/headless/internal/version"
)

// Config configures a Daemon. Only StatePath and SocketPath are required;
// the rest default to the production behaviour and are overridden by tests.
type Config struct {
	StatePath  string
	SocketPath string
	// SingboxPath overrides where the sing-box binary is looked up.
	SingboxPath string
	APIBase     string
	Group       string
	Interface   string
	DNS         string

	Log *slog.Logger
	API *api.Client
	// Runner starts sing-box (default: os/exec).
	Runner Runner
	// FindCore resolves the sing-box binary (default FindSingbox).
	FindCore func(explicit string) (string, error)
	// WorkDir is sing-box's working directory; RunDir holds its generated
	// configuration (default: next to the state file / the socket).
	WorkDir string
	RunDir  string

	IfaceUp      func(name string) bool
	ReadyTimeout time.Duration
	// Settle is how long the tunnel must stay up after its interface
	// appears before the connection counts as established.
	Settle time.Duration
	Sleep  func(context.Context, time.Duration) error
	// Backoff returns the wait before reconnect attempt n (0-based).
	Backoff func(attempt int) time.Duration
	Now     func() time.Time
}

// Daemon is colitud.
type Daemon struct {
	cfg   Config
	log   *slog.Logger
	store *state.Store

	root context.Context

	// mu guards everything below it.
	mu      sync.Mutex
	st      state.State
	paused  *proto.Paused
	lastErr string
	sess    *session
	link    *linkSession

	// connMu serialises connect, disconnect, logout and login completion.
	connMu sync.Mutex
	// loginMu serialises starting and cancelling a sign-in, so two
	// login_start calls cannot leave one running that nobody can cancel.
	loginMu sync.Mutex
	// tokMu serialises token refreshes (refresh tokens are single-use).
	tokMu sync.Mutex
}

// New returns a daemon with defaults filled in.
func New(cfg Config) *Daemon {
	if cfg.Log == nil {
		cfg.Log = slog.Default()
	}
	if cfg.API == nil {
		cfg.API = api.New(cfg.APIBase)
	}
	if cfg.Runner == nil {
		cfg.Runner = execRunner{}
	}
	if cfg.FindCore == nil {
		cfg.FindCore = FindSingbox
	}
	if cfg.Group == "" {
		cfg.Group = proto.Group
	}
	if cfg.Interface == "" {
		cfg.Interface = singbox.DefaultInterface
	}
	if cfg.StatePath == "" {
		cfg.StatePath = state.DefaultPath
	}
	if cfg.SocketPath == "" {
		cfg.SocketPath = proto.DefaultSocket
	}
	if cfg.WorkDir == "" {
		cfg.WorkDir = filepath.Join(filepath.Dir(cfg.StatePath), "singbox")
	}
	if cfg.RunDir == "" {
		cfg.RunDir = filepath.Dir(cfg.SocketPath)
	}
	if cfg.IfaceUp == nil {
		cfg.IfaceUp = interfaceUp
	}
	if cfg.ReadyTimeout <= 0 {
		cfg.ReadyTimeout = 25 * time.Second
	}
	if cfg.Settle < 0 {
		cfg.Settle = 0
	}
	if cfg.Sleep == nil {
		cfg.Sleep = api.Sleep
	}
	if cfg.Backoff == nil {
		cfg.Backoff = defaultBackoff
	}
	if cfg.Now == nil {
		cfg.Now = time.Now
	}
	return &Daemon{cfg: cfg, log: cfg.Log, store: state.NewStore(cfg.StatePath), root: context.Background()}
}

func defaultBackoff(attempt int) time.Duration {
	d := 2 * time.Second << min(attempt, 5)
	return min(d, time.Minute)
}

// Init loads the saved state. root bounds the background work (login
// polling, reconnects); it must stay valid until shutdown.
func (d *Daemon) Init(root context.Context) error {
	st, fixed, err := d.store.Load()
	if err != nil {
		return fmt.Errorf("loading state: %w", err)
	}
	if fixed {
		d.log.Warn("state file was readable by others; tightened to 0600", "path", d.cfg.StatePath)
	}
	if st.DeviceKey == "" {
		if st.DeviceKey, err = state.NewDeviceKey(); err != nil {
			return err
		}
		if err := d.store.Save(st); err != nil {
			return fmt.Errorf("saving state: %w", err)
		}
	}
	d.mu.Lock()
	d.st = st
	d.root = root
	d.mu.Unlock()
	return nil
}

// Run serves the socket until ctx ends, then tears the tunnel down.
func (d *Daemon) Run(ctx context.Context) error {
	if err := d.Init(ctx); err != nil {
		return err
	}
	ln, err := proto.Listen(d.cfg.SocketPath, d.cfg.Group)
	if err != nil {
		return err
	}
	d.autoConnectAtStart()
	d.log.Info("colitud ready", "version", version.Version, "socket", d.cfg.SocketPath)
	err = proto.Serve(ctx, ln, d.Handle)
	d.Shutdown()
	_ = os.Remove(d.cfg.SocketPath)
	return err
}

// Shutdown stops the tunnel and background work.
func (d *Daemon) Shutdown() {
	d.cancelLogin()
	d.stopSession()
}

func (d *Daemon) autoConnectAtStart() {
	d.mu.Lock()
	st := d.st
	d.mu.Unlock()
	if !st.AutoConnect || !st.LoggedIn() || st.LastServerID == "" {
		return
	}
	d.log.Info("autoconnect: reconnecting to the last server", "server", st.LastServerName)
	d.connMu.Lock()
	defer d.connMu.Unlock()
	d.startSession(Target{ServerID: st.LastServerID, Name: st.LastServerName, Country: st.LastServerCountry}, true)
}

// Handle runs one CLI command.
func (d *Daemon) Handle(ctx context.Context, req proto.Request) (any, *proto.Error) {
	switch req.Cmd {
	case proto.CmdPing:
		return map[string]string{"version": version.Version}, nil
	case proto.CmdStatus:
		return d.status(), nil
	case proto.CmdLoginStart:
		return d.loginStart(ctx)
	case proto.CmdLoginStatus:
		return d.loginStatus()
	case proto.CmdLoginCancel:
		d.loginMu.Lock()
		d.cancelLogin()
		d.loginMu.Unlock()
		return struct{}{}, nil
	case proto.CmdLogout:
		var a proto.LogoutArgs
		if e := decodeArgs(req, &a); e != nil {
			return nil, e
		}
		return d.logout(ctx, a)
	case proto.CmdServers:
		return d.servers(ctx)
	case proto.CmdConnect:
		var a proto.ConnectArgs
		if e := decodeArgs(req, &a); e != nil {
			return nil, e
		}
		return d.connect(ctx, a)
	case proto.CmdDisconnect:
		return d.disconnect()
	case proto.CmdActivate:
		return d.activate(ctx)
	case proto.CmdAutoConnect:
		var a proto.AutoConnectArgs
		if e := decodeArgs(req, &a); e != nil {
			return nil, e
		}
		return d.setAutoConnect(a.Enabled)
	}
	return nil, &proto.Error{Code: proto.CodeUnknownCommand, Message: "unknown command " + req.Cmd}
}

func decodeArgs(req proto.Request, v any) *proto.Error {
	if len(req.Args) == 0 {
		return nil
	}
	if err := json.Unmarshal(req.Args, v); err != nil {
		return &proto.Error{Code: proto.CodeBadRequest, Message: "bad arguments for " + req.Cmd}
	}
	return nil
}

// ---- errors

var errNotLoggedIn = errors.New("not signed in")

func (d *Daemon) fail(err error) *proto.Error {
	var pe *proto.Error
	switch {
	case errors.As(err, &pe):
		return pe
	case errors.Is(err, errNotLoggedIn):
		return &proto.Error{Code: proto.CodeNotLoggedIn, Message: "not signed in: run `colitu login`"}
	case errors.Is(err, ErrNoMatch):
		return &proto.Error{Code: proto.CodeNoServer, Message: err.Error()}
	case errors.Is(err, singbox.ErrNoUsableOutbound), errors.Is(err, ErrCoreMissing):
		return &proto.Error{Code: proto.CodeCore, Message: err.Error()}
	case errors.Is(err, context.Canceled):
		return &proto.Error{Code: proto.CodeInternal, Message: "cancelled"}
	}
	if ae, ok := api.AsError(err); ok {
		if ae.Code == api.CodeDeviceOverLimit {
			return d.pausedError()
		}
		return &proto.Error{Code: proto.CodeUpstream, Message: describeAPIError(ae)}
	}
	return &proto.Error{Code: proto.CodeUpstream, Message: "cannot reach the Colitu service: " + err.Error()}
}

func describeAPIError(e *api.Error) string {
	switch e.Code {
	case "ENTITLEMENT_INACTIVE", "ENTITLEMENT_EXPIRED":
		return "your account has no active plan (" + e.Code + ")"
	case "EMAIL_NOT_VERIFIED":
		return "verify your e-mail address on colitu.com first (EMAIL_NOT_VERIFIED)"
	case "DEVICE_LIMIT_REACHED":
		return "the plan's device limit is reached: remove a device on colitu.com/account first (DEVICE_LIMIT_REACHED)"
	case "QUOTA_EXCEEDED":
		return "the traffic quota of the plan is used up (QUOTA_EXCEEDED)"
	}
	return e.Error()
}

func (d *Daemon) pausedError() *proto.Error {
	d.mu.Lock()
	p := d.paused
	d.mu.Unlock()
	e := &proto.Error{Code: proto.CodePaused, Message: "this device is paused: the plan allows fewer devices. Run `colitu activate` to use this device instead"}
	if p != nil {
		e.Data, _ = json.Marshal(p)
	}
	return e
}

func (d *Daemon) setPaused(e *api.Error) {
	d.mu.Lock()
	defer d.mu.Unlock()
	if e == nil {
		d.paused = nil
		return
	}
	p := &proto.Paused{DeviceLimit: e.DeviceLimit}
	for _, a := range e.ActiveDevices {
		p.ActiveDevices = append(p.ActiveDevices, proto.Device{ID: a.ID, Name: a.Name, Platform: a.Platform, LastSeenAt: a.LastSeenAt})
	}
	d.paused = p
}

func (d *Daemon) setLastErr(msg string) {
	d.mu.Lock()
	d.lastErr = msg
	d.mu.Unlock()
}

// ---- commands

func (d *Daemon) status() proto.Status {
	d.mu.Lock()
	defer d.mu.Unlock()
	return d.statusLocked()
}

func (d *Daemon) statusLocked() proto.Status {
	st := proto.Status{
		Version:     version.Version,
		LoggedIn:    d.st.LoggedIn(),
		AutoConnect: d.st.AutoConnect,
		LastError:   d.lastErr,
		Paused:      d.paused,
	}
	switch {
	case !st.LoggedIn:
		st.State = proto.StateLoggedOut
		st.Paused = nil
	case d.sess != nil:
		st.State = d.sess.state
		if d.sess.server != nil {
			srv := *d.sess.server
			st.Server = &srv
		}
		if d.sess.state == proto.StateConnected {
			since := d.sess.since
			st.Since = &since
		}
	default:
		st.State = proto.StateDisconnected
	}
	if st.LoggedIn {
		st.Device = &proto.Device{ID: d.st.DeviceID, Name: d.st.DeviceName, Platform: api.Platform}
		if d.st.LastServerID != "" {
			st.LastServer = &proto.ConnectedServer{ID: d.st.LastServerID, Name: d.st.LastServerName, Country: d.st.LastServerCountry}
		}
	}
	return st
}

func (d *Daemon) servers(ctx context.Context) (any, *proto.Error) {
	var list []api.Server
	err := d.call(ctx, func(a api.Auth) (err error) {
		list, err = d.cfg.API.Servers(ctx, a)
		return err
	})
	if err != nil {
		return nil, d.fail(err)
	}
	out := make([]proto.Server, 0, len(list))
	for _, s := range list {
		out = append(out, proto.Server{ID: s.ID, Name: s.Name, Country: s.Country, City: s.City, Region: s.Region, Status: s.Status, Load: s.Load, Protocols: s.Protocols})
	}
	sort.SliceStable(out, func(i, j int) bool {
		if out[i].Country != out[j].Country {
			return out[i].Country < out[j].Country
		}
		return out[i].Name < out[j].Name
	})
	return out, nil
}

func (d *Daemon) connect(ctx context.Context, a proto.ConnectArgs) (any, *proto.Error) {
	d.connMu.Lock()
	defer d.connMu.Unlock()
	if !d.loggedIn() {
		return nil, d.fail(errNotLoggedIn)
	}
	d.stopSession()

	var list []api.Server
	err := d.call(ctx, func(au api.Auth) (err error) {
		list, err = d.cfg.API.Servers(ctx, au)
		return err
	})
	if err != nil {
		return nil, d.fail(err)
	}
	srv, err := PickServer(list, a.ServerID, a.Country)
	if err != nil {
		return nil, d.fail(err)
	}
	s := d.startSession(Target{ServerID: srv.ID, Name: srv.Name, Country: srv.Country, City: srv.City}, false)
	select {
	case err := <-s.first:
		if err != nil {
			<-s.done
			return nil, d.fail(err)
		}
	case <-ctx.Done():
		s.cancel()
		<-s.done
		return nil, d.fail(ctx.Err())
	}
	return d.status(), nil
}

func (d *Daemon) disconnect() (any, *proto.Error) {
	d.connMu.Lock()
	defer d.connMu.Unlock()
	d.stopSession()
	d.setLastErr("")
	return d.status(), nil
}

func (d *Daemon) activate(ctx context.Context) (any, *proto.Error) {
	var res api.ActivateResult
	// Not through d.call: a paused device is exactly the one that activates.
	auth, err := d.auth(ctx, false)
	if err == nil {
		res, err = d.cfg.API.Activate(ctx, auth)
		if api.HasCode(err, api.CodeTokenExpired) {
			if auth, err = d.auth(ctx, true); err == nil {
				res, err = d.cfg.API.Activate(ctx, auth)
			}
		}
	}
	if err != nil {
		if api.IsSessionEnded(err) {
			d.endSession("the account signed this device out")
			err = errNotLoggedIn
		}
		return nil, d.fail(err)
	}
	d.setPaused(nil)
	d.setLastErr("")
	out := proto.ActivateResult{Suspended: []proto.Device{}}
	for _, s := range res.Suspended {
		out.Suspended = append(out.Suspended, proto.Device{ID: s.ID, Name: s.Name, Platform: s.Platform, LastSeenAt: s.LastSeenAt})
	}
	return out, nil
}

func (d *Daemon) setAutoConnect(on bool) (any, *proto.Error) {
	d.mu.Lock()
	defer d.mu.Unlock()
	if !d.st.LoggedIn() {
		return nil, d.fail(errNotLoggedIn)
	}
	d.st.AutoConnect = on
	if err := d.store.Save(d.st); err != nil {
		d.st.AutoConnect = !on
		return nil, &proto.Error{Code: proto.CodeInternal, Message: "cannot save the setting: " + err.Error()}
	}
	return d.statusLocked(), nil
}

func (d *Daemon) loggedIn() bool {
	d.mu.Lock()
	defer d.mu.Unlock()
	return d.st.LoggedIn()
}

// save persists d.st. The caller holds d.mu.
func (d *Daemon) saveLocked() {
	if err := d.store.Save(d.st); err != nil {
		d.log.Error("cannot save state", "err", err)
	}
}
