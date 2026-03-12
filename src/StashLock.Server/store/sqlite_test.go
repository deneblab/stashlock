package store

import (
	"testing"
	"time"

	"github.com/deneblab/stashlock-servergo/models"
	"github.com/jmoiron/sqlx"
	_ "modernc.org/sqlite"
)

func setupTestDB(t *testing.T) *sqlx.DB {
	t.Helper()
	db, err := sqlx.Open("sqlite", ":memory:")
	if err != nil {
		t.Fatalf("open db: %v", err)
	}
	if err := RunMigrations(db); err != nil {
		t.Fatalf("migrations: %v", err)
	}
	t.Cleanup(func() { db.Close() })
	return db
}

func mustKey(t *testing.T, s string) *models.KeyStore {
	t.Helper()
	ks, err := models.NewKeyStore(s)
	if err != nil {
		t.Fatalf("NewKeyStore(%q): %v", s, err)
	}
	return ks
}

func TestSetAndGet_RoundTrip(t *testing.T) {
	db := setupTestDB(t)
	svc := NewStorageService(db, 100*1024)
	key := mustKey(t, "app.config.00001")

	err := svc.Set(key, "hello world", nil)
	if err != nil {
		t.Fatalf("Set: %v", err)
	}

	val, err := svc.Get(key)
	if err != nil {
		t.Fatalf("Get: %v", err)
	}
	if val != "hello world" {
		t.Errorf("Get = %q, want %q", val, "hello world")
	}
}

func TestGet_ReturnsEmptyForMissingKey(t *testing.T) {
	db := setupTestDB(t)
	svc := NewStorageService(db, 100*1024)
	key := mustKey(t, "miss.ing.00001")

	val, err := svc.Get(key)
	if err != nil {
		t.Fatalf("Get: %v", err)
	}
	if val != "" {
		t.Errorf("expected empty, got %q", val)
	}
}

func TestSet_RejectsEmptyValue(t *testing.T) {
	db := setupTestDB(t)
	svc := NewStorageService(db, 100*1024)
	key := mustKey(t, "app.cfg.00001")

	err := svc.Set(key, "", nil)
	if err == nil {
		t.Error("expected error for empty value")
	}
}

func TestSet_RejectsOversizedValue(t *testing.T) {
	db := setupTestDB(t)
	svc := NewStorageService(db, 100) // 100 bytes max
	key := mustKey(t, "app.cfg.00001")

	bigValue := string(make([]byte, 200))
	err := svc.Set(key, bigValue, nil)
	if err == nil {
		t.Error("expected error for oversized value")
	}
}

func TestDelete_SoftDeletesKey(t *testing.T) {
	db := setupTestDB(t)
	svc := NewStorageService(db, 100*1024)
	key := mustKey(t, "app.cfg.00001")

	_ = svc.Set(key, "value", nil)
	err := svc.Delete(key)
	if err != nil {
		t.Fatalf("Delete: %v", err)
	}

	// Get should return empty after delete
	val, _ := svc.Get(key)
	if val != "" {
		t.Errorf("expected empty after delete, got %q", val)
	}
}

func TestDelete_ErrorOnMissingKey(t *testing.T) {
	db := setupTestDB(t)
	svc := NewStorageService(db, 100*1024)
	key := mustKey(t, "miss.ing.00001")

	err := svc.Delete(key)
	if err == nil {
		t.Error("expected error deleting missing key")
	}
}

func TestDelete_ErrorOnAlreadyDeleted(t *testing.T) {
	db := setupTestDB(t)
	svc := NewStorageService(db, 100*1024)
	key := mustKey(t, "app.cfg.00001")

	_ = svc.Set(key, "value", nil)
	_ = svc.Delete(key)

	err := svc.Delete(key)
	if err == nil {
		t.Error("expected error deleting already-deleted key")
	}
}

func TestSet_OverwriteCreatesHistory(t *testing.T) {
	db := setupTestDB(t)
	svc := NewStorageService(db, 100*1024)
	key := mustKey(t, "app.cfg.00001")

	_ = svc.Set(key, "v1", nil)
	_ = svc.Set(key, "v2", nil)

	history, err := svc.GetHistory(key)
	if err != nil {
		t.Fatalf("GetHistory: %v", err)
	}
	if len(history) != 1 {
		t.Fatalf("expected 1 history entry, got %d", len(history))
	}
	if history[0].VersionNumber != 1 {
		t.Errorf("version = %d, want 1", history[0].VersionNumber)
	}

	// Current value should be v2
	val, _ := svc.Get(key)
	if val != "v2" {
		t.Errorf("current value = %q, want %q", val, "v2")
	}
}

func TestGetMetadata(t *testing.T) {
	db := setupTestDB(t)
	svc := NewStorageService(db, 100*1024)
	key := mustKey(t, "app.cfg.00001")

	_ = svc.Set(key, "test data", nil)
	meta, err := svc.GetMetadata(key)
	if err != nil {
		t.Fatalf("GetMetadata: %v", err)
	}
	if meta == nil {
		t.Fatal("metadata is nil")
	}
	if meta.SizeBytes != len("test data") {
		t.Errorf("SizeBytes = %d, want %d", meta.SizeBytes, len("test data"))
	}
	if meta.IsDeleted {
		t.Error("should not be deleted")
	}
	if meta.VersionCount != 0 {
		t.Errorf("VersionCount = %d, want 0", meta.VersionCount)
	}
}

func TestGetMetadata_ReturnsNilForMissing(t *testing.T) {
	db := setupTestDB(t)
	svc := NewStorageService(db, 100*1024)
	key := mustKey(t, "miss.ing.00001")

	meta, err := svc.GetMetadata(key)
	if err != nil {
		t.Fatalf("GetMetadata: %v", err)
	}
	if meta != nil {
		t.Error("expected nil for missing key")
	}
}

func TestListKeys(t *testing.T) {
	db := setupTestDB(t)
	svc := NewStorageService(db, 100*1024)

	_ = svc.Set(mustKey(t, "app.cfg.00001"), "v1", nil)
	_ = svc.Set(mustKey(t, "app.secrets.00001"), "v2", nil)
	_ = svc.Set(mustKey(t, "other.cfg.00001"), "v3", nil)

	// List all
	keys, err := svc.ListKeys(nil, 100, 0)
	if err != nil {
		t.Fatalf("ListKeys: %v", err)
	}
	if len(keys) != 3 {
		t.Errorf("expected 3 keys, got %d", len(keys))
	}

	// List with prefix
	prefix := "app."
	keys, err = svc.ListKeys(&prefix, 100, 0)
	if err != nil {
		t.Fatalf("ListKeys with prefix: %v", err)
	}
	if len(keys) != 2 {
		t.Errorf("expected 2 keys with prefix %q, got %d", prefix, len(keys))
	}
}

func TestExpiredKey_NotReturned(t *testing.T) {
	db := setupTestDB(t)
	svc := NewStorageService(db, 100*1024)
	key := mustKey(t, "app.cfg.00001")

	// Set with already-expired time
	past := time.Now().UTC().Add(-1 * time.Hour).Format(time.RFC3339Nano)
	err := svc.Set(key, "expired data", &past)
	if err != nil {
		t.Fatalf("Set: %v", err)
	}

	val, err := svc.Get(key)
	if err != nil {
		t.Fatalf("Get: %v", err)
	}
	if val != "" {
		t.Errorf("expired key should return empty, got %q", val)
	}
}

func TestSet_RestoresSoftDeleted(t *testing.T) {
	db := setupTestDB(t)
	svc := NewStorageService(db, 100*1024)
	key := mustKey(t, "app.cfg.00001")

	_ = svc.Set(key, "original", nil)
	_ = svc.Delete(key)

	// Re-set should restore
	err := svc.Set(key, "restored", nil)
	if err != nil {
		t.Fatalf("Set after delete: %v", err)
	}

	val, _ := svc.Get(key)
	if val != "restored" {
		t.Errorf("got %q, want %q", val, "restored")
	}
}

func TestGetVersion(t *testing.T) {
	db := setupTestDB(t)
	svc := NewStorageService(db, 100*1024)
	key := mustKey(t, "app.cfg.00001")

	_ = svc.Set(key, "v1", nil)
	_ = svc.Set(key, "v2", nil)
	_ = svc.Set(key, "v3", nil)

	// History should have v1 and v2 (v3 is current)
	val, found, err := svc.GetVersion(key, 1)
	if err != nil {
		t.Fatalf("GetVersion: %v", err)
	}
	if !found {
		t.Fatal("version 1 not found")
	}
	if val != "v1" {
		t.Errorf("version 1 = %q, want %q", val, "v1")
	}
}
