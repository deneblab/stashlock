package handlers

import (
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strconv"
	"strings"
	"time"

	"github.com/deneblab/stashlock-servergo/middleware"
	"github.com/deneblab/stashlock-servergo/models"
	"github.com/deneblab/stashlock-servergo/store"
	"github.com/go-chi/chi/v5"
)

// requirePermission checks that the caller is an API key with the given permission on the key's box.tag.
// Master keys administer the server and are always refused here. A nil key checks the permission only.
func requirePermission(w http.ResponseWriter, r *http.Request, permission string, key *models.KeyStore) bool {
	identity := middleware.GetIdentity(r)
	if identity == nil {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.Unauthorized, "Authentication required", 401))
		return false
	}

	if identity.IsMaster {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.Forbidden,
			"Master keys are for administration only; use an API key created for the box.tag", 403))
		return false
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

	if key != nil && !identity.CanAccess(key) {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.Forbidden,
			fmt.Sprintf("API key is not valid for '%s.%s'", key.Box, key.Tag), 403))
		return false
	}

	return true
}

// scopedPrefix narrows a requested list prefix to the identity's box.tag.
// Returns false when the requested prefix lies outside it.
func scopedPrefix(identity *models.ApiKeyIdentity, requested string) (string, bool) {
	own := identity.KeyPrefix()
	switch {
	case requested == "" || strings.HasPrefix(own, requested):
		return own, true
	case strings.HasPrefix(requested, own):
		return requested, true
	default:
		return "", false
	}
}

type BoxesHandler struct {
	storage      *store.StorageService
	auditStore   *store.AuditStore
	version      string
	maxKeyLength int
}

func NewBoxesHandler(storage *store.StorageService, auditStore *store.AuditStore, version string, maxKeyLength int) *BoxesHandler {
	return &BoxesHandler{storage: storage, auditStore: auditStore, version: version, maxKeyLength: maxKeyLength}
}

// requireLive answers 404 when the entry is deleted or expired, so its history stays hidden from API keys.
func (h *BoxesHandler) requireLive(w http.ResponseWriter, r *http.Request, key *models.KeyStore) bool {
	live, err := h.storage.IsLive(key)
	if err != nil {
		middleware.WriteStashLockError(w, r, err)
		return false
	}
	if !live {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.KeyNotFound, fmt.Sprintf("Key not found: %s", key.FullAddress()), 404))
		return false
	}
	return true
}

// GetRoot handles GET /
func (h *BoxesHandler) GetRoot(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "text/plain; charset=utf-8")
	w.WriteHeader(http.StatusOK)
	fmt.Fprintf(w, "OK ; Version: %s", h.version)
}

// ListBoxes handles GET /boxes
func (h *BoxesHandler) ListBoxes(w http.ResponseWriter, r *http.Request) {
	if !requirePermission(w, r, "read", nil) {
		return
	}

	prefix, ok := scopedPrefix(middleware.GetIdentity(r), r.URL.Query().Get("prefix"))
	if !ok {
		writeJSON(w, http.StatusOK, []models.KeyEntry{})
		return
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

	keys, err := h.storage.ListKeys(&prefix, limit, offset)
	if err != nil {
		middleware.WriteStashLockError(w, r, err)
		return
	}

	writeJSON(w, http.StatusOK, keys)
}

// SetBox handles POST /boxes/{keyTextUrl}
func (h *BoxesHandler) SetBox(w http.ResponseWriter, r *http.Request) {
	keyTextUrl := chi.URLParam(r, "keyTextUrl")
	if len(keyTextUrl) > h.maxKeyLength {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.InvalidKeyFormat, fmt.Sprintf("Key exceeds maximum length of %d characters", h.maxKeyLength), 400))
		return
	}

	key, err := models.FromBase64Url(keyTextUrl)
	if err != nil {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.InvalidBase64, err.Error(), 400))
		return
	}

	if !requirePermission(w, r, "write", key) {
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
		middleware.WriteStashLockError(w, r, err)
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

	if !requirePermission(w, r, "read", key) {
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
			fmt.Sprintf("Key not found: %s. Use GET /boxes to list available keys.", key.FullAddress()),
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

	if !requirePermission(w, r, "delete", key) {
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

	if !requirePermission(w, r, "read", key) {
		return
	}
	if !h.requireLive(w, r, key) {
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

	if !requirePermission(w, r, "read", key) {
		return
	}
	if !h.requireLive(w, r, key) {
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

	h.audit(r, "get", key.FullAddress(), "ok", fmt.Sprintf("revision #%d", versionNumber))
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

	if !requirePermission(w, r, "read", key) {
		return
	}
	if !h.requireLive(w, r, key) {
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
