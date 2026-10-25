package store

import (
	"errors"
	"strings"
	"testing"
)

const testMasterKey = "0123456789abcdef0123456789abcdef"

func TestConsumeMasterKey_OnlyOnce(t *testing.T) {
	db := setupTestDB(t)
	s := NewApiKeyStore(db)

	resp, err := s.ConsumeMasterKey(testMasterKey, "admin")
	if err != nil {
		t.Fatalf("first use: %v", err)
	}
	if !strings.HasPrefix(resp.Key, AdminKeyPrefix) || !resp.Info.IsAdmin {
		t.Errorf("expected admin key, got %q (isAdmin=%v)", resp.Key, resp.Info.IsAdmin)
	}

	identity, err := s.Validate(resp.Key)
	if err != nil || identity == nil || !identity.IsMaster {
		t.Fatalf("admin key should validate as admin: %v %+v", err, identity)
	}

	if _, err := s.ConsumeMasterKey(testMasterKey, "admin"); !errors.Is(err, ErrMasterKeyConsumed) {
		t.Errorf("second use: err = %v, want ErrMasterKeyConsumed", err)
	}
	if consumed, _ := s.IsMasterKeyConsumed(testMasterKey); !consumed {
		t.Error("master key should be marked consumed")
	}
}

func TestAdminAccessAvailable(t *testing.T) {
	db := setupTestDB(t)
	s := NewApiKeyStore(db)

	if ok, _ := s.AdminAccessAvailable(""); ok {
		t.Error("empty database without master key: expected no admin access")
	}
	if ok, _ := s.AdminAccessAvailable(testMasterKey); !ok {
		t.Error("unused master key: expected admin access")
	}
	if _, err := s.ConsumeMasterKey(testMasterKey, "admin"); err != nil {
		t.Fatal(err)
	}
	if ok, _ := s.AdminAccessAvailable(""); !ok {
		t.Error("active admin key: expected admin access")
	}
}

func TestLastAdminCannotBeRemoved(t *testing.T) {
	db := setupTestDB(t)
	s := NewApiKeyStore(db)

	first, err := s.ConsumeMasterKey(testMasterKey, "first")
	if err != nil {
		t.Fatal(err)
	}
	if err := s.Revoke(first.Info.Id); err == nil {
		t.Error("revoking the only admin key should fail")
	}
	if err := s.Delete(first.Info.Id); err == nil {
		t.Error("deleting the only admin key should fail")
	}

	second, err := s.Create(CreateApiKeyRequest{Name: "second", IsAdmin: true})
	if err != nil {
		t.Fatal(err)
	}
	if err := s.Revoke(first.Info.Id); err != nil {
		t.Errorf("revoking one of two admin keys: %v", err)
	}
	if err := s.Delete(second.Info.Id); err == nil {
		t.Error("deleting the last active admin key should fail")
	}
}

func TestCreate_AdminKeyIgnoresBoxTagAndPermissions(t *testing.T) {
	db := setupTestDB(t)
	s := NewApiKeyStore(db)
	yes := true
	resp, err := s.Create(CreateApiKeyRequest{Name: "a", IsAdmin: true, Box: "x", Tag: "y", CanRead: &yes, CanWrite: &yes})
	if err != nil {
		t.Fatal(err)
	}
	if resp.Info.Box != "" || resp.Info.Tag != "" || resp.Info.CanRead || resp.Info.CanWrite {
		t.Errorf("admin key should have no box/tag/permissions: %+v", resp.Info)
	}
}
