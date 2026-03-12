package store

import (
	"database/sql"
	"fmt"
	"strings"
	"time"

	"github.com/deneblab/stashlock-servergo/models"
	"github.com/jmoiron/sqlx"
	"github.com/rs/zerolog/log"
)

// StorageService handles key-value CRUD operations.
type StorageService struct {
	db               *sqlx.DB
	maxValueSizeBytes int
}

func NewStorageService(db *sqlx.DB, maxValueSizeBytes int) *StorageService {
	return &StorageService{db: db, maxValueSizeBytes: maxValueSizeBytes}
}

func (s *StorageService) Set(key *models.KeyStore, value string, expiresAt *string) error {
	if value == "" {
		return models.NewStashLockError(models.ErrorCodes.ValueRequired, "Value cannot be null or empty", 400)
	}

	if len(value) > s.maxValueSizeBytes {
		return models.NewStashLockError(models.ErrorCodes.ValueTooLarge,
			fmt.Sprintf("Value size (%d bytes) exceeds maximum allowed size (%d bytes)", len(value), s.maxValueSizeBytes), 400)
	}

	now := time.Now().UTC().Format(time.RFC3339Nano)
	keyAddress := key.FullAddress()

	var entity models.KeyValueEntity
	err := s.db.Get(&entity, "SELECT * FROM KeyValues WHERE Key = ?", keyAddress)

	if err == sql.ErrNoRows {
		// Insert new
		_, err = s.db.Exec(
			"INSERT INTO KeyValues (Key, Value, CreatedAt, UpdatedAt, IsDeleted, ExpiresAt) VALUES (?, ?, ?, ?, 0, ?)",
			keyAddress, value, now, now, nullStr(expiresAt),
		)
		if err != nil {
			return fmt.Errorf("insert key: %w", err)
		}
	} else if err != nil {
		return fmt.Errorf("query key: %w", err)
	} else {
		// Save history for non-deleted entities before overwriting
		if !entity.IsDeleted {
			var maxVersion sql.NullInt64
			err = s.db.Get(&maxVersion, "SELECT MAX(VersionNumber) FROM KeyValueHistory WHERE BoxKey = ?", keyAddress)
			if err != nil {
				return fmt.Errorf("query max version: %w", err)
			}

			nextVersion := 1
			if maxVersion.Valid {
				nextVersion = int(maxVersion.Int64) + 1
			}

			_, err = s.db.Exec(
				"INSERT INTO KeyValueHistory (BoxKey, Value, VersionNumber, SavedAt) VALUES (?, ?, ?, ?)",
				keyAddress, entity.Value, nextVersion, now,
			)
			if err != nil {
				return fmt.Errorf("insert history: %w", err)
			}
		}

		// Update existing (or restore soft-deleted)
		if entity.IsDeleted {
			_, err = s.db.Exec(
				"UPDATE KeyValues SET Value = ?, UpdatedAt = ?, IsDeleted = 0, DeletedAt = NULL, ExpiresAt = ? WHERE Key = ?",
				value, now, nullStr(expiresAt), keyAddress,
			)
		} else {
			_, err = s.db.Exec(
				"UPDATE KeyValues SET Value = ?, UpdatedAt = ?, ExpiresAt = ? WHERE Key = ?",
				value, now, nullStr(expiresAt), keyAddress,
			)
		}
		if err != nil {
			return fmt.Errorf("update key: %w", err)
		}
	}

	log.Info().Str("key", keyAddress).Msg("Stored value")
	return nil
}

func (s *StorageService) Get(key *models.KeyStore) (string, error) {
	keyAddress := key.FullAddress()

	var entity models.KeyValueEntity
	err := s.db.Get(&entity, "SELECT * FROM KeyValues WHERE Key = ?", keyAddress)
	if err == sql.ErrNoRows {
		return "", nil
	}
	if err != nil {
		return "", fmt.Errorf("query key: %w", err)
	}

	if entity.IsDeleted || isExpired(entity) {
		return "", nil
	}

	log.Info().Str("key", keyAddress).Msg("Retrieved value")
	return entity.Value, nil
}

func (s *StorageService) Delete(key *models.KeyStore) error {
	keyAddress := key.FullAddress()

	var entity models.KeyValueEntity
	err := s.db.Get(&entity, "SELECT * FROM KeyValues WHERE Key = ?", keyAddress)
	if err == sql.ErrNoRows {
		return models.NewStashLockError(models.ErrorCodes.KeyNotFound,
			fmt.Sprintf("Key not found: %s", keyAddress), 404)
	}
	if err != nil {
		return fmt.Errorf("query key: %w", err)
	}

	if entity.IsDeleted {
		return models.NewStashLockError(models.ErrorCodes.AlreadyDeleted,
			fmt.Sprintf("Key already deleted: %s", keyAddress), 409)
	}

	now := time.Now().UTC().Format(time.RFC3339Nano)
	_, err = s.db.Exec(
		"UPDATE KeyValues SET IsDeleted = 1, DeletedAt = ? WHERE Key = ?",
		now, keyAddress,
	)
	if err != nil {
		return fmt.Errorf("soft-delete key: %w", err)
	}

	log.Info().Str("key", keyAddress).Msg("Soft-deleted key")
	return nil
}

func (s *StorageService) GetMetadata(key *models.KeyStore) (*models.KeyMetadata, error) {
	keyAddress := key.FullAddress()

	var entity models.KeyValueEntity
	err := s.db.Get(&entity, "SELECT * FROM KeyValues WHERE Key = ?", keyAddress)
	if err == sql.ErrNoRows {
		return nil, nil
	}
	if err != nil {
		return nil, fmt.Errorf("query key: %w", err)
	}

	var versionCount int
	err = s.db.Get(&versionCount, "SELECT COUNT(*) FROM KeyValueHistory WHERE BoxKey = ?", keyAddress)
	if err != nil {
		return nil, fmt.Errorf("query history count: %w", err)
	}

	meta := &models.KeyMetadata{
		CreatedAt:    entity.CreatedAt,
		UpdatedAt:    entity.UpdatedAt,
		SizeBytes:    len(entity.Value),
		IsDeleted:    entity.IsDeleted,
		IsExpired:    isExpired(entity),
		VersionCount: versionCount,
	}
	if entity.DeletedAt.Valid {
		meta.DeletedAt = entity.DeletedAt.String
	}
	if entity.ExpiresAt.Valid {
		meta.ExpiresAt = entity.ExpiresAt.String
	}
	return meta, nil
}

func (s *StorageService) ListKeys(prefix *string, limit, offset int) ([]models.KeyEntry, error) {
	var entities []models.KeyValueEntity
	var err error

	if prefix != nil && *prefix != "" {
		escaped := escapeLike(*prefix)
		err = s.db.Select(&entities,
			"SELECT * FROM KeyValues WHERE IsDeleted = 0 AND Key LIKE ? ESCAPE '\\' ORDER BY Key LIMIT ? OFFSET ?",
			escaped+"%", limit, offset,
		)
	} else {
		err = s.db.Select(&entities,
			"SELECT * FROM KeyValues WHERE IsDeleted = 0 ORDER BY Key LIMIT ? OFFSET ?",
			limit, offset,
		)
	}
	if err != nil {
		return nil, fmt.Errorf("list keys: %w", err)
	}

	result := make([]models.KeyEntry, 0, len(entities))
	for _, e := range entities {
		if isExpired(e) {
			continue
		}
		parts := strings.SplitN(e.Key, ".", 3)
		entry := models.KeyEntry{
			Key:       e.Key,
			CreatedAt: e.CreatedAt,
			UpdatedAt: e.UpdatedAt,
			SizeBytes: len(e.Value),
		}
		if len(parts) > 0 {
			entry.Box = parts[0]
		}
		if len(parts) > 1 {
			entry.Tag = parts[1]
		}
		if len(parts) > 2 {
			entry.Version = parts[2]
		}
		if e.ExpiresAt.Valid {
			entry.ExpiresAt = e.ExpiresAt.String
		}
		result = append(result, entry)
	}
	return result, nil
}

func (s *StorageService) GetHistory(key *models.KeyStore) ([]models.HistoryEntry, error) {
	keyAddress := key.FullAddress()

	var entities []models.KeyValueHistoryEntity
	err := s.db.Select(&entities,
		"SELECT * FROM KeyValueHistory WHERE BoxKey = ? ORDER BY VersionNumber DESC",
		keyAddress,
	)
	if err != nil {
		return nil, fmt.Errorf("query history: %w", err)
	}

	result := make([]models.HistoryEntry, 0, len(entities))
	for _, h := range entities {
		result = append(result, models.HistoryEntry{
			VersionNumber: h.VersionNumber,
			SavedAt:       h.SavedAt,
			SizeBytes:     len(h.Value),
		})
	}
	return result, nil
}

func (s *StorageService) GetVersion(key *models.KeyStore, versionNumber int) (string, bool, error) {
	keyAddress := key.FullAddress()

	var entity models.KeyValueHistoryEntity
	err := s.db.Get(&entity,
		"SELECT * FROM KeyValueHistory WHERE BoxKey = ? AND VersionNumber = ?",
		keyAddress, versionNumber,
	)
	if err == sql.ErrNoRows {
		return "", false, nil
	}
	if err != nil {
		return "", false, fmt.Errorf("query version: %w", err)
	}
	return entity.Value, true, nil
}

func isExpired(entity models.KeyValueEntity) bool {
	if !entity.ExpiresAt.Valid || entity.ExpiresAt.String == "" {
		return false
	}
	expiresAt, err := time.Parse(time.RFC3339Nano, entity.ExpiresAt.String)
	if err != nil {
		// Also try RFC3339 (without nanoseconds) for compatibility
		expiresAt, err = time.Parse(time.RFC3339, entity.ExpiresAt.String)
		if err != nil {
			return false
		}
	}
	return expiresAt.Before(time.Now().UTC())
}

// escapeLike escapes SQL LIKE wildcard characters (%, _, \).
func escapeLike(s string) string {
	s = strings.ReplaceAll(s, `\`, `\\`)
	s = strings.ReplaceAll(s, `%`, `\%`)
	s = strings.ReplaceAll(s, `_`, `\_`)
	return s
}

func nullStr(s *string) interface{} {
	if s == nil {
		return nil
	}
	return *s
}
