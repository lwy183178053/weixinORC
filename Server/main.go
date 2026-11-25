package main

import (
	"context"
	"errors"
	"fmt"
	"log"
	"net"
	"net/http"
	"os"
	"os/signal"
	"strconv"
	"strings"
	"syscall"
	"time"

	"weixinorc/server/internal/api"
	"weixinorc/server/internal/auth"
	"weixinorc/server/internal/store"
)

type appConfig struct {
	addr          string
	dbPath        string
	sessionTTL    time.Duration
	authHTTPURL   string
	authHTTPToken string
	mockAuth      bool
	mockAccount   string
	mockPassword  string
}

func loadConfig() (appConfig, error) {
	mockAuthRaw := strings.TrimSpace(os.Getenv("MOCK_AUTH"))
	cfg := appConfig{
		addr:          envOr("SERVER_ADDR", "127.0.0.1:28765"),
		dbPath:        envOr("DATABASE_PATH", "data/weixinorc.db"),
		sessionTTL:    7 * 24 * time.Hour,
		authHTTPURL:   strings.TrimSpace(os.Getenv("AUTH_HTTP_URL")),
		authHTTPToken: strings.TrimSpace(os.Getenv("AUTH_HTTP_TOKEN")),
		mockAuth:      strings.EqualFold(mockAuthRaw, "1") || strings.EqualFold(mockAuthRaw, "true"),
		mockAccount:   strings.TrimSpace(os.Getenv("MOCK_ACCOUNT")),
		mockPassword:  strings.TrimSpace(os.Getenv("MOCK_PASSWORD")),
	}

	if raw := strings.TrimSpace(os.Getenv("SESSION_TTL_HOURS")); raw != "" {
		hours, err := strconv.Atoi(raw)
		if err != nil || hours < 1 || hours > 24*30 {
			return appConfig{}, fmt.Errorf("SESSION_TTL_HOURS must be between 1 and 720")
		}
		cfg.sessionTTL = time.Duration(hours) * time.Hour
	}

	if cfg.authHTTPURL == "" && cfg.mockAuth {
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
	switch {
	case cfg.authHTTPURL != "":
		verifier = auth.NewHTTPVerifier(cfg.authHTTPURL, cfg.authHTTPToken, 10*time.Second)
	case cfg.mockAuth:
		verifier = auth.NewMockVerifier(cfg.mockAccount, cfg.mockPassword)
	}

	srv := &http.Server{
		Addr:              cfg.addr,
		Handler:           api.New(db, verifier, auth.NewSessions(cfg.sessionTTL)),
		ReadHeaderTimeout: 5 * time.Second,
		ReadTimeout:       30 * time.Second,
		WriteTimeout:      150 * time.Second,
		IdleTimeout:       90 * time.Second,
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	go func() {
		<-ctx.Done()
		shutdownCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_ = srv.Shutdown(shutdownCtx)
	}()

	log.Printf("server listening on %s", cfg.addr)
	if err := srv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
		log.Fatalf("server stopped: %v", err)
	}
}
