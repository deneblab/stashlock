package handlers

import (
	"encoding/json"
	"fmt"
	"net/http"

	"github.com/deneblab/stashlock-servergo/crypto"
	"github.com/deneblab/stashlock-servergo/middleware"
	"github.com/deneblab/stashlock-servergo/models"
	"github.com/deneblab/stashlock-servergo/store"
)

type DecodeHandler struct {
	storage *store.StorageService
}

func NewDecodeHandler(storage *store.StorageService) *DecodeHandler {
	return &DecodeHandler{storage: storage}
}

// DecodeBox handles POST /boxes/decode
func (h *DecodeHandler) DecodeBox(w http.ResponseWriter, r *http.Request) {
	var req models.DecodeRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.MissingRequiredFields, "Invalid request body", 400))
		return
	}

	if req.Box == "" || req.Tag == "" {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.MissingRequiredFields, "box and tag are required", 400))
		return
	}

	if req.PrivateKey == "" {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.MissingRequiredFields, "privateKey is required", 400))
		return
	}

	version := req.Version
	if version == "" {
		version = "00001"
	}

	key, err := models.NewKeyStore(fmt.Sprintf("%s.%s.%s", req.Box, req.Tag, version))
	if err != nil {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.InvalidKeyFormat, err.Error(), 400))
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
			fmt.Sprintf("Key not found: %s", key.FullAddress()), 404))
		return
	}

	decrypted, err := crypto.OpenSealedBoxBase64(value, req.PrivateKey)
	if err != nil {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.DecryptionFailed,
			"Decryption failed — invalid private key or corrupted data", 400))
		return
	}

	w.Header().Set("Content-Type", "text/plain; charset=utf-8")
	w.WriteHeader(http.StatusOK)
	w.Write(decrypted)
}
