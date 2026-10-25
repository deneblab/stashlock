package store

import (
	"database/sql"
	"fmt"
	"strings"
	"time"

	"github.com/deneblab/stashlock-servergo/models"
	"github.com/rs/zerolog/log"
)

// ListOptions filters and pages the box listing used by the dashboard.
type ListOptions struct {
	Box            string
	Tag            string
	Prefix         string
	IncludeDeleted bool
	Limit          int
	Offset         int
}

// ListPage is one page of box entries plus the total number of matches.
type ListPage struct {
	Entries []models.KeyEntry
	Total   int
}

// ListFiltered returns box entries ordered by key, honouring box/tag/prefix filters.
// Expired entries are skipped; soft-deleted ones only with IncludeDeleted.
func (s *StorageService) ListFiltered(opts ListOptions) (*ListPage, error) {
	where := []string{"1=1"}
	var args []interface{}
	if !opts.IncludeDeleted {
		where = append(where, "IsDeleted = 0")
	}
	if opts.Box != "" {
		where = append(where, `Key LIKE ? ESCAPE '\'`)
		args = append(args, escapeLike(opts.Box)+".%")
	}
	if opts.Tag != "" {
		where = append(where, `Key LIKE ? ESCAPE '\'`)
		args = append(args, "%."+escapeLike(opts.Tag)+".%")
	}
	if opts.Prefix != "" {
		where = append(where, `Key LIKE ? ESCAPE '\'`)
		args = append(args, escapeLike(opts.Prefix)+"%")
	}
	// Expired-but-not-deleted entries are hidden, as in the API listing
	where = append(where, "(ExpiresAt IS NULL OR ExpiresAt = '' OR ExpiresAt > ? OR IsDeleted = 1)")
	args = append(args, time.Now().UTC().Format(time.RFC3339Nano))
	cond := strings.Join(where, " AND ")

	var total int
	if err := s.db.Get(&total, "SELECT COUNT(*) FROM KeyValues WHERE "+cond, args...); err != nil {
		return nil, fmt.Errorf("count keys: %w", err)
	}

	limit := opts.Limit
	if limit <= 0 || limit > 200 {
		limit = 50
	}
	var entities []models.KeyValueEntity
	err := s.db.Select(&entities, "SELECT * FROM KeyValues WHERE "+cond+" ORDER BY Key LIMIT ? OFFSET ?",
		append(args, limit, opts.Offset)...)
	if err != nil {
		return nil, fmt.Errorf("list keys: %w", err)
	}

	page := &ListPage{Entries: make([]models.KeyEntry, 0, len(entities)), Total: total}
	for _, e := range entities {
		entry := models.KeyEntry{
			Key:       e.Key,
			CreatedAt: e.CreatedAt,
			UpdatedAt: e.UpdatedAt,
			SizeBytes: len(e.Value),
			IsDeleted: e.IsDeleted,
		}
		parts := strings.SplitN(e.Key, ".", 3)
		if len(parts) == 3 {
			entry.Box, entry.Tag, entry.Version = parts[0], parts[1], parts[2]
		}
		if e.ExpiresAt.Valid {
			entry.ExpiresAt = e.ExpiresAt.String
		}
		if e.DeletedAt.Valid {
			entry.DeletedAt = e.DeletedAt.String
		}
		page.Entries = append(page.Entries, entry)
	}
	return page, nil
}

// BoxTags returns the distinct box names and tag names in use (for filters and suggestions).
func (s *StorageService) BoxTags() (boxes, tags []string, err error) {
	var keys []string
	if err := s.db.Select(&keys, "SELECT Key FROM KeyValues"); err != nil {
		return nil, nil, fmt.Errorf("list keys: %w", err)
	}
	seenB, seenT := map[string]bool{}, map[string]bool{}
	for _, k := range keys {
		parts := strings.SplitN(k, ".", 3)
		if len(parts) != 3 {
			continue
		}
		if !seenB[parts[0]] {
			seenB[parts[0]] = true
			boxes = append(boxes, parts[0])
		}
		if !seenT[parts[1]] {
			seenT[parts[1]] = true
			tags = append(tags, parts[1])
		}
	}
	return boxes, tags, nil
}

// Restore undoes a soft delete.
func (s *StorageService) Restore(key *models.KeyStore) error {
	res, err := s.db.Exec("UPDATE KeyValues SET IsDeleted = 0, DeletedAt = NULL WHERE Key = ? AND IsDeleted = 1", key.FullAddress())
	if err != nil {
		return fmt.Errorf("restore key: %w", err)
	}
	if n, _ := res.RowsAffected(); n == 0 {
		return models.NewStashLockError(models.ErrorCodes.KeyNotFound,
			fmt.Sprintf("No deleted entry for %s", key.FullAddress()), 404)
	}
	log.Info().Str("key", key.FullAddress()).Msg("Restored soft-deleted key")
	return nil
}

// RestoreVersion makes a historic value current again (the current value goes to history).
func (s *StorageService) RestoreVersion(key *models.KeyStore, versionNumber int) error {
	value, found, err := s.GetVersion(key, versionNumber)
	if err != nil {
		return err
	}
	if !found {
		return models.NewStashLockError(models.ErrorCodes.KeyNotFound,
			fmt.Sprintf("Version %d not found for %s", versionNumber, key.FullAddress()), 404)
	}
	var expiresAt sql.NullString
	if err := s.db.Get(&expiresAt, "SELECT ExpiresAt FROM KeyValues WHERE Key = ?", key.FullAddress()); err != nil && err != sql.ErrNoRows {
		return fmt.Errorf("query key: %w", err)
	}
	var exp *string
	if expiresAt.Valid && expiresAt.String != "" {
		exp = &expiresAt.String
	}
	return s.Set(key, value, exp)
}

// DashboardStats is what the overview page shows.
type DashboardStats struct {
	BoxTags        int
	Entries        int
	DeletedEntries int
	SizeBytes      int64
	ApiKeysActive  int
	ApiKeysOther   int
	AdminKeys      int
	AuditLast24h   int
}

// Stats computes the overview numbers in SQL (no values loaded into memory).
func (s *StorageService) Stats() (*DashboardStats, error) {
	st := &DashboardStats{}
	now := time.Now().UTC()
	nowStr := now.Format(time.RFC3339Nano)
	active := "IsDeleted = 0 AND (ExpiresAt IS NULL OR ExpiresAt = '' OR ExpiresAt > ?)"
	queries := []struct {
		dst  interface{}
		sql  string
		args []interface{}
	}{
		{&st.Entries, "SELECT COUNT(*) FROM KeyValues WHERE " + active, []interface{}{nowStr}},
		{&st.DeletedEntries, "SELECT COUNT(*) FROM KeyValues WHERE IsDeleted = 1", nil},
		{&st.SizeBytes, "SELECT COALESCE(SUM(length(Value)), 0) FROM KeyValues WHERE " + active, []interface{}{nowStr}},
		{&st.ApiKeysActive, "SELECT COUNT(*) FROM ApiKeys WHERE IsAdmin = 0 AND IsActive = 1 AND (ExpiresAt IS NULL OR ExpiresAt > ?)", []interface{}{now.Format(time.RFC3339)}},
		{&st.ApiKeysOther, "SELECT COUNT(*) FROM ApiKeys WHERE IsAdmin = 0 AND NOT (IsActive = 1 AND (ExpiresAt IS NULL OR ExpiresAt > ?))", []interface{}{now.Format(time.RFC3339)}},
		{&st.AdminKeys, "SELECT COUNT(*) FROM ApiKeys WHERE IsAdmin = 1 AND IsActive = 1", nil},
		{&st.AuditLast24h, "SELECT COUNT(*) FROM AuditLogs WHERE OccurredAt > ?", []interface{}{now.Add(-24 * time.Hour).Format(time.RFC3339Nano)}},
	}
	for _, q := range queries {
		if err := s.db.Get(q.dst, q.sql, q.args...); err != nil {
			return nil, fmt.Errorf("stats: %w", err)
		}
	}
	// Distinct box.tag groups among active entries
	var keys []string
	if err := s.db.Select(&keys, "SELECT Key FROM KeyValues WHERE "+active, nowStr); err != nil {
		return nil, fmt.Errorf("stats: %w", err)
	}
	groups := map[string]bool{}
	for _, k := range keys {
		if i := strings.LastIndex(k, "."); i > 0 {
			groups[k[:i]] = true
		}
	}
	st.BoxTags = len(groups)
	return st, nil
}

// IsLive reports whether the entry exists and is neither deleted nor expired.
func (s *StorageService) IsLive(key *models.KeyStore) (bool, error) {
	var entity models.KeyValueEntity
	err := s.db.Get(&entity, "SELECT * FROM KeyValues WHERE Key = ?", key.FullAddress())
	if err == sql.ErrNoRows {
		return false, nil
	}
	if err != nil {
		return false, fmt.Errorf("query key: %w", err)
	}
	return !entity.IsDeleted && !isExpired(entity), nil
}

// PurgeDeleted permanently removes entries soft-deleted before the cutoff, with their history.
func (s *StorageService) PurgeDeleted(before time.Time) (int64, error) {
	cutoff := before.UTC().Format(time.RFC3339Nano)
	tx, err := s.db.Beginx()
	if err != nil {
		return 0, err
	}
	defer tx.Rollback()
	if _, err := tx.Exec(`DELETE FROM KeyValueHistory WHERE BoxKey IN
		(SELECT Key FROM KeyValues WHERE IsDeleted = 1 AND DeletedAt IS NOT NULL AND DeletedAt < ?)`, cutoff); err != nil {
		return 0, fmt.Errorf("purge history: %w", err)
	}
	res, err := tx.Exec("DELETE FROM KeyValues WHERE IsDeleted = 1 AND DeletedAt IS NOT NULL AND DeletedAt < ?", cutoff)
	if err != nil {
		return 0, fmt.Errorf("purge keys: %w", err)
	}
	n, _ := res.RowsAffected()
	return n, tx.Commit()
}
