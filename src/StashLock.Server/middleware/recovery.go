package middleware

import (
	"encoding/json"
	"errors"
	"net/http"

	"github.com/deneblab/stashlock-servergo/models"
)

// Recovery catches panics and StashLockErrors, returning JSON error responses.
func Recovery(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		defer func() {
			if rec := recover(); rec != nil {
				Log(r).Error().Interface("panic", rec).Str("path", r.URL.Path).Msg("Panic recovered")
				writeError(w, r, http.StatusInternalServerError, "Internal Server Error",
					"An unexpected error occurred", models.ErrorCodes.InternalError)
			}
		}()
		next.ServeHTTP(w, r)
	})
}

// ErrorHandler is middleware that wraps the handler and catches StashLockErrors.
// This is applied via a custom ResponseWriter that intercepts errors.
// For chi, we use a different approach — handlers call WriteStashLockError directly.

// WriteStashLockError writes a StashLockError or generic error as a JSON response.
func WriteStashLockError(w http.ResponseWriter, r *http.Request, err error) {
	var slErr *models.StashLockError
	if errors.As(err, &slErr) {
		title := getTitleForStatus(slErr.StatusCode)
		writeError(w, r, slErr.StatusCode, title, slErr.Message, slErr.ErrorCode)
		return
	}

	// ArgumentError → 400
	writeError(w, r, http.StatusInternalServerError, "Internal Server Error",
		err.Error(), models.ErrorCodes.InternalError)
}

func writeError(w http.ResponseWriter, r *http.Request, status int, title, detail, errorCode string) {
	traceId := r.Header.Get("X-Correlation-Id")
	if traceId == "" {
		traceId = w.Header().Get("X-Correlation-Id")
	}

	apiErr := models.ApiError{
		Status:    status,
		Title:     title,
		Detail:    detail,
		TraceId:   traceId,
		ErrorCode: errorCode,
	}

	Log(r).Error().
		Int("status", status).
		Str("errorCode", errorCode).
		Str("detail", detail).
		Str("path", r.URL.Path).
		Msg("Error response")

	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(apiErr)
}

func getTitleForStatus(status int) string {
	switch status {
	case 400:
		return "Bad Request"
	case 401:
		return "Unauthorized"
	case 403:
		return "Forbidden"
	case 404:
		return "Not Found"
	case 409:
		return "Conflict"
	default:
		return "Internal Server Error"
	}
}
