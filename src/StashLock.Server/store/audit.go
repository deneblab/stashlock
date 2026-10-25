package store

import (
	"fmt"
	"strings"
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

// AuditFilter selects audit entries for the dashboard.
type AuditFilter struct {
	Action string
	BoxKey string // prefix match, e.g. "exampleapp.develop"
	Key    string // matches ApiKeyName or ApiKeyId prefix
	From   string // YYYY-MM-DD, inclusive (UTC)
	To     string // YYYY-MM-DD, inclusive (UTC)
	Limit  int
	Offset int
}

// QueryPage returns one page of audit entries (newest first) and the total match count.
func (s *AuditStore) QueryPage(f AuditFilter) ([]AuditEntry, int, error) {
	where := []string{"1=1"}
	var args []interface{}
	if f.Action != "" {
		where = append(where, "Action = ?")
		args = append(args, f.Action)
	}
	if f.BoxKey != "" {
		where = append(where, `BoxKey LIKE ? ESCAPE '\'`)
		args = append(args, escapeLike(f.BoxKey)+"%")
	}
	if f.Key != "" {
		where = append(where, `(ApiKeyName LIKE ? ESCAPE '\' OR ApiKeyId LIKE ? ESCAPE '\')`)
		args = append(args, "%"+escapeLike(f.Key)+"%", escapeLike(f.Key)+"%")
	}
	if t, err := time.Parse("2006-01-02", f.From); err == nil {
		where = append(where, "OccurredAt >= ?")
		args = append(args, t.UTC().Format(time.RFC3339Nano))
	}
	if t, err := time.Parse("2006-01-02", f.To); err == nil {
		where = append(where, "OccurredAt < ?")
		args = append(args, t.Add(24*time.Hour).UTC().Format(time.RFC3339Nano))
	}
	cond := strings.Join(where, " AND ")

	var total int
	if err := s.db.Get(&total, "SELECT COUNT(*) FROM AuditLogs WHERE "+cond, args...); err != nil {
		return nil, 0, fmt.Errorf("count audit logs: %w", err)
	}
	limit := f.Limit
	if limit <= 0 || limit > 200 {
		limit = 50
	}
	entries := []AuditEntry{}
	err := s.db.Select(&entries, "SELECT * FROM AuditLogs WHERE "+cond+" ORDER BY Id DESC LIMIT ? OFFSET ?",
		append(args, limit, f.Offset)...)
	if err != nil {
		return nil, 0, fmt.Errorf("query audit logs: %w", err)
	}
	return entries, total, nil
}

// CountSince counts entries of an action since the given time.
func (s *AuditStore) CountSince(action string, since time.Time) (int, error) {
	var n int
	err := s.db.Get(&n, "SELECT COUNT(*) FROM AuditLogs WHERE Action = ? AND OccurredAt > ?", action, since.UTC().Format(time.RFC3339Nano))
	return n, err
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
