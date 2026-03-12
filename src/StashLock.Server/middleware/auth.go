package middleware

import (
	"context"
	"net/http"
	"strings"

	"github.com/deneblab/stashlock-servergo/models"
	"github.com/deneblab/stashlock-servergo/store"
)

type contextKey string

const apiKeyIdentityKey contextKey = "ApiKeyIdentity"

// GetIdentity retrieves the ApiKeyIdentity from the request context.
func GetIdentity(r *http.Request) *models.ApiKeyIdentity {
	if v := r.Context().Value(apiKeyIdentityKey); v != nil {
		if id, ok := v.(*models.ApiKeyIdentity); ok {
			return id
		}
	}
	return nil
}

// skipPaths that don't require authentication.
var skipPaths = []string{"/healthz", "/swagger", "/admin", "/static"}

func shouldSkipAuth(path string) bool {
	if path == "/" || path == "/healthz" {
		return true
	}
	for _, p := range skipPaths {
		if strings.HasPrefix(path, p) {
			return true
		}
	}
	return false
}

// ApiKeyAuth creates middleware that validates Bearer tokens against the master key and DB keys.
func ApiKeyAuth(masterKey string, apiKeyStore *store.ApiKeyStore) func(http.Handler) http.Handler {
	return func(next http.Handler) http.Handler {
		return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			// Open access mode — no master key configured
			if masterKey == "" {
				identity := &models.ApiKeyIdentity{
					Id:       "open",
					Name:     "Open Access",
					IsMaster: true,
					CanRead:  true,
					CanWrite: true,
					CanDelete: true,
					Scopes:   []string{"*"},
				}
				ctx := context.WithValue(r.Context(), apiKeyIdentityKey, identity)
				next.ServeHTTP(w, r.WithContext(ctx))
				return
			}

			// Skip auth for certain paths
			if shouldSkipAuth(r.URL.Path) {
				next.ServeHTTP(w, r)
				return
			}

			// Extract Bearer token
			authHeader := r.Header.Get("Authorization")
			if authHeader == "" || !strings.HasPrefix(strings.ToLower(authHeader), "bearer ") {
				Log(r).Warn().Str("path", r.URL.Path).Msg("Unauthorized request — missing or invalid Authorization header")
				WriteStashLockError(w, r, models.NewStashLockError(
					models.ErrorCodes.Unauthorized, "Missing or invalid Authorization header", 401))
				return
			}
			token := strings.TrimSpace(authHeader[7:])

			// Check master key
			if token == masterKey {
				identity := &models.ApiKeyIdentity{
					Id:       "master",
					Name:     "Master Key",
					IsMaster: true,
					CanRead:  true,
					CanWrite: true,
					CanDelete: true,
					Scopes:   []string{"*"},
				}
				ctx := context.WithValue(r.Context(), apiKeyIdentityKey, identity)
				next.ServeHTTP(w, r.WithContext(ctx))
				return
			}

			// Check database key
			identity, err := apiKeyStore.Validate(token)
			if err != nil {
				WriteStashLockError(w, r, models.NewStashLockError(
					models.ErrorCodes.InternalError, "Failed to validate API key", 500))
				return
			}
			if identity == nil {
				Log(r).Warn().Str("path", r.URL.Path).Msg("Unauthorized request — invalid API key")
				WriteStashLockError(w, r, models.NewStashLockError(
					models.ErrorCodes.Unauthorized, "Invalid API key", 401))
				return
			}

			ctx := context.WithValue(r.Context(), apiKeyIdentityKey, identity)
			next.ServeHTTP(w, r.WithContext(ctx))
		})
	}
}
