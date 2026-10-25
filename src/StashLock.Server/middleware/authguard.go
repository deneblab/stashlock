package middleware

import (
	"fmt"
	"net"
	"net/http"
	"sync"
	"time"

	"github.com/deneblab/stashlock-servergo/models"
	"github.com/rs/zerolog/log"
)

// AuthGuard slows down key guessing: after MaxFailures failed authentications from one
// client IP within Window, every request from that IP gets 429 for BlockFor.
type AuthGuard struct {
	MaxFailures int
	Window      time.Duration
	BlockFor    time.Duration

	proxies []*net.IPNet
	now     func() time.Time

	mu        sync.Mutex
	clients   map[string]*guardEntry
	lastPrune time.Time
}

type guardEntry struct {
	failures     int
	windowStart  time.Time
	blockedUntil time.Time
}

const maxGuardEntries = 100000

func NewAuthGuard(trustedProxies []string) *AuthGuard {
	return &AuthGuard{
		MaxFailures: 10,
		Window:      5 * time.Minute,
		BlockFor:    15 * time.Minute,
		proxies:     parseCIDRs(trustedProxies, "TrustedProxies"),
		now:         time.Now,
		clients:     map[string]*guardEntry{},
	}
}

func (g *AuthGuard) ip(r *http.Request) string {
	if ip := clientIP(r, g.proxies); ip != nil {
		return ip.String()
	}
	return r.RemoteAddr
}

// Fail records a failed authentication (an API 401 or a failed dashboard sign-in).
func (g *AuthGuard) Fail(r *http.Request) {
	if g == nil {
		return
	}
	ip := g.ip(r)
	now := g.now()
	g.mu.Lock()
	defer g.mu.Unlock()
	g.prune(now)
	e := g.clients[ip]
	if e == nil || now.Sub(e.windowStart) > g.Window {
		e = &guardEntry{windowStart: now}
		g.clients[ip] = e
	}
	e.failures++
	if e.failures >= g.MaxFailures && now.After(e.blockedUntil) {
		e.blockedUntil = now.Add(g.BlockFor)
		log.Warn().Str("ip", ip).Int("failures", e.failures).Dur("blockedFor", g.BlockFor).Msg("Too many failed authentications; client blocked")
	}
}

// retryAfter returns how long the client is still blocked (0 = not blocked).
func (g *AuthGuard) retryAfter(r *http.Request) time.Duration {
	ip := g.ip(r)
	now := g.now()
	g.mu.Lock()
	defer g.mu.Unlock()
	if e := g.clients[ip]; e != nil && now.Before(e.blockedUntil) {
		return e.blockedUntil.Sub(now)
	}
	return 0
}

func (g *AuthGuard) prune(now time.Time) {
	if now.Sub(g.lastPrune) < time.Minute && len(g.clients) < maxGuardEntries {
		return
	}
	g.lastPrune = now
	for ip, e := range g.clients {
		if now.After(e.blockedUntil) && now.Sub(e.windowStart) > g.Window {
			delete(g.clients, ip)
		}
	}
	if len(g.clients) >= maxGuardEntries {
		log.Warn().Int("entries", len(g.clients)).Msg("Auth guard table full; resetting")
		g.clients = map[string]*guardEntry{}
	}
}

// Middleware refuses blocked clients and counts 401 responses as failures.
func (g *AuthGuard) Middleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if wait := g.retryAfter(r); wait > 0 {
			w.Header().Set("Retry-After", fmt.Sprintf("%d", int(wait.Seconds())+1))
			WriteStashLockError(w, r, models.NewStashLockError(models.ErrorCodes.RateLimited,
				"Too many failed authentication attempts from your address. Try again later.", http.StatusTooManyRequests))
			return
		}
		sw := &statusRecorder{ResponseWriter: w, status: http.StatusOK}
		next.ServeHTTP(sw, r)
		if sw.status == http.StatusUnauthorized {
			g.Fail(r)
		}
	})
}

type statusRecorder struct {
	http.ResponseWriter
	status int
}

func (s *statusRecorder) WriteHeader(code int) {
	s.status = code
	s.ResponseWriter.WriteHeader(code)
}

// Unwrap lets http.ResponseController and MaxBytesReader reach the underlying writer.
func (s *statusRecorder) Unwrap() http.ResponseWriter { return s.ResponseWriter }
