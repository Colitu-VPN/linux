//go:build windows

package proto

import (
	"errors"
	"syscall"
)

// WSAECONNREFUSED; Go's syscall.ECONNREFUSED is a different, invented value
// on Windows. Windows is only a development host for colitud.
const wsaeconnrefused = syscall.Errno(10061)

func isConnRefused(err error) bool {
	return errors.Is(err, wsaeconnrefused)
}
