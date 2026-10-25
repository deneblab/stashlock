package store

import (
	"strings"
	"testing"

	"github.com/jmoiron/sqlx"
)

func openMemDB(t *testing.T) *sqlx.DB {
	t.Helper()
	db, err := sqlx.Open("sqlite", ":memory:")
	if err != nil {
		t.Fatal(err)
	}
	db.SetMaxOpenConns(1)
	t.Cleanup(func() { db.Close() })
	return db
}

func schemaVersion(t *testing.T, db *sqlx.DB) int {
	t.Helper()
	var v int
	if err := db.Get(&v, "SELECT MAX(Version) FROM SchemaVersion"); err != nil {
		t.Fatal(err)
	}
	return v
}

func TestMigrations_FreshDatabase(t *testing.T) {
	db := openMemDB(t)
	if err := RunMigrations(db); err != nil {
		t.Fatal(err)
	}
	if v := schemaVersion(t, db); v != SchemaVersionLatest {
		t.Errorf("version = %d, want %d", v, SchemaVersionLatest)
	}
	if ok, _ := hasColumn(db, "ApiKeys", "LastUsedAt"); !ok {
		t.Error("LastUsedAt missing")
	}
	// Running again is a no-op
	if err := RunMigrations(db); err != nil {
		t.Fatal(err)
	}
}

func TestMigrations_NewerDatabaseIsRefused(t *testing.T) {
	db := openMemDB(t)
	if err := RunMigrations(db); err != nil {
		t.Fatal(err)
	}
	if _, err := db.Exec("INSERT INTO SchemaVersion (Version, Name, AppliedAt) VALUES (?, 'future', '2026-10-25T00:00:00Z')", SchemaVersionLatest+1); err != nil {
		t.Fatal(err)
	}
	err := RunMigrations(db)
	if err == nil || !strings.Contains(err.Error(), "newer than this server supports") {
		t.Errorf("err = %v", err)
	}
}
