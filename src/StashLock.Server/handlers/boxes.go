package handlers

import (
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strconv"
	"time"

	"github.com/deneblab/stashlock-servergo/middleware"
	"github.com/deneblab/stashlock-servergo/models"
	"github.com/deneblab/stashlock-servergo/store"
	"github.com/go-chi/chi/v5"
)

// requirePermission checks that the identity has the given permission, optionally scoped to a box.
func requirePermission(w http.ResponseWriter, r *http.Request, permission string, box string) bool {
	identity := middleware.GetIdentity(r)
	if identity == nil {
		// Open access mode
		return true
	}

	var allowed bool
	switch permission {
	case "read":
		allowed = identity.CanRead
	case "write":
		allowed = identity.CanWrite
	case "delete":
		allowed = identity.CanDelete
	}

	if !allowed {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.Forbidden,
			fmt.Sprintf("API key does not have '%s' permission", permission), 403))
		return false
	}

	if box != "" && !identity.HasScopeFor(box) {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.Forbidden,
			fmt.Sprintf("API key does not have access to box '%s'", box), 403))
		return false
	}

	return true
}

type BoxesHandler struct {
	storage    *store.StorageService
	auditStore *store.AuditStore
	version    string
}

func NewBoxesHandler(storage *store.StorageService, auditStore *store.AuditStore, version string) *BoxesHandler {
	return &BoxesHandler{storage: storage, auditStore: auditStore, version: version}
}

// GetRoot handles GET /
func (h *BoxesHandler) GetRoot(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "text/plain; charset=utf-8")
	w.WriteHeader(http.StatusOK)
	fmt.Fprintf(w, "OK ; Version: %s", h.version)
}

// ListBoxes handles GET /boxes
func (h *BoxesHandler) ListBoxes(w http.ResponseWriter, r *http.Request) {
	if !requirePermission(w, r, "read", "") {
		return
	}

	prefix := r.URL.Query().Get("prefix")
	var prefixPtr *string
	if prefix != "" {
		prefixPtr = &prefix
	}

	limit := 100
	if l := r.URL.Query().Get("limit"); l != "" {
		if n, err := strconv.Atoi(l); err == nil && n > 0 && n <= 10000 {
			limit = n
		}
	}
	offset := 0
	if o := r.URL.Query().Get("offset"); o != "" {
		if n, err := strconv.Atoi(o); err == nil && n >= 0 {
			offset = n
		}
	}

	keys, err := h.storage.ListKeys(prefixPtr, limit, offset)
	if err != nil {
		middleware.WriteStashLockError(w, r, err)
		return
	}

	writeJSON(w, http.StatusOK, keys)
}

// SetBox handles POST /boxes/{keyTextUrl}
func (h *BoxesHandler) SetBox(w http.ResponseWriter, r *http.Request) {
	keyTextUrl := chi.URLParam(r, "keyTextUrl")
	if len(keyTextUrl) > 120 {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.InvalidKeyFormat, "Key exceeds maximum length of 120 characters", 400))
		return
	}

	key, err := models.FromBase64Url(keyTextUrl)
	if err != nil {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.InvalidBase64, err.Error(), 400))
		return
	}

	if !requirePermission(w, r, "write", key.Box) {
		return
	}

	// Parse optional expiresIn query parameter
	var expiresAt *string
	if expiresInStr := r.URL.Query().Get("expiresIn"); expiresInStr != "" {
		expiresIn, err := strconv.Atoi(expiresInStr)
		if err != nil || expiresIn <= 0 {
			middleware.WriteStashLockError(w, r, models.NewStashLockError(
				models.ErrorCodes.InvalidTtl, "expiresIn must be greater than 0", 400))
			return
		}
		exp := time.Now().UTC().Add(time.Duration(expiresIn) * time.Second).Format(time.RFC3339Nano)
		expiresAt = &exp
	}

	// Read body as plain text (no Content-Type requirement)
	body, err := io.ReadAll(r.Body)
	if err != nil {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.InternalError, "Failed to read request body", 500))
		return
	}
	content := string(body)

	if err := h.storage.Set(key, content, expiresAt); err != nil {
		h.audit(r, "set", key.FullAddress(), "error", err.Error())
		middleware.WriteStashLockError(w, r, err)
		return
	}

	h.audit(r, "set", key.FullAddress(), "ok", "")
	writeJSON(w, http.StatusOK, models.ApiResult{
		Status: "ok",
		Key:    key.FullAddress(),
	})
}

// GetBox handles GET /boxes/{keyTextUrl}
func (h *BoxesHandler) GetBox(w http.ResponseWriter, r *http.Request) {
	keyTextUrl := chi.URLParam(r, "keyTextUrl")
	key, err := models.FromBase64Url(keyTextUrl)
	if err != nil {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.InvalidBase64, err.Error(), 400))
		return
	}

	if !requirePermission(w, r, "read", key.Box) {
		return
	}

	value, err := h.storage.Get(key)
	if err != nil {
		middleware.WriteStashLockError(w, r, err)
		return
	}

	if value == "" {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.KeyNotFound,
			fmt.Sprintf("Key not found: %s. Use GET /boxes?prefix=%s to list available keys.", key.FullAddress(), key.Box),
			404))
		return
	}

	h.audit(r, "get", key.FullAddress(), "ok", "")
	w.Header().Set("Content-Type", "text/plain; charset=utf-8")
	w.WriteHeader(http.StatusOK)
	w.Write([]byte(value))
}

// DeleteBox handles DELETE /boxes/{keyTextUrl}
func (h *BoxesHandler) DeleteBox(w http.ResponseWriter, r *http.Request) {
	keyTextUrl := chi.URLParam(r, "keyTextUrl")
	key, err := models.FromBase64Url(keyTextUrl)
	if err != nil {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.InvalidBase64, err.Error(), 400))
		return
	}

	if !requirePermission(w, r, "delete", key.Box) {
		return
	}

	if err := h.storage.Delete(key); err != nil {
		h.audit(r, "delete", key.FullAddress(), "error", err.Error())
		middleware.WriteStashLockError(w, r, err)
		return
	}

	h.audit(r, "delete", key.FullAddress(), "ok", "")
	writeJSON(w, http.StatusOK, models.ApiResult{
		Status:  "ok",
		Key:     key.FullAddress(),
		Message: "Deleted",
	})
}

// GetHistory handles GET /boxes/{keyTextUrl}/history
func (h *BoxesHandler) GetHistory(w http.ResponseWriter, r *http.Request) {
	keyTextUrl := chi.URLParam(r, "keyTextUrl")
	key, err := models.FromBase64Url(keyTextUrl)
	if err != nil {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.InvalidBase64, err.Error(), 400))
		return
	}

	if !requirePermission(w, r, "read", key.Box) {
		return
	}

	entries, err := h.storage.GetHistory(key)
	if err != nil {
		middleware.WriteStashLockError(w, r, err)
		return
	}

	writeJSON(w, http.StatusOK, models.HistoryResponse{
		Key:           key.FullAddress(),
		TotalVersions: len(entries),
		Versions:      entries,
	})
}

// GetHistoryVersion handles GET /boxes/{keyTextUrl}/history/{versionNumber}
func (h *BoxesHandler) GetHistoryVersion(w http.ResponseWriter, r *http.Request) {
	keyTextUrl := chi.URLParam(r, "keyTextUrl")
	key, err := models.FromBase64Url(keyTextUrl)
	if err != nil {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.InvalidBase64, err.Error(), 400))
		return
	}

	if !requirePermission(w, r, "read", key.Box) {
		return
	}

	versionStr := chi.URLParam(r, "versionNumber")
	versionNumber, err := strconv.Atoi(versionStr)
	if err != nil {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.InvalidKeyFormat, "Invalid version number", 400))
		return
	}

	value, found, err := h.storage.GetVersion(key, versionNumber)
	if err != nil {
		middleware.WriteStashLockError(w, r, err)
		return
	}

	if !found {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.KeyNotFound,
			fmt.Sprintf("Version %d not found for key: %s", versionNumber, key.FullAddress()),
			404))
		return
	}

	w.Header().Set("Content-Type", "text/plain; charset=utf-8")
	w.WriteHeader(http.StatusOK)
	w.Write([]byte(value))
}

// GetMetadata handles GET /boxes/{keyTextUrl}/meta
func (h *BoxesHandler) GetMetadata(w http.ResponseWriter, r *http.Request) {
	keyTextUrl := chi.URLParam(r, "keyTextUrl")
	key, err := models.FromBase64Url(keyTextUrl)
	if err != nil {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.InvalidBase64, err.Error(), 400))
		return
	}

	if !requirePermission(w, r, "read", key.Box) {
		return
	}

	metadata, err := h.storage.GetMetadata(key)
	if err != nil {
		middleware.WriteStashLockError(w, r, err)
		return
	}

	if metadata == nil {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.KeyNotFound,
			fmt.Sprintf("Key not found: %s", key.FullAddress()),
			404))
		return
	}

	writeJSON(w, http.StatusOK, metadata)
}

func (h *BoxesHandler) audit(r *http.Request, action, boxKey, outcome, detail string) {
	identity := middleware.GetIdentity(r)
	var keyId, keyName string
	if identity != nil {
		keyId = identity.Id
		keyName = identity.Name
	}
	correlationId := r.Header.Get("X-Correlation-Id")
	h.auditStore.Log(action, boxKey, keyId, keyName, correlationId, outcome, detail)
}

func writeJSON(w http.ResponseWriter, status int, v interface{}) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}
