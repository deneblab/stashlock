package store

import (
	"github.com/jmoiron/sqlx"
	"github.com/rs/zerolog/log"
)

var migrations = []string{
	`CREATE TABLE IF NOT EXISTS KeyValues (
		Key       TEXT PRIMARY KEY NOT NULL,
		Value     TEXT NOT NULL,
		CreatedAt TEXT NOT NULL,
		UpdatedAt TEXT NOT NULL,
		IsDeleted INTEGER NOT NULL DEFAULT 0,
		DeletedAt TEXT,
		ExpiresAt TEXT
	)`,
	`CREATE TABLE IF NOT EXISTS KeyValueHistory (
		Id            INTEGER PRIMARY KEY AUTOINCREMENT,
		BoxKey        TEXT NOT NULL,
		Value         TEXT NOT NULL,
		VersionNumber INTEGER NOT NULL,
		SavedAt       TEXT NOT NULL
	)`,
	`CREATE UNIQUE INDEX IF NOT EXISTS IX_KeyValueHistory_BoxKey_Version
		ON KeyValueHistory (BoxKey, VersionNumber)`,
	`CREATE INDEX IF NOT EXISTS IX_KeyValueHistory_BoxKey
		ON KeyValueHistory (BoxKey)`,
	`CREATE TABLE IF NOT EXISTS ApiKeys (
		Id                 TEXT PRIMARY KEY NOT NULL,
		Name               TEXT NOT NULL,
		KeyHash            TEXT NOT NULL,
		KeyPrefix          TEXT NOT NULL,
		Scopes             TEXT NOT NULL DEFAULT '*',
		CanRead            INTEGER NOT NULL DEFAULT 1,
		CanWrite           INTEGER NOT NULL DEFAULT 1,
		CanDelete          INTEGER NOT NULL DEFAULT 0,
		IsActive           INTEGER NOT NULL DEFAULT 1,
		ExpiresAt          TEXT,
		RateLimitPerMinute INTEGER,
		CreatedAt          TEXT NOT NULL
	)`,
	`CREATE UNIQUE INDEX IF NOT EXISTS IX_ApiKeys_KeyHash
		ON ApiKeys (KeyHash)`,
	`CREATE TABLE IF NOT EXISTS AuditLogs (
		Id            INTEGER PRIMARY KEY AUTOINCREMENT,
		Action        TEXT NOT NULL,
		BoxKey        TEXT,
		ApiKeyId      TEXT,
		ApiKeyName    TEXT,
		CorrelationId TEXT,
		OccurredAt    TEXT NOT NULL,
		Outcome       TEXT NOT NULL,
		Detail        TEXT
	)`,
	`CREATE INDEX IF NOT EXISTS IX_AuditLogs_OccurredAt
		ON AuditLogs (OccurredAt)`,
	`CREATE INDEX IF NOT EXISTS IX_AuditLogs_BoxKey
		ON AuditLogs (BoxKey)`,
}

// RunMigrations creates tables and indexes if they don't exist.
func RunMigrations(db *sqlx.DB) error {
	for _, m := range migrations {
		if _, err := db.Exec(m); err != nil {
			log.Error().Err(err).Str("sql", m[:60]).Msg("Migration failed")
			return err
		}
	}
	log.Info().Msg("Database migrations applied")
	return nil
}
