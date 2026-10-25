package main

import (
	"context"
	"net"
	"net/http"
	"strings"
	"testing"
	"time"

	"github.com/deneblab/stashlock-servergo/config"
	"github.com/deneblab/stashlock-servergo/handlers"
)

func startServer(t *testing.T) (base string, cancel context.CancelFunc, done <-chan error) {
	t.Helper()
	handlers.TemplateFS = templatesFS
	handlers.StaticEmbedFS = staticFS
	cfg := &config.Config{
		WorkDir: t.TempDir(), MasterKey: "0123456789abcdef0123456789abcdef",
		MaxValueSizeKB: 1, MaxKeyLength: 120, MaxRequestBodySizeKB: 2,
		DefaultRateLimitPerMin: 60, DeletedRetentionDays: 30, Version: "test",
	}
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	errc := make(chan error, 1)
	go func() { errc <- run(ctx, cfg, ln) }()
	base = "http://" + ln.Addr().String()
	for i := 0; i < 50; i++ {
		if r, err := http.Get(base + "/healthz"); err == nil {
			r.Body.Close()
			break
		}
		time.Sleep(20 * time.Millisecond)
	}
	return base, cancel, errc
}

func TestRun_GracefulShutdownOnCancel(t *testing.T) {
	base, cancel, done := startServer(t)
	cancel()
	select {
	case err := <-done:
		if err != nil {
			t.Fatalf("run returned %v", err)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("server did not stop after cancel")
	}
	if _, err := http.Get(base + "/healthz"); err == nil {
		t.Error("server still answering after shutdown")
	}
}

func TestRouter_HardeningEndToEnd(t *testing.T) {
	base, cancel, done := startServer(t)
	defer func() { cancel(); <-done }()

	// /boxes/decode is gone
	resp, err := http.Post(base+"/boxes/decode", "application/json", strings.NewReader(`{}`))
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != http.StatusUnauthorized && resp.StatusCode != http.StatusNotFound && resp.StatusCode != http.StatusMethodNotAllowed {
		t.Errorf("/boxes/decode: status %d", resp.StatusCode)
	}

	// Oversized body → 413 before auth
	big := strings.Repeat("x", 3*1024)
	resp, err = http.Post(base+"/boxes/YQ", "text/plain", strings.NewReader(big))
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != http.StatusRequestEntityTooLarge {
		t.Errorf("oversized body: status %d, want 413", resp.StatusCode)
	}

	// Health check never leaks error details
	resp, _ = http.Get(base + "/healthz")
	resp.Body.Close()
	if resp.StatusCode != 200 {
		t.Errorf("healthz: %d", resp.StatusCode)
	}

	// Ten failed authentications → the client is blocked, even for /admin
	for i := 0; i < 10; i++ {
		req, _ := http.NewRequest("GET", base+"/boxes", nil)
		req.Header.Set("Authorization", "Bearer slk_wrong")
		r, err := http.DefaultClient.Do(req)
		if err != nil {
			t.Fatal(err)
		}
		r.Body.Close()
	}
	r, err := http.Get(base + "/admin/login")
	if err != nil {
		t.Fatal(err)
	}
	r.Body.Close()
	if r.StatusCode != http.StatusTooManyRequests || r.Header.Get("Retry-After") == "" {
		t.Errorf("after 10 failures: status %d Retry-After %q", r.StatusCode, r.Header.Get("Retry-After"))
	}
}
