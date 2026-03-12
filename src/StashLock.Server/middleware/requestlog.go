package middleware

import (
	"net/http"
	"strings"
	"time"
)

// statusWriter wraps http.ResponseWriter to capture the status code.
type statusWriter struct {
	http.ResponseWriter
	status int
	written int64
}

func (w *statusWriter) WriteHeader(code int) {
	w.status = code
	w.ResponseWriter.WriteHeader(code)
}

func (w *statusWriter) Write(b []byte) (int, error) {
	n, err := w.ResponseWriter.Write(b)
	w.written += int64(n)
	return n, err
}

// RequestLog logs every HTTP request with method, path, status, latency, and size.
// Skips noisy paths: /healthz, /static/.
func RequestLog(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		// Skip noisy paths
		path := r.URL.Path
		if path == "/healthz" || strings.HasPrefix(path, "/static/") {
			next.ServeHTTP(w, r)
			return
		}

		start := time.Now()
		sw := &statusWriter{ResponseWriter: w, status: 200}

		next.ServeHTTP(sw, r)

		latency := time.Since(start)
		logger := Log(r)

		event := logger.Info()
		if sw.status >= 500 {
			event = logger.Error()
		} else if sw.status >= 400 {
			event = logger.Warn()
		}

		event.
			Str("method", r.Method).
			Str("path", path).
			Int("status", sw.status).
			Dur("latency", latency).
			Int64("responseBytes", sw.written).
			Msg("HTTP request")
	})
}
