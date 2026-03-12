package handlers

import (
	"net/http"
	"strconv"

	"github.com/deneblab/stashlock-servergo/middleware"
	"github.com/deneblab/stashlock-servergo/models"
	"github.com/deneblab/stashlock-servergo/store"
)

type AuditHandler struct {
	auditStore *store.AuditStore
}

func NewAuditHandler(auditStore *store.AuditStore) *AuditHandler {
	return &AuditHandler{auditStore: auditStore}
}

// GetAuditLogs handles GET /audit
func (h *AuditHandler) GetAuditLogs(w http.ResponseWriter, r *http.Request) {
	if !requireMaster(w, r) {
		return
	}

	boxKey := r.URL.Query().Get("boxKey")
	action := r.URL.Query().Get("action")
	limit := 100
	if l := r.URL.Query().Get("limit"); l != "" {
		if n, err := strconv.Atoi(l); err == nil && n > 0 {
			limit = n
		}
	}

	entries, err := h.auditStore.Query(boxKey, action, limit)
	if err != nil {
		middleware.WriteStashLockError(w, r, models.NewStashLockError(
			models.ErrorCodes.InternalError, "Failed to query audit logs", 500))
		return
	}

	writeJSON(w, http.StatusOK, entries)
}
