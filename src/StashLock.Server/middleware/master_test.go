package middleware

import "testing"

func TestMatchMasterKey(t *testing.T) {
	key := "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
	if !MatchMasterKey(key, key) {
		t.Error("expected match")
	}
	for _, bad := range []string{"", "a", key[:31], key + "x"} {
		if MatchMasterKey(key, bad) {
			t.Errorf("MatchMasterKey(%q) unexpectedly matched", bad)
		}
	}
	if MatchMasterKey("", "") {
		t.Error("empty configured key must never match")
	}
}
