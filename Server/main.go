package main

import (
	"errors"
	"fmt"
	"log"
	"net"
	"net/http"
	"os"
	"strconv"
	"strings"
	"time"

	"weixinorc/server/internal/api"
	"weixinorc/server/internal/auth"
	"weixinorc/server/internal/store"
)

type appConfig struct {
	addr         string
	dbPath       string
	sessionTTL   time.Duration
	mockAuth     bool
	mockAccount  string
	mockPassword string
}

func loadConfig() (appConfig, error) {
	cfg := appConfig{
		addr:         envOr("SERVER_ADDR", "127.0.0.1:28765"),
		dbPath:       envOr("DATABASE_PATH", "data/weixinorc.db"),
		sessionTTL:   7 * 24 * time.Hour,
		mockAuth:     strings.EqualFold(os.Getenv("MOCK_AUTH"), "1") || strings.EqualFold(os.Getenv("MOCK_AUTH"), "true"),
		mockAccount:  os.Getenv("MOCK_ACCOUNT"),
		mockPassword: os.Getenv("MOCK_PASSWORD"),
	}

	if raw := os.Getenv("SESSION_TTL_HOURS"); raw != "" {
		hours, err := strconv.Atoi(raw)
		if err != nil || hours < 1 || hours > 24*30 {
			return appConfig{}, fmt.Errorf("SESSION_TTL_HOURS must be between 1 and 720")
		}
		cfg.sessionTTL = time.Duration(hours) * time.Hour
	}

	if cfg.mockAuth {
		if cfg.mockAccount == "" || cfg.mockPassword == "" {
			return appConfig{}, fmt.Errorf("MOCK_ACCOUNT and MOCK_PASSWORD are required when MOCK_AUTH is enabled")
		}
		if !isLoopbackAddr(cfg.addr) {
			return appConfig{}, fmt.Errorf("MOCK_AUTH may only bind to localhost/loopback")
		}
	}

	return cfg, nil
}

func envOr(key, fallback string) string {
	if v := strings.TrimSpace(os.Getenv(key)); v != "" {
		return v
	}
	return fallback
}

func isLoopbackAddr(addr string) bool {
	host, _, err := net.SplitHostPort(addr)
	if err != nil {
		return false
	}
	if strings.EqualFold(host, "localhost") {
		return true
	}
	ip := net.ParseIP(host)
	return ip != nil && ip.IsLoopback()
}

func main() {
	cfg, err := loadConfig()
	if err != nil {
		log.Fatal(err)
	}

	db, err := store.Open(cfg.dbPath)
	if err != nil {
		log.Fatal(err)
	}
	defer db.Close()

	var verifier auth.Verifier
	if cfg.mockAuth {
		verifier = auth.NewMockVerifier(cfg.mockAccount, cfg.mockPassword)
	}

	srv := &http.Server{
		Addr:              cfg.addr,
		Handler:           api.New(db, verifier, auth.NewSessions(cfg.sessionTTL)),
		ReadHeaderTimeout: 5 * time.Second,
		ReadTimeout:       15 * time.Second,
		WriteTimeout:      30 * time.Second,
		IdleTimeout:       60 * time.Second,
	}

	log.Printf("server listening on %s", cfg.addr)
	if err := srv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
		log.Fatalf("server stopped: %v", err)
	}
}
