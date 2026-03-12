package config

import (
	"encoding/json"
	"os"
	"strconv"
	"strings"
)

type Config struct {
	Port                  int    `json:"port"`
	WorkDir               string `json:"workDir"`
	ApiKey                string `json:"apiKey"`
	AllowedOrigins        string `json:"allowedOrigins"`
	MaxValueSizeKB        int    `json:"maxValueSizeKB"`
	MaxKeyLength          int    `json:"maxKeyLength"`
	MaxRequestBodySizeKB  int    `json:"maxRequestBodySizeKB"`
	DefaultRateLimitPerMin int   `json:"defaultRateLimitPerMinute"`
	AdminAllowedNetworks  string `json:"adminAllowedNetworks"`
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
	if c.AllowedOrigins == "" {
		return nil
	}
	origins := strings.Split(c.AllowedOrigins, ",")
	result := make([]string, 0, len(origins))
	for _, o := range origins {
		o = strings.TrimSpace(o)
		if o != "" {
			result = append(result, o)
		}
	}
	return result
}

func (c *Config) AdminNetworks() []string {
	if c.AdminAllowedNetworks == "" {
		return nil
	}
	nets := strings.Split(c.AdminAllowedNetworks, ",")
	result := make([]string, 0, len(nets))
	for _, n := range nets {
		n = strings.TrimSpace(n)
		if n != "" {
			result = append(result, n)
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
	if v := os.Getenv("STASHLOCK_API_KEY"); v != "" {
		cfg.ApiKey = v
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
	if v := os.Getenv("STASHLOCK_ADMIN_NETWORKS"); v != "" {
		cfg.AdminAllowedNetworks = v
	}
	if v := os.Getenv("STASHLOCK_LOG_DIR"); v != "" {
		cfg.LogDir = v
	}
	if v := os.Getenv("STASHLOCK_LOG_LEVEL"); v != "" {
		cfg.LogLevel = v
	}

	return cfg
}
