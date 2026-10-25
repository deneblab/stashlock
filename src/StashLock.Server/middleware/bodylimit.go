package middleware

import (
	"errors"
	"fmt"
	"net/http"

	"github.com/deneblab/stashlock-servergo/models"
)

// BodyLimit caps request bodies at maxBytes. Declared oversize bodies are refused
// up front; others fail on read with *http.MaxBytesError (see IsBodyTooLarge).
func BodyLimit(maxBytes int64) func(http.Handler) http.Handler {
	return func(next http.Handler) http.Handler {
		return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			if r.ContentLength > maxBytes {
				WriteStashLockError(w, r, models.NewStashLockError(models.ErrorCodes.ValueTooLarge,
					fmt.Sprintf("Request body is larger than %d KB", maxBytes/1024), http.StatusRequestEntityTooLarge))
				return
			}
			r.Body = http.MaxBytesReader(w, r.Body, maxBytes)
			next.ServeHTTP(w, r)
		})
	}
}

// IsBodyTooLarge reports whether err came from reading past the BodyLimit.
func IsBodyTooLarge(err error) bool {
	var mbe *http.MaxBytesError
	return errors.As(err, &mbe)
}
