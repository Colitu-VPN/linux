package daemon

import (
	"context"
	"errors"
	"fmt"
	"os"
	"time"

	"github.com/colitu/colitu-linux/headless/internal/api"
	"github.com/colitu/colitu-linux/headless/internal/proto"
	"github.com/colitu/colitu-linux/headless/internal/singbox"
)

// stopGrace is how long sing-box gets to remove its routes after SIGTERM.
const stopGrace = 8 * time.Second

// stableAfter is how long a tunnel must stay up for the reconnect backoff to
// start over: a tunnel that dies at once, again and again, backs off.
const stableAfter = 30 * time.Second

// Target is the server a session connects to.
type Target struct {
	ServerID string
	Name     string
	Country  string
	City     string
}

// session is one connect request: it starts the tunnel, watches it and
// restarts it if sing-box dies, until it is cancelled. Its mutable fields
// are guarded by Daemon.mu.
type session struct {
	target     Target
	retryFirst bool
	cancel     context.CancelFunc
	done       chan struct{}
	// first receives the outcome of the first start (nil = connected).
	first chan error

	state  string
	server *proto.ConnectedServer
	since  time.Time
}

// startSession launches a session in the background. The caller holds
// connMu and has stopped any previous session. With retryFirst (autoconnect
// at boot) a failing first start is retried with backoff instead of being
// reported and abandoned.
func (d *Daemon) startSession(t Target, retryFirst bool) *session {
	ctx, cancel := context.WithCancel(d.root)
	s := &session{
		target:     t,
		retryFirst: retryFirst,
		cancel:     cancel,
		done:       make(chan struct{}),
		first:      make(chan error, 1),
		state:      proto.StateConnecting,
		server:     &proto.ConnectedServer{ID: t.ServerID, Name: t.Name, Country: t.Country, City: t.City},
	}
	d.mu.Lock()
	d.sess = s
	d.lastErr = ""
	d.mu.Unlock()
	go d.runSession(ctx, s)
	return s
}

// stopSession ends the current session and waits until sing-box is gone and
// the routes are back.
func (d *Daemon) stopSession() {
	d.mu.Lock()
	s := d.sess
	d.mu.Unlock()
	if s == nil {
		return
	}
	s.cancel()
	<-s.done
}

// hardError reports failures a retry cannot fix.
func hardError(err error) bool {
	switch {
	case errors.Is(err, errNotLoggedIn),
		errors.Is(err, ErrCoreMissing),
		errors.Is(err, singbox.ErrNoUsableOutbound),
		api.HasCode(err, api.CodeDeviceOverLimit):
		return true
	}
	return false
}

func (d *Daemon) runSession(ctx context.Context, s *session) {
	defer close(s.done)
	defer func() {
		d.mu.Lock()
		if d.sess == s {
			d.sess = nil
		}
		d.mu.Unlock()
	}()

	attempt := 0
	first := true
	reportFirst := func(err error) {
		if first {
			first = false
			s.first <- err
		}
	}
	defer reportFirst(context.Canceled)

	for {
		startedAt := d.cfg.Now()
		proc, cfgPath, info, err := d.startTunnel(ctx, s)
		if err != nil {
			if ctx.Err() != nil {
				return
			}
			d.log.Warn("tunnel start failed", "err", err, "attempt", attempt+1)
			d.setLastErr(d.fail(err).Message)
			if hardError(err) || (first && !s.retryFirst) {
				reportFirst(err)
				return
			}
			if d.cfg.Sleep(ctx, d.cfg.Backoff(attempt)) != nil {
				return
			}
			attempt++
			continue
		}

		d.mu.Lock()
		s.state = proto.StateConnected
		s.since = d.cfg.Now()
		s.server = info
		d.lastErr = ""
		d.st.LastServerID, d.st.LastServerName, d.st.LastServerCountry = info.ID, info.Name, info.Country
		d.saveLocked()
		d.mu.Unlock()
		d.log.Info("connected", "server", info.Name, "transports", info.Protocols)
		reportFirst(nil)

		select {
		case <-ctx.Done():
			proc.Stop(stopGrace)
			removeFile(cfgPath)
			d.log.Info("disconnected")
			return
		case werr := <-proc.Done():
			removeFile(cfgPath)
			if ctx.Err() != nil {
				return
			}
			d.log.Warn("sing-box exited; reconnecting", "err", werr)
			d.mu.Lock()
			s.state = proto.StateReconnecting
			d.lastErr = "the tunnel process exited; reconnecting"
			d.mu.Unlock()
			if d.cfg.Now().Sub(startedAt) >= stableAfter {
				attempt = 0
			}
			if d.cfg.Sleep(ctx, d.cfg.Backoff(attempt)) != nil {
				return
			}
			attempt++
		}
	}
}

func removeFile(path string) {
	if path != "" {
		_ = os.Remove(path)
	}
}

// startTunnel fetches the server's configuration, writes the sing-box
// config and starts sing-box, returning once the TUN interface is up. The
// process is stopped again on any failure.
func (d *Daemon) startTunnel(ctx context.Context, s *session) (Process, string, *proto.ConnectedServer, error) {
	var cfg api.Config
	err := d.call(ctx, func(a api.Auth) error {
		if err := d.cfg.API.SetPreference(ctx, a, api.Preference{Protocol: "auto", NodeID: s.target.ServerID}); err != nil {
			return err
		}
		c, err := d.cfg.API.Config(ctx, a)
		cfg = c
		return err
	})
	if err != nil {
		return nil, "", nil, err
	}
	cands := make([]singbox.Candidate, 0, len(cfg.Candidates)+1)
	for _, c := range cfg.Candidates {
		cands = append(cands, singbox.Candidate{Protocol: c.Protocol, Payload: c.Profile.Payload})
	}
	if len(cands) == 0 && len(cfg.Profile.Payload) > 0 {
		cands = append(cands, singbox.Candidate{Payload: cfg.Profile.Payload})
	}
	built, err := singbox.Build(cands, singbox.Options{Interface: d.cfg.Interface, DNS: d.cfg.DNS})
	if err != nil {
		return nil, "", nil, err
	}
	for _, skipped := range built.Skipped {
		d.log.Info("transport skipped", "why", skipped)
	}
	core, err := d.cfg.FindCore(d.cfg.SingboxPath)
	if err != nil {
		return nil, "", nil, err
	}
	cfgPath, err := writeConfig(d.cfg.RunDir, built.JSON)
	if err != nil {
		return nil, "", nil, fmt.Errorf("cannot write the sing-box config: %w", err)
	}
	proc, err := d.cfg.Runner.Start(core, cfgPath, d.cfg.WorkDir)
	if err != nil {
		removeFile(cfgPath)
		return nil, "", nil, err
	}
	if err := d.waitReady(ctx, proc); err != nil {
		proc.Stop(stopGrace)
		removeFile(cfgPath)
		return nil, "", nil, err
	}

	info := &proto.ConnectedServer{ID: s.target.ServerID, Name: s.target.Name, Country: s.target.Country, City: s.target.City, Protocols: built.Protocols}
	if id := cfg.ServerID(); id != "" {
		// The panel may have substituted another server (FallbackUsed).
		if id != s.target.ServerID {
			info = &proto.ConnectedServer{ID: id, Protocols: built.Protocols}
		}
		if n, _ := cfg.Server["name"].(string); n != "" {
			info.Name = n
		}
		if c, _ := cfg.Server["country"].(string); c != "" {
			info.Country = c
		}
	}
	return proc, cfgPath, info, nil
}

// waitReady waits for the TUN interface to appear and stay up.
func (d *Daemon) waitReady(ctx context.Context, proc Process) error {
	deadline := time.NewTimer(d.cfg.ReadyTimeout)
	defer deadline.Stop()
	tick := time.NewTicker(200 * time.Millisecond)
	defer tick.Stop()
	for {
		if d.cfg.IfaceUp(d.cfg.Interface) {
			break
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case err := <-proc.Done():
			return fmt.Errorf("sing-box exited while starting: %v", exitText(err))
		case <-deadline.C:
			return errors.New("the tunnel did not come up in time (check `journalctl -u colitud`)")
		case <-tick.C:
		}
	}
	if d.cfg.Settle > 0 {
		select {
		case <-ctx.Done():
			return ctx.Err()
		case err := <-proc.Done():
			return fmt.Errorf("sing-box exited while starting: %v", exitText(err))
		case <-time.After(d.cfg.Settle):
		}
	}
	return nil
}

func exitText(err error) string {
	if err == nil {
		return "exit status 0"
	}
	return err.Error()
}
