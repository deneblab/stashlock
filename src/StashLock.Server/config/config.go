package config

import (
	"encoding/json"
	"fmt"
	"net"
	"os"
	"strconv"
	"strings"
)

// MinMasterKeyLength is the minimum length of the one-time master key.
const MinMasterKeyLength = 32

type Config struct {
	Port                  int    `json:"port"`
	WorkDir               string `json:"workDir"`
	MasterKey             string `json:"masterKey"`
	AllowedOrigins        string `json:"allowedOrigins"`
	MaxValueSizeKB        int    `json:"maxValueSizeKB"`
	MaxKeyLength          int    `json:"maxKeyLength"`
	MaxRequestBodySizeKB  int    `json:"maxRequestBodySizeKB"`
	DefaultRateLimitPerMin int   `json:"defaultRateLimitPerMinute"`
	DeletedRetentionDays  int    `json:"deletedRetentionDays"`
	AuditRetentionDays    int    `json:"auditRetentionDays"`
	AdminAllowedNetworks  string `json:"adminAllowedNetworks"`
	TrustedProxies        string `json:"trustedProxies"`
	LogDir                string `json:"logDir"`
	LogLevel              string `json:"logLevel"`
	Version               string `json:"-"`
}

func (c *Config) MaxValueSizeBytes() int {
	return c.MaxValueSizeKB * 1024
}

func (c *Config) MaxRequestBodySizeBytes() int {
	return c.MaxRequestBodySizeKB * 1024
}

func (c *Config) AllowedOriginsList() []string {
	return splitList(c.AllowedOrigins)
}

func (c *Config) AdminNetworks() []string {
	return splitList(c.AdminAllowedNetworks)
}

func (c *Config) TrustedProxyNetworks() []string {
	return splitList(c.TrustedProxies)
}

// Validate checks settings that must be correct before the server starts.
// Whether an empty master key is acceptable depends on the database (see store.ApiKeyStore.AdminAccessAvailable).
func (c *Config) Validate() error {
	if c.MasterKey != "" && len(c.MasterKey) < MinMasterKeyLength {
		return fmt.Errorf("STASHLOCK_MASTER_KEY is shorter than %d characters", MinMasterKeyLength)
	}
	if c.MaxRequestBodySizeKB < c.MaxValueSizeKB {
		return fmt.Errorf("maxRequestBodySizeKB (%d) must be at least maxValueSizeKB (%d)", c.MaxRequestBodySizeKB, c.MaxValueSizeKB)
	}
	if c.DeletedRetentionDays < 0 {
		return fmt.Errorf("STASHLOCK_DELETED_RETENTION_DAYS must be 0 (keep forever) or more")
	}
	if c.AuditRetentionDays < 0 {
		return fmt.Errorf("STASHLOCK_AUDIT_RETENTION_DAYS must be 0 (keep forever) or more")
	}
	for _, list := range [][]string{c.AdminNetworks(), c.TrustedProxyNetworks()} {
		for _, n := range list {
			if _, _, err := net.ParseCIDR(n); err != nil {
				return fmt.Errorf("invalid CIDR %q: %w", n, err)
			}
		}
	}
	return nil
}

func splitList(v string) []string {
	if v == "" {
		return nil
	}
	parts := strings.Split(v, ",")
	result := make([]string, 0, len(parts))
	for _, p := range parts {
		p = strings.TrimSpace(p)
		if p != "" {
			result = append(result, p)
		}
	}
	return result
}

// BuildVersion is set at compile time via -ldflags "-X ...config.BuildVersion=x.y.z"
var BuildVersion = "0.1.0-dev"

func Load() *Config {
	cfg := &Config{
		Port:                  8080,
		WorkDir:               "./work",
		MaxValueSizeKB:        100,
		MaxKeyLength:          120,
		MaxRequestBodySizeKB:  110,
		DefaultRateLimitPerMin: 60,
		DeletedRetentionDays:  30,
		AuditRetentionDays:    60,
		LogDir:                "./logs",
		LogLevel:              "info",
		Version:               BuildVersion,
	}

	// Try loading from config.json
	if data, err := os.ReadFile("config.json"); err == nil {
		_ = json.Unmarshal(data, cfg)
	}

	// Environment variables override
	if v := os.Getenv("STASHLOCK_PORT"); v != "" {
		if p, err := strconv.Atoi(v); err == nil {
			cfg.Port = p
		}
	}
	if v := os.Getenv("STASHLOCK_WORK_DIR"); v != "" {
		cfg.WorkDir = v
	}
	if v := os.Getenv("STASHLOCK_MASTER_KEY"); v != "" {
		cfg.MasterKey = strings.TrimSpace(v)
	}
	if v := os.Getenv("STASHLOCK_ALLOWED_ORIGINS"); v != "" {
		cfg.AllowedOrigins = v
	}
	if v := os.Getenv("STASHLOCK_MAX_VALUE_SIZE_KB"); v != "" {
		if n, err := strconv.Atoi(v); err == nil {
			cfg.MaxValueSizeKB = n
		}
	}
	if v := os.Getenv("STASHLOCK_DEFAULT_RATE_LIMIT"); v != "" {
		if n, err := strconv.Atoi(v); err == nil {
			cfg.DefaultRateLimitPerMin = n
		}
	}
	if v := os.Getenv("STASHLOCK_MAX_REQUEST_BODY_KB"); v != "" {
		if n, err := strconv.Atoi(v); err == nil {
			cfg.MaxRequestBodySizeKB = n
		}
	}
	if v := os.Getenv("STASHLOCK_AUDIT_RETENTION_DAYS"); v != "" {
		if n, err := strconv.Atoi(v); err == nil {
			cfg.AuditRetentionDays = n
		}
	}
	if v := os.Getenv("STASHLOCK_DELETED_RETENTION_DAYS"); v != "" {
		if n, err := strconv.Atoi(v); err == nil {
			cfg.DeletedRetentionDays = n
		}
	}
	if v := os.Getenv("STASHLOCK_ADMIN_NETWORKS"); v != "" {
		cfg.AdminAllowedNetworks = v
	}
	if v := os.Getenv("STASHLOCK_TRUSTED_PROXIES"); v != "" {
		cfg.TrustedProxies = v
	}
	if v := os.Getenv("STASHLOCK_LOG_DIR"); v != "" {
		cfg.LogDir = v
	}
	if v := os.Getenv("STASHLOCK_LOG_LEVEL"); v != "" {
		cfg.LogLevel = v
	}

	return cfg
}
