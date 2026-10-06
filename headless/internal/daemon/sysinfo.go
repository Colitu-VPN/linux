package daemon

import (
	"bufio"
	"crypto/sha256"
	"encoding/hex"
	"os"
	"runtime"
	"strings"
)

// deviceName is what the account's device list shows for this machine.
func deviceName() string {
	host, err := os.Hostname()
	host = strings.TrimSpace(host)
	if err != nil || host == "" {
		host = "colitu"
	}
	const suffix = " (headless)"
	if len(host)+len(suffix) > 100 {
		host = host[:100-len(suffix)]
	}
	return host + suffix
}

// osVersion describes the operating system for the device record.
func osVersion() string {
	f, err := os.Open("/etc/os-release")
	if err == nil {
		defer f.Close()
		sc := bufio.NewScanner(f)
		for sc.Scan() {
			if v, ok := strings.CutPrefix(sc.Text(), "PRETTY_NAME="); ok {
				v = strings.Trim(strings.TrimSpace(v), `"'`)
				if v != "" {
					return clip(v+" "+runtime.GOARCH, 100)
				}
			}
		}
	}
	return runtime.GOOS + "/" + runtime.GOARCH
}

// hardwareID is a stable identifier of this installation of the OS: a hash
// of the machine id, namespaced so the raw id never leaves the machine. The
// panel hashes it again and only compares it (trial abuse checks).
func hardwareID() string {
	for _, p := range []string{"/etc/machine-id", "/var/lib/dbus/machine-id"} {
		raw, err := os.ReadFile(p)
		if err != nil {
			continue
		}
		id := strings.TrimSpace(string(raw))
		if id == "" {
			continue
		}
		sum := sha256.Sum256([]byte("colitu-headless:" + id))
		return hex.EncodeToString(sum[:])
	}
	return ""
}

func clip(s string, n int) string {
	if len(s) <= n {
		return s
	}
	return s[:n]
}
