package state

import (
	"io/fs"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"
)

func TestModeOK(t *testing.T) {
	cases := []struct {
		mode fs.FileMode
		want bool
	}{
		{0o600, true},
		{0o400, true},
		{0o700, true},
		{0o640, false},
		{0o604, false},
		{0o644, false},
		{0o660, false},
		{0o666, false},
		{fs.ModeDir | 0o700, false},     // not a regular file
		{fs.ModeSymlink | 0o600, false}, // not a regular file
	}
	for _, c := range cases {
		if got := ModeOK(c.mode); got != c.want {
			t.Errorf("ModeOK(%v) = %v, want %v", c.mode, got, c.want)
		}
	}
}

func TestLoadMissingFileIsEmptyState(t *testing.T) {
	s := NewStore(filepath.Join(t.TempDir(), "state.json"))
	st, fixed, err := s.Load()
	if err != nil || fixed {
		t.Fatalf("Load() = %v, fixed=%v", err, fixed)
	}
	if st.LoggedIn() || st.DeviceKey != "" {
		t.Errorf("a fresh state must be empty: %+v", st)
	}
}

func TestSaveLoadRoundTrip(t *testing.T) {
	dir := t.TempDir()
	s := NewStore(filepath.Join(dir, "sub", "state.json"))
	want := State{
		DeviceKey: "k-1234567890123456789012345", DeviceID: "dev-1", DeviceName: "pi (headless)",
		AccessToken: "a", RefreshToken: "r", AccessExpiresAt: time.Date(2026, 10, 6, 12, 0, 0, 0, time.UTC),
		AutoConnect: true, LastServerID: "srv", LastServerName: "Istanbul", LastServerCountry: "TR",
	}
	if err := s.Save(want); err != nil {
		t.Fatal(err)
	}
	got, _, err := s.Load()
	if err != nil {
		t.Fatal(err)
	}
	want.Schema = schema
	if got != want {
		t.Errorf("round trip changed the state:\n got %+v\nwant %+v", got, want)
	}
	if !got.LoggedIn() {
		t.Error("state with refresh token and device id must be logged in")
	}
	// No temporary files are left behind.
	entries, _ := os.ReadDir(filepath.Join(dir, "sub"))
	if len(entries) != 1 {
		t.Errorf("directory holds %d entries, want only state.json", len(entries))
	}
}

func TestSaveWritesPrivateFile(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("Unix permission bits are not meaningful on Windows")
	}
	dir := t.TempDir()
	path := filepath.Join(dir, "state.json")
	if err := NewStore(path).Save(State{RefreshToken: "secret"}); err != nil {
		t.Fatal(err)
	}
	info, err := os.Stat(path)
	if err != nil {
		t.Fatal(err)
	}
	if info.Mode().Perm() != 0o600 {
		t.Errorf("state file mode = %v, want 0600", info.Mode().Perm())
	}
	// Overwriting keeps the mode.
	if err := NewStore(path).Save(State{RefreshToken: "secret2"}); err != nil {
		t.Fatal(err)
	}
	info, _ = os.Stat(path)
	if info.Mode().Perm() != 0o600 {
		t.Errorf("mode after rewrite = %v, want 0600", info.Mode().Perm())
	}
}

func TestLoadTightensLooseMode(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("Unix permission bits are not meaningful on Windows")
	}
	path := filepath.Join(t.TempDir(), "state.json")
	if err := os.WriteFile(path, []byte(`{"refresh_token":"r","device_id":"d"}`), 0o644); err != nil {
		t.Fatal(err)
	}
	st, fixed, err := NewStore(path).Load()
	if err != nil {
		t.Fatal(err)
	}
	if !fixed {
		t.Error("a 0644 state file must be reported as tightened")
	}
	if !st.LoggedIn() {
		t.Error("the content must still be read")
	}
	info, _ := os.Stat(path)
	if info.Mode().Perm() != 0o600 {
		t.Errorf("mode after Load = %v, want 0600", info.Mode().Perm())
	}
}

func TestLoadRefusesNonRegularFile(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("symlinks need privileges on Windows")
	}
	dir := t.TempDir()
	target := filepath.Join(dir, "elsewhere.json")
	if err := os.WriteFile(target, []byte(`{}`), 0o600); err != nil {
		t.Fatal(err)
	}
	link := filepath.Join(dir, "state.json")
	if err := os.Symlink(target, link); err != nil {
		t.Fatal(err)
	}
	if _, _, err := NewStore(link).Load(); err == nil || !strings.Contains(err.Error(), "regular file") {
		t.Errorf("a symlinked state file must be refused, got %v", err)
	}
}

func TestLoadDamagedFile(t *testing.T) {
	path := filepath.Join(t.TempDir(), "state.json")
	if err := os.WriteFile(path, []byte(`{not json`), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, _, err := NewStore(path).Load(); err == nil || !strings.Contains(err.Error(), "damaged") {
		t.Errorf("Load() error = %v, want a 'damaged' error", err)
	}
}

func TestClearSessionKeepsDeviceKey(t *testing.T) {
	st := State{DeviceKey: "key", DeviceID: "d", RefreshToken: "r", AccessToken: "a", AutoConnect: true, LastServerID: "s"}
	st.ClearSession()
	if st.LoggedIn() || st.AccessToken != "" || st.AutoConnect || st.LastServerID != "" {
		t.Errorf("session not cleared: %+v", st)
	}
	if st.DeviceKey != "key" {
		t.Error("the device key must survive a logout so signing in again reuses the device")
	}
}

func TestNewDeviceKeyLengthAndUniqueness(t *testing.T) {
	a, err := NewDeviceKey()
	if err != nil {
		t.Fatal(err)
	}
	b, _ := NewDeviceKey()
	if len(a) < 20 || len(a) > 256 {
		t.Errorf("device key length %d is outside the panel's 20..256", len(a))
	}
	if a == b {
		t.Error("device keys must be random")
	}
}
