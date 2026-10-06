package daemon

import (
	"context"
	"time"

	"github.com/colitu/colitu-linux/headless/internal/api"
)

// accessSkew is how long before its expiry an access token is replaced.
const accessSkew = 60 * time.Second

// auth returns valid credentials, refreshing the access token when it is
// (about to be) expired or force is set. Refresh tokens are single-use, so
// the new pair is saved before it is used.
func (d *Daemon) auth(ctx context.Context, force bool) (api.Auth, error) {
	d.tokMu.Lock()
	defer d.tokMu.Unlock()

	d.mu.Lock()
	st := d.st
	d.mu.Unlock()
	if !st.LoggedIn() {
		return api.Auth{}, errNotLoggedIn
	}
	if !force && st.AccessToken != "" && d.cfg.Now().Add(accessSkew).Before(st.AccessExpiresAt) {
		return api.Auth{Bearer: st.AccessToken, DeviceID: st.DeviceID}, nil
	}
	tokens, err := d.cfg.API.Refresh(ctx, st.RefreshToken, st.DeviceID)
	if err != nil {
		if api.IsSessionEnded(err) {
			d.endSession("the Colitu account signed this device out; sign in again")
			return api.Auth{}, errNotLoggedIn
		}
		return api.Auth{}, err
	}
	d.mu.Lock()
	defer d.mu.Unlock()
	if !d.st.LoggedIn() || d.st.RefreshToken != st.RefreshToken {
		// Signed out (or in again) while the refresh was in flight.
		return api.Auth{}, errNotLoggedIn
	}
	d.st.AccessToken = tokens.AccessToken
	d.st.RefreshToken = tokens.RefreshToken
	d.st.AccessExpiresAt = d.cfg.Now().Add(time.Duration(tokens.ExpiresIn) * time.Second)
	d.saveLocked()
	return api.Auth{Bearer: tokens.AccessToken, DeviceID: d.st.DeviceID}, nil
}

// call runs an authenticated API request. An expired access token is
// refreshed and the request repeated once; a paused device is remembered
// (status shows it); a session the panel ended signs the daemon out.
func (d *Daemon) call(ctx context.Context, fn func(api.Auth) error) error {
	for attempt := 0; ; attempt++ {
		auth, err := d.auth(ctx, attempt > 0)
		if err != nil {
			return err
		}
		err = fn(auth)
		switch {
		case err == nil:
			d.setPaused(nil)
			return nil
		case attempt == 0 && (api.HasCode(err, api.CodeTokenExpired) || api.HasCode(err, api.CodeInvalidCredentials)):
			continue
		case api.HasCode(err, api.CodeDeviceOverLimit):
			e, _ := api.AsError(err)
			d.setPaused(e)
			return err
		case api.IsSessionEnded(err):
			d.endSession("the Colitu account signed this device out; sign in again")
			return errNotLoggedIn
		}
		return err
	}
}

// endSession forgets the credentials (the device key stays) and stops the
// tunnel, because the panel will no longer accept this device. It does not
// wait for the tunnel, so it is safe to call from the session itself.
func (d *Daemon) endSession(reason string) {
	d.mu.Lock()
	d.st.ClearSession()
	d.saveLocked()
	d.lastErr = reason
	d.paused = nil
	s := d.sess
	d.mu.Unlock()
	d.log.Warn("session ended", "reason", reason)
	if s != nil {
		s.cancel()
	}
}
