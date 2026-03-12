package handlers

import (
	"net/http"
	"time"

	"github.com/deneblab/stashlock-servergo/models"
	"github.com/jmoiron/sqlx"
	"github.com/rs/zerolog/log"

)

type StatsHandler struct {
	db      *sqlx.DB
	version string
}

func NewStatsHandler(db *sqlx.DB, version string) *StatsHandler {
	return &StatsHandler{db: db, version: version}
}

// GetStats handles GET /stats
func (h *StatsHandler) GetStats(w http.ResponseWriter, r *http.Request) {
	if !requireMaster(w, r) {
		return
	}

	nowStr := time.Now().UTC().Format(time.RFC3339Nano)

	var totalKeys, activeKeys, deletedKeys, expiredKeys int
	var totalSizeBytes int64

	if err := h.db.Get(&totalKeys, "SELECT COUNT(*) FROM KeyValues"); err != nil {
		log.Error().Err(err).Msg("Failed to count total keys")
	}
	if err := h.db.Get(&deletedKeys, "SELECT COUNT(*) FROM KeyValues WHERE IsDeleted = 1"); err != nil {
		log.Error().Err(err).Msg("Failed to count deleted keys")
	}
	if err := h.db.Get(&expiredKeys, "SELECT COUNT(*) FROM KeyValues WHERE IsDeleted = 0 AND ExpiresAt IS NOT NULL AND ExpiresAt < ?", nowStr); err != nil {
		log.Error().Err(err).Msg("Failed to count expired keys")
	}
	activeKeys = totalKeys - deletedKeys - expiredKeys

	if err := h.db.Get(&totalSizeBytes, "SELECT COALESCE(SUM(LENGTH(Value)), 0) FROM KeyValues WHERE IsDeleted = 0 AND (ExpiresAt IS NULL OR ExpiresAt >= ?)", nowStr); err != nil {
		log.Error().Err(err).Msg("Failed to sum value sizes")
	}

	// API key counts
	var totalApiKeys, activeApiKeys int
	_ = h.db.Get(&totalApiKeys, "SELECT COUNT(*) FROM ApiKeys")
	_ = h.db.Get(&activeApiKeys, "SELECT COUNT(*) FROM ApiKeys WHERE IsActive = 1")

	// Audit event count
	var totalAuditEvents int64
	_ = h.db.Get(&totalAuditEvents, "SELECT COUNT(*) FROM AuditLogs")

	writeJSON(w, http.StatusOK, models.StatsResponse{
		TotalKeys:        totalKeys,
		ActiveKeys:       activeKeys,
		DeletedKeys:      deletedKeys,
		ExpiredKeys:      expiredKeys,
		TotalSizeBytes:   totalSizeBytes,
		TotalApiKeys:     totalApiKeys,
		ActiveApiKeys:    activeApiKeys,
		TotalAuditEvents: totalAuditEvents,
		ServerVersion:    h.version,
	})
}

