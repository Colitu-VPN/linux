package daemon

import (
	"context"
	"errors"
	"time"

	"github.com/colitu/colitu-linux/headless/internal/api"
	"github.com/colitu/colitu-linux/headless/internal/proto"
	"github.com/colitu/colitu-linux/headless/internal/state"
	"github.com/colitu/colitu-linux/headless/internal/version"
)

// linkSession is one device-link sign-in in progress (or just finished).
type linkSession struct {
	cancel context.CancelFunc
	done   chan struct{}

	// state and message are guarded by Daemon.mu.
	state   string
	message string
}

func (d *Daemon) loginStart(ctx context.Context) (any, *proto.Error) {
	d.loginMu.Lock()
	defer d.loginMu.Unlock()
	if d.loggedIn() {
		return nil, &proto.Error{Code: proto.CodeAlreadyIn, Message: "already signed in: run `colitu logout` first to use another account"}
	}
	d.cancelLogin()
	name := deviceName()
	start, err := d.cfg.API.LinkStart(ctx, name)
	if err != nil {
		return nil, d.fail(err)
	}
	lctx, cancel := context.WithCancel(d.root)
	ls := &linkSession{cancel: cancel, done: make(chan struct{}), state: proto.LoginPending}
	d.mu.Lock()
	d.link = ls
	d.mu.Unlock()
	go d.runLogin(lctx, ls, start)
	return proto.LoginInfo{
		Code:       start.Code,
		URL:        start.URL,
		ExpiresAt:  d.cfg.Now().Add(time.Duration(start.ExpiresIn) * time.Second),
		DeviceName: name,
	}, nil
}

func (d *Daemon) setLink(ls *linkSession, st, msg string) {
	d.mu.Lock()
	ls.state, ls.message = st, msg
	d.mu.Unlock()
}

func (d *Daemon) runLogin(ctx context.Context, ls *linkSession, start api.LinkStart) {
	defer close(ls.done)
	tokens, err := d.cfg.API.WaitLink(ctx, start, d.cfg.Sleep)
	switch {
	case err == nil:
		if err := d.completeLogin(ctx, ls, tokens); err != nil {
			d.log.Warn("sign-in could not be completed", "err", err)
			d.setLink(ls, proto.LoginFailed, d.fail(err).Message)
			return
		}
		d.setLink(ls, proto.LoginApproved, "")
	case ctx.Err() != nil:
		d.setLink(ls, proto.LoginFailed, "cancelled")
	case api.HasCode(err, api.CodeLinkDenied):
		d.setLink(ls, proto.LoginDenied, "the sign-in was declined")
	case api.HasCode(err, api.CodeLinkExpired), api.HasCode(err, api.CodeLinkNotFound):
		d.setLink(ls, proto.LoginExpired, "the code expired before it was approved")
	default:
		d.setLink(ls, proto.LoginFailed, d.fail(err).Message)
	}
}

// completeLogin registers this device with the account the user approved
// and stores the credentials. A failure revokes the tokens again so no
// half-finished session is left on the account.
func (d *Daemon) completeLogin(ctx context.Context, ls *linkSession, tokens api.Tokens) error {
	d.connMu.Lock()
	defer d.connMu.Unlock()

	d.mu.Lock()
	st := d.st
	current := d.link == ls
	d.mu.Unlock()
	// A sign-in that was replaced or cancelled in the meantime, or one that
	// would overwrite a session already stored, must not sign the daemon in.
	if !current || ctx.Err() != nil || st.LoggedIn() {
		revoke(d, tokens.RefreshToken)
		return errors.New("the sign-in was cancelled")
	}
	if st.DeviceKey == "" {
		key, err := state.NewDeviceKey()
		if err != nil {
			return err
		}
		st.DeviceKey = key
	}
	name := deviceName()
	dev, err := d.cfg.API.RegisterDevice(ctx, tokens.AccessToken, api.Registration{
		DeviceKey:    st.DeviceKey,
		Name:         name,
		Platform:     api.Platform,
		AppVersion:   version.Version,
		OSVersion:    osVersion(),
		HardwareID:   hardwareID(),
		Capabilities: api.ClientCapabilities(),
	})
	if err != nil {
		revoke(d, tokens.RefreshToken)
		return err
	}
	// Bind the tokens to the device; if that fails the unbound pair still
	// works with X-Device-ID and binds itself on its first refresh.
	if bound, err := d.cfg.API.Refresh(ctx, tokens.RefreshToken, dev.ID); err == nil {
		tokens = bound
	}
	st.DeviceID = dev.ID
	st.DeviceName = dev.Name
	if st.DeviceName == "" {
		st.DeviceName = name
	}
	st.AccessToken = tokens.AccessToken
	st.RefreshToken = tokens.RefreshToken
	st.AccessExpiresAt = d.cfg.Now().Add(time.Duration(tokens.ExpiresIn) * time.Second)
	if err := d.store.Save(st); err != nil {
		revoke(d, tokens.RefreshToken)
		return errors.New("cannot save the credentials: " + err.Error())
	}
	d.mu.Lock()
	d.st = st
	d.lastErr = ""
	d.paused = nil
	d.mu.Unlock()
	d.log.Info("signed in", "device", st.DeviceName)
	return nil
}

// revoke signs a token out, ignoring failures: it only tidies up.
func revoke(d *Daemon, refreshToken string) {
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	_ = d.cfg.API.Logout(ctx, refreshToken)
}

func (d *Daemon) loginStatus() (any, *proto.Error) {
	d.mu.Lock()
	defer d.mu.Unlock()
	if d.link == nil {
		if d.st.LoggedIn() {
			return proto.LoginState{State: proto.LoginApproved}, nil
		}
		return nil, &proto.Error{Code: proto.CodeNoLogin, Message: "no sign-in in progress"}
	}
	return proto.LoginState{State: d.link.state, Message: d.link.message}, nil
}

// cancelLogin stops a sign-in in progress and waits for it to end.
func (d *Daemon) cancelLogin() {
	d.mu.Lock()
	ls := d.link
	d.mu.Unlock()
	if ls == nil {
		return
	}
	ls.cancel()
	<-ls.done
}

func (d *Daemon) logout(ctx context.Context, a proto.LogoutArgs) (any, *proto.Error) {
	d.loginMu.Lock()
	d.cancelLogin()
	d.loginMu.Unlock()
	d.connMu.Lock()
	defer d.connMu.Unlock()
	d.stopSession()

	d.mu.Lock()
	st := d.st
	d.mu.Unlock()
	if st.LoggedIn() {
		// Best effort: the device is signed out locally whatever the panel says.
		if a.RemoveDevice {
			if auth, err := d.auth(ctx, false); err == nil {
				if err := d.cfg.API.RemoveDevice(ctx, auth); err != nil {
					d.log.Warn("could not remove the device from the account", "err", err)
				}
			}
		}
		lctx, cancel := context.WithTimeout(ctx, 5*time.Second)
		_ = d.cfg.API.Logout(lctx, st.RefreshToken)
		cancel()
	}
	d.mu.Lock()
	d.st.ClearSession()
	d.lastErr = ""
	d.paused = nil
	d.link = nil
	err := d.store.Save(d.st)
	d.mu.Unlock()
	if err != nil {
		return nil, &proto.Error{Code: proto.CodeInternal, Message: "signed out, but the state file could not be saved: " + err.Error()}
	}
	return d.status(), nil
}
