package handlers

import (
	"net/http"

	"github.com/jmoiron/sqlx"
)

type HealthHandler struct {
	db *sqlx.DB
}

func NewHealthHandler(db *sqlx.DB) *HealthHandler {
	return &HealthHandler{db: db}
}

// Healthz handles GET /healthz — verifies DB connectivity.
func (h *HealthHandler) Healthz(w http.ResponseWriter, r *http.Request) {
	if err := h.db.Ping(); err != nil {
		w.WriteHeader(http.StatusServiceUnavailable)
		w.Write([]byte("Unhealthy: " + err.Error()))
		return
	}
	w.WriteHeader(http.StatusOK)
	w.Write([]byte("Healthy"))
}
