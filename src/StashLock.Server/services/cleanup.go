package services

import (
	"context"
	"time"

	"github.com/deneblab/stashlock-servergo/store"
	"github.com/jmoiron/sqlx"
	"github.com/rs/zerolog/log"
)

// Retention says how long deleted entries and audit entries are kept (days; 0 = forever).
type Retention struct {
	DeletedDays int
	AuditDays   int
}

// StartExpiredKeyCleanup runs every 5 minutes: it soft-deletes expired entries, permanently removes
// entries deleted longer ago than Retention.DeletedDays and audit entries older than Retention.AuditDays.
// It returns when ctx is cancelled.
func StartExpiredKeyCleanup(ctx context.Context, db *sqlx.DB, storage *store.StorageService, audit *store.AuditStore, retention Retention) <-chan struct{} {
	done := make(chan struct{})
	ticker := time.NewTicker(5 * time.Minute)

	go func() {
		defer close(done)
		defer ticker.Stop()
		RunCleanup(db, storage, audit, retention, time.Now())
		for {
			select {
			case <-ctx.Done():
				log.Info().Msg("Expired key cleanup stopped")
				return
			case <-ticker.C:
				RunCleanup(db, storage, audit, retention, time.Now())
			}
		}
	}()

	log.Info().Int("deletedRetentionDays", retention.DeletedDays).Int("auditRetentionDays", retention.AuditDays).
		Msg("Cleanup service started (5 min interval)")
	return done
}

// RunCleanup performs one cleanup pass.
func RunCleanup(db *sqlx.DB, storage *store.StorageService, audit *store.AuditStore, retention Retention, now time.Time) {
	nowStr := now.UTC().Format(time.RFC3339Nano)

	result, err := db.Exec(
		"UPDATE KeyValues SET IsDeleted = 1, DeletedAt = ? WHERE IsDeleted = 0 AND ExpiresAt IS NOT NULL AND ExpiresAt < ?",
		nowStr, nowStr,
	)
	if err != nil {
		log.Error().Err(err).Msg("Error cleaning up expired keys")
	} else if count, _ := result.RowsAffected(); count > 0 {
		log.Info().Int64("count", count).Msg("Soft-deleted expired keys")
	}

	if retention.DeletedDays > 0 {
		purged, err := storage.PurgeDeleted(now.AddDate(0, 0, -retention.DeletedDays))
		if err != nil {
			log.Error().Err(err).Msg("Error purging deleted keys")
		} else if purged > 0 {
			log.Info().Int64("count", purged).Int("retentionDays", retention.DeletedDays).Msg("Permanently removed deleted keys")
		}
	}

	if retention.AuditDays > 0 {
		purged, err := audit.PurgeBefore(now.AddDate(0, 0, -retention.AuditDays))
		if err != nil {
			log.Error().Err(err).Msg("Error purging audit log")
		} else if purged > 0 {
			log.Info().Int64("count", purged).Int("retentionDays", retention.AuditDays).Msg("Removed old audit entries")
		}
	}
}
