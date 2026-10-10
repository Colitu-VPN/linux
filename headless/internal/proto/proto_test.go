package proto

import (
	"context"
	"encoding/json"
	"errors"
	"net"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"
)

// shortSocket returns a socket path short enough for sun_path.
func shortSocket(t *testing.T) string {
	t.Helper()
	dir, err := os.MkdirTemp("", "cl")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { os.RemoveAll(dir) })
	return filepath.Join(dir, "d.sock")
}

func serve(t *testing.T, h Handler) string {
	t.Helper()
	path := shortSocket(t)
	ln, err := Listen(path, "")
	if err != nil {
		t.Skipf("unix sockets unavailable here: %v", err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go func() {
		defer close(done)
		_ = Serve(ctx, ln, h)
	}()
	t.Cleanup(func() {
		cancel()
		select {
		case <-done:
		case <-time.After(5 * time.Second):
			t.Error("Serve did not return after cancel")
		}
	})
	return path
}

func TestCallRoundTrip(t *testing.T) {
	var gotArgs ConnectArgs
	path := serve(t, func(ctx context.Context, req Request) (any, *Error) {
		switch req.Cmd {
		case CmdConnect:
			if err := json.Unmarshal(req.Args, &gotArgs); err != nil {
				return nil, &Error{Code: CodeBadRequest, Message: "args"}
			}
			return Status{State: StateConnected, Server: &ConnectedServer{ID: "s1", Name: "Istanbul"}}, nil
		}
		return nil, &Error{Code: CodeUnknownCommand, Message: "unknown"}
	})

	var st Status
	err := Call(context.Background(), path, CmdConnect, ConnectArgs{Country: "TR"}, &st, 5*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	if gotArgs.Country != "TR" {
		t.Errorf("handler saw args %+v", gotArgs)
	}
	if st.State != StateConnected || st.Server == nil || st.Server.Name != "Istanbul" {
		t.Errorf("answer = %+v", st)
	}
}

func TestCallReturnsHandlerError(t *testing.T) {
	path := serve(t, func(context.Context, Request) (any, *Error) {
		return nil, &Error{Code: CodePaused, Message: "paused", Data: json.RawMessage(`{"device_limit":1}`)}
	})
	err := Call(context.Background(), path, CmdServers, nil, nil, 5*time.Second)
	var pe *Error
	if !errors.As(err, &pe) {
		t.Fatalf("err = %v, want *Error", err)
	}
	if pe.Code != CodePaused || !strings.Contains(string(pe.Data), "device_limit") {
		t.Errorf("error lost its content: %+v", pe)
	}
}

func TestServeRejectsMalformedRequests(t *testing.T) {
	called := false
	path := serve(t, func(context.Context, Request) (any, *Error) {
		called = true
		return struct{}{}, nil
	})
	for name, line := range map[string]string{
		"not json":   "hello\n",
		"no cmd":     `{"args":{}}` + "\n",
		"json array": `[1,2]` + "\n",
	} {
		t.Run(name, func(t *testing.T) {
			conn, err := net.Dial("unix", path)
			if err != nil {
				t.Fatal(err)
			}
			defer conn.Close()
			if _, err := conn.Write([]byte(line)); err != nil {
				t.Fatal(err)
			}
			buf := make([]byte, 4096)
			_ = conn.SetReadDeadline(time.Now().Add(5 * time.Second))
			n, _ := conn.Read(buf)
			var resp Response
			if err := json.Unmarshal(buf[:n], &resp); err != nil {
				t.Fatalf("answer %q is not a response: %v", buf[:n], err)
			}
			if resp.OK || resp.Error == nil || resp.Error.Code != CodeBadRequest {
				t.Errorf("response = %+v, want BAD_REQUEST", resp)
			}
		})
	}
	if called {
		t.Error("the handler must not run for a malformed request")
	}
}

func TestServeSurvivesHandlerPanic(t *testing.T) {
	path := serve(t, func(_ context.Context, req Request) (any, *Error) {
		if req.Cmd == "boom" {
			panic("bad")
		}
		return "fine", nil
	})
	err := Call(context.Background(), path, "boom", nil, nil, 5*time.Second)
	var pe *Error
	if !errors.As(err, &pe) || pe.Code != CodeInternal {
		t.Fatalf("panic must become an INTERNAL error, got %v", err)
	}
	var s string
	if err := Call(context.Background(), path, "ok", nil, &s, 5*time.Second); err != nil || s != "fine" {
		t.Errorf("the daemon must keep serving after a panic: %v %q", err, s)
	}
}

func TestServeCancelsHandlerWhenClientHangsUp(t *testing.T) {
	started := make(chan struct{})
	cancelled := make(chan struct{})
	path := serve(t, func(ctx context.Context, _ Request) (any, *Error) {
		close(started)
		select {
		case <-ctx.Done():
			close(cancelled)
		case <-time.After(10 * time.Second):
		}
		return struct{}{}, nil
	})
	ctx, cancel := context.WithCancel(context.Background())
	go func() { _ = Call(ctx, path, CmdConnect, nil, nil, 0) }()
	<-started
	cancel() // the CLI got Ctrl+C
	select {
	case <-cancelled:
	case <-time.After(5 * time.Second):
		t.Error("handler context was not cancelled when the client disconnected")
	}
}

func TestServeRejectsOversizedRequest(t *testing.T) {
	path := serve(t, func(context.Context, Request) (any, *Error) { return struct{}{}, nil })
	conn, err := net.Dial("unix", path)
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()
	go func() {
		chunk := []byte(strings.Repeat("a", 64<<10))
		for i := 0; i < 20; i++ { // 1.25 MiB, no newline
			if _, err := conn.Write(chunk); err != nil {
				return
			}
		}
	}()
	buf := make([]byte, 4096)
	_ = conn.SetReadDeadline(time.Now().Add(5 * time.Second))
	n, _ := conn.Read(buf)
	if !strings.Contains(string(buf[:n]), CodeBadRequest) {
		t.Errorf("oversized request answer = %q", buf[:n])
	}
}

func TestCallNoDaemon(t *testing.T) {
	err := Call(context.Background(), filepath.Join(t.TempDir(), "none.sock"), CmdStatus, nil, nil, 2*time.Second)
	if !errors.Is(err, ErrNoDaemon) {
		t.Errorf("err = %v, want ErrNoDaemon", err)
	}
}

func TestListenSetsSocketMode(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("Unix permission bits are not meaningful on Windows")
	}
	path := shortSocket(t)
	ln, err := Listen(path, "")
	if err != nil {
		t.Fatal(err)
	}
	defer ln.Close()
	info, err := os.Stat(path)
	if err != nil {
		t.Fatal(err)
	}
	if info.Mode().Perm() != 0o660 {
		t.Errorf("socket mode = %v, want 0660", info.Mode().Perm())
	}
	dirInfo, _ := os.Stat(filepath.Dir(path))
	if dirInfo.Mode().Perm() != 0o750 {
		t.Errorf("socket dir mode = %v, want 0750", dirInfo.Mode().Perm())
	}
}

func TestListenReplacesStaleSocketButNotALiveOne(t *testing.T) {
	path := shortSocket(t)
	ln, err := Listen(path, "")
	if err != nil {
		t.Skipf("unix sockets unavailable here: %v", err)
	}
	// A second daemon must not steal a live socket.
	if _, err := Listen(path, ""); err == nil {
		t.Error("Listen must fail while another daemon answers on the socket")
	}
	// A crashed daemon leaves its socket file behind: keep it on close.
	ln.(*net.UnixListener).SetUnlinkOnClose(false)
	ln.Close()
	if fi, err := os.Lstat(path); err != nil || fi.Mode().Type() != os.ModeSocket {
		t.Fatalf("expected a stale socket at %s (%v)", path, err)
	}
	ln2, err := Listen(path, "")
	if err != nil {
		t.Fatalf("a stale socket file must be replaced: %v", err)
	}
	ln2.Close()
}

func TestListenNeverDeletesAFileThatIsNotASocket(t *testing.T) {
	path := shortSocket(t)
	if err := os.WriteFile(path, []byte("keep"), 0o600); err != nil {
		t.Fatal(err)
	}
	if ln, err := Listen(path, ""); err == nil {
		ln.Close()
		t.Fatal("Listen must refuse a path that holds a regular file")
	}
	if b, err := os.ReadFile(path); err != nil || string(b) != "keep" {
		t.Fatalf("the file was touched: %q, %v", b, err)
	}
}

func TestListenRefusesASharedDirectory(t *testing.T) {
	dir, err := os.MkdirTemp("", "cl")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { os.RemoveAll(dir) })
	if err := os.Chmod(dir, os.ModeSticky|0o777); err != nil {
		t.Fatal(err)
	}
	if ln, err := Listen(filepath.Join(dir, "d.sock"), ""); err == nil {
		ln.Close()
		t.Fatal("Listen must refuse a world-writable directory such as /tmp")
	}
	fi, _ := os.Stat(dir)
	if fi.Mode().Perm() != 0o777 || fi.Mode()&os.ModeSticky == 0 {
		t.Errorf("the shared directory's mode changed to %v", fi.Mode())
	}
}
