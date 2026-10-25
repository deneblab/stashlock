package middleware

import (
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

var okHandler = http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(200) })

func TestBodyLimit(t *testing.T) {
	read := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if _, err := io.ReadAll(r.Body); err != nil {
			WriteStashLockError(w, r, err)
			return
		}
		w.WriteHeader(200)
	})
	h := BodyLimit(10)(read)

	w := httptest.NewRecorder()
	h.ServeHTTP(w, httptest.NewRequest("POST", "/", strings.NewReader("12345")))
	if w.Code != 200 {
		t.Errorf("small body: %d", w.Code)
	}

	w = httptest.NewRecorder()
	h.ServeHTTP(w, httptest.NewRequest("POST", "/", strings.NewReader(strings.Repeat("x", 11))))
	if w.Code != http.StatusRequestEntityTooLarge {
		t.Errorf("declared oversize body: %d", w.Code)
	}

	// Unknown length (chunked) is cut off while reading
	req := httptest.NewRequest("POST", "/", io.NopCloser(strings.NewReader(strings.Repeat("x", 50))))
	req.ContentLength = -1
	w = httptest.NewRecorder()
	h.ServeHTTP(w, req)
	if w.Code != http.StatusRequestEntityTooLarge {
		t.Errorf("streamed oversize body: %d", w.Code)
	}
}

func TestAuthGuard_BlocksAfterFailuresAndExpires(t *testing.T) {
	g := NewAuthGuard(nil)
	now := time.Date(2026, 10, 25, 12, 0, 0, 0, time.UTC)
	g.now = func() time.Time { return now }
	unauthorized := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(401) })
	h := g.Middleware(unauthorized)

	call := func(addr string) int {
		req := httptest.NewRequest("GET", "/boxes", nil)
		req.RemoteAddr = addr
		w := httptest.NewRecorder()
		h.ServeHTTP(w, req)
		return w.Code
	}
	for i := 0; i < 10; i++ {
		if c := call("203.0.113.5:1"); c != 401 {
			t.Fatalf("attempt %d: %d", i+1, c)
		}
	}
	if c := call("203.0.113.5:1"); c != 429 {
		t.Errorf("after 10 failures: %d, want 429", c)
	}
	if c := call("198.51.100.7:1"); c != 401 {
		t.Errorf("other client should not be blocked: %d", c)
	}
	now = now.Add(16 * time.Minute)
	if c := call("203.0.113.5:1"); c != 401 {
		t.Errorf("after block expired: %d", c)
	}
}

func TestAuthGuard_FailuresOutsideWindowDoNotAddUp(t *testing.T) {
	g := NewAuthGuard(nil)
	now := time.Date(2026, 10, 25, 12, 0, 0, 0, time.UTC)
	g.now = func() time.Time { return now }
	req := httptest.NewRequest("GET", "/", nil)
	for i := 0; i < 9; i++ {
		g.Fail(req)
	}
	now = now.Add(6 * time.Minute)
	g.Fail(req)
	if g.retryAfter(req) != 0 {
		t.Error("failures from an old window should not block")
	}
}

func TestSameOriginAdminPosts(t *testing.T) {
	h := SameOriginAdminPosts(okHandler)
	cases := []struct {
		name, method, path, site, origin string
		want                             int
	}{
		{"same-origin post", "POST", "/admin/keys/create", "same-origin", "http://example.com", 200},
		{"cross-site post", "POST", "/admin/keys/create", "cross-site", "", 403},
		{"foreign origin", "POST", "/admin/keys/create", "", "https://evil.example", 403},
		{"no browser headers", "POST", "/admin/keys/create", "", "", 200},
		{"cross-site get", "GET", "/admin/keys", "cross-site", "", 200},
		{"api post", "POST", "/boxes/x", "cross-site", "", 200},
	}
	for _, c := range cases {
		req := httptest.NewRequest(c.method, "http://example.com"+c.path, nil)
		if c.site != "" {
			req.Header.Set("Sec-Fetch-Site", c.site)
		}
		if c.origin != "" {
			req.Header.Set("Origin", c.origin)
		}
		w := httptest.NewRecorder()
		h.ServeHTTP(w, req)
		if w.Code != c.want {
			t.Errorf("%s: %d, want %d", c.name, w.Code, c.want)
		}
	}
}

func TestRecovery_CatchesPanicInLaterMiddlewareAndHidesErrors(t *testing.T) {
	panicky := func(next http.Handler) http.Handler {
		return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { panic("boom") })
	}
	w := httptest.NewRecorder()
	Recovery(panicky(okHandler)).ServeHTTP(w, httptest.NewRequest("GET", "/", nil))
	if w.Code != 500 {
		t.Errorf("panic: %d", w.Code)
	}

	w = httptest.NewRecorder()
	WriteStashLockError(w, httptest.NewRequest("GET", "/", nil), errors.New("insert key: UNIQUE constraint failed: secret detail"))
	if w.Code != 500 || strings.Contains(w.Body.String(), "secret detail") {
		t.Errorf("unknown error leaked: %d %s", w.Code, w.Body.String())
	}
	if title := getTitleForStatus(429); title != "Too Many Requests" {
		t.Errorf("429 title %q", title)
	}
}
