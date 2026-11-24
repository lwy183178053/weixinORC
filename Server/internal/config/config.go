package config

import (
	"fmt"
	"net"
	"os"
	"strconv"
	"strings"
	"time"
)

type Config struct {
	Address      string
	DatabasePath string
	SessionTTL   time.Duration
	MockAuth     bool
	MockAccount  string
	MockPassword string
}

func Load() (Config, error) {
	cfg := Config{
		Address:      envOr("SERVER_ADDR", "127.0.0.1:8080"),
		DatabasePath: envOr("DATABASE_PATH", "data/weixinorc.db"),
		SessionTTL:   7 * 24 * time.Hour,
		MockAuth:     strings.EqualFold(os.Getenv("MOCK_AUTH"), "1") || strings.EqualFold(os.Getenv("MOCK_AUTH"), "true"),
		MockAccount:  os.Getenv("MOCK_ACCOUNT"),
		MockPassword: os.Getenv("MOCK_PASSWORD"),
	}

	if raw := os.Getenv("SESSION_TTL_HOURS"); raw != "" {
		hours, err := strconv.Atoi(raw)
		if err != nil || hours < 1 || hours > 24*30 {
			return Config{}, fmt.Errorf("SESSION_TTL_HOURS must be between 1 and 720")
		}
		cfg.SessionTTL = time.Duration(hours) * time.Hour
	}
	if strings.TrimSpace(cfg.Address) == "" || strings.TrimSpace(cfg.DatabasePath) == "" {
		return Config{}, fmt.Errorf("SERVER_ADDR and DATABASE_PATH must not be empty")
	}
	if cfg.MockAuth {
		if cfg.MockAccount == "" || cfg.MockPassword == "" {
			return Config{}, fmt.Errorf("MOCK_ACCOUNT and MOCK_PASSWORD are required when MOCK_AUTH is enabled")
		}
		host, _, err := net.SplitHostPort(cfg.Address)
		if err != nil || (host != "localhost" && net.ParseIP(host) == nil) || (host != "localhost" && !net.ParseIP(host).IsLoopback()) {
			return Config{}, fmt.Errorf("mock authentication may only bind to a loopback address")
		}
	}
	return cfg, nil
}

func envOr(key, fallback string) string {
	if value := os.Getenv(key); value != "" {
		return value
	}
	return fallback
}
