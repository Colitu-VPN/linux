package cli

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"
	"text/tabwriter"
	"time"

	"github.com/skip2/go-qrcode"

	"github.com/colitu/colitu-linux/headless/internal/proto"
)

const (
	shortTimeout   = 20 * time.Second
	connectTimeout = 90 * time.Second
	loginPoll      = 2 * time.Second
)

func writeJSON(env *Env, v any) {
	enc := json.NewEncoder(env.Stdout)
	enc.SetIndent("", "  ")
	_ = enc.Encode(v)
}

// ---- login

func cmdLogin(ctx context.Context, args []string, env *Env) int {
	fs := newFlags("login", env)
	noQR := fs.Bool("no-qr", false, "do not draw the QR code")
	forceQR := fs.Bool("qr", false, "draw the QR code even when stdout is not a terminal")
	if _, ok := parse(fs, args, 0); !ok {
		return ExitUsage
	}

	var info proto.LoginInfo
	if err := env.Call(ctx, proto.CmdLoginStart, nil, &info, shortTimeout); err != nil {
		return report(env, err)
	}
	out := env.Stdout
	fmt.Fprintln(out, "To sign in, approve this device in your Colitu account:")
	fmt.Fprintln(out)
	fmt.Fprintf(out, "  1. Open  %s\n", info.URL)
	fmt.Fprintf(out, "     (or go to colitu.com/link on any signed-in device)\n")
	fmt.Fprintf(out, "  2. Check that the code is  %s\n", prettyCode(info.Code))
	fmt.Fprintf(out, "     and the device is \"%s\", then approve it.\n", info.DeviceName)
	if (*forceQR || env.IsTTY()) && !*noQR {
		if q, err := qrcode.New(info.URL, qrcode.Low); err == nil {
			fmt.Fprintln(out)
			fmt.Fprint(out, q.ToSmallString(false))
		}
	}
	fmt.Fprintln(out)
	fmt.Fprintf(out, "Waiting for approval (the code is valid for about %s)... Ctrl+C cancels.\n", roundMinutes(info.ExpiresAt.Sub(env.Now())))

	for {
		if err := env.Sleep(ctx, loginPoll); err != nil {
			// Ctrl+C: tell the daemon to stop polling.
			cctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
			_ = env.Call(cctx, proto.CmdLoginCancel, nil, nil, 0)
			cancel()
			fmt.Fprintln(env.Stderr, "colitu: sign-in cancelled")
			return ExitSig
		}
		var st proto.LoginState
		if err := env.Call(ctx, proto.CmdLoginStatus, nil, &st, shortTimeout); err != nil {
			if ctx.Err() != nil {
				continue
			}
			return report(env, err)
		}
		switch st.State {
		case proto.LoginPending:
			continue
		case proto.LoginApproved:
			fmt.Fprintln(out, "Signed in. Connect with:  colitu connect")
			fmt.Fprintln(out, "Reconnect automatically at boot:  colitu autoconnect on")
			return ExitOK
		case proto.LoginExpired:
			fmt.Fprintln(env.Stderr, "colitu: the code expired before it was approved. Run `colitu login` again.")
			return ExitError
		case proto.LoginDenied:
			fmt.Fprintln(env.Stderr, "colitu: the sign-in was declined in your account.")
			return ExitError
		default:
			msg := st.Message
			if msg == "" {
				msg = "sign-in failed"
			}
			fmt.Fprintf(env.Stderr, "colitu: %s\n", msg)
			return ExitError
		}
	}
}

// prettyCode groups the 8-character code as ABCD-2345.
func prettyCode(code string) string {
	if len(code) == 8 {
		return code[:4] + "-" + code[4:]
	}
	return code
}

func roundMinutes(d time.Duration) string {
	if d <= 0 {
		return "a few minutes"
	}
	m := int(d.Round(time.Minute) / time.Minute)
	if m < 1 {
		m = 1
	}
	return fmt.Sprintf("%d min", m)
}

// ---- logout

func cmdLogout(ctx context.Context, args []string, env *Env) int {
	fs := newFlags("logout", env)
	remove := fs.Bool("remove-device", false, "also delete this device from your account")
	if _, ok := parse(fs, args, 0); !ok {
		return ExitUsage
	}
	if err := env.Call(ctx, proto.CmdLogout, proto.LogoutArgs{RemoveDevice: *remove}, nil, shortTimeout); err != nil {
		return report(env, err)
	}
	fmt.Fprintln(env.Stdout, "Signed out.")
	return ExitOK
}

// ---- servers

func cmdServers(ctx context.Context, args []string, env *Env) int {
	fs := newFlags("servers", env)
	asJSON := fs.Bool("json", false, "machine-readable output")
	if _, ok := parse(fs, args, 0); !ok {
		return ExitUsage
	}
	var list []proto.Server
	if err := env.Call(ctx, proto.CmdServers, nil, &list, shortTimeout); err != nil {
		return report(env, err)
	}
	if *asJSON {
		if list == nil {
			list = []proto.Server{}
		}
		writeJSON(env, list)
		return ExitOK
	}
	tw := tabwriter.NewWriter(env.Stdout, 0, 4, 2, ' ', 0)
	fmt.Fprintln(tw, "COUNTRY\tCITY\tNAME\tLOAD\tSTATUS\tID")
	for _, s := range list {
		fmt.Fprintf(tw, "%s\t%s\t%s\t%s\t%s\t%s\n", s.Country, s.City, s.Name, dash(s.Load), dash(s.Status), s.ID)
	}
	_ = tw.Flush()
	return ExitOK
}

func dash(s string) string {
	if s == "" {
		return "-"
	}
	return s
}

// ---- connect / disconnect

func cmdConnect(ctx context.Context, args []string, env *Env) int {
	fs := newFlags("connect", env)
	country := fs.String("country", "", "connect to the best server of this country (two-letter code, e.g. tr)")
	server := fs.String("server", "", "connect to this server (id or name, see `colitu servers`)")
	if _, ok := parse(fs, args, 0); !ok {
		return ExitUsage
	}
	if *country != "" && *server != "" {
		fmt.Fprintln(env.Stderr, "colitu: use either --country or --server, not both")
		return ExitUsage
	}
	if c := strings.TrimSpace(*country); c != "" && len(c) != 2 {
		fmt.Fprintln(env.Stderr, "colitu: --country takes a two-letter country code such as tr or de")
		return ExitUsage
	}
	fmt.Fprintln(env.Stdout, "Connecting...")
	var st proto.Status
	err := env.Call(ctx, proto.CmdConnect, proto.ConnectArgs{ServerID: strings.TrimSpace(*server), Country: countryUpper(*country)}, &st, connectTimeout)
	if err != nil {
		return report(env, err)
	}
	if st.Server != nil {
		fmt.Fprintf(env.Stdout, "Connected to %s.\n", serverLabel(st.Server))
	} else {
		fmt.Fprintln(env.Stdout, "Connected.")
	}
	return ExitOK
}

func cmdDisconnect(ctx context.Context, args []string, env *Env) int {
	fs := newFlags("disconnect", env)
	if _, ok := parse(fs, args, 0); !ok {
		return ExitUsage
	}
	if err := env.Call(ctx, proto.CmdDisconnect, nil, nil, shortTimeout+connectTimeout/3); err != nil {
		return report(env, err)
	}
	fmt.Fprintln(env.Stdout, "Disconnected.")
	return ExitOK
}

func serverLabel(s *proto.ConnectedServer) string {
	label := s.Name
	if label == "" {
		label = s.ID
	}
	var where []string
	if s.City != "" {
		where = append(where, s.City)
	}
	if s.Country != "" {
		where = append(where, s.Country)
	}
	if len(where) > 0 {
		label += " (" + strings.Join(where, ", ") + ")"
	}
	return label
}

// ---- status

func cmdStatus(ctx context.Context, args []string, env *Env) int {
	fs := newFlags("status", env)
	asJSON := fs.Bool("json", false, "machine-readable output")
	if _, ok := parse(fs, args, 0); !ok {
		return ExitUsage
	}
	var st proto.Status
	if err := env.Call(ctx, proto.CmdStatus, nil, &st, shortTimeout); err != nil {
		return report(env, err)
	}
	if *asJSON {
		writeJSON(env, st)
		return ExitOK
	}
	printStatus(env, st)
	return ExitOK
}

func printStatus(env *Env, st proto.Status) {
	w := env.Stdout
	row := func(k, v string) { fmt.Fprintf(w, "%-13s %s\n", k+":", v) }
	switch st.State {
	case proto.StateLoggedOut:
		row("State", "signed out")
		fmt.Fprintln(w, "\nSign in with `colitu login`.")
	case proto.StateConnected:
		row("State", "connected")
	case proto.StateConnecting:
		row("State", "connecting")
	case proto.StateReconnecting:
		row("State", "reconnecting")
	default:
		if st.Paused != nil {
			row("State", "paused (device limit)")
		} else {
			row("State", "disconnected")
		}
	}
	if st.Server != nil && st.State != proto.StateDisconnected {
		row("Server", serverLabel(st.Server))
		if len(st.Server.Protocols) > 0 {
			row("Transports", strings.Join(st.Server.Protocols, ", "))
		}
	}
	if st.Since != nil {
		row("Since", fmt.Sprintf("%s (%s)", st.Since.Local().Format("2006-01-02 15:04:05"), ago(env.Now().Sub(*st.Since))))
	}
	if st.LoggedIn {
		if st.Device != nil {
			row("Device", st.Device.Name)
		}
		auto := "off"
		if st.AutoConnect {
			auto = "on"
			if st.LastServer != nil {
				auto += " (" + serverLabel(st.LastServer) + ")"
			}
		}
		row("Autoconnect", auto)
	}
	if st.Paused != nil {
		fmt.Fprintln(w)
		fmt.Fprintln(w, "This device is paused: the plan allows fewer devices than are signed in.")
		printPaused(w, st.Paused)
	}
	if st.LastError != "" {
		row("Last error", st.LastError)
	}
}

func ago(d time.Duration) string {
	if d < 0 {
		d = 0
	}
	switch {
	case d < time.Minute:
		return fmt.Sprintf("%ds", int(d.Seconds()))
	case d < time.Hour:
		return fmt.Sprintf("%dm", int(d.Minutes()))
	case d < 48*time.Hour:
		return fmt.Sprintf("%dh %dm", int(d.Hours()), int(d.Minutes())%60)
	}
	return fmt.Sprintf("%dd %dh", int(d.Hours())/24, int(d.Hours())%24)
}

// ---- activate / autoconnect

func cmdActivate(ctx context.Context, args []string, env *Env) int {
	fs := newFlags("activate", env)
	if _, ok := parse(fs, args, 0); !ok {
		return ExitUsage
	}
	var res proto.ActivateResult
	if err := env.Call(ctx, proto.CmdActivate, nil, &res, shortTimeout); err != nil {
		return report(env, err)
	}
	fmt.Fprintln(env.Stdout, "This device is active now.")
	for _, d := range res.Suspended {
		fmt.Fprintf(env.Stdout, "  Paused to make room: %s (%s)\n", d.Name, d.Platform)
	}
	return ExitOK
}

func cmdAutoConnect(ctx context.Context, args []string, env *Env) int {
	fs := newFlags("autoconnect", env)
	rest, ok := parse(fs, args, 1)
	if !ok {
		return ExitUsage
	}
	var on bool
	switch strings.ToLower(rest[0]) {
	case "on", "yes", "true", "1":
		on = true
	case "off", "no", "false", "0":
	default:
		fmt.Fprintln(env.Stderr, "colitu: usage: colitu autoconnect on|off")
		return ExitUsage
	}
	var st proto.Status
	if err := env.Call(ctx, proto.CmdAutoConnect, proto.AutoConnectArgs{Enabled: on}, &st, shortTimeout); err != nil {
		return report(env, err)
	}
	if !on {
		fmt.Fprintln(env.Stdout, "Autoconnect is off.")
		return ExitOK
	}
	if st.LastServer != nil {
		fmt.Fprintf(env.Stdout, "Autoconnect is on: colitud reconnects to %s at start.\n", serverLabel(st.LastServer))
	} else {
		fmt.Fprintln(env.Stdout, "Autoconnect is on. Connect once with `colitu connect`; colitud then reconnects to that server at start.")
	}
	return ExitOK
}
