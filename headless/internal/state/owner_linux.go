//go:build linux

package state

import (
	"io/fs"
	"os"
	"syscall"
)

// enforceModes: Unix permission bits are authoritative here.
const enforceModes = true

// checkOwner refuses a state file that neither the current user nor root
// owns: a file another user could have written is not to be trusted with
// tokens.
func checkOwner(info fs.FileInfo) error {
	st, ok := info.Sys().(*syscall.Stat_t)
	if !ok {
		return nil
	}
	uid := uint32(os.Geteuid())
	if st.Uid != uid && st.Uid != 0 {
		return ErrForeignOwner
	}
	return nil
}

func isUnsupportedChmod(error) bool { return false }
