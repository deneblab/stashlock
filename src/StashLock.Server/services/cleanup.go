package services

import (
	"context"
	"time"

	"github.com/jmoiron/sqlx"
	"github.com/rs/zerolog/log"
)

// StartExpiredKeyCleanup launches a background goroutine that soft-deletes expired keys every 5 minutes.
func StartExpiredKeyCleanup(ctx context.Context, db *sqlx.DB) {
	ticker := time.NewTicker(5 * time.Minute)

	go func() {
		for {
			select {
			case <-ctx.Done():
				ticker.Stop()
				log.Info().Msg("Expired key cleanup stopped")
				return
			case <-ticker.C:
				cleanupExpiredKeys(db)
			}
		}
	}()

	log.Info().Msg("Expired key cleanup service started (5 min interval)")
}

func cleanupExpiredKeys(db *sqlx.DB) {
	nowStr := time.Now().UTC().Format(time.RFC3339Nano)

	result, err := db.Exec(
		"UPDATE KeyValues SET IsDeleted = 1, DeletedAt = ? WHERE IsDeleted = 0 AND ExpiresAt IS NOT NULL AND ExpiresAt < ?",
		nowStr, nowStr,
	)
	if err != nil {
		log.Error().Err(err).Msg("Error cleaning up expired keys")
		return
	}

	if count, _ := result.RowsAffected(); count > 0 {
		log.Info().Int64("count", count).Msg("Cleaned up expired keys")
	}
}
