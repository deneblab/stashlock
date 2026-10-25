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

func TestMigrations_UnversionedSL061DatabaseIsUpgradedInPlace(t *testing.T) {
	db := openMemDB(t)
	for _, stmt := range schemaV1 {
		if _, err := db.Exec(stmt); err != nil {
			t.Fatal(err)
		}
	}
	if _, err := db.Exec(`INSERT INTO ApiKeys (Id, Name, KeyHash, KeyPrefix, IsAdmin, CreatedAt) VALUES ('a', 'admin', 'h', 'slk_adm_', 1, '2026-10-25T00:00:00Z')`); err != nil {
		t.Fatal(err)
	}

	if err := RunMigrations(db); err != nil {
		t.Fatal(err)
	}
	if v := schemaVersion(t, db); v != SchemaVersionLatest {
		t.Errorf("version = %d", v)
	}
	if ok, _ := hasColumn(db, "ApiKeys", "LastUsedAt"); !ok {
		t.Error("LastUsedAt should be added")
	}
	var n int
	if err := db.Get(&n, "SELECT COUNT(*) FROM ApiKeys"); err != nil || n != 1 {
		t.Errorf("existing rows lost: n=%d err=%v", n, err)
	}
}

func TestMigrations_UnversionedSL066DatabaseKeepsLastUsedAt(t *testing.T) {
	db := openMemDB(t)
	for _, stmt := range schemaV1 {
		if _, err := db.Exec(stmt); err != nil {
			t.Fatal(err)
		}
	}
	if _, err := db.Exec("ALTER TABLE ApiKeys ADD COLUMN LastUsedAt TEXT"); err != nil {
		t.Fatal(err)
	}
	if err := RunMigrations(db); err != nil {
		t.Fatalf("SL-066 database should migrate: %v", err)
	}
}

func TestMigrations_PreSL061DatabaseIsRefused(t *testing.T) {
	db := openMemDB(t)
	if _, err := db.Exec("CREATE TABLE ApiKeys (Id TEXT PRIMARY KEY, Scopes TEXT NOT NULL DEFAULT '*')"); err != nil {
		t.Fatal(err)
	}
	err := RunMigrations(db)
	if err == nil || !strings.Contains(err.Error(), "before SL-061") {
		t.Errorf("err = %v", err)
	}
}
