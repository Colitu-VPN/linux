// Package state persists what colitud must remember across restarts:
// tokens, the device identity and the autoconnect choice. The file holds
// secrets, so it is written 0600 and loaded only when its mode is sane.
package state

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"sync"
	"time"
)

// DefaultPath is where the packaged service keeps its state
// (StateDirectory=colitu).
const DefaultPath = "/var/lib/colitu/state.json"

const (
	fileMode = 0o600
	dirMode  = 0o700
	// schema is bumped when the file layout changes incompatibly.
	schema = 1
)

// State is the persisted data.
type State struct {
	Schema int `json:"schema"`

	// DeviceKey is the random identity this machine registers with. It
	// survives logout so signing in again reuses the same device record.
	DeviceKey string `json:"device_key"`
	DeviceID  string `json:"device_id,omitempty"`
	// DeviceName is what the account's device list shows.
	DeviceName string `json:"device_name,omitempty"`

	AccessToken     string    `json:"access_token,omitempty"`
	RefreshToken    string    `json:"refresh_token,omitempty"`
	AccessExpiresAt time.Time `json:"access_expires_at,omitempty"`

	AutoConnect bool `json:"autoconnect,omitempty"`
	// Last* describe the server of the last successful connection.
	LastServerID      string `json:"last_server_id,omitempty"`
	LastServerName    string `json:"last_server_name,omitempty"`
	LastServerCountry string `json:"last_server_country,omitempty"`
}

// LoggedIn reports whether the state holds usable credentials.
func (s State) LoggedIn() bool {
	return s.RefreshToken != "" && s.DeviceID != ""
}

// ClearSession drops tokens and the device id but keeps the device key.
func (s *State) ClearSession() {
	s.DeviceID = ""
	s.AccessToken = ""
	s.RefreshToken = ""
	s.AccessExpiresAt = time.Time{}
	s.AutoConnect = false
	s.LastServerID, s.LastServerName, s.LastServerCountry = "", "", ""
}

// NewDeviceKey returns a fresh random device key (the panel requires 20 to
// 256 characters).
func NewDeviceKey() (string, error) {
	b := make([]byte, 32)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	return hex.EncodeToString(b), nil
}

// ModeOK reports whether a state file with this mode is acceptable: a
// regular file that neither group nor others can touch.
func ModeOK(mode fs.FileMode) bool {
	return mode.IsRegular() && mode.Perm()&0o077 == 0
}

// ErrForeignOwner is returned when the state file belongs to someone other
// than the user colitud runs as.
var ErrForeignOwner = errors.New("state file is owned by another user")

// Store reads and writes the state file.
type Store struct {
	Path string
	mu   sync.Mutex
}

// NewStore returns a store for path ("" = DefaultPath).
func NewStore(path string) *Store {
	if path == "" {
		path = DefaultPath
	}
	return &Store{Path: path}
}

// Load returns the saved state, or an empty one when no file exists yet. A
// file that is group- or world-accessible is tightened to 0600 first (the
// previous mode is reported through fixed); a file owned by another user is
// refused.
func (s *Store) Load() (st State, fixed bool, err error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	info, err := os.Lstat(s.Path)
	if errors.Is(err, fs.ErrNotExist) {
		return State{Schema: schema}, false, nil
	}
	if err != nil {
		return State{}, false, err
	}
	if !info.Mode().IsRegular() {
		return State{}, false, fmt.Errorf("%s is not a regular file", s.Path)
	}
	if err := checkOwner(info); err != nil {
		return State{}, false, fmt.Errorf("%s: %w", s.Path, err)
	}
	if enforceModes && !ModeOK(info.Mode()) {
		if err := os.Chmod(s.Path, fileMode); err != nil {
			return State{}, false, fmt.Errorf("tightening %s: %w", s.Path, err)
		}
		fixed = true
	}
	raw, err := os.ReadFile(s.Path)
	if err != nil {
		return State{}, fixed, err
	}
	if err := json.Unmarshal(raw, &st); err != nil {
		return State{}, fixed, fmt.Errorf("%s is damaged: %w", s.Path, err)
	}
	if st.Schema == 0 {
		st.Schema = schema
	}
	return st, fixed, nil
}

// Save writes the state atomically: a temporary file in the same directory
// (created 0600), fsynced, then renamed over the old file.
func (s *Store) Save(st State) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	st.Schema = schema
	raw, err := json.MarshalIndent(st, "", "  ")
	if err != nil {
		return err
	}
	dir := filepath.Dir(s.Path)
	if err := os.MkdirAll(dir, dirMode); err != nil {
		return err
	}
	tmp, err := os.CreateTemp(dir, ".state-*.tmp")
	if err != nil {
		return err
	}
	name := tmp.Name()
	cleanup := func() { _ = os.Remove(name) }
	if err := tmp.Chmod(fileMode); err != nil && !isUnsupportedChmod(err) {
		tmp.Close()
		cleanup()
		return err
	}
	if _, err := tmp.Write(raw); err != nil {
		tmp.Close()
		cleanup()
		return err
	}
	if err := tmp.Sync(); err != nil {
		tmp.Close()
		cleanup()
		return err
	}
	if err := tmp.Close(); err != nil {
		cleanup()
		return err
	}
	if err := os.Rename(name, s.Path); err != nil {
		cleanup()
		return err
	}
	return nil
}
