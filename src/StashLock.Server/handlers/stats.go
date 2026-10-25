package handlers

import (
	"net/http"

	"github.com/deneblab/stashlock-servergo/middleware"
	"github.com/deneblab/stashlock-servergo/store"
)

type StatsHandler struct {
	storage *store.StorageService
	version string
}

func NewStatsHandler(storage *store.StorageService, version string) *StatsHandler {
	return &StatsHandler{storage: storage, version: version}
}

// GetStats handles GET /stats
func (h *StatsHandler) GetStats(w http.ResponseWriter, r *http.Request) {
	if !requireMaster(w, r) {
		return
	}
	stats, err := h.storage.ApiStats()
	if err != nil {
		middleware.WriteStashLockError(w, r, err)
		return
	}
	stats.ServerVersion = h.version
	writeJSON(w, http.StatusOK, stats)
}
