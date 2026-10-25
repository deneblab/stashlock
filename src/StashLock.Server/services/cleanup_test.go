package services

import (
	"testing"
	"time"

	"github.com/deneblab/stashlock-servergo/store"
	"github.com/jmoiron/sqlx"
	_ "modernc.org/sqlite"
)

func TestRunCleanup_AuditRetention(t *testing.T) {
	db, err := sqlx.Open("sqlite", ":memory:")
	if err != nil {
		t.Fatal(err)
	}
	db.SetMaxOpenConns(1)
	defer db.Close()
	if err := store.RunMigrations(db); err != nil {
		t.Fatal(err)
	}
	audit := store.NewAuditStore(db)
	storage := store.NewStorageService(db, 1024)

	now := time.Now().UTC()
	insert := func(age time.Duration) {
		if _, err := db.Exec(`INSERT INTO AuditLogs (Action, OccurredAt, Outcome) VALUES ('get', ?, 'ok')`,
			now.Add(-age).Format(time.RFC3339Nano)); err != nil {
			t.Fatal(err)
		}
	}
	insert(400 * 24 * time.Hour)
	insert(100 * 24 * time.Hour)
	insert(time.Hour)

	count := func() int {
		var n int
		_ = db.Get(&n, "SELECT COUNT(*) FROM AuditLogs")
		return n
	}

	RunCleanup(db, storage, audit, Retention{AuditDays: 0}, now)
	if n := count(); n != 3 {
		t.Fatalf("AuditDays=0 must keep everything, have %d", n)
	}
	RunCleanup(db, storage, audit, Retention{AuditDays: 365}, now)
	if n := count(); n != 2 {
		t.Fatalf("365 days: want 2 entries, have %d", n)
	}
	RunCleanup(db, storage, audit, Retention{AuditDays: 30}, now)
	if n := count(); n != 1 {
		t.Fatalf("30 days: want 1 entry, have %d", n)
	}
}
