package handlers

import (
	"encoding/json"
	"fmt"
	"net/http"

	"github.com/deneblab/stashlock-servergo/middleware"
	"github.com/deneblab/stashlock-servergo/models"
	"github.com/deneblab/stashlock-servergo/store"
	"github.com/go-chi/chi/v5"
)

type KeysHandler struct {
	apiKeyStore *store.ApiKeyStore
}

func NewKeysHandler(apiKeyStore *store.ApiKeyStore) *KeysHandler {
	return &KeysHandler{apiKeyStore: apiKeyStore}
}

// requireMaster checks that the current identity is a master key.
func requireMaster(w http.ResponseWriter, r *http.Request) bool {
	identity := middleware.GetIdentity(r)
	if identity == nil || !identity.IsMaster {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.Forbidden, "This endpoint requires master key authentication", 403))
		return false
	}
	return true
}

// CreateKey handles POST /keys
func (h *KeysHandler) CreateKey(w http.ResponseWriter, r *http.Request) {
	if !requireMaster(w, r) {
		return
	}

	var req store.CreateApiKeyRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.MissingRequiredFields, "Invalid request body", 400))
		return
	}

	resp, err := h.apiKeyStore.Create(req)
	if err != nil {
		middleware.WriteStashLockError(w, r, err)
		return
	}

	writeJSON(w, http.StatusCreated, resp)
}

// ListKeys handles GET /keys
func (h *KeysHandler) ListKeys(w http.ResponseWriter, r *http.Request) {
	if !requireMaster(w, r) {
		return
	}

	keys, err := h.apiKeyStore.List()
	if err != nil {
		middleware.WriteStashLockError(w, r, err)
		return
	}

	writeJSON(w, http.StatusOK, keys)
}

// GetKey handles GET /keys/{id}
func (h *KeysHandler) GetKey(w http.ResponseWriter, r *http.Request) {
	if !requireMaster(w, r) {
		return
	}

	id := chi.URLParam(r, "id")
	key, err := h.apiKeyStore.Get(id)
	if err != nil {
		middleware.WriteStashLockError(w, r, err)
		return
	}

	if key == nil {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.KeyNotFound, fmt.Sprintf("API key not found: %s", id), 404))
		return
	}

	writeJSON(w, http.StatusOK, key)
}

// RevokeKey handles DELETE /keys/{id}
func (h *KeysHandler) RevokeKey(w http.ResponseWriter, r *http.Request) {
	if !requireMaster(w, r) {
		return
	}

	id := chi.URLParam(r, "id")
	if err := h.apiKeyStore.Revoke(id); err != nil {
		middleware.WriteStashLockError(w, r, err)
		return
	}

	writeJSON(w, http.StatusOK, models.ApiResult{Status: "ok", Message: "API key revoked"})
}

// DeleteKey handles DELETE /keys/{id}/permanent
func (h *KeysHandler) DeleteKey(w http.ResponseWriter, r *http.Request) {
	if !requireMaster(w, r) {
		return
	}

	id := chi.URLParam(r, "id")
	if err := h.apiKeyStore.Delete(id); err != nil {
		middleware.WriteStashLockError(w, r, err)
		return
	}

	writeJSON(w, http.StatusOK, models.ApiResult{Status: "ok", Message: "API key permanently deleted"})
}
