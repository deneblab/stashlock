package services

import (
	"context"
	"time"

	"github.com/deneblab/stashlock-servergo/store"
	"github.com/jmoiron/sqlx"
	"github.com/rs/zerolog/log"
)

// StartExpiredKeyCleanup runs every 5 minutes: it soft-deletes expired entries and, when
// retentionDays > 0, permanently removes entries deleted more than retentionDays ago.
// It returns when ctx is cancelled.
func StartExpiredKeyCleanup(ctx context.Context, db *sqlx.DB, storage *store.StorageService, retentionDays int) <-chan struct{} {
	done := make(chan struct{})
	ticker := time.NewTicker(5 * time.Minute)

	go func() {
		defer close(done)
		defer ticker.Stop()
		RunCleanup(db, storage, retentionDays, time.Now())
		for {
			select {
			case <-ctx.Done():
				log.Info().Msg("Expired key cleanup stopped")
				return
			case <-ticker.C:
				RunCleanup(db, storage, retentionDays, time.Now())
			}
		}
	}()

	log.Info().Int("deletedRetentionDays", retentionDays).Msg("Expired key cleanup service started (5 min interval)")
	return done
}

// RunCleanup performs one cleanup pass.
func RunCleanup(db *sqlx.DB, storage *store.StorageService, retentionDays int, now time.Time) {
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

	if retentionDays <= 0 {
		return
	}
	purged, err := storage.PurgeDeleted(now.AddDate(0, 0, -retentionDays))
	if err != nil {
		log.Error().Err(err).Msg("Error purging deleted keys")
	} else if purged > 0 {
		log.Info().Int64("count", purged).Int("retentionDays", retentionDays).Msg("Permanently removed deleted keys")
	}
}
