package api

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"runtime"
	"strings"
	"time"

	"github.com/colitu/colitu-linux/headless/internal/version"
)

// maxBody bounds every API answer; a config with several candidates is a
// few kilobytes.
const maxBody = 4 << 20

// Client talks to the panel API. It holds no credentials: every
// authenticated call takes an Auth.
type Client struct {
	Base string
	HTTP *http.Client
	// UserAgent defaults to "colitud/<version> (linux/<arch>)".
	UserAgent string
}

// New returns a client for base ("" = COLITU_API_BASE or DefaultBase).
func New(base string) *Client {
	if base == "" {
		base = os.Getenv("COLITU_API_BASE")
	}
	if base == "" {
		base = DefaultBase
	}
	return &Client{
		Base: strings.TrimRight(base, "/"),
		HTTP: &http.Client{Timeout: 20 * time.Second},
	}
}

// LinkStatus is the outcome of one poll of a pending sign-in.
type LinkStatus int

const (
	// LinkPending means nobody has decided yet.
	LinkPending LinkStatus = iota
	// LinkApproved means the tokens in the result are valid.
	LinkApproved
)

func (c *Client) do(ctx context.Context, method, path string, query url.Values, auth *Auth, body, out any) (int, error) {
	u := c.Base + path
	if len(query) > 0 {
		u += "?" + query.Encode()
	}
	var reader io.Reader
	if body != nil {
		raw, err := json.Marshal(body)
		if err != nil {
			return 0, err
		}
		reader = bytes.NewReader(raw)
	}
	req, err := http.NewRequestWithContext(ctx, method, u, reader)
	if err != nil {
		return 0, err
	}
	ua := c.UserAgent
	if ua == "" {
		ua = fmt.Sprintf("colitud/%s (linux/%s)", version.Version, runtime.GOARCH)
	}
	req.Header.Set("User-Agent", ua)
	req.Header.Set("Accept", "application/json")
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	if auth != nil {
		if auth.Bearer != "" {
			req.Header.Set("Authorization", "Bearer "+auth.Bearer)
		}
		if auth.DeviceID != "" {
			req.Header.Set("X-Device-ID", auth.DeviceID)
		}
	}
	client := c.HTTP
	if client == nil {
		client = http.DefaultClient
	}
	resp, err := client.Do(req)
	if err != nil {
		return 0, err
	}
	defer resp.Body.Close()
	raw, err := io.ReadAll(io.LimitReader(resp.Body, maxBody))
	if err != nil {
		return resp.StatusCode, err
	}
	if resp.StatusCode >= 400 {
		return resp.StatusCode, parseError(resp.StatusCode, raw)
	}
	if out != nil && len(bytes.TrimSpace(raw)) > 0 && resp.StatusCode != http.StatusNoContent {
		if err := json.Unmarshal(raw, out); err != nil {
			return resp.StatusCode, fmt.Errorf("unexpected answer from the panel: %w", err)
		}
	}
	return resp.StatusCode, nil
}

func parseError(status int, raw []byte) error {
	var env struct {
		Error         *Error        `json:"error"`
		DeviceLimit   int           `json:"device_limit"`
		ActiveDevices []DeviceBrief `json:"active_devices"`
	}
	e := &Error{Status: status}
	if json.Unmarshal(raw, &env) == nil && env.Error != nil {
		e = env.Error
		e.Status = status
		e.DeviceLimit = env.DeviceLimit
		e.ActiveDevices = env.ActiveDevices
	}
	if e.Code == "" && e.Message == "" {
		e.Message = http.StatusText(status)
	}
	return e
}

// AsError returns the panel error inside err, if any.
func AsError(err error) (*Error, bool) {
	var e *Error
	if errors.As(err, &e) {
		return e, true
	}
	return nil, false
}

// HasCode reports whether err is a panel error with the given code.
func HasCode(err error, code string) bool {
	e, ok := AsError(err)
	return ok && e.Code == code
}

// IsSessionEnded reports whether the panel says the stored credentials can
// never work again (signed out elsewhere, device removed, token reuse).
func IsSessionEnded(err error) bool {
	e, ok := AsError(err)
	if !ok {
		return false
	}
	switch e.Code {
	case CodeInvalidCredentials, CodeRefreshReused, CodeDeviceRevoked, CodeDeviceMismatch, CodeDeviceNotFound:
		return true
	}
	return false
}

// LinkStart opens a device-link sign-in: the user approves the returned code
// at the returned URL while the device polls with the poll token.
func (c *Client) LinkStart(ctx context.Context, deviceName string) (LinkStart, error) {
	var out LinkStart
	_, err := c.do(ctx, http.MethodPost, "/auth/link/start", nil, nil,
		map[string]string{"device_name": deviceName, "platform": Platform}, &out)
	if err == nil && (out.PollToken == "" || out.Code == "") {
		err = errors.New("the panel answered without a sign-in code")
	}
	return out, err
}

// LinkPoll asks once whether the sign-in was approved. A pending request
// returns (LinkPending, zero tokens, nil); an expired, declined or unknown
// request is a *Error with code LINK_EXPIRED, LINK_DENIED or LINK_NOT_FOUND.
func (c *Client) LinkPoll(ctx context.Context, pollToken string) (LinkStatus, Tokens, error) {
	var out struct {
		Tokens
		Status string `json:"status"`
	}
	status, err := c.do(ctx, http.MethodPost, "/auth/link/poll", nil, nil, map[string]string{"poll_token": pollToken}, &out)
	if err != nil {
		return LinkPending, Tokens{}, err
	}
	if status == http.StatusAccepted || out.AccessToken == "" {
		return LinkPending, Tokens{}, nil
	}
	return LinkApproved, out.Tokens, nil
}

// WaitLink polls until the sign-in is approved or fails. It returns the
// tokens, or a *Error (expired, declined), or the context's error. A
// transient network error does not end the wait. sleep is injectable for
// tests; nil means Sleep.
func (c *Client) WaitLink(ctx context.Context, start LinkStart, sleep func(context.Context, time.Duration) error) (Tokens, error) {
	if sleep == nil {
		sleep = Sleep
	}
	interval := time.Duration(start.Interval) * time.Second
	if interval < time.Second {
		interval = 3 * time.Second
	}
	deadline := time.Now().Add(time.Duration(start.ExpiresIn)*time.Second + 5*time.Minute)
	for {
		status, tokens, err := c.LinkPoll(ctx, start.PollToken)
		if ctx.Err() != nil {
			return Tokens{}, ctx.Err()
		}
		if err == nil && status == LinkApproved {
			return tokens, nil
		}
		if _, isAPI := AsError(err); isAPI && !HasCode(err, CodeRateLimited) {
			return Tokens{}, err
		}
		if time.Now().After(deadline) {
			return Tokens{}, &Error{Code: CodeLinkExpired, Message: "the sign-in code has expired"}
		}
		if err := sleep(ctx, interval); err != nil {
			return Tokens{}, err
		}
	}
}

// Sleep waits d or until ctx ends.
func Sleep(ctx context.Context, d time.Duration) error {
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-t.C:
		return nil
	}
}

// RegisterDevice registers (or re-registers, by device key) this device. The
// access token comes from link poll and is not yet bound to a device.
func (c *Client) RegisterDevice(ctx context.Context, bearer string, reg Registration) (Device, error) {
	var out struct {
		Device
		Data *Device `json:"data"`
	}
	_, err := c.do(ctx, http.MethodPost, "/devices/register", nil, &Auth{Bearer: bearer}, reg, &out)
	if err != nil {
		return Device{}, err
	}
	d := out.Device
	if d.ID == "" && out.Data != nil {
		d = *out.Data
	}
	if d.ID == "" {
		return Device{}, errors.New("the panel registered the device without an id")
	}
	return d, nil
}

// Refresh exchanges a refresh token for new tokens bound to deviceID. The
// old refresh token is single-use.
func (c *Client) Refresh(ctx context.Context, refreshToken, deviceID string) (Tokens, error) {
	var out Tokens
	_, err := c.do(ctx, http.MethodPost, "/auth/refresh", nil, &Auth{DeviceID: deviceID}, map[string]string{"refresh_token": refreshToken}, &out)
	if err == nil && (out.AccessToken == "" || out.RefreshToken == "") {
		err = errors.New("the panel answered the refresh without tokens")
	}
	return out, err
}

// Logout revokes a refresh token.
func (c *Client) Logout(ctx context.Context, refreshToken string) error {
	_, err := c.do(ctx, http.MethodPost, "/auth/logout", nil, nil, map[string]string{"refresh_token": refreshToken}, nil)
	return err
}

// Servers lists the locations. A paused device gets a DEVICE_OVER_LIMIT
// error (see HasCode and AsError).
func (c *Client) Servers(ctx context.Context, auth Auth) ([]Server, error) {
	var out struct {
		Servers []Server `json:"servers"`
	}
	_, err := c.do(ctx, http.MethodGet, "/servers", nil, &auth, nil, &out)
	return out.Servers, err
}

// SetPreference pins the server the next Config call selects.
func (c *Client) SetPreference(ctx context.Context, auth Auth, p Preference) error {
	_, err := c.do(ctx, http.MethodPut, "/me/preferences", nil, &auth, p, nil)
	return err
}

// Config fetches the client configuration with all transports of the
// preferred server ("auto" protocol).
func (c *Client) Config(ctx context.Context, auth Auth) (Config, error) {
	var out Config
	_, err := c.do(ctx, http.MethodGet, "/config", url.Values{"protocol": {"auto"}}, &auth, nil, &out)
	return out, err
}

// Activate makes this device the active one on a plan with fewer devices.
func (c *Client) Activate(ctx context.Context, auth Auth) (ActivateResult, error) {
	var out struct {
		Data ActivateResult `json:"data"`
	}
	_, err := c.do(ctx, http.MethodPost, "/devices/"+url.PathEscape(auth.DeviceID)+"/activate", nil, &auth, nil, &out)
	return out.Data, err
}

// RemoveDevice deletes this device from the account (its VPN credentials
// are revoked on the nodes).
func (c *Client) RemoveDevice(ctx context.Context, auth Auth) error {
	_, err := c.do(ctx, http.MethodDelete, "/devices/"+url.PathEscape(auth.DeviceID), nil, &auth, nil, nil)
	return err
}
