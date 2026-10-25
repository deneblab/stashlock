package config

import (
	"strings"
	"testing"
)

const validKey = "0123456789abcdef0123456789abcdef"

func TestValidate(t *testing.T) {
	tests := []struct {
		name    string
		cfg     Config
		wantErr string
	}{
		{"no master key (decided by database)", Config{}, ""},
		{"short key", Config{MasterKey: "short"}, "shorter than 32"},
		{"bad admin cidr", Config{MasterKey: validKey, AdminAllowedNetworks: "10.0.0.0/33"}, "invalid CIDR"},
		{"bad proxy cidr", Config{MasterKey: validKey, TrustedProxies: "nope"}, "invalid CIDR"},
		{"valid key", Config{MasterKey: validKey}, ""},
		{"with networks", Config{MasterKey: validKey, AdminAllowedNetworks: "100.64.0.0/10", TrustedProxies: "172.18.0.0/16"}, ""},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			err := tt.cfg.Validate()
			if tt.wantErr == "" {
				if err != nil {
					t.Fatalf("unexpected error: %v", err)
				}
				return
			}
			if err == nil || !strings.Contains(err.Error(), tt.wantErr) {
				t.Fatalf("error = %v, want containing %q", err, tt.wantErr)
			}
		})
	}
}

func TestLoad_ReadsMasterKeyAndTrustedProxies(t *testing.T) {
	t.Setenv("STASHLOCK_MASTER_KEY", " "+validKey+" ")
	t.Setenv("STASHLOCK_TRUSTED_PROXIES", "172.18.0.0/16")
	cfg := Load()
	if cfg.MasterKey != validKey {
		t.Errorf("MasterKey = %q", cfg.MasterKey)
	}
	if got := cfg.TrustedProxyNetworks(); len(got) != 1 || got[0] != "172.18.0.0/16" {
		t.Errorf("TrustedProxyNetworks = %v", got)
	}
}
