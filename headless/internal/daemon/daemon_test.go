package daemon

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/colitu/colitu-linux/headless/internal/api"
	"github.com/colitu/colitu-linux/headless/internal/proto"
	"github.com/colitu/colitu-linux/headless/internal/state"
)

// ---- fake sing-box

type fakeProc struct {
	cfgPath string
	cfg     []byte
	done    chan error
	once    sync.Once
	stopped atomic.Bool
}

func (p *fakeProc) Done() <-chan error { return p.done }
func (p *fakeProc) exit(err error) {
	p.once.Do(func() {
		p.done <- err
		close(p.done)
	})
}
func (p *fakeProc) Stop(time.Duration) { p.stopped.Store(true); p.exit(nil) }

type fakeRunner struct {
	mu      sync.Mutex
	procs   []*fakeProc
	failing error
}

func (r *fakeRunner) Start(binary, configPath, workDir string) (Process, error) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.failing != nil {
		return nil, r.failing
	}
	raw, err := os.ReadFile(configPath)
	if err != nil {
		return nil, err
	}
	p := &fakeProc{cfgPath: configPath, cfg: raw, done: make(chan error, 1)}
	r.procs = append(r.procs, p)
	return p, nil
}

func (r *fakeRunner) count() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.procs)
}

func (r *fakeRunner) last() *fakeProc {
	r.mu.Lock()
	defer r.mu.Unlock()
	if len(r.procs) == 0 {
		return nil
	}
	return r.procs[len(r.procs)-1]
}

// ---- fake panel

type fakePanel struct {
	t   *testing.T
	srv *httptest.Server

	mu          sync.Mutex
	overLimit   bool
	refreshErr  *api.Error
	refreshes   int
	registered  []map[string]any
	preferences []api.Preference
	activated   int
	loggedOut   int
	removed     int
	linkPolls   int
	accessTTL   int64
}

const (
	panelDevice = "dev-0001"
	pollTok     = "0123456789012345678901234567890123456789012"
)

func newFakePanel(t *testing.T) *fakePanel {
	p := &fakePanel{t: t, accessTTL: 900}
	mux := http.NewServeMux()
	mux.HandleFunc("POST /api/v1/auth/link/start", func(w http.ResponseWriter, r *http.Request) {
		reply(w, 201, api.LinkStart{Code: "ABCD2345", URL: "https://colitu.test/link?c=ABCD2345", PollToken: pollTok, ExpiresIn: 600, Interval: 1})
	})
	mux.HandleFunc("POST /api/v1/auth/link/poll", func(w http.ResponseWriter, r *http.Request) {
		p.mu.Lock()
		p.linkPolls++
		n := p.linkPolls
		p.mu.Unlock()
		if n < 2 {
			reply(w, 202, map[string]string{"status": "pending"})
			return
		}
		reply(w, 200, api.Tokens{AccessToken: "acc-0", RefreshToken: "ref-0", TokenType: "Bearer", ExpiresIn: p.accessTTL})
	})
	mux.HandleFunc("POST /api/v1/devices/register", func(w http.ResponseWriter, r *http.Request) {
		var body map[string]any
		_ = json.NewDecoder(r.Body).Decode(&body)
		p.mu.Lock()
		p.registered = append(p.registered, body)
		p.mu.Unlock()
		if r.Header.Get("Authorization") != "Bearer acc-0" {
			errReply(w, 401, "AUTH_INVALID_CREDENTIALS", "no")
			return
		}
		reply(w, 201, api.Device{ID: panelDevice, Name: body["name"].(string)})
	})
	mux.HandleFunc("POST /api/v1/auth/refresh", func(w http.ResponseWriter, r *http.Request) {
		p.mu.Lock()
		p.refreshes++
		n := p.refreshes
		fail := p.refreshErr
		p.mu.Unlock()
		if r.Header.Get("X-Device-ID") != panelDevice {
			errReply(w, 400, "DEVICE_REQUIRED", "device")
			return
		}
		if fail != nil {
			errReply(w, fail.Status, fail.Code, fail.Message)
			return
		}
		reply(w, 200, api.Tokens{AccessToken: "acc-" + itoa(n), RefreshToken: "ref-" + itoa(n), TokenType: "Bearer", ExpiresIn: p.accessTTL})
	})
	mux.HandleFunc("POST /api/v1/auth/logout", func(w http.ResponseWriter, r *http.Request) {
		p.mu.Lock()
		p.loggedOut++
		p.mu.Unlock()
		w.WriteHeader(204)
	})
	mux.HandleFunc("DELETE /api/v1/devices/{id}", func(w http.ResponseWriter, r *http.Request) {
		p.mu.Lock()
		p.removed++
		p.mu.Unlock()
		w.WriteHeader(204)
	})
	authed := func(h http.HandlerFunc) http.HandlerFunc {
		return func(w http.ResponseWriter, r *http.Request) {
			if !strings.HasPrefix(r.Header.Get("Authorization"), "Bearer acc-") || r.Header.Get("X-Device-ID") != panelDevice {
				errReply(w, 401, "AUTH_TOKEN_EXPIRED", "expired")
				return
			}
			h(w, r)
		}
	}
	limited := func(h http.HandlerFunc) http.HandlerFunc {
		return authed(func(w http.ResponseWriter, r *http.Request) {
			p.mu.Lock()
			over := p.overLimit
			p.mu.Unlock()
			if over {
				reply(w, 403, map[string]any{
					"error":          map[string]string{"code": "DEVICE_OVER_LIMIT", "message": "paused"},
					"device_limit":   1,
					"active_devices": []map[string]string{{"id": "other", "name": "Phone", "platform": "android"}},
				})
				return
			}
			h(w, r)
		})
	}
	mux.HandleFunc("GET /api/v1/servers", limited(func(w http.ResponseWriter, r *http.Request) {
		reply(w, 200, map[string]any{"servers": testServers})
	}))
	mux.HandleFunc("PUT /api/v1/me/preferences", authed(func(w http.ResponseWriter, r *http.Request) {
		var pref api.Preference
		dec := json.NewDecoder(r.Body)
		dec.DisallowUnknownFields()
		if err := dec.Decode(&pref); err != nil {
			errReply(w, 400, "INVALID_REQUEST", err.Error())
			return
		}
		p.mu.Lock()
		p.preferences = append(p.preferences, pref)
		p.mu.Unlock()
		reply(w, 200, pref)
	}))
	mux.HandleFunc("GET /api/v1/config", limited(func(w http.ResponseWriter, r *http.Request) {
		p.mu.Lock()
		pref := api.Preference{}
		if n := len(p.preferences); n > 0 {
			pref = p.preferences[n-1]
		}
		p.mu.Unlock()
		name, country := "Ankara", "TR"
		for _, s := range testServers {
			if s.ID == pref.NodeID {
				name, country = s.Name, s.Country
			}
		}
		pay := func(outbound string) json.RawMessage {
			return json.RawMessage(`{"outbounds":[` + outbound + `],"route":{"final":"proxy"}}`)
		}
		reply(w, 200, map[string]any{
			"revision": 1,
			"server":   map[string]any{"id": pref.NodeID, "name": name, "country": country},
			"profile":  map[string]any{"format": "sing-box", "payload": pay(`{"type":"hysteria2","server":"hy.example.test","server_port":8443,"password":"p","tls":{"enabled":true,"server_name":"hy.example.test"}}`)},
			"candidates": []map[string]any{
				{"protocol": "hysteria2", "profile": map[string]any{"format": "sing-box", "payload": pay(`{"type":"hysteria2","server":"hy.example.test","server_port":8443,"password":"p","tls":{"enabled":true,"server_name":"hy.example.test"}}`)}},
				{"protocol": "vless-xhttp", "profile": map[string]any{"format": "sing-box", "payload": pay(`{"type":"vless","server":"x.example.test","server_port":8445,"uuid":"u"}`)}},
			},
		})
	}))
	mux.HandleFunc("POST /api/v1/devices/{id}/activate", authed(func(w http.ResponseWriter, r *http.Request) {
		p.mu.Lock()
		p.activated++
		p.overLimit = false
		p.mu.Unlock()
		reply(w, 200, map[string]any{"data": api.ActivateResult{DeviceID: panelDevice, Suspended: []api.DeviceBrief{{ID: "other", Name: "Phone", Platform: "android"}}}})
	}))
	p.srv = httptest.NewServer(mux)
	t.Cleanup(p.srv.Close)
	return p
}

func reply(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}

func errReply(w http.ResponseWriter, status int, code, msg string) {
	reply(w, status, map[string]any{"error": map[string]string{"code": code, "message": msg}})
}

func itoa(n int) string {
	b, _ := json.Marshal(n)
	return string(b)
}

func (p *fakePanel) set(f func(p *fakePanel)) {
	p.mu.Lock()
	defer p.mu.Unlock()
	f(p)
}

// ---- harness

type harness struct {
	t      *testing.T
	d      *Daemon
	panel  *fakePanel
	runner *fakeRunner
	dir    string
	ctx    context.Context
}

func newHarness(t *testing.T) *harness {
	t.Helper()
	dir := t.TempDir()
	panel := newFakePanel(t)
	runner := &fakeRunner{}
	ctx, cancel := context.WithCancel(context.Background())
	d := New(Config{
		StatePath:  filepath.Join(dir, "state", "state.json"),
		SocketPath: filepath.Join(dir, "run", "colitud.sock"),
		Log:        slog.New(slog.NewTextHandler(io.Discard, nil)),
		API:        api.New(panel.srv.URL + "/api/v1"),
		Runner:     runner,
		FindCore:   func(string) (string, error) { return "fake-sing-box", nil },
		IfaceUp:    func(string) bool { return true },
		Sleep:      func(ctx context.Context, _ time.Duration) error { return ctx.Err() },
		Backoff:    func(int) time.Duration { return 0 },
	})
	if err := d.Init(ctx); err != nil {
		t.Fatal(err)
	}
	h := &harness{t: t, d: d, panel: panel, runner: runner, dir: dir, ctx: ctx}
	t.Cleanup(func() {
		d.Shutdown()
		cancel()
	})
	return h
}

// do runs a command and decodes the answer into out.
func (h *harness) do(cmd string, args, out any) *proto.Error {
	h.t.Helper()
	req := proto.Request{Cmd: cmd}
	if args != nil {
		req.Args, _ = json.Marshal(args)
	}
	data, perr := h.d.Handle(h.ctx, req)
	if perr != nil {
		return perr
	}
	if out != nil {
		raw, err := json.Marshal(data)
		if err != nil {
			h.t.Fatal(err)
		}
		if err := json.Unmarshal(raw, out); err != nil {
			h.t.Fatal(err)
		}
	}
	return nil
}

func (h *harness) mustDo(cmd string, args, out any) {
	h.t.Helper()
	if perr := h.do(cmd, args, out); perr != nil {
		h.t.Fatalf("%s failed: %v", cmd, perr)
	}
}

func (h *harness) status() proto.Status {
	h.t.Helper()
	var st proto.Status
	h.mustDo(proto.CmdStatus, nil, &st)
	return st
}

func (h *harness) eventually(what string, cond func() bool) {
	h.t.Helper()
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		if cond() {
			return
		}
		time.Sleep(5 * time.Millisecond)
	}
	h.t.Fatalf("timed out waiting for %s", what)
}

// login signs in through the real device-link commands.
func (h *harness) login() {
	h.t.Helper()
	var info proto.LoginInfo
	h.mustDo(proto.CmdLoginStart, nil, &info)
	if info.Code != "ABCD2345" || !strings.HasSuffix(info.DeviceName, "(headless)") {
		h.t.Fatalf("login info = %+v", info)
	}
	h.eventually("login to finish", func() bool {
		var ls proto.LoginState
		h.mustDo(proto.CmdLoginStatus, nil, &ls)
		return ls.State != proto.LoginPending
	})
	var ls proto.LoginState
	h.mustDo(proto.CmdLoginStatus, nil, &ls)
	if ls.State != proto.LoginApproved {
		h.t.Fatalf("login state = %+v", ls)
	}
}

func (h *harness) readState() state.State {
	h.t.Helper()
	st, _, err := state.NewStore(filepath.Join(h.dir, "state", "state.json")).Load()
	if err != nil {
		h.t.Fatal(err)
	}
	return st
}

// ---- tests

func TestNotLoggedInCommandsAreRefused(t *testing.T) {
	h := newHarness(t)
	if st := h.status(); st.State != proto.StateLoggedOut || st.LoggedIn {
		t.Fatalf("fresh status = %+v", st)
	}
	for _, cmd := range []string{proto.CmdServers, proto.CmdConnect, proto.CmdAutoConnect, proto.CmdActivate} {
		perr := h.do(cmd, map[string]any{"enabled": true}, nil)
		if perr == nil || perr.Code != proto.CodeNotLoggedIn {
			t.Errorf("%s while signed out = %v, want NOT_LOGGED_IN", cmd, perr)
		}
	}
}

func TestLoginRegistersDeviceAndSavesPrivateState(t *testing.T) {
	h := newHarness(t)
	h.login()

	st := h.status()
	if !st.LoggedIn || st.State != proto.StateDisconnected || st.Device == nil || st.Device.ID != panelDevice {
		t.Fatalf("status after login = %+v", st)
	}
	h.panel.mu.Lock()
	reg := h.panel.registered
	refreshes := h.panel.refreshes
	h.panel.mu.Unlock()
	if len(reg) != 1 {
		t.Fatalf("registrations = %d", len(reg))
	}
	if reg[0]["platform"] != "linux" || reg[0]["device_key"] == "" {
		t.Errorf("registration = %v", reg[0])
	}
	if refreshes != 1 {
		t.Errorf("refreshes = %d, want 1 (binding the tokens to the device)", refreshes)
	}
	saved := h.readState()
	if saved.DeviceID != panelDevice || saved.RefreshToken != "ref-1" || saved.AccessToken != "acc-1" {
		t.Errorf("saved state = %+v", saved)
	}
	if runtime.GOOS != "windows" {
		info, err := os.Stat(filepath.Join(h.dir, "state", "state.json"))
		if err != nil {
			t.Fatal(err)
		}
		if info.Mode().Perm() != 0o600 {
			t.Errorf("state file mode = %v", info.Mode().Perm())
		}
	}
	// A second login while signed in is refused.
	if perr := h.do(proto.CmdLoginStart, nil, nil); perr == nil || perr.Code != proto.CodeAlreadyIn {
		t.Errorf("second login = %v, want ALREADY_LOGGED_IN", perr)
	}
}

func TestLoginExpiredAndDenied(t *testing.T) {
	for name, tc := range map[string]struct {
		status int
		code   string
		want   string
	}{
		"expired": {410, "LINK_EXPIRED", proto.LoginExpired},
		"denied":  {403, "LINK_DENIED", proto.LoginDenied},
	} {
		t.Run(name, func(t *testing.T) {
			h := newHarness(t)
			mux := http.NewServeMux()
			mux.HandleFunc("POST /api/v1/auth/link/start", func(w http.ResponseWriter, r *http.Request) {
				reply(w, 201, api.LinkStart{Code: "ABCD2345", URL: "u", PollToken: pollTok, ExpiresIn: 600, Interval: 1})
			})
			mux.HandleFunc("POST /api/v1/auth/link/poll", func(w http.ResponseWriter, r *http.Request) {
				errReply(w, tc.status, tc.code, "x")
			})
			srv := httptest.NewServer(mux)
			defer srv.Close()
			h.d.cfg.API = api.New(srv.URL + "/api/v1")

			h.mustDo(proto.CmdLoginStart, nil, nil)
			var ls proto.LoginState
			h.eventually("the login to end", func() bool {
				h.mustDo(proto.CmdLoginStatus, nil, &ls)
				return ls.State != proto.LoginPending
			})
			if ls.State != tc.want {
				t.Errorf("login state = %+v, want %s", ls, tc.want)
			}
			if h.status().LoggedIn {
				t.Error("a failed sign-in must not log in")
			}
		})
	}
}

func TestLoginCanBeCancelled(t *testing.T) {
	h := newHarness(t)
	h.panel.set(func(p *fakePanel) { p.linkPolls = -1000 }) // never approves
	h.mustDo(proto.CmdLoginStart, nil, nil)
	h.mustDo(proto.CmdLoginCancel, nil, nil)
	var ls proto.LoginState
	h.mustDo(proto.CmdLoginStatus, nil, &ls)
	if ls.State != proto.LoginFailed {
		t.Errorf("state after cancel = %+v", ls)
	}
}

func TestConnectByCountryUsesBestServerAndBuildsConfig(t *testing.T) {
	h := newHarness(t)
	h.login()

	var st proto.Status
	h.mustDo(proto.CmdConnect, proto.ConnectArgs{Country: "TR"}, &st)
	if st.State != proto.StateConnected || st.Server == nil || st.Server.ID != "id-tr-2" {
		t.Fatalf("status = %+v, want connected to Ankara (lowest load in TR)", st)
	}
	if st.Since == nil {
		t.Error("connected status must carry the start time")
	}
	h.panel.mu.Lock()
	prefs := append([]api.Preference(nil), h.panel.preferences...)
	h.panel.mu.Unlock()
	if len(prefs) != 1 || prefs[0].NodeID != "id-tr-2" || prefs[0].Protocol != "auto" {
		t.Errorf("preferences = %+v", prefs)
	}

	proc := h.runner.last()
	if proc == nil {
		t.Fatal("sing-box was not started")
	}
	var cfg map[string]any
	if err := json.Unmarshal(proc.cfg, &cfg); err != nil {
		t.Fatalf("config is not JSON: %v", err)
	}
	tun := cfg["inbounds"].([]any)[0].(map[string]any)
	if tun["strict_route"] != true {
		t.Error("the kill switch (strict_route) must be on")
	}
	if strings.Contains(string(proc.cfg), "x.example.test") {
		t.Error("the XHTTP candidate must not reach sing-box")
	}
	if len(st.Server.Protocols) != 1 || st.Server.Protocols[0] != "hysteria2" {
		t.Errorf("transports = %v", st.Server.Protocols)
	}
	if h.readState().LastServerID != "id-tr-2" {
		t.Error("the last server must be remembered for autoconnect")
	}

	// Disconnect stops sing-box and removes the config with its secrets.
	h.mustDo(proto.CmdDisconnect, nil, &st)
	if st.State != proto.StateDisconnected {
		t.Errorf("state after disconnect = %s", st.State)
	}
	if !proc.stopped.Load() {
		t.Error("sing-box was not stopped")
	}
	if _, err := os.Stat(proc.cfgPath); !errors.Is(err, os.ErrNotExist) {
		t.Errorf("config file still exists (%v)", err)
	}
}

func TestConfigFileIsPrivate(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("Unix permission bits are not meaningful on Windows")
	}
	h := newHarness(t)
	h.login()
	h.mustDo(proto.CmdConnect, proto.ConnectArgs{}, nil)
	info, err := os.Stat(h.runner.last().cfgPath)
	if err != nil {
		t.Fatal(err)
	}
	if info.Mode().Perm() != 0o600 {
		t.Errorf("sing-box config mode = %v, want 0600 (it holds the device's credentials)", info.Mode().Perm())
	}
}

func TestConnectBestServerAndByID(t *testing.T) {
	h := newHarness(t)
	h.login()
	var st proto.Status
	h.mustDo(proto.CmdConnect, proto.ConnectArgs{}, &st)
	if st.Server.ID != "id-nl-1" {
		t.Errorf("best server = %s, want id-nl-1", st.Server.ID)
	}
	// Connecting again replaces the running tunnel.
	first := h.runner.last()
	h.mustDo(proto.CmdConnect, proto.ConnectArgs{ServerID: "id-de-1"}, &st)
	if st.Server.ID != "id-de-1" || !first.stopped.Load() {
		t.Errorf("status = %+v, first stopped = %v", st, first.stopped.Load())
	}
	if perr := h.do(proto.CmdConnect, proto.ConnectArgs{ServerID: "missing"}, nil); perr == nil || perr.Code != proto.CodeNoServer {
		t.Errorf("unknown server = %v, want NO_SERVER", perr)
	}
}

func TestConnectFailureIsReportedNotRetried(t *testing.T) {
	h := newHarness(t)
	h.login()
	h.runner.mu.Lock()
	h.runner.failing = errors.New("exec: permission denied")
	h.runner.mu.Unlock()
	perr := h.do(proto.CmdConnect, proto.ConnectArgs{}, nil)
	if perr == nil {
		t.Fatal("connect must fail when sing-box cannot start")
	}
	if st := h.status(); st.State != proto.StateDisconnected {
		t.Errorf("state = %s, want disconnected", st.State)
	}
}

func TestPausedDeviceAndActivate(t *testing.T) {
	h := newHarness(t)
	h.login()
	h.panel.set(func(p *fakePanel) { p.overLimit = true })

	perr := h.do(proto.CmdConnect, proto.ConnectArgs{}, nil)
	if perr == nil || perr.Code != proto.CodePaused {
		t.Fatalf("connect on a paused device = %v, want DEVICE_PAUSED", perr)
	}
	var p proto.Paused
	if err := json.Unmarshal(perr.Data, &p); err != nil || p.DeviceLimit != 1 || len(p.ActiveDevices) != 1 || p.ActiveDevices[0].Name != "Phone" {
		t.Errorf("paused detail = %s (%v)", perr.Data, err)
	}
	st := h.status()
	if st.Paused == nil || st.Paused.ActiveDevices[0].Name != "Phone" {
		t.Errorf("status must show the paused info: %+v", st)
	}
	if st.State != proto.StateDisconnected || !st.LoggedIn {
		t.Errorf("a paused device stays signed in: %+v", st)
	}

	var res proto.ActivateResult
	h.mustDo(proto.CmdActivate, nil, &res)
	if len(res.Suspended) != 1 || res.Suspended[0].Name != "Phone" {
		t.Errorf("activate result = %+v", res)
	}
	if st := h.status(); st.Paused != nil {
		t.Errorf("paused info must clear after activate: %+v", st.Paused)
	}
	h.mustDo(proto.CmdConnect, proto.ConnectArgs{}, &st)
	if st.State != proto.StateConnected {
		t.Errorf("connect after activate: %+v", st)
	}
}

func TestExpiredAccessTokenIsRefreshedAndSaved(t *testing.T) {
	h := newHarness(t)
	h.login()
	h.d.mu.Lock()
	h.d.st.AccessExpiresAt = time.Now().Add(-time.Minute)
	h.d.mu.Unlock()

	h.mustDo(proto.CmdServers, nil, nil)
	h.panel.mu.Lock()
	refreshes := h.panel.refreshes
	h.panel.mu.Unlock()
	if refreshes != 2 {
		t.Errorf("refreshes = %d, want 2 (bind at login, then the expired one)", refreshes)
	}
	if got := h.readState().RefreshToken; got != "ref-2" {
		t.Errorf("saved refresh token = %q, want the rotated ref-2", got)
	}
}

func TestRevokedSessionSignsOut(t *testing.T) {
	h := newHarness(t)
	h.login()
	h.mustDo(proto.CmdConnect, proto.ConnectArgs{}, nil)
	h.panel.set(func(p *fakePanel) {
		p.refreshErr = &api.Error{Status: 401, Code: "AUTH_REFRESH_REUSED", Message: "reuse"}
	})
	h.d.mu.Lock()
	h.d.st.AccessExpiresAt = time.Now().Add(-time.Minute)
	h.d.mu.Unlock()

	perr := h.do(proto.CmdServers, nil, nil)
	if perr == nil || perr.Code != proto.CodeNotLoggedIn {
		t.Fatalf("servers with a dead session = %v, want NOT_LOGGED_IN", perr)
	}
	h.eventually("the tunnel to stop", func() bool { return h.status().State == proto.StateLoggedOut })
	st := h.status()
	if st.LoggedIn || st.LastError == "" {
		t.Errorf("status = %+v", st)
	}
	h.eventually("sing-box to stop", func() bool { return h.runner.last().stopped.Load() })
	saved := h.readState()
	if saved.LoggedIn() || saved.RefreshToken != "" || saved.DeviceKey == "" {
		t.Errorf("tokens must be wiped but the device key kept: %+v", saved)
	}
}

func TestTunnelCrashReconnects(t *testing.T) {
	h := newHarness(t)
	h.login()
	h.mustDo(proto.CmdConnect, proto.ConnectArgs{Country: "DE"}, nil)
	first := h.runner.last()
	first.exit(errors.New("signal: killed"))
	h.eventually("a second sing-box", func() bool { return h.runner.count() == 2 })
	h.eventually("connected again", func() bool { return h.status().State == proto.StateConnected })
	if st := h.status(); st.Server == nil || st.Server.ID != "id-de-1" {
		t.Errorf("reconnected to %+v, want the same server", st.Server)
	}
}

func TestAutoConnectAtStart(t *testing.T) {
	h := newHarness(t)
	h.login()
	h.mustDo(proto.CmdConnect, proto.ConnectArgs{Country: "NL"}, nil)
	h.mustDo(proto.CmdAutoConnect, proto.AutoConnectArgs{Enabled: true}, nil)
	if !h.readState().AutoConnect {
		t.Fatal("autoconnect was not saved")
	}
	h.mustDo(proto.CmdDisconnect, nil, nil)
	before := h.runner.count()

	// "Reboot": a new daemon on the same state file.
	d2 := New(Config{
		StatePath:  filepath.Join(h.dir, "state", "state.json"),
		SocketPath: filepath.Join(h.dir, "run", "colitud.sock"),
		Log:        slog.New(slog.NewTextHandler(io.Discard, nil)),
		API:        api.New(h.panel.srv.URL + "/api/v1"),
		Runner:     h.runner,
		FindCore:   func(string) (string, error) { return "fake-sing-box", nil },
		IfaceUp:    func(string) bool { return true },
		Sleep:      func(ctx context.Context, _ time.Duration) error { return ctx.Err() },
		Backoff:    func(int) time.Duration { return 0 },
	})
	ctx, cancel := context.WithCancel(context.Background())
	defer func() { cancel(); d2.Shutdown() }()
	if err := d2.Init(ctx); err != nil {
		t.Fatal(err)
	}
	d2.autoConnectAtStart()
	h.eventually("autoconnect", func() bool { return d2.status().State == proto.StateConnected })
	if h.runner.count() != before+1 {
		t.Errorf("sing-box starts = %d, want %d", h.runner.count(), before+1)
	}
	if st := d2.status(); st.Server == nil || st.Server.ID != "id-nl-1" {
		t.Errorf("autoconnected to %+v, want the last server", st.Server)
	}
}

func TestAutoConnectKeepsRetryingUntilTheNetworkIsUp(t *testing.T) {
	h := newHarness(t)
	h.login()
	h.mustDo(proto.CmdConnect, proto.ConnectArgs{Country: "NL"}, nil)
	h.mustDo(proto.CmdAutoConnect, proto.AutoConnectArgs{Enabled: true}, nil)
	h.mustDo(proto.CmdDisconnect, nil, nil)

	var up atomic.Bool
	h.d.cfg.IfaceUp = func(string) bool { return up.Load() }
	h.d.cfg.ReadyTimeout = 30 * time.Millisecond
	h.d.autoConnectAtStart()
	time.Sleep(120 * time.Millisecond) // a few failed attempts
	if st := h.status(); st.State != proto.StateConnecting {
		t.Errorf("state while retrying = %s, want connecting", st.State)
	}
	up.Store(true)
	h.eventually("autoconnect to succeed", func() bool { return h.status().State == proto.StateConnected })
}

func TestLogoutClearsSessionAndStopsTunnel(t *testing.T) {
	h := newHarness(t)
	h.login()
	h.mustDo(proto.CmdConnect, proto.ConnectArgs{}, nil)
	proc := h.runner.last()

	var st proto.Status
	h.mustDo(proto.CmdLogout, proto.LogoutArgs{RemoveDevice: true}, &st)
	if st.LoggedIn || st.State != proto.StateLoggedOut {
		t.Errorf("status after logout = %+v", st)
	}
	if !proc.stopped.Load() {
		t.Error("logout must stop the tunnel")
	}
	h.panel.mu.Lock()
	loggedOut, removed := h.panel.loggedOut, h.panel.removed
	h.panel.mu.Unlock()
	if loggedOut != 1 || removed != 1 {
		t.Errorf("panel saw logout=%d remove=%d, want 1 and 1", loggedOut, removed)
	}
	saved := h.readState()
	if saved.RefreshToken != "" || saved.AutoConnect {
		t.Errorf("saved state = %+v", saved)
	}
}

func TestStatusAfterRestartKeepsDeviceKey(t *testing.T) {
	h := newHarness(t)
	key := h.readState().DeviceKey
	if key == "" {
		t.Fatal("Init must create the device key")
	}
	h.login()
	h.mustDo(proto.CmdLogout, nil, nil)
	if got := h.readState().DeviceKey; got != key {
		t.Errorf("device key changed across login/logout: %q -> %q", key, got)
	}
}

func TestUnknownCommand(t *testing.T) {
	h := newHarness(t)
	if perr := h.do("nope", nil, nil); perr == nil || perr.Code != proto.CodeUnknownCommand {
		t.Errorf("unknown command = %v", perr)
	}
}

func TestConcurrentLoginStartsLeaveNoOrphanSignIn(t *testing.T) {
	h := newHarness(t)
	h.panel.set(func(p *fakePanel) { p.linkPolls = -1 << 30 }) // pending until told otherwise
	var wg sync.WaitGroup
	for range 5 {
		wg.Add(1)
		go func() {
			defer wg.Done()
			_ = h.do(proto.CmdLoginStart, nil, nil)
		}()
	}
	wg.Wait()
	h.mustDo(proto.CmdLoginCancel, nil, nil)
	// Approving now must not sign the daemon in through a sign-in nobody can see.
	h.panel.set(func(p *fakePanel) { p.linkPolls = 10 })
	time.Sleep(200 * time.Millisecond)
	if st := h.status(); st.LoggedIn {
		t.Fatal("a cancelled sign-in signed the daemon in")
	}
	h.panel.mu.Lock()
	registered := len(h.panel.registered)
	h.panel.mu.Unlock()
	if registered != 0 {
		t.Fatalf("%d devices registered after the sign-in was cancelled", registered)
	}
}
