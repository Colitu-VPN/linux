//go:build !linux

package state

import "io/fs"

// Ownership and Unix modes are only meaningful on Linux, the one platform
// colitud runs on; other platforms exist so the code builds and tests run.
const enforceModes = false

func checkOwner(fs.FileInfo) error { return nil }

func isUnsupportedChmod(error) bool { return true }
