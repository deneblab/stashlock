package store

import (
	"fmt"
	"time"

	"github.com/jmoiron/sqlx"
	"github.com/rs/zerolog/log"
)

// AuditStore handles audit log operations.
type AuditStore struct {
	db *sqlx.DB
}

func NewAuditStore(db *sqlx.DB) *AuditStore {
	return &AuditStore{db: db}
}

// Log writes an audit log entry. Failures are logged but never propagated.
func (s *AuditStore) Log(action, boxKey, apiKeyId, apiKeyName, correlationId, outcome, detail string) {
	now := time.Now().UTC().Format(time.RFC3339Nano)
	_, err := s.db.Exec(
		`INSERT INTO AuditLogs (Action, BoxKey, ApiKeyId, ApiKeyName, CorrelationId, OccurredAt, Outcome, Detail)
		 VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
		action, boxKey, apiKeyId, apiKeyName,
		correlationId, now, outcome, detail,
	)
	if err != nil {
		log.Error().Err(err).
			Str("action", action).
			Str("boxKey", boxKey).
			Str("outcome", outcome).
			Msg("Failed to write audit log entry")
	}
}

// AuditEntry is the JSON representation of an audit log entry.
type AuditEntry struct {
	Id            int64  `json:"id" db:"Id"`
	Action        string `json:"action" db:"Action"`
	BoxKey        string `json:"boxKey,omitempty" db:"BoxKey"`
	ApiKeyId      string `json:"apiKeyId,omitempty" db:"ApiKeyId"`
	ApiKeyName    string `json:"apiKeyName,omitempty" db:"ApiKeyName"`
	CorrelationId string `json:"correlationId,omitempty" db:"CorrelationId"`
	OccurredAt    string `json:"occurredAt" db:"OccurredAt"`
	Outcome       string `json:"outcome" db:"Outcome"`
	Detail        string `json:"detail,omitempty" db:"Detail"`
}

// Query returns audit log entries with optional filters.
func (s *AuditStore) Query(boxKey, action string, limit int) ([]AuditEntry, error) {
	if limit <= 0 {
		limit = 100
	}

	query := "SELECT * FROM AuditLogs WHERE 1=1"
	args := []interface{}{}

	if boxKey != "" {
		query += " AND BoxKey = ?"
		args = append(args, boxKey)
	}
	if action != "" {
		query += " AND Action = ?"
		args = append(args, action)
	}

	query += fmt.Sprintf(" ORDER BY Id DESC LIMIT %d", limit)

	var entries []AuditEntry
	err := s.db.Select(&entries, query, args...)
	if err != nil {
		return nil, fmt.Errorf("query audit logs: %w", err)
	}

	if entries == nil {
		entries = []AuditEntry{}
	}
	return entries, nil
}
