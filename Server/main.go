package main

import (
	"errors"
	"log"
	"net/http"
	"os"
	"time"

	"weixinorc/server/internal/api"
	"weixinorc/server/internal/auth"
	"weixinorc/server/internal/config"
	"weixinorc/server/internal/store"
)

func main() {
	cfg, err := config.Load()
	if err != nil {
		log.Fatal(err)
	}
	database, err := store.Open(cfg.DatabasePath)
	if err != nil {
		log.Fatal(err)
	}
	defer database.Close()

	var verifier auth.Verifier = auth.UnavailableVerifier{}
	if cfg.MockAuth {
		verifier = auth.NewMockVerifier(cfg.MockAccount, cfg.MockPassword)
	}
	sessions := auth.NewSessions(cfg.SessionTTL)
	server := &http.Server{
		Addr:              cfg.Address,
		Handler:           api.New(database, verifier, sessions),
		ReadHeaderTimeout: 5 * time.Second,
		ReadTimeout:       15 * time.Second,
		WriteTimeout:      30 * time.Second,
		IdleTimeout:       60 * time.Second,
	}
	log.Printf("server listening on %s", cfg.Address)
	if err := server.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
		log.Printf("server stopped: %v", err)
		os.Exit(1)
	}
}
