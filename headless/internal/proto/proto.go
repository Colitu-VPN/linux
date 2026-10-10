// Package proto is the tiny protocol between the colitu CLI and colitud:
// one JSON request line per connection, one JSON response line back, over a
// unix socket.
//
//	-> {"cmd":"connect","args":{"country":"tr"}}\n
//	<- {"ok":true,"data":{...}}\n   or   {"ok":false,"error":{"code":"...","message":"..."}}\n
package proto

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"os"
	"os/user"
	"path/filepath"
	"strconv"
	"sync"
	"time"
)

// Default locations and names.
const (
	DefaultSocket = "/run/colitu/colitud.sock"
	// Group is the group whose members may talk to the daemon.
	Group = "colitu"
)

// Commands.
const (
	CmdPing        = "ping"
	CmdStatus      = "status"
	CmdLoginStart  = "login_start"
	CmdLoginStatus = "login_status"
	CmdLoginCancel = "login_cancel"
	CmdLogout      = "logout"
	CmdServers     = "servers"
	CmdConnect     = "connect"
	CmdDisconnect  = "disconnect"
	CmdActivate    = "activate"
	CmdAutoConnect = "autoconnect"
)

// maxLine bounds a request or response line.
const maxLine = 1 << 20

// Request is one command.
type Request struct {
	Cmd  string          `json:"cmd"`
	Args json.RawMessage `json:"args,omitempty"`
}

// Error is a failed command. Data carries structured detail, such as the
// active devices of a paused one.
type Error struct {
	Code    string          `json:"code"`
	Message string          `json:"message"`
	Data    json.RawMessage `json:"data,omitempty"`
}

func (e *Error) Error() string {
	if e.Message == "" {
		return e.Code
	}
	return e.Message
}

// Error codes of the daemon's own answers.
const (
	CodeBadRequest     = "BAD_REQUEST"
	CodeUnknownCommand = "UNKNOWN_COMMAND"
	CodeNotLoggedIn    = "NOT_LOGGED_IN"
	CodeAlreadyIn      = "ALREADY_LOGGED_IN"
	CodeNoServer       = "NO_SERVER"
	CodePaused         = "DEVICE_PAUSED"
	CodeNoLogin        = "NO_LOGIN_IN_PROGRESS"
	CodeCore           = "CORE_ERROR"
	CodeInternal       = "INTERNAL"
	CodeUpstream       = "UPSTREAM_ERROR"
)

// Response is a command's answer.
type Response struct {
	OK    bool            `json:"ok"`
	Data  json.RawMessage `json:"data,omitempty"`
	Error *Error          `json:"error,omitempty"`
}

// Handler runs one command. It returns the data to send, or an *Error.
type Handler func(ctx context.Context, req Request) (any, *Error)

// Listen creates the daemon's socket: the directory 0750, the socket 0660,
// both handed to group (best effort: without CAP_CHOWN a service running
// with Group=colitu already creates them in the right group). A stale
// socket file from a crashed run is replaced; a live one is an error.
func Listen(path, group string) (net.Listener, error) {
	dir := filepath.Dir(path)
	if err := os.MkdirAll(dir, 0o750); err != nil {
		return nil, err
	}
	// The directory is chmod'ed and handed to the group below: never do that
	// to a shared one such as /tmp (it would lose its sticky bit).
	if fi, err := os.Stat(dir); err != nil {
		return nil, err
	} else if fi.Mode().Perm()&0o002 != 0 || fi.Mode()&os.ModeSticky != 0 {
		return nil, fmt.Errorf("%s is writable by others: put the socket in a directory of its own", dir)
	}
	gid := lookupGID(group)
	if gid >= 0 {
		_ = os.Chown(dir, -1, gid)
	}
	_ = os.Chmod(dir, 0o750)
	if c, err := net.DialTimeout("unix", path, time.Second); err == nil {
		c.Close()
		return nil, fmt.Errorf("another colitud already listens on %s", path)
	}
	// Only a stale socket is replaced, never a file that happens to sit there.
	if fi, err := os.Lstat(path); err == nil {
		if fi.Mode().Type() != os.ModeSocket {
			return nil, fmt.Errorf("%s exists and is not a socket", path)
		}
		_ = os.Remove(path)
	}
	ln, err := net.Listen("unix", path)
	if err != nil {
		return nil, err
	}
	if gid >= 0 {
		_ = os.Chown(path, -1, gid)
	}
	if err := os.Chmod(path, 0o660); err != nil {
		ln.Close()
		return nil, err
	}
	return ln, nil
}

func lookupGID(group string) int {
	if group == "" {
		return -1
	}
	g, err := user.LookupGroup(group)
	if err != nil {
		return -1
	}
	n, err := strconv.Atoi(g.Gid)
	if err != nil {
		return -1
	}
	return n
}

// Serve answers connections until ctx ends or the listener closes. Each
// connection carries exactly one request.
func Serve(ctx context.Context, ln net.Listener, h Handler) error {
	go func() {
		<-ctx.Done()
		ln.Close()
	}()
	var wg sync.WaitGroup
	defer wg.Wait()
	for {
		conn, err := ln.Accept()
		if err != nil {
			if ctx.Err() != nil || errors.Is(err, net.ErrClosed) {
				return nil
			}
			if ne, ok := err.(net.Error); ok && ne.Timeout() {
				continue
			}
			return err
		}
		wg.Add(1)
		go func() {
			defer wg.Done()
			serveConn(ctx, conn, h)
		}()
	}
}

func serveConn(ctx context.Context, conn net.Conn, h Handler) {
	defer conn.Close()
	_ = conn.SetReadDeadline(time.Now().Add(10 * time.Second))
	line, err := readLine(conn)
	if err != nil {
		writeResponse(conn, Response{Error: &Error{Code: CodeBadRequest, Message: "unreadable request"}})
		return
	}
	_ = conn.SetReadDeadline(time.Time{})
	var req Request
	if err := json.Unmarshal(line, &req); err != nil || req.Cmd == "" {
		writeResponse(conn, Response{Error: &Error{Code: CodeBadRequest, Message: "request must be a JSON object with a cmd"}})
		return
	}
	// A client that hangs up (Ctrl+C in the CLI) cancels the work.
	reqCtx, cancel := context.WithCancel(ctx)
	defer cancel()
	go watchClose(conn, cancel)

	data, herr := safeHandle(reqCtx, h, req)
	if herr != nil {
		writeResponse(conn, Response{Error: herr})
		return
	}
	raw, err := json.Marshal(data)
	if err != nil {
		writeResponse(conn, Response{Error: &Error{Code: CodeInternal, Message: err.Error()}})
		return
	}
	writeResponse(conn, Response{OK: true, Data: raw})
}

func safeHandle(ctx context.Context, h Handler, req Request) (data any, herr *Error) {
	defer func() {
		if r := recover(); r != nil {
			herr = &Error{Code: CodeInternal, Message: fmt.Sprintf("internal error: %v", r)}
		}
	}()
	return h(ctx, req)
}

// watchClose cancels when the peer closes its end. The client sends one line
// and waits, so any further read result means it is gone.
func watchClose(conn net.Conn, cancel context.CancelFunc) {
	buf := make([]byte, 1)
	for {
		if _, err := conn.Read(buf); err != nil {
			cancel()
			return
		}
	}
}

func readLine(r io.Reader) ([]byte, error) {
	br := bufio.NewReaderSize(io.LimitReader(r, maxLine+1), 4096)
	line, err := br.ReadBytes('\n')
	if err != nil && !(errors.Is(err, io.EOF) && len(line) > 0) {
		return nil, err
	}
	if len(line) > maxLine {
		return nil, errors.New("request too large")
	}
	return line, nil
}

func writeResponse(conn net.Conn, resp Response) {
	raw, err := json.Marshal(resp)
	if err != nil {
		raw = []byte(`{"ok":false,"error":{"code":"INTERNAL","message":"cannot encode the answer"}}`)
	}
	_ = conn.SetWriteDeadline(time.Now().Add(5 * time.Second))
	_, _ = conn.Write(append(raw, '\n'))
}

// Dial errors the CLI turns into hints.
var (
	// ErrNoDaemon means nothing listens on the socket (service not running).
	ErrNoDaemon = errors.New("colitud is not running")
	// ErrNoAccess means the socket exists but this user may not open it.
	ErrNoAccess = errors.New("no permission to use the colitud socket")
)

// Call sends one command and decodes the data into out (nil to ignore). A
// failed command is returned as *Error. timeout bounds the whole call
// (0 = no limit); ctx can cancel it earlier.
func Call(ctx context.Context, socket, cmd string, args, out any, timeout time.Duration) error {
	var d net.Dialer
	dctx := ctx
	if timeout > 0 {
		var cancel context.CancelFunc
		dctx, cancel = context.WithTimeout(ctx, timeout)
		defer cancel()
	}
	conn, err := d.DialContext(dctx, "unix", socket)
	if err != nil {
		switch {
		case errors.Is(err, os.ErrPermission):
			return fmt.Errorf("%w (%s)", ErrNoAccess, socket)
		case errors.Is(err, os.ErrNotExist), isConnRefused(err):
			return fmt.Errorf("%w (%s)", ErrNoDaemon, socket)
		}
		return err
	}
	defer conn.Close()
	go func() {
		<-dctx.Done()
		conn.Close()
	}()
	req := Request{Cmd: cmd}
	if args != nil {
		raw, err := json.Marshal(args)
		if err != nil {
			return err
		}
		req.Args = raw
	}
	raw, err := json.Marshal(req)
	if err != nil {
		return err
	}
	if _, err := conn.Write(append(raw, '\n')); err != nil {
		return err
	}
	line, err := readLine(conn)
	if err != nil {
		if dctx.Err() != nil {
			return dctx.Err()
		}
		return fmt.Errorf("no answer from colitud: %w", err)
	}
	var resp Response
	if err := json.Unmarshal(line, &resp); err != nil {
		return fmt.Errorf("unreadable answer from colitud: %w", err)
	}
	if !resp.OK {
		if resp.Error == nil {
			return &Error{Code: CodeInternal, Message: "colitud failed without a reason"}
		}
		return resp.Error
	}
	if out != nil && len(resp.Data) > 0 {
		return json.Unmarshal(resp.Data, out)
	}
	return nil
}
