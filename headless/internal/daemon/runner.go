package daemon

import (
	"errors"
	"fmt"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"syscall"
	"time"
)

// DefaultSingbox is where the .deb installs sing-box.
const DefaultSingbox = "/opt/colitu-vpn/bin/sing_box/sing-box"

// ErrCoreMissing means the sing-box binary cannot be found.
var ErrCoreMissing = errors.New("sing-box was not found")

// FindSingbox resolves the sing-box binary: the explicit path, else
// COLITU_SINGBOX, else the package location, else $PATH.
func FindSingbox(explicit string) (string, error) {
	candidates := []string{explicit, os.Getenv("COLITU_SINGBOX"), DefaultSingbox}
	for _, c := range candidates {
		if c == "" {
			continue
		}
		if info, err := os.Stat(c); err == nil && !info.IsDir() {
			return c, nil
		}
		if c == explicit || c == os.Getenv("COLITU_SINGBOX") {
			// An explicitly named binary must exist; do not silently use
			// another one.
			return "", fmt.Errorf("%w at %s (set by --singbox or COLITU_SINGBOX)", ErrCoreMissing, c)
		}
	}
	if p, err := exec.LookPath("sing-box"); err == nil {
		return p, nil
	}
	return "", fmt.Errorf("%w: expected %s (install the colitu-vpn package) or set COLITU_SINGBOX", ErrCoreMissing, DefaultSingbox)
}

// Process is a running sing-box.
type Process interface {
	// Done delivers the exit result once and is then closed.
	Done() <-chan error
	// Stop asks the process to exit and kills it after grace.
	Stop(grace time.Duration)
}

// Runner starts sing-box.
type Runner interface {
	Start(binary, configPath, workDir string) (Process, error)
}

type execRunner struct{}

func (execRunner) Start(binary, configPath, workDir string) (Process, error) {
	if err := os.MkdirAll(workDir, 0o700); err != nil {
		return nil, err
	}
	cmd := exec.Command(binary, "run", "-c", configPath, "-D", workDir, "--disable-color")
	// sing-box logs to stderr; systemd sends it to the journal.
	cmd.Stdout = os.Stderr
	cmd.Stderr = os.Stderr
	if err := cmd.Start(); err != nil {
		return nil, fmt.Errorf("starting sing-box: %w", err)
	}
	p := &execProcess{cmd: cmd, done: make(chan error, 1)}
	go func() {
		p.done <- cmd.Wait()
		close(p.done)
	}()
	return p, nil
}

type execProcess struct {
	cmd  *exec.Cmd
	done chan error
}

func (p *execProcess) Done() <-chan error { return p.done }

func (p *execProcess) Stop(grace time.Duration) {
	if p.cmd.Process == nil {
		return
	}
	_ = p.cmd.Process.Signal(syscall.SIGTERM)
	t := time.NewTimer(grace)
	defer t.Stop()
	select {
	case <-p.done:
	case <-t.C:
		_ = p.cmd.Process.Kill()
		<-p.done
	}
}

// writeConfig stores the sing-box configuration (it holds the device's
// credentials) next to the socket with mode 0600 and returns its path.
func writeConfig(dir string, raw []byte) (string, error) {
	if err := os.MkdirAll(dir, 0o750); err != nil {
		return "", err
	}
	path := filepath.Join(dir, "sing-box.json")
	tmp, err := os.CreateTemp(dir, ".sing-box-*.tmp")
	if err != nil {
		return "", err
	}
	name := tmp.Name()
	fail := func(err error) (string, error) {
		tmp.Close()
		_ = os.Remove(name)
		return "", err
	}
	_ = tmp.Chmod(0o600)
	if _, err := tmp.Write(raw); err != nil {
		return fail(err)
	}
	if err := tmp.Close(); err != nil {
		_ = os.Remove(name)
		return "", err
	}
	if err := os.Rename(name, path); err != nil {
		_ = os.Remove(name)
		return "", err
	}
	return path, nil
}

// interfaceUp reports whether the named network interface exists and is up.
func interfaceUp(name string) bool {
	ifc, err := net.InterfaceByName(name)
	return err == nil && ifc.Flags&net.FlagUp != 0
}
