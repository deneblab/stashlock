package middleware

import (
	"fmt"
	"net/http"
	"strings"
	"sync"

	"github.com/deneblab/stashlock-servergo/models"
	"golang.org/x/time/rate"
)

type rateLimitEntry struct {
	limiter *rate.Limiter
}

// RateLimit creates middleware that enforces per-key fixed-window rate limiting.
func RateLimit(defaultLimitPerMin int) func(http.Handler) http.Handler {
	var mu sync.Mutex
	limiters := make(map[string]*rateLimitEntry)

	getLimiter := func(key string, limitPerMin int) *rate.Limiter {
		mu.Lock()
		defer mu.Unlock()

		if entry, ok := limiters[key]; ok {
			return entry.limiter
		}

		// rate.Limit is events per second
		l := rate.NewLimiter(rate.Limit(float64(limitPerMin)/60.0), limitPerMin)
		limiters[key] = &rateLimitEntry{limiter: l}
		return l
	}

	return func(next http.Handler) http.Handler {
		return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			// Skip for health/swagger/admin paths
			path := r.URL.Path
			if path == "/" || path == "/healthz" || strings.HasPrefix(path, "/swagger") || strings.HasPrefix(path, "/admin") || strings.HasPrefix(path, "/static") {
				next.ServeHTTP(w, r)
				return
			}

			identity := GetIdentity(r)

			// Master keys are exempt
			if identity != nil && identity.IsMaster {
				next.ServeHTTP(w, r)
				return
			}

			// Determine rate limit
			limitPerMin := defaultLimitPerMin
			key := "anonymous"

			if identity != nil {
				key = identity.Id
				if identity.RateLimitPerMinute != nil {
					if *identity.RateLimitPerMinute == 0 {
						// Unlimited
						next.ServeHTTP(w, r)
						return
					}
					limitPerMin = *identity.RateLimitPerMinute
				}
			}

			limiter := getLimiter(key, limitPerMin)

			if !limiter.Allow() {
				w.Header().Set("X-RateLimit-Limit", fmt.Sprintf("%d", limitPerMin))
				w.Header().Set("X-RateLimit-Remaining", "0")
				w.Header().Set("Retry-After", "60")
				WriteStashLockError(w, r, models.NewStashLockError(
					models.ErrorCodes.RateLimited, "Rate limit exceeded. Try again later.", 429))
				return
			}

			next.ServeHTTP(w, r)
		})
	}
}
