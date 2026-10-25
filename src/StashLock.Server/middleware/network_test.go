package middleware

import (
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestNetworkAccess(t *testing.T) {
	ok := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(200) })

	tests := []struct {
		name       string
		proxies    []string
		remoteAddr string
		xff        string
		path       string
		want       int
	}{
		{"allowed direct", nil, "10.1.2.3:5000", "", "/admin/login", 200},
		{"denied direct", nil, "203.0.113.5:5000", "", "/admin/login", 403},
		{"spoofed xff ignored without trusted proxies", nil, "203.0.113.5:5000", "10.1.2.3", "/admin/login", 403},
		{"spoofed xff ignored from untrusted peer", []string{"172.18.0.0/16"}, "203.0.113.5:5000", "10.1.2.3", "/admin/login", 403},
		{"trusted proxy forwards allowed client", []string{"172.18.0.0/16"}, "172.18.0.2:5000", "10.1.2.3", "/admin/login", 200},
		{"trusted proxy forwards denied client", []string{"172.18.0.0/16"}, "172.18.0.2:5000", "203.0.113.5", "/admin/login", 403},
		{"client-prepended entry ignored", []string{"172.18.0.0/16"}, "172.18.0.2:5000", "10.1.2.3, 203.0.113.5", "/admin/login", 403},
		{"chain of trusted proxies", []string{"172.18.0.0/16"}, "172.18.0.2:5000", "10.1.2.3, 172.18.0.9", "/admin/login", 200},
		{"non-admin path not restricted", nil, "203.0.113.5:5000", "", "/boxes", 200},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			h := NetworkAccess([]string{"10.0.0.0/8"}, tt.proxies)(ok)
			req := httptest.NewRequest("GET", tt.path, nil)
			req.RemoteAddr = tt.remoteAddr
			if tt.xff != "" {
				req.Header.Set("X-Forwarded-For", tt.xff)
			}
			w := httptest.NewRecorder()
			h.ServeHTTP(w, req)
			if w.Code != tt.want {
				t.Errorf("status = %d, want %d", w.Code, tt.want)
			}
		})
	}
}

func TestNetworkAccess_NoNetworksAllowsAll(t *testing.T) {
	ok := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(200) })
	h := NetworkAccess(nil, nil)(ok)
	req := httptest.NewRequest("GET", "/admin/login", nil)
	req.RemoteAddr = "203.0.113.5:5000"
	w := httptest.NewRecorder()
	h.ServeHTTP(w, req)
	if w.Code != 200 {
		t.Errorf("status = %d, want 200", w.Code)
	}
}
