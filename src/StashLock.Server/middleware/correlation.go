package middleware

import (
	"context"
	"net/http"

	"github.com/google/uuid"
	"github.com/rs/zerolog"
	"github.com/rs/zerolog/log"
)

type loggerKey struct{}

// Correlation adds or propagates X-Correlation-Id header on every request
// and injects a per-request zerolog logger with the correlation ID into the context.
func Correlation(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		correlationId := r.Header.Get("X-Correlation-Id")
		if correlationId == "" {
			correlationId = uuid.New().String()
		}

		r.Header.Set("X-Correlation-Id", correlationId)
		w.Header().Set("X-Correlation-Id", correlationId)

		// Inject per-request logger with correlation ID
		logger := log.With().Str("correlationId", correlationId).Logger()
		ctx := context.WithValue(r.Context(), loggerKey{}, &logger)
		next.ServeHTTP(w, r.WithContext(ctx))
	})
}

// Log returns the per-request logger from context, falling back to the global logger.
func Log(r *http.Request) *zerolog.Logger {
	if l, ok := r.Context().Value(loggerKey{}).(*zerolog.Logger); ok {
		return l
	}
	return &log.Logger
}
