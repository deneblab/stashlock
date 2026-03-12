package models

import "database/sql"

// KeyValueEntity maps to the KeyValues table.
type KeyValueEntity struct {
	Key       string         `db:"Key"`
	Value     string         `db:"Value"`
	CreatedAt string         `db:"CreatedAt"`
	UpdatedAt string         `db:"UpdatedAt"`
	IsDeleted bool           `db:"IsDeleted"`
	DeletedAt sql.NullString `db:"DeletedAt"`
	ExpiresAt sql.NullString `db:"ExpiresAt"`
}

// KeyValueHistoryEntity maps to the KeyValueHistory table.
type KeyValueHistoryEntity struct {
	Id            int64  `db:"Id"`
	BoxKey        string `db:"BoxKey"`
	Value         string `db:"Value"`
	VersionNumber int    `db:"VersionNumber"`
	SavedAt       string `db:"SavedAt"`
}

// ApiKeyEntity maps to the ApiKeys table.
type ApiKeyEntity struct {
	Id                 string         `db:"Id"`
	Name               string         `db:"Name"`
	KeyHash            string         `db:"KeyHash"`
	KeyPrefix          string         `db:"KeyPrefix"`
	Scopes             string         `db:"Scopes"`
	CanRead            bool           `db:"CanRead"`
	CanWrite           bool           `db:"CanWrite"`
	CanDelete          bool           `db:"CanDelete"`
	IsActive           bool           `db:"IsActive"`
	ExpiresAt          sql.NullString `db:"ExpiresAt"`
	RateLimitPerMinute sql.NullInt64  `db:"RateLimitPerMinute"`
	CreatedAt          string         `db:"CreatedAt"`
}

// AuditLogEntity maps to the AuditLogs table.
type AuditLogEntity struct {
	Id            int64          `db:"Id"`
	Action        string         `db:"Action"`
	BoxKey        sql.NullString `db:"BoxKey"`
	ApiKeyId      sql.NullString `db:"ApiKeyId"`
	ApiKeyName    sql.NullString `db:"ApiKeyName"`
	CorrelationId sql.NullString `db:"CorrelationId"`
	OccurredAt    string         `db:"OccurredAt"`
	Outcome       string         `db:"Outcome"`
	Detail        sql.NullString `db:"Detail"`
}
