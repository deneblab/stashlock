package store

import (
	"testing"
	"time"

	"github.com/deneblab/stashlock-servergo/models"
)

func TestListFiltered_FiltersPagesAndDeleted(t *testing.T) {
	db := setupTestDB(t)
	s := NewStorageService(db, 1024)
	for _, k := range []string{"app.develop.00001", "app.develop.00002", "app.production.00001", "billing.develop.00001"} {
		if err := s.Set(mustKey(t, k), "v", nil); err != nil {
			t.Fatal(err)
		}
	}
	if err := s.Delete(mustKey(t, "app.develop.00002")); err != nil {
		t.Fatal(err)
	}

	cases := []struct {
		name string
		opts ListOptions
		want int
	}{
		{"all active", ListOptions{}, 3},
		{"with deleted", ListOptions{IncludeDeleted: true}, 4},
		{"box", ListOptions{Box: "app", IncludeDeleted: true}, 3},
		{"tag", ListOptions{Tag: "develop"}, 2},
		{"prefix", ListOptions{Prefix: "app.prod"}, 1},
		{"underscore is literal", ListOptions{Prefix: "app_"}, 0},
	}
	for _, c := range cases {
		page, err := s.ListFiltered(c.opts)
		if err != nil {
			t.Fatalf("%s: %v", c.name, err)
		}
		if page.Total != c.want || len(page.Entries) != c.want {
			t.Errorf("%s: total=%d entries=%d, want %d", c.name, page.Total, len(page.Entries), c.want)
		}
	}

	page, _ := s.ListFiltered(ListOptions{Limit: 2, Offset: 2})
	if page.Total != 3 || len(page.Entries) != 1 {
		t.Errorf("paging: total=%d entries=%d", page.Total, len(page.Entries))
	}
}

func TestRestoreAndRestoreVersion(t *testing.T) {
	db := setupTestDB(t)
	s := NewStorageService(db, 1024)
	k := mustKey(t, "app.develop.00001")
	_ = s.Set(k, "one", nil)
	_ = s.Set(k, "two", nil)

	if err := s.RestoreVersion(k, 1); err != nil {
		t.Fatal(err)
	}
	if v, _ := s.Get(k); v != "one" {
		t.Errorf("after restoring revision 1: %q", v)
	}
	if err := s.RestoreVersion(k, 99); err == nil {
		t.Error("missing revision should fail")
	}

	if err := s.Restore(k); err == nil {
		t.Error("restoring an entry that is not deleted should fail")
	}
	_ = s.Delete(k)
	if err := s.Restore(k); err != nil {
		t.Fatal(err)
	}
	if v, _ := s.Get(k); v != "one" {
		t.Errorf("after restore: %q", v)
	}
}

func TestStats(t *testing.T) {
	db := setupTestDB(t)
	s := NewStorageService(db, 1024)
	_ = s.Set(mustKey(t, "app.develop.00001"), "abc", nil)
	_ = s.Set(mustKey(t, "app.develop.00002"), "de", nil)
	_ = s.Set(mustKey(t, "app.production.00001"), "f", nil)
	_ = s.Delete(mustKey(t, "app.production.00001"))

	st, err := s.Stats()
	if err != nil {
		t.Fatal(err)
	}
	if st.BoxTags != 1 || st.Entries != 2 || st.DeletedEntries != 1 || st.SizeBytes != 5 {
		t.Errorf("stats = %+v", st)
	}
}

func TestNormalizeExpiry(t *testing.T) {
	future := time.Now().AddDate(1, 0, 0)
	if got, err := NormalizeExpiry(future.Format("2006-01-02")); err != nil || got != future.Format("2006-01-02")+"T23:59:59Z" {
		t.Errorf("date: %q %v", got, err)
	}
	if _, err := NormalizeExpiry(future.UTC().Format(time.RFC3339)); err != nil {
		t.Errorf("rfc3339: %v", err)
	}
	for _, bad := range []string{"2020-01-01", "31.12.2099", "tomorrow"} {
		if _, err := NormalizeExpiry(bad); err == nil {
			t.Errorf("%q should be rejected", bad)
		}
	}
}

func TestValidate_RecordsLastUsed(t *testing.T) {
	db := setupTestDB(t)
	s := NewApiKeyStore(db)
	resp, err := s.Create(CreateApiKeyRequest{Name: "ci", Box: "app", Tag: "develop"})
	if err != nil {
		t.Fatal(err)
	}
	if resp.Info.LastUsedAt != nil {
		t.Error("new key should have no LastUsedAt")
	}
	if id, _ := s.Validate(resp.Key); id == nil {
		t.Fatal("key should validate")
	}
	info, _ := s.Get(resp.Info.Id)
	if info.LastUsedAt == nil {
		t.Error("LastUsedAt should be set after use")
	}
}

func TestAuditQueryPage(t *testing.T) {
	db := setupTestDB(t)
	a := NewAuditStore(db)
	a.Log("get", "app.develop.00001", "k1", "ci", "", "ok", "")
	a.Log("set", "app.develop.00001", "k1", "ci", "", "ok", "")
	a.Log("login.failed", "", "", "203.0.113.5", "", "failed", "")

	if _, total, _ := a.QueryPage(AuditFilter{}); total != 3 {
		t.Errorf("all: %d", total)
	}
	if _, total, _ := a.QueryPage(AuditFilter{BoxKey: "app.develop"}); total != 2 {
		t.Errorf("box prefix: %d", total)
	}
	if _, total, _ := a.QueryPage(AuditFilter{Key: "ci", Action: "set"}); total != 1 {
		t.Errorf("key+action: %d", total)
	}
	today := time.Now().UTC().Format("2006-01-02")
	if _, total, _ := a.QueryPage(AuditFilter{From: today, To: today}); total != 3 {
		t.Errorf("today: %d", total)
	}
	if _, total, _ := a.QueryPage(AuditFilter{To: "2020-01-01"}); total != 0 {
		t.Errorf("old range: %d", total)
	}
	if n, _ := a.CountSince("login.failed", time.Now().Add(-time.Hour)); n != 1 {
		t.Errorf("CountSince: %d", n)
	}
}

func TestPurgeDeletedAndIsLive(t *testing.T) {
	db := setupTestDB(t)
	s := NewStorageService(db, 1024)
	old, recent := mustKey(t, "app.develop.00001"), mustKey(t, "app.develop.00002")
	for _, k := range []*models.KeyStore{old, recent} {
		_ = s.Set(k, "a", nil)
		_ = s.Set(k, "b", nil)
		_ = s.Delete(k)
	}
	if live, _ := s.IsLive(old); live {
		t.Error("deleted entry should not be live")
	}
	if _, err := db.Exec("UPDATE KeyValues SET DeletedAt = ? WHERE Key = ?", time.Now().AddDate(0, 0, -40).UTC().Format(time.RFC3339Nano), old.FullAddress()); err != nil {
		t.Fatal(err)
	}

	n, err := s.PurgeDeleted(time.Now().AddDate(0, 0, -30))
	if err != nil || n != 1 {
		t.Fatalf("purged %d, err %v", n, err)
	}
	if meta, _ := s.GetMetadata(old); meta != nil {
		t.Error("old deleted entry should be gone")
	}
	if h, _ := s.GetHistory(old); len(h) != 0 {
		t.Error("history of purged entry should be gone")
	}
	if meta, _ := s.GetMetadata(recent); meta == nil {
		t.Error("recently deleted entry must stay restorable")
	}
}
