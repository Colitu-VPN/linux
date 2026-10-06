package singbox

import (
	"encoding/json"
	"errors"
	"flag"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
)

var update = flag.Bool("update", false, "rewrite the golden files")

// Payloads as colitu-panel renders them (internal/subscription/render.go,
// singOutbounds). All values are placeholders: example.test is a reserved
// name and the secrets are fake.
func payload(outbound string) json.RawMessage {
	return json.RawMessage(`{"log":{"level":"warn"},"outbounds":[` + outbound + `],"route":{"final":"proxy"}}`)
}

var (
	pReality = payload(`{"type":"vless","tag":"Test Node","server":"node.example.test","server_port":443,"uuid":"11111111-2222-3333-4444-555555555555","flow":"xtls-rprx-vision","tls":{"enabled":true,"server_name":"front.example.test","reality":{"enabled":true,"public_key":"jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0","short_id":"ab12"}}}`)
	pHy2     = payload(`{"type":"hysteria2","tag":"Test Node","server":"node.example.test","server_port":8443,"password":"hy2-secret","tls":{"enabled":true,"server_name":"node.example.test","insecure":true}}`)
	pTUIC    = payload(`{"type":"tuic","tag":"Test Node","server":"node.example.test","server_port":8444,"uuid":"11111111-2222-3333-4444-555555555555","password":"tuic-secret","tls":{"enabled":true,"server_name":"node.example.test"}}`)
	pSS      = payload(`{"type":"shadowsocks","tag":"Test Node","server":"203.0.113.7","server_port":8388,"method":"aes-128-gcm","password":"ss-secret"}`)
	pXHTTP   = payload(`{"type":"vless","tag":"Test Node","server":"node.example.test","server_port":8445,"uuid":"11111111-2222-3333-4444-555555555555","tls":{"enabled":true,"server_name":"front.example.test"}}`)
)

func golden(t *testing.T, name string, got []byte) {
	t.Helper()
	path := filepath.Join("testdata", name)
	if *update {
		if err := os.WriteFile(path, got, 0o644); err != nil {
			t.Fatal(err)
		}
	}
	want, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("missing golden file (run with -update): %v", err)
	}
	var a, b any
	if err := json.Unmarshal(want, &a); err != nil {
		t.Fatalf("golden %s is not JSON: %v", name, err)
	}
	if err := json.Unmarshal(got, &b); err != nil {
		t.Fatalf("generated config is not JSON: %v", err)
	}
	if !reflect.DeepEqual(a, b) {
		t.Errorf("%s differs from the generated config (rerun with -update if the change is intended):\n%s", name, got)
	}
}

func TestBuildSingleTransportGolden(t *testing.T) {
	res, err := Build([]Candidate{{Protocol: ProtoVLESSReality, Payload: pReality}}, Options{})
	if err != nil {
		t.Fatal(err)
	}
	golden(t, "single_reality.golden.json", res.JSON)
	if got := strings.Join(res.Protocols, ","); got != "vless-reality" {
		t.Errorf("protocols = %q", got)
	}
}

func TestBuildAllTransportsGolden(t *testing.T) {
	res, err := Build([]Candidate{
		{Protocol: ProtoVLESSReality, Payload: pReality},
		{Protocol: "vless-xhttp", Payload: pXHTTP},
		{Protocol: ProtoHysteria2, Payload: pHy2},
		{Protocol: ProtoTUIC, Payload: pTUIC},
		{Protocol: ProtoShadowsocks, Payload: pSS},
	}, Options{Interface: "colitu1", DNS: "9.9.9.9", LogLevel: "info"})
	if err != nil {
		t.Fatal(err)
	}
	golden(t, "all_transports.golden.json", res.JSON)
	if got := strings.Join(res.Protocols, ","); got != "vless-reality,hysteria2,tuic,shadowsocks" {
		t.Errorf("protocols = %q", got)
	}
	if len(res.Skipped) != 1 || !strings.HasPrefix(res.Skipped[0], "vless-xhttp") {
		t.Errorf("xhttp must be reported as skipped, got %v", res.Skipped)
	}
}

// parse reads a built config back into generic JSON.
func parse(t *testing.T, raw []byte) map[string]any {
	t.Helper()
	var m map[string]any
	if err := json.Unmarshal(raw, &m); err != nil {
		t.Fatal(err)
	}
	return m
}

func TestBuildTunIsStrictAndKeepsLAN(t *testing.T) {
	res, err := Build([]Candidate{{Protocol: ProtoHysteria2, Payload: pHy2}}, Options{})
	if err != nil {
		t.Fatal(err)
	}
	cfg := parse(t, res.JSON)
	tun := cfg["inbounds"].([]any)[0].(map[string]any)
	if tun["type"] != "tun" || tun["auto_route"] != true || tun["strict_route"] != true {
		t.Errorf("tun inbound must auto_route with strict_route: %v", tun)
	}
	if ex, _ := tun["route_exclude_address"].([]any); len(ex) == 0 {
		t.Error("the LAN must stay reachable (SSH into a headless device)")
	}
	route := cfg["route"].(map[string]any)
	if route["final"] != "proxy" {
		t.Errorf("final outbound = %v, want proxy", route["final"])
	}
	dns := cfg["dns"].(map[string]any)
	if dns["final"] != "dns-tunnel" {
		t.Errorf("dns must go through the tunnel, final = %v", dns["final"])
	}
	servers := dns["servers"].([]any)
	if servers[0].(map[string]any)["detour"] != "proxy" {
		t.Error("the tunnel DNS server must dial through the proxy outbound")
	}
}

func TestBuildNeverCopiesInsecureTLS(t *testing.T) {
	res, err := Build([]Candidate{{Protocol: ProtoHysteria2, Payload: pHy2}}, Options{})
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(string(res.JSON), "insecure") {
		t.Errorf("insecure leaked into the config:\n%s", res.JSON)
	}
}

func TestBuildRealityGetsUTLS(t *testing.T) {
	res, err := Build([]Candidate{{Protocol: ProtoVLESSReality, Payload: pReality}}, Options{})
	if err != nil {
		t.Fatal(err)
	}
	cfg := parse(t, res.JSON)
	var vless map[string]any
	for _, o := range cfg["outbounds"].([]any) {
		if m := o.(map[string]any); m["type"] == "vless" {
			vless = m
		}
	}
	tls := vless["tls"].(map[string]any)
	if u, _ := tls["utls"].(map[string]any); u["enabled"] != true {
		t.Errorf("Reality needs uTLS: %v", tls)
	}
	if vless["tag"] != "proxy" {
		t.Errorf("a single transport is the proxy outbound itself, tag = %v", vless["tag"])
	}
}

func TestBuildMultipleTransportsUseURLTest(t *testing.T) {
	res, err := Build([]Candidate{
		{Protocol: ProtoVLESSReality, Payload: pReality},
		{Protocol: ProtoHysteria2, Payload: pHy2},
	}, Options{})
	if err != nil {
		t.Fatal(err)
	}
	cfg := parse(t, res.JSON)
	var group map[string]any
	for _, o := range cfg["outbounds"].([]any) {
		if m := o.(map[string]any); m["tag"] == "proxy" {
			group = m
		}
	}
	if group["type"] != "urltest" {
		t.Fatalf("proxy must be a urltest group, got %v", group)
	}
	if n := len(group["outbounds"].([]any)); n != 2 {
		t.Errorf("group members = %d, want 2", n)
	}
}

func TestBuildErrors(t *testing.T) {
	cases := map[string][]Candidate{
		"no candidates":      nil,
		"only xhttp":         {{Protocol: "vless-xhttp", Payload: pXHTTP}},
		"garbage payload":    {{Protocol: ProtoHysteria2, Payload: json.RawMessage(`{"outbounds":`)}},
		"empty outbounds":    {{Protocol: ProtoTUIC, Payload: json.RawMessage(`{"outbounds":[]}`)}},
		"protocol mismatch":  {{Protocol: ProtoTUIC, Payload: pHy2}},
		"unsafe type":        {{Protocol: "", Payload: payload(`{"type":"direct","server":"a.example.test","server_port":1}`)}},
		"vless without tls":  {{Protocol: ProtoVLESSReality, Payload: payload(`{"type":"vless","server":"a.example.test","server_port":443,"uuid":"x"}`)}},
		"port out of range":  {{Protocol: ProtoShadowsocks, Payload: payload(`{"type":"shadowsocks","server":"a.example.test","server_port":70000,"method":"m","password":"p"}`)}},
		"shadowsocks no key": {{Protocol: ProtoShadowsocks, Payload: payload(`{"type":"shadowsocks","server":"a.example.test","server_port":8388}`)}},
	}
	for name, cands := range cases {
		t.Run(name, func(t *testing.T) {
			res, err := Build(cands, Options{})
			if !errors.Is(err, ErrNoUsableOutbound) {
				t.Fatalf("err = %v, want ErrNoUsableOutbound (config: %s)", err, res.JSON)
			}
		})
	}
}

func TestBuildIgnoresBadDNS(t *testing.T) {
	res, err := Build([]Candidate{{Protocol: ProtoHysteria2, Payload: pHy2}}, Options{DNS: "evil.example.test"})
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(string(res.JSON), "evil.example.test") {
		t.Error("a hostname must not be accepted as the tunnel DNS (it could not be resolved before the tunnel is up)")
	}
}
