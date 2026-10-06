// Command colitud is the headless Colitu daemon: it keeps the account
// session, runs the tunnel and answers the colitu CLI over a unix socket.
// It must run as root (it creates the TUN device and the routes); the
// packaged systemd unit does that.
package main

import (
	"context"
	"flag"
	"fmt"
	"log/slog"
	"os"
	"os/signal"
	"syscall"

	"github.com/colitu/colitu-linux/headless/internal/daemon"
	"github.com/colitu/colitu-linux/headless/internal/proto"
	"github.com/colitu/colitu-linux/headless/internal/state"
	"github.com/colitu/colitu-linux/headless/internal/version"
)

func main() {
	os.Exit(run(os.Args[1:]))
}

func envOr(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
}

func run(args []string) int {
	fs := flag.NewFlagSet("colitud", flag.ContinueOnError)
	statePath := fs.String("state", envOr("COLITU_STATE", state.DefaultPath), "state file (tokens, device id); mode 0600")
	socket := fs.String("socket", envOr("COLITU_SOCKET", proto.DefaultSocket), "unix socket of the colitu CLI")
	singbox := fs.String("singbox", "", "sing-box binary (default: $COLITU_SINGBOX, /opt/colitu-vpn/bin/sing_box/sing-box, $PATH)")
	group := fs.String("group", proto.Group, "group allowed to use the socket")
	debug := fs.Bool("debug", false, "verbose logging")
	showVersion := fs.Bool("version", false, "print the version and exit")
	allowNonRoot := fs.Bool("allow-non-root", false, "development only: run without root (the tunnel will not start)")
	if err := fs.Parse(args); err != nil {
		return 2
	}
	if *showVersion {
		fmt.Println("colitud", version.Version)
		return 0
	}

	level := slog.LevelInfo
	if *debug {
		level = slog.LevelDebug
	}
	// The journal adds timestamps; keep lines short.
	log := slog.New(slog.NewTextHandler(os.Stderr, &slog.HandlerOptions{
		Level: level,
		ReplaceAttr: func(groups []string, a slog.Attr) slog.Attr {
			if a.Key == slog.TimeKey && len(groups) == 0 {
				return slog.Attr{}
			}
			return a
		},
	}))

	if os.Geteuid() != 0 && !*allowNonRoot {
		log.Error("colitud must run as root (it creates the TUN device); use the systemd service: sudo systemctl enable --now colitud")
		return 1
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	d := daemon.New(daemon.Config{
		StatePath:   *statePath,
		SocketPath:  *socket,
		SingboxPath: *singbox,
		Group:       *group,
		Log:         log,
	})
	if err := d.Run(ctx); err != nil {
		log.Error("colitud stopped", "err", err)
		return 1
	}
	return 0
}
