package models

import (
	"testing"
)

func TestNewKeyStore_ValidKey(t *testing.T) {
	tests := []struct {
		input              string
		box, tag, version  string
		fullAddress        string
	}{
		{"mybox.mytag.00001", "mybox", "mytag", "00001", "mybox.mytag.00001"},
		{"app.config.00002", "app", "config", "00002", "app.config.00002"},
		{"vault.secrets.00001", "vault", "secrets", "00001", "vault.secrets.00001"},
	}
	for _, tt := range tests {
		t.Run(tt.input, func(t *testing.T) {
			ks, err := NewKeyStore(tt.input)
			if err != nil {
				t.Fatalf("NewKeyStore(%q) error: %v", tt.input, err)
			}
			if ks.Box != tt.box {
				t.Errorf("Box = %q, want %q", ks.Box, tt.box)
			}
			if ks.Tag != tt.tag {
				t.Errorf("Tag = %q, want %q", ks.Tag, tt.tag)
			}
			if ks.Version != tt.version {
				t.Errorf("Version = %q, want %q", ks.Version, tt.version)
			}
			if ks.FullAddress() != tt.fullAddress {
				t.Errorf("FullAddress() = %q, want %q", ks.FullAddress(), tt.fullAddress)
			}
		})
	}
}

func TestNewKeyStore_InvalidKey(t *testing.T) {
	tests := []struct {
		name  string
		input string
	}{
		{"empty", ""},
		{"one part", "box"},
		{"two parts", "box.tag"},
		{"four parts", "box.tag.ver.extra"},
		{"empty box", ".tag.ver"},
		{"empty tag", "box..ver"},
		{"empty version", "box.tag."},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			_, err := NewKeyStore(tt.input)
			if err == nil {
				t.Errorf("NewKeyStore(%q) should return error", tt.input)
			}
		})
	}
}

func TestBase64UrlEncode_Decode_RoundTrip(t *testing.T) {
	tests := []string{
		"mybox.mytag.00001",
		"app.config.00002",
		"vault.secrets.00001",
		"test-box.test-tag.v1",
	}
	for _, input := range tests {
		encoded := Base64UrlEncode(input)
		decoded, err := Base64UrlDecode(encoded)
		if err != nil {
			t.Fatalf("Base64UrlDecode(%q) error: %v", encoded, err)
		}
		if decoded != input {
			t.Errorf("round-trip failed: got %q, want %q", decoded, input)
		}
	}
}

func TestFromBase64Url(t *testing.T) {
	// Encode "mybox.mytag.00001" to base64url then parse
	encoded := Base64UrlEncode("mybox.mytag.00001")
	ks, err := FromBase64Url(encoded)
	if err != nil {
		t.Fatalf("FromBase64Url() error: %v", err)
	}
	if ks.Box != "mybox" || ks.Tag != "mytag" || ks.Version != "00001" {
		t.Errorf("unexpected result: box=%q tag=%q version=%q", ks.Box, ks.Tag, ks.Version)
	}
}

func TestBase64UrlEncode_UrlSafe(t *testing.T) {
	// Ensure no +, /, or = characters in output
	encoded := Base64UrlEncode("test+value/with=special")
	for _, ch := range encoded {
		if ch == '+' || ch == '/' || ch == '=' {
			t.Errorf("Base64UrlEncode output contains unsafe char %q: %s", ch, encoded)
		}
	}
}
