package api

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

// panelMock stands in for colitu-panel's /api/v1. Each handler records the
// request so tests can check headers and bodies against the real handlers'
// expectations (strict JSON, Bearer + X-Device-ID).
type panelMock struct {
	t   *testing.T
	srv *httptest.Server
	mux *http.ServeMux
}

func newPanel(t *testing.T) *panelMock {
	t.Helper()
	m := &panelMock{t: t, mux: http.NewServeMux()}
	m.srv = httptest.NewServer(m.mux)
	t.Cleanup(m.srv.Close)
	return m
}

func (m *panelMock) client() *Client { return New(m.srv.URL + "/api/v1") }

func jsonReply(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}

func apiError(w http.ResponseWriter, status int, code, msg string) {
	jsonReply(w, status, map[string]any{"error": map[string]string{"code": code, "message": msg}})
}

func noSleep(context.Context, time.Duration) error { return nil }

const pollToken = "0123456789012345678901234567890123456789012" // 43 chars like the panel's

func TestLinkStartSendsDeviceNameAndPlatform(t *testing.T) {
	m := newPanel(t)
	m.mux.HandleFunc("POST /api/v1/auth/link/start", func(w http.ResponseWriter, r *http.Request) {
		if ct := r.Header.Get("Content-Type"); ct != "application/json" {
			t.Errorf("Content-Type = %q", ct)
		}
		var body map[string]string
		if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
			t.Fatal(err)
		}
		if body["device_name"] != "pi (headless)" || body["platform"] != "linux" || len(body) != 2 {
			t.Errorf("body = %v (the panel rejects unknown fields)", body)
		}
		jsonReply(w, 201, LinkStart{Code: "ABCD2345", URL: "https://colitu.com/link?c=ABCD2345", PollToken: pollToken, ExpiresIn: 600, Interval: 3})
	})
	start, err := m.client().LinkStart(context.Background(), "pi (headless)")
	if err != nil {
		t.Fatal(err)
	}
	if start.Code != "ABCD2345" || start.PollToken != pollToken || start.Interval != 3 {
		t.Errorf("start = %+v", start)
	}
}

func TestLinkFlowApproved(t *testing.T) {
	m := newPanel(t)
	var polls atomic.Int32
	m.mux.HandleFunc("POST /api/v1/auth/link/poll", func(w http.ResponseWriter, r *http.Request) {
		var body map[string]string
		_ = json.NewDecoder(r.Body).Decode(&body)
		if body["poll_token"] != pollToken {
			t.Errorf("poll token = %q", body["poll_token"])
		}
		if polls.Add(1) < 3 {
			jsonReply(w, 202, map[string]string{"status": "pending"})
			return
		}
		jsonReply(w, 200, Tokens{AccessToken: "acc", RefreshToken: "ref", TokenType: "Bearer", ExpiresIn: 900})
	})
	tokens, err := m.client().WaitLink(context.Background(), LinkStart{PollToken: pollToken, Interval: 3, ExpiresIn: 600}, noSleep)
	if err != nil {
		t.Fatal(err)
	}
	if tokens.AccessToken != "acc" || tokens.RefreshToken != "ref" || tokens.ExpiresIn != 900 {
		t.Errorf("tokens = %+v", tokens)
	}
	if polls.Load() != 3 {
		t.Errorf("polls = %d, want 3 (two pending, one approved)", polls.Load())
	}
}

func TestLinkFlowEndsWithPanelErrors(t *testing.T) {
	cases := []struct {
		name   string
		status int
		code   string
	}{
		{"expired", 410, CodeLinkExpired},
		{"denied", 403, CodeLinkDenied},
		{"unknown", 404, CodeLinkNotFound},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			m := newPanel(t)
			var polls atomic.Int32
			m.mux.HandleFunc("POST /api/v1/auth/link/poll", func(w http.ResponseWriter, r *http.Request) {
				if polls.Add(1) == 1 {
					jsonReply(w, 202, map[string]string{"status": "pending"})
					return
				}
				apiError(w, c.status, c.code, "x")
			})
			_, err := m.client().WaitLink(context.Background(), LinkStart{PollToken: pollToken, Interval: 3, ExpiresIn: 600}, noSleep)
			if !HasCode(err, c.code) {
				t.Fatalf("err = %v, want %s", err, c.code)
			}
			if e, _ := AsError(err); e.Status != c.status {
				t.Errorf("status = %d, want %d", e.Status, c.status)
			}
		})
	}
}

func TestLinkFlowSurvivesNetworkErrorsAndRateLimit(t *testing.T) {
	m := newPanel(t)
	var polls atomic.Int32
	m.mux.HandleFunc("POST /api/v1/auth/link/poll", func(w http.ResponseWriter, r *http.Request) {
		switch polls.Add(1) {
		case 1:
			// Drop the connection mid-request.
			conn, _, _ := w.(http.Hijacker).Hijack()
			conn.Close()
		case 2:
			apiError(w, 429, CodeRateLimited, "too many requests")
		default:
			jsonReply(w, 200, Tokens{AccessToken: "a", RefreshToken: "r", ExpiresIn: 60})
		}
	})
	tokens, err := m.client().WaitLink(context.Background(), LinkStart{PollToken: pollToken, Interval: 1, ExpiresIn: 600}, noSleep)
	if err != nil || tokens.AccessToken != "a" {
		t.Fatalf("WaitLink = %+v, %v", tokens, err)
	}
}

func TestLinkFlowStopsWhenContextEnds(t *testing.T) {
	m := newPanel(t)
	m.mux.HandleFunc("POST /api/v1/auth/link/poll", func(w http.ResponseWriter, r *http.Request) {
		jsonReply(w, 202, map[string]string{"status": "pending"})
	})
	ctx, cancel := context.WithCancel(context.Background())
	sleep := func(ctx context.Context, _ time.Duration) error { cancel(); return ctx.Err() }
	_, err := m.client().WaitLink(ctx, LinkStart{PollToken: pollToken, Interval: 1, ExpiresIn: 600}, sleep)
	if err == nil || ctx.Err() == nil {
		t.Fatalf("err = %v, want the context error", err)
	}
}

func TestDeviceOverLimitErrorCarriesActiveDevices(t *testing.T) {
	m := newPanel(t)
	m.mux.HandleFunc("GET /api/v1/servers", func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("Authorization") != "Bearer acc" || r.Header.Get("X-Device-ID") != "dev-1" {
			t.Errorf("headers: %v", r.Header)
		}
		jsonReply(w, 403, map[string]any{
			"error":          map[string]string{"code": "DEVICE_OVER_LIMIT", "message": "paused"},
			"device_limit":   1,
			"active_devices": []map[string]string{{"id": "d2", "name": "Phone", "platform": "android", "last_seen_at": "2026-10-06T10:00:00Z"}},
		})
	})
	_, err := m.client().Servers(context.Background(), Auth{Bearer: "acc", DeviceID: "dev-1"})
	e, ok := AsError(err)
	if !ok || e.Code != CodeDeviceOverLimit {
		t.Fatalf("err = %v", err)
	}
	if e.DeviceLimit != 1 || len(e.ActiveDevices) != 1 || e.ActiveDevices[0].Name != "Phone" {
		t.Errorf("over-limit detail lost: %+v", e)
	}
}

func TestRefreshSendsDeviceHeaderWithoutBearer(t *testing.T) {
	m := newPanel(t)
	m.mux.HandleFunc("POST /api/v1/auth/refresh", func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("X-Device-ID") != "dev-1" {
			t.Errorf("X-Device-ID = %q", r.Header.Get("X-Device-ID"))
		}
		if r.Header.Get("Authorization") != "" {
			t.Error("refresh must not send an access token")
		}
		var body map[string]string
		_ = json.NewDecoder(r.Body).Decode(&body)
		if body["refresh_token"] != "old" || len(body) != 1 {
			t.Errorf("body = %v", body)
		}
		jsonReply(w, 200, Tokens{AccessToken: "new-a", RefreshToken: "new-r", ExpiresIn: 900})
	})
	tok, err := m.client().Refresh(context.Background(), "old", "dev-1")
	if err != nil || tok.RefreshToken != "new-r" {
		t.Fatalf("Refresh = %+v, %v", tok, err)
	}
}

func TestRegisterDeviceBodyMatchesPanelContract(t *testing.T) {
	m := newPanel(t)
	m.mux.HandleFunc("POST /api/v1/devices/register", func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("Authorization") != "Bearer acc" {
			t.Errorf("Authorization = %q", r.Header.Get("Authorization"))
		}
		var body struct {
			DeviceKey    string `json:"device_key"`
			Name         string `json:"name"`
			Platform     string `json:"platform"`
			AppVersion   string `json:"app_version"`
			OSVersion    string `json:"os_version"`
			HardwareID   string `json:"hardware_id"`
			Capabilities struct {
				ConfigFormats []string `json:"config_formats"`
				Protocols     []string `json:"protocols"`
			} `json:"capabilities"`
		}
		dec := json.NewDecoder(r.Body)
		dec.DisallowUnknownFields() // the panel does the same
		if err := dec.Decode(&body); err != nil {
			t.Fatalf("panel would reject the body: %v", err)
		}
		if body.Platform != "linux" || body.Name == "" || len(body.DeviceKey) < 20 {
			t.Errorf("body = %+v", body)
		}
		if strings.Contains(strings.Join(body.Capabilities.Protocols, ","), "xhttp") {
			t.Error("must not claim vless-xhttp: sing-box has no XHTTP")
		}
		if len(body.Capabilities.ConfigFormats) != 1 || body.Capabilities.ConfigFormats[0] != "sing-box" {
			t.Errorf("config formats = %v", body.Capabilities.ConfigFormats)
		}
		jsonReply(w, 201, Device{ID: "dev-1", Name: body.Name})
	})
	dev, err := m.client().RegisterDevice(context.Background(), "acc", Registration{
		DeviceKey: strings.Repeat("k", 32), Name: "pi (headless)", Platform: Platform, AppVersion: "0.1.0",
		OSVersion: "Debian", Capabilities: ClientCapabilities(),
	})
	if err != nil || dev.ID != "dev-1" {
		t.Fatalf("RegisterDevice = %+v, %v", dev, err)
	}
}

func TestIsSessionEnded(t *testing.T) {
	for code, want := range map[string]bool{
		CodeInvalidCredentials: true, CodeRefreshReused: true, CodeDeviceRevoked: true,
		CodeDeviceMismatch: true, CodeDeviceNotFound: true,
		CodeTokenExpired: false, CodeDeviceOverLimit: false, CodeRateLimited: false,
	} {
		if got := IsSessionEnded(&Error{Code: code}); got != want {
			t.Errorf("IsSessionEnded(%s) = %v, want %v", code, got, want)
		}
	}
	if IsSessionEnded(context.DeadlineExceeded) {
		t.Error("a network error never ends the session")
	}
}

func TestNewReadsAPIBaseFromEnvironment(t *testing.T) {
	t.Setenv("COLITU_API_BASE", "http://127.0.0.1:9/api/v1/")
	if got := New("").Base; got != "http://127.0.0.1:9/api/v1" {
		t.Errorf("Base = %q", got)
	}
	t.Setenv("COLITU_API_BASE", "")
	if got := New("").Base; got != DefaultBase {
		t.Errorf("Base = %q, want the default", got)
	}
}
