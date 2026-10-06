package proto

import "time"

// Daemon states reported in Status.State.
const (
	StateLoggedOut    = "logged_out"
	StateDisconnected = "disconnected"
	StateConnecting   = "connecting"
	StateConnected    = "connected"
	StateReconnecting = "reconnecting"
)

// Login states reported in LoginState.State.
const (
	LoginPending  = "pending"
	LoginApproved = "approved"
	LoginExpired  = "expired"
	LoginDenied   = "denied"
	LoginFailed   = "failed"
)

// Device is a device of the account.
type Device struct {
	ID         string `json:"id"`
	Name       string `json:"name"`
	Platform   string `json:"platform,omitempty"`
	LastSeenAt string `json:"last_seen_at,omitempty"`
}

// Paused describes a device the plan's device limit has paused: the VPN
// refuses it until it is activated (which pauses another device).
type Paused struct {
	DeviceLimit   int      `json:"device_limit"`
	ActiveDevices []Device `json:"active_devices"`
}

// ConnectedServer is the server of the current or last session.
type ConnectedServer struct {
	ID        string   `json:"id"`
	Name      string   `json:"name"`
	Country   string   `json:"country,omitempty"`
	City      string   `json:"city,omitempty"`
	Protocols []string `json:"protocols,omitempty"`
}

// Status is the answer of the status command.
type Status struct {
	Version     string           `json:"version"`
	State       string           `json:"state"`
	LoggedIn    bool             `json:"logged_in"`
	Device      *Device          `json:"device,omitempty"`
	Server      *ConnectedServer `json:"server,omitempty"`
	Since       *time.Time       `json:"since,omitempty"`
	Paused      *Paused          `json:"paused,omitempty"`
	AutoConnect bool             `json:"autoconnect"`
	// LastServer is the server autoconnect would reconnect to.
	LastServer *ConnectedServer `json:"last_server,omitempty"`
	LastError  string           `json:"last_error,omitempty"`
}

// Server is one location of the server list.
type Server struct {
	ID        string   `json:"id"`
	Name      string   `json:"name"`
	Country   string   `json:"country"`
	City      string   `json:"city"`
	Region    string   `json:"region,omitempty"`
	Status    string   `json:"status"`
	Load      string   `json:"load"`
	Protocols []string `json:"protocols,omitempty"`
}

// ConnectArgs selects the server of a connect command. Both empty means the
// best (lowest-load, online) server.
type ConnectArgs struct {
	ServerID string `json:"server_id,omitempty"`
	Country  string `json:"country,omitempty"`
}

// LogoutArgs are the arguments of the logout command.
type LogoutArgs struct {
	// RemoveDevice also deletes this device from the account.
	RemoveDevice bool `json:"remove_device,omitempty"`
}

// AutoConnectArgs are the arguments of the autoconnect command.
type AutoConnectArgs struct {
	Enabled bool `json:"enabled"`
}

// LoginInfo is the answer of login_start: what the user must do.
type LoginInfo struct {
	Code       string    `json:"code"`
	URL        string    `json:"url"`
	ExpiresAt  time.Time `json:"expires_at"`
	DeviceName string    `json:"device_name"`
}

// LoginState is the answer of login_status.
type LoginState struct {
	State   string `json:"state"`
	Message string `json:"message,omitempty"`
}

// ActivateResult is the answer of activate: the devices the plan paused to
// make room for this one.
type ActivateResult struct {
	Suspended []Device `json:"suspended"`
}
