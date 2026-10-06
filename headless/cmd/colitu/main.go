// Command colitu controls the headless Colitu daemon (colitud): sign in,
// pick a server, connect, disconnect and read the status.
package main

import (
	"context"
	"os"
	"os/signal"
	"syscall"

	"github.com/colitu/colitu-linux/headless/internal/cli"
	"github.com/colitu/colitu-linux/headless/internal/proto"
)

func main() {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	socket := os.Getenv("COLITU_SOCKET")
	if socket == "" {
		socket = proto.DefaultSocket
	}
	os.Exit(cli.Run(ctx, os.Args[1:], &cli.Env{Socket: socket}))
}
