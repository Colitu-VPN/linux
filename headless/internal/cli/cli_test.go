package cli

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"strings"
	"testing"
	"time"

	"github.com/colitu/colitu-linux/headless/internal/proto"
)

// fakeDaemon answers CLI calls from a script.
type fakeDaemon struct {
	calls   []string
	args    map[string]any
	answers map[string][]any // cmd -> answers in order (last repeats); error values are returned as errors
}

func (f *fakeDaemon) call(ctx context.Context, cmd string, args, out any, _ time.Duration) error {
	f.calls = append(f.calls, cmd)
	if f.args == nil {
		f.args = map[string]any{}
	}
	f.args[cmd] = args
	list := f.answers[cmd]
	if len(list) == 0 {
		return nil
	}
	ans := list[0]
	if len(list) > 1 {
		f.answers[cmd] = list[1:]
	}
	if err, ok := ans.(error); ok {
		return err
	}
	if out != nil {
		raw, _ := json.Marshal(ans)
		return json.Unmarshal(raw, out)
	}
	return nil
}

type runResult struct {
	code        int
	stdout, err string
}

func run(t *testing.T, f *fakeDaemon, tty bool, args ...string) runResult {
	t.Helper()
	var so, se bytes.Buffer
	now := time.Date(2026, 10, 6, 12, 0, 0, 0, time.UTC)
	env := &Env{
		Stdout: &so, Stderr: &se, Socket: "/tmp/x.sock",
		Call:  f.call,
		IsTTY: func() bool { return tty },
		Sleep: func(ctx context.Context, _ time.Duration) error { return ctx.Err() },
		Now:   func() time.Time { return now },
	}
	code := Run(context.Background(), args, env)
	return runResult{code, so.String(), se.String()}
}

func TestNoArgsAndUnknownCommandAreUsageErrors(t *testing.T) {
	if r := run(t, &fakeDaemon{}, false); r.code != ExitUsage || !strings.Contains(r.err, "Usage:") {
		t.Errorf("no args: %+v", r)
	}
	if r := run(t, &fakeDaemon{}, false, "frobnicate"); r.code != ExitUsage || !strings.Contains(r.err, "unknown command") {
		t.Errorf("unknown: %+v", r)
	}
	if r := run(t, &fakeDaemon{}, false, "version"); r.code != ExitOK || !strings.HasPrefix(r.stdout, "colitu ") {
		t.Errorf("version: %+v", r)
	}
}

func TestStatusJSONIsMachineReadable(t *testing.T) {
	since := time.Date(2026, 10, 6, 11, 0, 0, 0, time.UTC)
	f := &fakeDaemon{answers: map[string][]any{proto.CmdStatus: {proto.Status{
		Version: "1", State: proto.StateConnected, LoggedIn: true, AutoConnect: true, Since: &since,
		Server: &proto.ConnectedServer{ID: "s1", Name: "Istanbul", Country: "TR"},
	}}}}
	r := run(t, f, false, "status", "--json")
	if r.code != ExitOK {
		t.Fatalf("%+v", r)
	}
	var st proto.Status
	if err := json.Unmarshal([]byte(r.stdout), &st); err != nil {
		t.Fatalf("stdout is not JSON: %v\n%s", err, r.stdout)
	}
	if st.State != proto.StateConnected || st.Server.Name != "Istanbul" || !st.AutoConnect {
		t.Errorf("status = %+v", st)
	}
}

func TestStatusText(t *testing.T) {
	since := time.Date(2026, 10, 6, 11, 0, 0, 0, time.UTC) // an hour before "now"
	cases := map[string]struct {
		st   proto.Status
		want []string
	}{
		"connected": {proto.Status{State: proto.StateConnected, LoggedIn: true, Since: &since,
			Device: &proto.Device{Name: "pi (headless)"}, AutoConnect: true,
			LastServer: &proto.ConnectedServer{Name: "Istanbul", Country: "TR"},
			Server:     &proto.ConnectedServer{Name: "Istanbul", City: "Istanbul", Country: "TR", Protocols: []string{"hysteria2", "tuic"}}},
			[]string{"State:        connected", "Istanbul (Istanbul, TR)", "hysteria2, tuic", "1h 0m", "pi (headless)", "Autoconnect:  on (Istanbul (TR))"}},
		"logged out": {proto.Status{State: proto.StateLoggedOut}, []string{"signed out", "colitu login"}},
		"paused": {proto.Status{State: proto.StateDisconnected, LoggedIn: true,
			Paused: &proto.Paused{DeviceLimit: 1, ActiveDevices: []proto.Device{{Name: "Phone", Platform: "android"}}}},
			[]string{"paused (device limit)", "allows 1 active device", "Phone (android)", "colitu activate"}},
		"error shown": {proto.Status{State: proto.StateReconnecting, LoggedIn: true, LastError: "the tunnel process exited"},
			[]string{"reconnecting", "Last error:", "the tunnel process exited"}},
	}
	for name, c := range cases {
		t.Run(name, func(t *testing.T) {
			r := run(t, &fakeDaemon{answers: map[string][]any{proto.CmdStatus: {c.st}}}, false, "status")
			for _, w := range c.want {
				if !strings.Contains(r.stdout, w) {
					t.Errorf("output lacks %q:\n%s", w, r.stdout)
				}
			}
		})
	}
}

func TestServersTableAndJSON(t *testing.T) {
	list := []proto.Server{
		{ID: "id-1", Name: "Istanbul", Country: "TR", City: "Istanbul", Status: "online", Load: "low"},
		{ID: "id-2", Name: "Frankfurt", Country: "DE", City: "Frankfurt", Status: "online"},
	}
	f := &fakeDaemon{answers: map[string][]any{proto.CmdServers: {list}}}
	r := run(t, f, false, "servers")
	if r.code != ExitOK || !strings.Contains(r.stdout, "COUNTRY") || !strings.Contains(r.stdout, "id-1") || !strings.Contains(r.stdout, "Frankfurt") {
		t.Errorf("table:\n%s", r.stdout)
	}
	r = run(t, f, false, "servers", "--json")
	var got []proto.Server
	if err := json.Unmarshal([]byte(r.stdout), &got); err != nil || len(got) != 2 || got[0].ID != "id-1" {
		t.Errorf("json: %v\n%s", err, r.stdout)
	}
	// An empty list is still a JSON array.
	r = run(t, &fakeDaemon{answers: map[string][]any{proto.CmdServers: {nil}}}, false, "servers", "--json")
	if strings.TrimSpace(r.stdout) != "[]" {
		t.Errorf("empty list = %q, want []", r.stdout)
	}
}

func TestConnectArguments(t *testing.T) {
	ok := proto.Status{State: proto.StateConnected, Server: &proto.ConnectedServer{Name: "Ankara", Country: "TR"}}

	f := &fakeDaemon{answers: map[string][]any{proto.CmdConnect: {ok}}}
	r := run(t, f, false, "connect", "--country", "tr")
	if r.code != ExitOK || !strings.Contains(r.stdout, "Connected to Ankara (TR).") {
		t.Errorf("%+v", r)
	}
	if a := f.args[proto.CmdConnect].(proto.ConnectArgs); a.Country != "TR" || a.ServerID != "" {
		t.Errorf("args = %+v, want country TR", a)
	}

	f = &fakeDaemon{answers: map[string][]any{proto.CmdConnect: {ok}}}
	run(t, f, false, "connect", "--server", "id-9")
	if a := f.args[proto.CmdConnect].(proto.ConnectArgs); a.ServerID != "id-9" {
		t.Errorf("args = %+v", a)
	}

	f = &fakeDaemon{answers: map[string][]any{proto.CmdConnect: {ok}}}
	run(t, f, false, "connect")
	if a := f.args[proto.CmdConnect].(proto.ConnectArgs); a != (proto.ConnectArgs{}) {
		t.Errorf("no arguments must mean best server, got %+v", a)
	}

	for _, bad := range [][]string{
		{"connect", "--country", "tr", "--server", "x"},
		{"connect", "--country", "turkey"},
		{"connect", "extra"},
	} {
		f := &fakeDaemon{}
		if r := run(t, f, false, bad...); r.code != ExitUsage {
			t.Errorf("%v: exit %d, want usage error", bad, r.code)
		}
		if len(f.calls) != 0 {
			t.Errorf("%v must not reach the daemon", bad)
		}
	}
}

func TestConnectPausedPrintsActivateHint(t *testing.T) {
	data, _ := json.Marshal(proto.Paused{DeviceLimit: 1, ActiveDevices: []proto.Device{{Name: "Phone", Platform: "android"}}})
	f := &fakeDaemon{answers: map[string][]any{proto.CmdConnect: {&proto.Error{Code: proto.CodePaused, Message: "this device is paused", Data: data}}}}
	r := run(t, f, false, "connect")
	if r.code != ExitError {
		t.Errorf("exit = %d", r.code)
	}
	for _, w := range []string{"this device is paused", "Phone (android)", "colitu activate"} {
		if !strings.Contains(r.err, w) {
			t.Errorf("stderr lacks %q:\n%s", w, r.err)
		}
	}
}

func TestSocketErrorsExplainTheFix(t *testing.T) {
	r := run(t, &fakeDaemon{answers: map[string][]any{proto.CmdStatus: {fmt.Errorf("%w (/run/colitu/colitud.sock)", proto.ErrNoAccess)}}}, false, "status")
	if r.code != ExitError || !strings.Contains(r.err, "sudo usermod -aG colitu $USER") {
		t.Errorf("no-access hint missing:\n%s", r.err)
	}
	r = run(t, &fakeDaemon{answers: map[string][]any{proto.CmdStatus: {fmt.Errorf("%w (/run/colitu/colitud.sock)", proto.ErrNoDaemon)}}}, false, "status")
	if r.code != ExitError || !strings.Contains(r.err, "sudo systemctl enable --now colitud") {
		t.Errorf("no-daemon hint missing:\n%s", r.err)
	}
}

func TestAutoConnect(t *testing.T) {
	f := &fakeDaemon{answers: map[string][]any{proto.CmdAutoConnect: {proto.Status{LastServer: &proto.ConnectedServer{Name: "Ankara", Country: "TR"}}}}}
	r := run(t, f, false, "autoconnect", "on")
	if r.code != ExitOK || !strings.Contains(r.stdout, "Ankara (TR)") {
		t.Errorf("%+v", r)
	}
	if a := f.args[proto.CmdAutoConnect].(proto.AutoConnectArgs); !a.Enabled {
		t.Error("on must enable")
	}
	f = &fakeDaemon{}
	if r := run(t, f, false, "autoconnect", "off"); r.code != ExitOK || !strings.Contains(r.stdout, "off") {
		t.Errorf("%+v", r)
	}
	if a := f.args[proto.CmdAutoConnect].(proto.AutoConnectArgs); a.Enabled {
		t.Error("off must disable")
	}
	if r := run(t, &fakeDaemon{}, false, "autoconnect", "maybe"); r.code != ExitUsage {
		t.Errorf("bad value: exit %d", r.code)
	}
	if r := run(t, &fakeDaemon{}, false, "autoconnect"); r.code != ExitUsage {
		t.Errorf("missing value: exit %d", r.code)
	}
}

func TestActivateListsPausedDevices(t *testing.T) {
	f := &fakeDaemon{answers: map[string][]any{proto.CmdActivate: {proto.ActivateResult{Suspended: []proto.Device{{Name: "Phone", Platform: "android"}}}}}}
	r := run(t, f, false, "activate")
	if r.code != ExitOK || !strings.Contains(r.stdout, "Phone (android)") {
		t.Errorf("%+v", r)
	}
}

func TestLogoutPassesRemoveDevice(t *testing.T) {
	f := &fakeDaemon{}
	if r := run(t, f, false, "logout", "--remove-device"); r.code != ExitOK {
		t.Fatalf("%+v", r)
	}
	if a := f.args[proto.CmdLogout].(proto.LogoutArgs); !a.RemoveDevice {
		t.Error("--remove-device was not passed on")
	}
}

// ---- login

func loginDaemon(final any) *fakeDaemon {
	return &fakeDaemon{answers: map[string][]any{
		proto.CmdLoginStart: {proto.LoginInfo{Code: "ABCD2345", URL: "https://colitu.com/link?c=ABCD2345", ExpiresAt: time.Date(2026, 10, 6, 12, 10, 0, 0, time.UTC), DeviceName: "pi (headless)"}},
		proto.CmdLoginStatus: {
			proto.LoginState{State: proto.LoginPending},
			proto.LoginState{State: proto.LoginPending},
			final,
		},
	}}
}

func TestLoginApproved(t *testing.T) {
	f := loginDaemon(proto.LoginState{State: proto.LoginApproved})
	r := run(t, f, false, "login")
	if r.code != ExitOK {
		t.Fatalf("%+v", r)
	}
	for _, w := range []string{"https://colitu.com/link?c=ABCD2345", "ABCD-2345", "pi (headless)", "10 min", "Signed in"} {
		if !strings.Contains(r.stdout, w) {
			t.Errorf("output lacks %q:\n%s", w, r.stdout)
		}
	}
	polls := 0
	for _, c := range f.calls {
		if c == proto.CmdLoginStatus {
			polls++
		}
	}
	if polls != 3 {
		t.Errorf("status polls = %d, want 3", polls)
	}
}

func TestLoginEndings(t *testing.T) {
	cases := map[string]struct {
		final proto.LoginState
		want  string
	}{
		"expired": {proto.LoginState{State: proto.LoginExpired}, "expired"},
		"denied":  {proto.LoginState{State: proto.LoginDenied}, "declined"},
		"failed":  {proto.LoginState{State: proto.LoginFailed, Message: "no active plan"}, "no active plan"},
	}
	for name, c := range cases {
		t.Run(name, func(t *testing.T) {
			r := run(t, loginDaemon(c.final), false, "login")
			if r.code != ExitError || !strings.Contains(r.err, c.want) {
				t.Errorf("%+v", r)
			}
			if strings.Contains(r.stdout, "Signed in") {
				t.Error("must not claim success")
			}
		})
	}
}

func TestLoginQR(t *testing.T) {
	approved := proto.LoginState{State: proto.LoginApproved}
	plain := run(t, loginDaemon(approved), false, "login").stdout
	tty := run(t, loginDaemon(approved), true, "login").stdout
	forced := run(t, loginDaemon(approved), false, "login", "--qr").stdout
	off := run(t, loginDaemon(approved), true, "login", "--no-qr").stdout

	if len(tty) <= len(plain)+200 {
		t.Errorf("a terminal must get a QR code (plain %d bytes, tty %d bytes)", len(plain), len(tty))
	}
	if forced != tty {
		t.Error("--qr must draw the QR code even when stdout is not a terminal")
	}
	if off != plain {
		t.Error("--no-qr must suppress the QR code on a terminal")
	}
}

func TestLoginCancelledByCtrlC(t *testing.T) {
	f := loginDaemon(proto.LoginState{State: proto.LoginPending})
	var so, se bytes.Buffer
	ctx, cancel := context.WithCancel(context.Background())
	env := &Env{
		Stdout: &so, Stderr: &se, Call: f.call, IsTTY: func() bool { return false },
		Sleep: func(ctx context.Context, _ time.Duration) error { cancel(); return ctx.Err() },
		Now:   func() time.Time { return time.Date(2026, 10, 6, 12, 0, 0, 0, time.UTC) },
	}
	code := Run(ctx, []string{"login"}, env)
	if code != ExitSig {
		t.Errorf("exit = %d, want %d", code, ExitSig)
	}
	if last := f.calls[len(f.calls)-1]; last != proto.CmdLoginCancel {
		t.Errorf("last call = %s, want login_cancel so the daemon stops polling", last)
	}
}

func TestLoginWhenAlreadySignedIn(t *testing.T) {
	f := &fakeDaemon{answers: map[string][]any{proto.CmdLoginStart: {&proto.Error{Code: proto.CodeAlreadyIn, Message: "already signed in"}}}}
	r := run(t, f, false, "login")
	if r.code != ExitError || !strings.Contains(r.err, "already signed in") {
		t.Errorf("%+v", r)
	}
	if len(f.calls) != 1 {
		t.Errorf("calls = %v", f.calls)
	}
}

func TestGlobalSocketFlag(t *testing.T) {
	var gotSocket string
	var so, se bytes.Buffer
	env := &Env{Stdout: &so, Stderr: &se}
	// With a real Call the socket flag selects the path; here we only check
	// that the flag is consumed and the command still runs.
	env.Call = func(ctx context.Context, cmd string, args, out any, _ time.Duration) error {
		gotSocket = env.Socket
		return nil
	}
	if code := Run(context.Background(), []string{"--socket", "/tmp/other.sock", "disconnect"}, env); code != ExitOK {
		t.Fatalf("exit %d: %s", code, se.String())
	}
	if gotSocket != "/tmp/other.sock" {
		t.Errorf("socket = %q", gotSocket)
	}
}
