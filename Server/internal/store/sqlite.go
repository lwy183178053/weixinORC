package store

import (
	"database/sql"
	"errors"
	"fmt"
	"net/url"
	"os"
	"path/filepath"
	"strings"

	_ "modernc.org/sqlite"
)

var (
	ErrNotFound = errors.New("not found")
	ErrConflict = errors.New("conflict")
)

type Store struct {
	db *sql.DB
}

func Open(path string) (*Store, error) {
	if path == "" {
		return nil, fmt.Errorf("database path must not be empty")
	}
	absPath, err := filepath.Abs(path)
	if err != nil {
		return nil, fmt.Errorf("resolve database path: %w", err)
	}
	if err := os.MkdirAll(filepath.Dir(absPath), 0750); err != nil {
		return nil, fmt.Errorf("create database directory: %w", err)
	}
	sqlitePath := filepath.ToSlash(absPath)
	if len(sqlitePath) >= 2 && sqlitePath[1] == ':' {
		sqlitePath = "/" + sqlitePath
	}
	dsn := (&url.URL{
		Scheme:   "file",
		Path:     sqlitePath,
		RawQuery: "_pragma=foreign_keys(1)&_pragma=busy_timeout(5000)",
	}).String()
	db, err := sql.Open("sqlite", dsn)
	if err != nil {
		return nil, fmt.Errorf("open sqlite: %w", err)
	}
	db.SetMaxOpenConns(1)
	if err := db.Ping(); err != nil {
		db.Close()
		return nil, fmt.Errorf("connect sqlite: %w", err)
	}
	if _, err := db.Exec(`
CREATE TABLE IF NOT EXISTS customers (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    owner_id TEXT NOT NULL,
    wechat_name TEXT NOT NULL DEFAULT '',
    wechat_account TEXT NOT NULL DEFAULT '',
    note TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE (id, owner_id)
);
CREATE UNIQUE INDEX IF NOT EXISTS customers_owner_wechat_account
    ON customers(owner_id, wechat_account)
    WHERE wechat_account <> '';
CREATE INDEX IF NOT EXISTS customers_owner_id_id
    ON customers(owner_id, id DESC);
CREATE TABLE IF NOT EXISTS messages (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    owner_id TEXT NOT NULL,
    customer_id INTEGER NOT NULL,
    direction TEXT NOT NULL CHECK (direction IN ('incoming', 'outgoing')),
    content TEXT NOT NULL,
    sent_at TEXT NOT NULL,
    created_at TEXT NOT NULL,
    FOREIGN KEY (customer_id, owner_id) REFERENCES customers(id, owner_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS messages_owner_customer_id
    ON messages(owner_id, customer_id, id DESC);`); err != nil {
		db.Close()
		return nil, fmt.Errorf("initialize sqlite schema: %w", err)
	}
	return &Store{db: db}, nil
}

func (s *Store) Close() error { return s.db.Close() }

func isUniqueViolation(err error) bool {
	if err == nil {
		return false
	}
	text := strings.ToLower(err.Error())
	return strings.Contains(text, "unique constraint failed")
}
