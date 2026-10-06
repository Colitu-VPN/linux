// Package cli implements the colitu command: a thin client that sends one
// command to colitud and prints the answer.
package cli

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"os"
	"strings"
	"time"

	"github.com/colitu/colitu-linux/headless/internal/proto"
	"github.com/colitu/colitu-linux/headless/internal/version"
)

// CallFunc sends a command to the daemon (proto.Call bound to a socket).
type CallFunc func(ctx context.Context, cmd string, args, out any, timeout time.Duration) error

// Env is everything the CLI touches outside itself, so tests can replace it.
type Env struct {
	Stdout io.Writer
	Stderr io.Writer
	Socket string
	// Call talks to the daemon; nil means proto.Call on Socket.
	Call CallFunc
	// IsTTY reports whether stdout is a terminal (decides the login QR).
	IsTTY func() bool
	// Sleep waits between login polls; nil means a real sleep.
	Sleep func(context.Context, time.Duration) error
	Now   func() time.Time

	// defaultCall is set when Call was filled in from Socket.
	defaultCall bool
}

func (e *Env) defaults() {
	if e.Stdout == nil {
		e.Stdout = os.Stdout
	}
	if e.Stderr == nil {
		e.Stderr = os.Stderr
	}
	if e.Socket == "" {
		e.Socket = proto.DefaultSocket
	}
	if e.Call == nil {
		e.defaultCall = true
		socket := e.Socket
		e.Call = func(ctx context.Context, cmd string, args, out any, timeout time.Duration) error {
			return proto.Call(ctx, socket, cmd, args, out, timeout)
		}
	}
	if e.IsTTY == nil {
		e.IsTTY = func() bool {
			info, err := os.Stdout.Stat()
			return err == nil && info.Mode()&os.ModeCharDevice != 0
		}
	}
	if e.Sleep == nil {
		e.Sleep = func(ctx context.Context, d time.Duration) error {
			t := time.NewTimer(d)
			defer t.Stop()
			select {
			case <-ctx.Done():
				return ctx.Err()
			case <-t.C:
				return nil
			}
		}
	}
	if e.Now == nil {
		e.Now = time.Now
	}
}

// Exit codes.
const (
	ExitOK    = 0
	ExitError = 1
	ExitUsage = 2
	ExitSig   = 130
)

const usage = `colitu - control the Colitu VPN daemon (colitud)

Usage: colitu [--socket PATH] <command> [options]

Commands:
  login                 sign in: shows a code to approve at colitu.com
  logout                sign out  (--remove-device also deletes this device from the account)
  servers [--json]      list locations
  connect               connect to the best server
          --country tr  ... the best server of a country
          --server ID   ... a specific server (id or name)
  status [--json]       connection state
  disconnect            disconnect
  activate              use this device when the plan paused it
  autoconnect on|off    reconnect to the last server when colitud starts
  version               print the version

The daemon runs as a system service: sudo systemctl enable --now colitud
`

// Run executes the command line (without the program name) and returns the
// exit code.
func Run(ctx context.Context, args []string, env *Env) int {
	env.defaults()
	// A global --socket may precede the command.
	for len(args) >= 2 && (args[0] == "--socket" || args[0] == "-socket") {
		env.Socket = args[1]
		if env.defaultCall {
			env.Call = nil
		}
		args = args[2:]
		env.defaults()
	}
	if len(args) == 0 {
		fmt.Fprint(env.Stderr, usage)
		return ExitUsage
	}
	cmd, rest := args[0], args[1:]
	switch cmd {
	case "help", "-h", "--help":
		fmt.Fprint(env.Stdout, usage)
		return ExitOK
	case "version", "-v", "--version":
		fmt.Fprintln(env.Stdout, "colitu", version.Version)
		return ExitOK
	case "login":
		return cmdLogin(ctx, rest, env)
	case "logout":
		return cmdLogout(ctx, rest, env)
	case "servers":
		return cmdServers(ctx, rest, env)
	case "connect":
		return cmdConnect(ctx, rest, env)
	case "status":
		return cmdStatus(ctx, rest, env)
	case "disconnect":
		return cmdDisconnect(ctx, rest, env)
	case "activate":
		return cmdActivate(ctx, rest, env)
	case "autoconnect":
		return cmdAutoConnect(ctx, rest, env)
	}
	fmt.Fprintf(env.Stderr, "colitu: unknown command %q\n\n%s", cmd, usage)
	return ExitUsage
}

func newFlags(name string, env *Env) *flag.FlagSet {
	fs := flag.NewFlagSet("colitu "+name, flag.ContinueOnError)
	fs.SetOutput(env.Stderr)
	return fs
}

// parse handles flags and rejects stray arguments.
func parse(fs *flag.FlagSet, args []string, positional int) ([]string, bool) {
	if err := fs.Parse(args); err != nil {
		return nil, false
	}
	if fs.NArg() != positional {
		fmt.Fprintf(fs.Output(), "colitu: %s takes %d argument(s)\n", fs.Name(), positional)
		return nil, false
	}
	return fs.Args(), true
}

// report prints an error with the hint that fits it and returns the exit
// code.
func report(env *Env, err error) int {
	var pe *proto.Error
	switch {
	case errors.Is(err, proto.ErrNoDaemon):
		fmt.Fprintf(env.Stderr, "colitu: %v\n", err)
		fmt.Fprintln(env.Stderr, "  Start it with: sudo systemctl enable --now colitud")
		fmt.Fprintln(env.Stderr, "  See why it is not running: journalctl -u colitud -e")
	case errors.Is(err, proto.ErrNoAccess):
		fmt.Fprintf(env.Stderr, "colitu: %v\n", err)
		fmt.Fprintln(env.Stderr, "  Add yourself to the colitu group, then log out and in again:")
		fmt.Fprintln(env.Stderr, "    sudo usermod -aG colitu $USER")
	case errors.As(err, &pe) && pe.Code == proto.CodePaused:
		fmt.Fprintf(env.Stderr, "colitu: %s\n", pe.Message)
		var p proto.Paused
		if json.Unmarshal(pe.Data, &p) == nil {
			printPaused(env.Stderr, &p)
		}
	case errors.As(err, &pe):
		fmt.Fprintf(env.Stderr, "colitu: %s\n", pe.Message)
	case errors.Is(err, context.Canceled):
		fmt.Fprintln(env.Stderr, "colitu: cancelled")
		return ExitSig
	default:
		fmt.Fprintf(env.Stderr, "colitu: %v\n", err)
	}
	return ExitError
}

func printPaused(w io.Writer, p *proto.Paused) {
	if p.DeviceLimit > 0 {
		fmt.Fprintf(w, "  The plan allows %d active device(s).\n", p.DeviceLimit)
	}
	for _, d := range p.ActiveDevices {
		fmt.Fprintf(w, "  Active: %s (%s)\n", d.Name, d.Platform)
	}
	fmt.Fprintln(w, "  `colitu activate` uses this device instead and pauses the least recently used one.")
}

func countryUpper(s string) string { return strings.ToUpper(strings.TrimSpace(s)) }
