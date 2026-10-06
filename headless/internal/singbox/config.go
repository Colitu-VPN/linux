// Package singbox builds the sing-box client configuration colitud runs.
//
// The panel renders every candidate transport of the chosen server as a
// small sing-box document holding one outbound (colitu-panel
// internal/subscription/render.go, singOutbounds). The headless client does
// not recompute that mapping from raw endpoint settings, which the app API
// never exposes; it takes those outbounds, keeps only the transports and
// fields sing-box supports here (VLESS Reality, Hysteria2, TUIC,
// Shadowsocks; VLESS XHTTP is skipped because sing-box has no XHTTP) and
// wraps them in a TUN inbound with strict routing, so nothing leaves the
// machine outside the tunnel while it is connected.
package singbox

import (
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"strings"
)

// Supported protocols, as the panel names them.
const (
	ProtoVLESSReality = "vless-reality"
	ProtoHysteria2    = "hysteria2"
	ProtoTUIC         = "tuic"
	ProtoShadowsocks  = "shadowsocks"
)

// DefaultInterface is the TUN device name.
const DefaultInterface = "colitu0"

// DefaultDNS is the resolver used through the tunnel (a DoH endpoint
// reached over the VPN, so the local network never sees the queries).
const DefaultDNS = "1.1.1.1"

// ErrNoUsableOutbound means none of the candidates can run on sing-box.
var ErrNoUsableOutbound = errors.New("none of the server's transports is supported by this client")

// Candidate is one transport as the panel returned it.
type Candidate struct {
	Protocol string
	Payload  json.RawMessage
}

// Options tune the generated configuration.
type Options struct {
	// Interface is the TUN name (default DefaultInterface).
	Interface string
	// DNS is the IP of the DoH resolver queried through the tunnel (default
	// DefaultDNS). Anything that is not an IP literal is ignored.
	DNS string
	// LogLevel is sing-box's log level (default "warn").
	LogLevel string
}

// Result is a built configuration.
type Result struct {
	JSON []byte
	// Protocols lists the transports included, in outbound order.
	Protocols []string
	// Skipped lists candidates left out (unsupported transport or an
	// unusable payload), as "protocol: reason".
	Skipped []string
}

// Build produces the sing-box configuration for the candidates.
func Build(cands []Candidate, opt Options) (Result, error) {
	if opt.Interface == "" {
		opt.Interface = DefaultInterface
	}
	if net.ParseIP(opt.DNS) == nil {
		opt.DNS = DefaultDNS
	}
	if opt.LogLevel == "" {
		opt.LogLevel = "warn"
	}

	var res Result
	var outbounds []map[string]any
	for _, c := range cands {
		ob, proto, err := outboundFromCandidate(c)
		if err != nil {
			res.Skipped = append(res.Skipped, fmt.Sprintf("%s: %v", labelOf(c), err))
			continue
		}
		outbounds = append(outbounds, ob)
		res.Protocols = append(res.Protocols, proto)
	}
	if len(outbounds) == 0 {
		return res, ErrNoUsableOutbound
	}

	// Each outbound resolves its own server name with the system resolver:
	// the tunnel's DNS runs through the proxy, which does not exist yet.
	var tags []any
	for i, ob := range outbounds {
		ob["domain_resolver"] = "dns-bootstrap"
		tag := fmt.Sprintf("%s-%d", res.Protocols[i], i+1)
		if len(outbounds) == 1 {
			tag = "proxy"
		}
		ob["tag"] = tag
		tags = append(tags, tag)
	}
	all := make([]any, 0, len(outbounds)+2)
	for _, ob := range outbounds {
		all = append(all, ob)
	}
	if len(outbounds) > 1 {
		// Several transports: sing-box measures them and uses the fastest
		// that works, the way the apps probe candidates.
		all = append(all, map[string]any{
			"type":      "urltest",
			"tag":       "proxy",
			"outbounds": tags,
			"url":       "https://cp.cloudflare.com/generate_204",
			"interval":  "3m",
			"tolerance": 50,
		})
	}
	all = append(all, map[string]any{"type": "direct", "tag": "direct"})

	cfg := map[string]any{
		"log": map[string]any{"level": opt.LogLevel, "timestamp": true},
		"dns": map[string]any{
			"servers": []any{
				map[string]any{
					"type": "https", "tag": "dns-tunnel",
					"server": opt.DNS, "server_port": 443, "path": "/dns-query",
					"detour": "proxy",
				},
				map[string]any{"type": "local", "tag": "dns-bootstrap"},
			},
			"final":    "dns-tunnel",
			"strategy": "prefer_ipv4",
		},
		"inbounds": []any{
			map[string]any{
				"type":           "tun",
				"tag":            "tun-in",
				"interface_name": opt.Interface,
				"address":        []any{"172.19.0.1/30", "fdfe:dcba:9876::1/126"},
				"mtu":            1400,
				"auto_route":     true,
				// strict_route stops traffic from bypassing the tunnel: this
				// is the kill switch while connected.
				"strict_route": true,
				"stack":        "system",
				// The local network stays reachable, so an SSH session into a
				// headless device survives connecting.
				"route_exclude_address": []any{
					"10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16",
					"fc00::/7", "fe80::/10",
				},
			},
		},
		"outbounds": all,
		"route": map[string]any{
			"auto_detect_interface":   true,
			"default_domain_resolver": map[string]any{"server": "dns-bootstrap", "strategy": "prefer_ipv4"},
			"rules": []any{
				map[string]any{"action": "sniff"},
				map[string]any{"protocol": "dns", "action": "hijack-dns"},
				map[string]any{"ip_is_private": true, "action": "route", "outbound": "direct"},
			},
			"final": "proxy",
		},
	}
	raw, err := json.MarshalIndent(cfg, "", "  ")
	if err != nil {
		return res, err
	}
	res.JSON = raw
	return res, nil
}

func labelOf(c Candidate) string {
	if c.Protocol != "" {
		return c.Protocol
	}
	return "unknown"
}

// protoOfType maps a sing-box outbound type back to the panel's name.
func protoOfType(t string) string {
	switch t {
	case "vless":
		return ProtoVLESSReality
	case "hysteria2", "tuic", "shadowsocks":
		return t
	}
	return ""
}

// fieldsByType whitelists what is copied from the panel's outbound: the
// payload is trusted, but the client still keeps to the fields it knows, so
// a server-side mistake cannot, for example, switch off certificate checks
// or add a detour.
var fieldsByType = map[string][]string{
	"vless":       {"uuid", "flow", "packet_encoding"},
	"hysteria2":   {"password", "obfs", "up_mbps", "down_mbps"},
	"tuic":        {"uuid", "password", "congestion_control", "udp_relay_mode"},
	"shadowsocks": {"method", "password", "plugin", "plugin_opts"},
}

func outboundFromCandidate(c Candidate) (map[string]any, string, error) {
	switch c.Protocol {
	case ProtoVLESSReality, ProtoHysteria2, ProtoTUIC, ProtoShadowsocks:
	case "":
		// Older answers carry the protocol only inside the payload.
	default:
		return nil, "", errors.New("transport not supported by sing-box")
	}
	var doc struct {
		Outbounds []map[string]any `json:"outbounds"`
	}
	if err := json.Unmarshal(c.Payload, &doc); err != nil {
		return nil, "", fmt.Errorf("unreadable profile: %w", err)
	}
	for _, src := range doc.Outbounds {
		typ, _ := src["type"].(string)
		proto := protoOfType(typ)
		if proto == "" || (c.Protocol != "" && proto != c.Protocol) {
			continue
		}
		ob, err := cleanOutbound(typ, src)
		if err != nil {
			return nil, "", err
		}
		return ob, proto, nil
	}
	return nil, "", errors.New("profile has no supported outbound")
}

func cleanOutbound(typ string, src map[string]any) (map[string]any, error) {
	server, _ := src["server"].(string)
	port, ok := asPort(src["server_port"])
	if strings.TrimSpace(server) == "" || !ok {
		return nil, errors.New("outbound lacks server or port")
	}
	ob := map[string]any{"type": typ, "server": server, "server_port": port}
	for _, k := range fieldsByType[typ] {
		if v, present := src[k]; present && v != nil && v != "" {
			ob[k] = v
		}
	}
	switch typ {
	case "vless":
		if _, ok := ob["uuid"].(string); !ok {
			return nil, errors.New("vless outbound lacks uuid")
		}
		tls := cleanTLS(src["tls"])
		if tls == nil {
			return nil, errors.New("vless outbound lacks tls")
		}
		if _, hasReality := tls["reality"]; !hasReality {
			return nil, errors.New("vless outbound is not Reality")
		}
		// Reality needs uTLS; the panel's profile leaves it to the client.
		if _, set := tls["utls"]; !set {
			tls["utls"] = map[string]any{"enabled": true, "fingerprint": "chrome"}
		}
		ob["tls"] = tls
		if _, set := ob["packet_encoding"]; !set {
			ob["packet_encoding"] = "xudp"
		}
	case "hysteria2", "tuic":
		tls := cleanTLS(src["tls"])
		if tls == nil {
			return nil, fmt.Errorf("%s outbound lacks tls", typ)
		}
		ob["tls"] = tls
	case "shadowsocks":
		if m, _ := ob["method"].(string); m == "" {
			return nil, errors.New("shadowsocks outbound lacks method")
		}
	}
	return ob, nil
}

// cleanTLS copies the TLS fields the client accepts. insecure is never
// copied: certificates are always verified.
func cleanTLS(v any) map[string]any {
	src, ok := v.(map[string]any)
	if !ok {
		return nil
	}
	out := map[string]any{"enabled": true}
	for _, k := range []string{"server_name", "alpn", "utls"} {
		if x, present := src[k]; present && x != nil && x != "" {
			out[k] = x
		}
	}
	if r, ok := src["reality"].(map[string]any); ok {
		reality := map[string]any{"enabled": true}
		for _, k := range []string{"public_key", "short_id"} {
			if x, present := r[k]; present && x != nil {
				reality[k] = x
			}
		}
		out["reality"] = reality
	}
	return out
}

func asPort(v any) (int, bool) {
	f, ok := v.(float64)
	if !ok || f < 1 || f > 65535 || f != float64(int(f)) {
		return 0, false
	}
	return int(f), true
}
