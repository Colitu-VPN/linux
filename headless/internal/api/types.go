// Package api is the client for the Colitu panel's app API (device link
// sign-in, device registration, token refresh, server list and config).
package api

import (
	"encoding/json"
	"fmt"
	"strings"
	"time"
)

// DefaultBase is the production API base. It can be overridden with the
// COLITU_API_BASE environment variable (tests, staging).
const DefaultBase = "https://colitu.com/api/v1"

// Platform is the platform name the panel knows this client by.
const Platform = "linux"

// Error codes the panel returns that the client acts on.
const (
	CodeDeviceOverLimit    = "DEVICE_OVER_LIMIT"
	CodeTokenExpired       = "AUTH_TOKEN_EXPIRED"
	CodeInvalidCredentials = "AUTH_INVALID_CREDENTIALS"
	CodeRefreshReused      = "AUTH_REFRESH_REUSED"
	CodeDeviceRevoked      = "DEVICE_REVOKED"
	CodeDeviceMismatch     = "DEVICE_TOKEN_MISMATCH"
	CodeDeviceNotFound     = "DEVICE_NOT_FOUND"
	CodeLinkNotFound       = "LINK_NOT_FOUND"
	CodeLinkExpired        = "LINK_EXPIRED"
	CodeLinkDenied         = "LINK_DENIED"
	CodeRateLimited        = "RATE_LIMITED"
)

// DeviceBrief is a device as listed in a DEVICE_OVER_LIMIT answer.
type DeviceBrief struct {
	ID         string `json:"id"`
	Name       string `json:"name"`
	Platform   string `json:"platform"`
	LastSeenAt string `json:"last_seen_at,omitempty"`
}

// Error is a panel error answer: {"error":{"code","message"}} plus, for
// DEVICE_OVER_LIMIT, the plan's device limit and the active devices.
type Error struct {
	Status        int           `json:"-"`
	Code          string        `json:"code"`
	Message       string        `json:"message"`
	DeviceLimit   int           `json:"device_limit,omitempty"`
	ActiveDevices []DeviceBrief `json:"active_devices,omitempty"`
}

func (e *Error) Error() string {
	switch {
	case e.Code != "" && e.Message != "":
		return fmt.Sprintf("%s: %s", e.Code, e.Message)
	case e.Code != "":
		return e.Code
	case e.Message != "":
		return e.Message
	}
	return fmt.Sprintf("HTTP %d", e.Status)
}

// Tokens is the answer of link poll and refresh.
type Tokens struct {
	AccessToken  string `json:"access_token"`
	RefreshToken string `json:"refresh_token"`
	TokenType    string `json:"token_type"`
	ExpiresIn    int64  `json:"expires_in"`
}

// LinkStart is what the device shows the user while it waits for approval.
type LinkStart struct {
	Code      string `json:"code"`
	URL       string `json:"url"`
	PollToken string `json:"poll_token"`
	ExpiresIn int64  `json:"expires_in"`
	Interval  int    `json:"interval"`
}

// Auth carries the headers of an authenticated device request.
type Auth struct {
	Bearer   string
	DeviceID string
}

// Registration is the body of POST /devices/register.
type Registration struct {
	DeviceKey    string       `json:"device_key"`
	Name         string       `json:"name"`
	Platform     string       `json:"platform"`
	AppVersion   string       `json:"app_version"`
	OSVersion    string       `json:"os_version"`
	HardwareID   string       `json:"hardware_id,omitempty"`
	Capabilities Capabilities `json:"capabilities"`
}

// Capabilities tell the panel which config formats and protocols this
// client can run, so it never hands out a profile the client cannot use.
type Capabilities struct {
	ConfigFormats []string `json:"config_formats"`
	Protocols     []string `json:"protocols"`
}

// ClientCapabilities is what the headless client supports: sing-box
// configs, no XHTTP (sing-box has no such transport).
func ClientCapabilities() Capabilities {
	return Capabilities{
		ConfigFormats: []string{"sing-box"},
		Protocols:     []string{"vless-reality", "hysteria2", "tuic", "shadowsocks"},
	}
}

// Device is a registered device.
type Device struct {
	ID   string `json:"id"`
	Name string `json:"name"`
}

// Server is one entry of GET /servers.
type Server struct {
	ID        string   `json:"id"`
	Name      string   `json:"name"`
	Country   string   `json:"country"`
	City      string   `json:"city"`
	Region    string   `json:"region"`
	Status    string   `json:"status"`
	Load      string   `json:"load"`
	Protocols []string `json:"protocols"`
}

// Online reports whether the server can take connections.
func (s Server) Online() bool { return s.Status == "" || strings.EqualFold(s.Status, "online") }

// Preference is the body of PUT /me/preferences. Pinning a node id is how
// a client chooses a specific server: GET /config only filters by region,
// country and protocol.
type Preference struct {
	Region   string `json:"preferred_region"`
	Country  string `json:"preferred_country"`
	Protocol string `json:"preferred_protocol"`
	NodeID   string `json:"preferred_node_id,omitempty"`
}

// ConfigProfile is a rendered client profile; for format "sing-box" the
// payload is a sing-box JSON document holding the candidate's outbound.
type ConfigProfile struct {
	Format  string          `json:"format"`
	Payload json.RawMessage `json:"payload"`
}

// Candidate is one transport of the selected server. The client builds one
// outbound per candidate and lets sing-box pick the fastest that works.
type Candidate struct {
	Server   map[string]any `json:"server"`
	Protocol string         `json:"protocol"`
	Profile  ConfigProfile  `json:"profile"`
}

// Config is the answer of GET /config.
type Config struct {
	Revision          uint64         `json:"revision"`
	GeneratedAt       time.Time      `json:"generated_at"`
	ExpiresAt         time.Time      `json:"expires_at"`
	OfflineGraceUntil time.Time      `json:"offline_grace_until"`
	Server            map[string]any `json:"server"`
	Profile           ConfigProfile  `json:"profile"`
	Candidates        []Candidate    `json:"candidates"`
	FallbackUsed      bool           `json:"fallback_used"`
}

// ServerID returns the id of the server the config is for.
func (c Config) ServerID() string { s, _ := c.Server["id"].(string); return s }

// ActivateResult is the answer of POST /devices/{id}/activate.
type ActivateResult struct {
	DeviceID  string        `json:"device_id"`
	Suspended []DeviceBrief `json:"suspended"`
}
