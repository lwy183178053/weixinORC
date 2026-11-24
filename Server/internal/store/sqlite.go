package store

import (
	"context"
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

const schemaSQL = `
PRAGMA foreign_keys = ON;
PRAGMA busy_timeout = 5000;

CREATE TABLE IF NOT EXISTS customers (
	id             INTEGER PRIMARY KEY AUTOINCREMENT,
	owner_id       TEXT NOT NULL,
	wechat_remark  TEXT NOT NULL DEFAULT '',
	wechat_name    TEXT NOT NULL DEFAULT '',
	wechat_account TEXT NOT NULL DEFAULT '',
	gender         TEXT NOT NULL DEFAULT '',
	avatar_url     TEXT NOT NULL DEFAULT '',
	description    TEXT NOT NULL DEFAULT '',
	tags           TEXT NOT NULL DEFAULT '',
	follow_status  TEXT NOT NULL DEFAULT '',
	local_key      TEXT NOT NULL DEFAULT '',
	native_id      TEXT NOT NULL DEFAULT '',
	note           TEXT NOT NULL DEFAULT '',
	region         TEXT NOT NULL DEFAULT '',
	phone          TEXT NOT NULL DEFAULT '',
	signature      TEXT NOT NULL DEFAULT '',
	source         TEXT NOT NULL DEFAULT '',
	added_date     TEXT NOT NULL DEFAULT '',
	created_at     TEXT NOT NULL,
	updated_at     TEXT NOT NULL,
	UNIQUE (id, owner_id)
);

CREATE INDEX IF NOT EXISTS customers_owner_wechat_account_idx
	ON customers(owner_id, wechat_account)
	WHERE wechat_account <> '';

CREATE INDEX IF NOT EXISTS customers_owner_id_id
	ON customers(owner_id, id DESC);

CREATE TABLE IF NOT EXISTS messages (
	id          INTEGER PRIMARY KEY AUTOINCREMENT,
	owner_id    TEXT NOT NULL,
	customer_id INTEGER NOT NULL,
	direction   TEXT NOT NULL CHECK (direction IN ('incoming', 'outgoing')),
	content     TEXT NOT NULL,
	sent_at     TEXT NOT NULL,
	created_at  TEXT NOT NULL,
	FOREIGN KEY (customer_id, owner_id) REFERENCES customers(id, owner_id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS messages_owner_customer_id
	ON messages(owner_id, customer_id, id DESC);

CREATE TABLE IF NOT EXISTS groups (
	id           INTEGER PRIMARY KEY AUTOINCREMENT,
	owner_id     TEXT NOT NULL,
	group_name   TEXT NOT NULL DEFAULT '',
	group_key    TEXT NOT NULL DEFAULT '',
	member_count INTEGER NOT NULL DEFAULT 0,
	members_text TEXT NOT NULL DEFAULT '',
	note         TEXT NOT NULL DEFAULT '',
	created_at   TEXT NOT NULL,
	updated_at   TEXT NOT NULL,
	UNIQUE (id, owner_id)
);

CREATE INDEX IF NOT EXISTS groups_owner_id_id
	ON groups(owner_id, id DESC);

CREATE TABLE IF NOT EXISTS group_members (
	id             INTEGER PRIMARY KEY AUTOINCREMENT,
	owner_id       TEXT NOT NULL,
	group_id       INTEGER NOT NULL,
	local_key      TEXT NOT NULL DEFAULT '',
	native_id      TEXT NOT NULL DEFAULT '',
	group_nickname TEXT NOT NULL DEFAULT '',
	wechat_name    TEXT NOT NULL DEFAULT '',
	wechat_account TEXT NOT NULL DEFAULT '',
	role           TEXT NOT NULL DEFAULT '',
	note           TEXT NOT NULL DEFAULT '',
	created_at     TEXT NOT NULL,
	updated_at     TEXT NOT NULL,
	FOREIGN KEY (group_id, owner_id) REFERENCES groups(id, owner_id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS group_members_owner_group_id
	ON group_members(owner_id, group_id, id DESC);

CREATE TABLE IF NOT EXISTS group_messages (
	id          INTEGER PRIMARY KEY AUTOINCREMENT,
	owner_id    TEXT NOT NULL,
	group_id    INTEGER NOT NULL,
	sender_name TEXT NOT NULL DEFAULT '',
	direction   TEXT NOT NULL CHECK (direction IN ('incoming', 'outgoing')),
	content     TEXT NOT NULL,
	sent_at     TEXT NOT NULL,
	created_at  TEXT NOT NULL,
	FOREIGN KEY (group_id, owner_id) REFERENCES groups(id, owner_id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS group_messages_owner_group_id
	ON group_messages(owner_id, group_id, id DESC);

CREATE TABLE IF NOT EXISTS wechat_accounts (
	id                INTEGER PRIMARY KEY AUTOINCREMENT,
	owner_id          TEXT NOT NULL,
	account_type      TEXT NOT NULL CHECK (account_type IN ('personal', 'enterprise')),
	wechat_id         TEXT NOT NULL DEFAULT '',
	nickname          TEXT NOT NULL DEFAULT '',
	status            TEXT NOT NULL DEFAULT 'offline' CHECK (status IN ('online', 'offline', 'disconnected', 'paused')),
	last_heartbeat_at TEXT NOT NULL DEFAULT '',
	created_at        TEXT NOT NULL,
	updated_at        TEXT NOT NULL,
	UNIQUE (owner_id, account_type)
);

CREATE INDEX IF NOT EXISTS wechat_accounts_owner
	ON wechat_accounts(owner_id, id DESC);

CREATE TABLE IF NOT EXISTS audit_logs (
	id          INTEGER PRIMARY KEY AUTOINCREMENT,
	owner_id    TEXT NOT NULL,
	action      TEXT NOT NULL,
	target_type TEXT NOT NULL DEFAULT '',
	target_id   TEXT NOT NULL DEFAULT '',
	detail      TEXT NOT NULL DEFAULT '',
	created_at  TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS audit_logs_owner_id
	ON audit_logs(owner_id, id DESC);
`

type Store struct {
	db      *sql.DB
	dataDir string
}

func Open(path string) (*Store, error) {
	if strings.TrimSpace(path) == "" {
		return nil, fmt.Errorf("database path must not be empty")
	}

	absPath, err := filepath.Abs(path)
	if err != nil {
		return nil, fmt.Errorf("resolve database path: %w", err)
	}
	dataDir := filepath.Dir(absPath)
	if err := os.MkdirAll(dataDir, 0o750); err != nil {
		return nil, fmt.Errorf("create database directory: %w", err)
	}
	if err := os.MkdirAll(filepath.Join(dataDir, "avatars"), 0o750); err != nil {
		return nil, fmt.Errorf("create avatars directory: %w", err)
	}

	dsn := buildSQLiteDSN(absPath)
	db, err := sql.Open("sqlite", dsn)
	if err != nil {
		return nil, fmt.Errorf("open sqlite: %w", err)
	}
	db.SetMaxOpenConns(1)
	db.SetMaxIdleConns(1)

	if _, err := db.Exec(schemaSQL); err != nil {
		db.Close()
		return nil, fmt.Errorf("initialize schema: %w", err)
	}

	if err := migrateCustomerColumns(db); err != nil {
		db.Close()
		return nil, fmt.Errorf("migrate customers table: %w", err)
	}

	return &Store{db: db, dataDir: dataDir}, nil
}

func buildSQLiteDSN(absPath string) string {
	slashPath := filepath.ToSlash(absPath)
	if !strings.HasPrefix(slashPath, "/") {
		slashPath = "/" + slashPath
	}
	u := url.URL{
		Scheme:   "file",
		Path:     slashPath,
		RawQuery: "_pragma=foreign_keys(1)&_pragma=busy_timeout(5000)&_pragma=journal_mode(WAL)&_pragma=synchronous(NORMAL)",
	}
	return u.String()
}

// AvatarDir returns the local directory where uploaded customer avatar files are stored.
func (s *Store) AvatarDir() string {
	return filepath.Join(s.dataDir, "avatars")
}

// migrateCustomerColumns safely adds new profile columns to pre-existing SQLite databases.
func migrateCustomerColumns(db *sql.DB) error {
	extraCols := []string{
		"wechat_remark", "region", "phone", "signature", "source", "added_date",
		"gender", "avatar_url", "description", "tags", "follow_status", "local_key", "native_id",
	}
	for _, col := range extraCols {
		_, err := db.Exec(fmt.Sprintf(`ALTER TABLE customers ADD COLUMN %s TEXT NOT NULL DEFAULT ''`, col))
		if err != nil && !strings.Contains(strings.ToLower(err.Error()), "duplicate column name") {
			return err
		}
	}
	// Replace legacy unique index so explicit user-confirmed duplicates are allowed.
	if _, err := db.Exec(`DROP INDEX IF EXISTS customers_owner_wechat_account`); err != nil {
		return err
	}
	return nil
}

func (s *Store) Close() error {
	return s.db.Close()
}

type rowScanner interface {
	Scan(dest ...any) error
}

func (s *Store) deleteOne(ctx context.Context, query string, args ...any) error {
	res, err := s.db.ExecContext(ctx, query, args...)
	if err != nil {
		return err
	}
	n, err := res.RowsAffected()
	if err != nil {
		return err
	}
	if n == 0 {
		return ErrNotFound
	}
	return nil
}

func listPage[T any](
	ctx context.Context,
	db *sql.DB,
	baseQuery string,
	args []any,
	idCol string,
	cursor int64,
	limit int,
	scan func(rowScanner) (T, error),
	getID func(T) int64,
) ([]T, int64, error) {
	if limit <= 0 {
		limit = 50
	}
	query := baseQuery
	qArgs := make([]any, 0, len(args)+2)
	qArgs = append(qArgs, args...)
	if cursor > 0 {
		query += ` AND ` + idCol + ` < ?`
		qArgs = append(qArgs, cursor)
	}
	query += ` ORDER BY ` + idCol + ` DESC LIMIT ?`
	qArgs = append(qArgs, limit+1)

	rows, err := db.QueryContext(ctx, query, qArgs...)
	if err != nil {
		return nil, 0, err
	}
	defer rows.Close()

	items := make([]T, 0, limit)
	for rows.Next() {
		item, err := scan(rows)
		if err != nil {
			return nil, 0, err
		}
		items = append(items, item)
	}
	if err := rows.Err(); err != nil {
		return nil, 0, err
	}

	var nextCursor int64
	if len(items) > limit {
		items = items[:limit]
		nextCursor = getID(items[len(items)-1])
	}
	return items, nextCursor, nil
}

func (s *Store) customerExists(ctx context.Context, ownerID string, customerID int64) (bool, error) {
	var exists int
	err := s.db.QueryRowContext(
		ctx,
		`SELECT 1 FROM customers WHERE owner_id = ? AND id = ?`,
		ownerID, customerID,
	).Scan(&exists)
	if errors.Is(err, sql.ErrNoRows) {
		return false, nil
	}
	return err == nil, err
}

func (s *Store) groupExists(ctx context.Context, ownerID string, groupID int64) (bool, error) {
	var exists int
	err := s.db.QueryRowContext(
		ctx,
		`SELECT 1 FROM groups WHERE owner_id = ? AND id = ?`,
		ownerID, groupID,
	).Scan(&exists)
	if errors.Is(err, sql.ErrNoRows) {
		return false, nil
	}
	return err == nil, err
}

func isForeignKeyViolation(err error) bool {
	return err != nil && strings.Contains(strings.ToLower(err.Error()), "foreign key constraint failed")
}
