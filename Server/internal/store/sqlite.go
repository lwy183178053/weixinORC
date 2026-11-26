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
	image_url   TEXT NOT NULL DEFAULT '',
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
	my_nickname  TEXT NOT NULL DEFAULT '',
	avatar_url   TEXT NOT NULL DEFAULT '',
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
	wechat_remark  TEXT NOT NULL DEFAULT '',
	wechat_account TEXT NOT NULL DEFAULT '',
	gender         TEXT NOT NULL DEFAULT '',
	avatar_url     TEXT NOT NULL DEFAULT '',
	region         TEXT NOT NULL DEFAULT '',
	phone          TEXT NOT NULL DEFAULT '',
	signature      TEXT NOT NULL DEFAULT '',
	source         TEXT NOT NULL DEFAULT '',
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
	image_url   TEXT NOT NULL DEFAULT '',
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

CREATE TABLE IF NOT EXISTS ai_settings (
	id            INTEGER PRIMARY KEY CHECK (id = 1),
	enabled       INTEGER NOT NULL DEFAULT 0,
	base_url      TEXT NOT NULL DEFAULT '',
	api_key       TEXT NOT NULL DEFAULT '',
	model         TEXT NOT NULL DEFAULT '',
	system_prompt TEXT NOT NULL DEFAULT '',
	temperature   REAL NOT NULL DEFAULT 0.5,
	max_tokens    INTEGER NOT NULL DEFAULT 1500,
	timeout_sec   INTEGER NOT NULL DEFAULT 60,
	updated_at    TEXT NOT NULL DEFAULT ''
);

CREATE TABLE IF NOT EXISTS knowledge_items (
	id             INTEGER PRIMARY KEY AUTOINCREMENT,
	title          TEXT NOT NULL DEFAULT '',
	category       TEXT NOT NULL DEFAULT '通用',
	tags           TEXT NOT NULL DEFAULT '',
	content        TEXT NOT NULL DEFAULT '',
	always_include INTEGER NOT NULL DEFAULT 0,
	created_at     TEXT NOT NULL,
	updated_at     TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS knowledge_items_id_idx
	ON knowledge_items(id DESC);
`

type Store struct {
	db      *sql.DB
	dataDir string
	hasFTS5 bool
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
	if err := os.MkdirAll(filepath.Join(dataDir, "chat_images"), 0o750); err != nil {
		return nil, fmt.Errorf("create chat_images directory: %w", err)
	}
	if err := os.MkdirAll(filepath.Join(dataDir, "chat_audio"), 0o750); err != nil {
		return nil, fmt.Errorf("create chat_audio directory: %w", err)
	}
	if err := os.MkdirAll(filepath.Join(dataDir, "chat_video"), 0o750); err != nil {
		return nil, fmt.Errorf("create chat_video directory: %w", err)
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

	hasFTS5 := initChatFTS5(db)

	return &Store{db: db, dataDir: dataDir, hasFTS5: hasFTS5}, nil
}

// initChatFTS5 creates trigram FTS5 virtual tables and sync triggers for messages and group_messages
// so AI cross-session keyword search stays millisecond-fast even with 100k+ messages.
// Gracefully returns false if FTS5 trigram is unavailable in the underlying SQLite build.
func initChatFTS5(db *sql.DB) bool {
	stmts := []string{
		`CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
			content,
			content='messages',
			content_rowid='id',
			tokenize='trigram'
		)`,
		`CREATE TRIGGER IF NOT EXISTS messages_fts_ai AFTER INSERT ON messages BEGIN
			INSERT INTO messages_fts(rowid, content) VALUES (new.id, new.content);
		END`,
		`CREATE TRIGGER IF NOT EXISTS messages_fts_ad AFTER DELETE ON messages BEGIN
			INSERT INTO messages_fts(messages_fts, rowid, content) VALUES('delete', old.id, old.content);
		END`,
		`CREATE TRIGGER IF NOT EXISTS messages_fts_au AFTER UPDATE OF content ON messages BEGIN
			INSERT INTO messages_fts(messages_fts, rowid, content) VALUES('delete', old.id, old.content);
			INSERT INTO messages_fts(rowid, content) VALUES (new.id, new.content);
		END`,
		`CREATE VIRTUAL TABLE IF NOT EXISTS group_messages_fts USING fts5(
			sender_name,
			content,
			content='group_messages',
			content_rowid='id',
			tokenize='trigram'
		)`,
		`CREATE TRIGGER IF NOT EXISTS group_messages_fts_ai AFTER INSERT ON group_messages BEGIN
			INSERT INTO group_messages_fts(rowid, sender_name, content) VALUES (new.id, new.sender_name, new.content);
		END`,
		`CREATE TRIGGER IF NOT EXISTS group_messages_fts_ad AFTER DELETE ON group_messages BEGIN
			INSERT INTO group_messages_fts(group_messages_fts, rowid, sender_name, content) VALUES('delete', old.id, old.sender_name, old.content);
		END`,
		`CREATE TRIGGER IF NOT EXISTS group_messages_fts_au AFTER UPDATE OF sender_name, content ON group_messages BEGIN
			INSERT INTO group_messages_fts(group_messages_fts, rowid, sender_name, content) VALUES('delete', old.id, old.sender_name, old.content);
			INSERT INTO group_messages_fts(rowid, sender_name, content) VALUES (new.id, new.sender_name, new.content);
		END`,
	}
	for _, stmt := range stmts {
		if _, err := db.Exec(stmt); err != nil {
			return false
		}
	}

	// One-time backfill for pre-existing rows if the FTS5 index is empty while source table has rows
	var srcCount, ftsCount int64
	if err := db.QueryRow(`SELECT COUNT(*) FROM messages`).Scan(&srcCount); err == nil && srcCount > 0 {
		if err := db.QueryRow(`SELECT COUNT(*) FROM messages_fts_docsize`).Scan(&ftsCount); err == nil && ftsCount == 0 {
			_, _ = db.Exec(`INSERT INTO messages_fts(messages_fts) VALUES('rebuild')`)
		}
	}
	srcCount, ftsCount = 0, 0
	if err := db.QueryRow(`SELECT COUNT(*) FROM group_messages`).Scan(&srcCount); err == nil && srcCount > 0 {
		if err := db.QueryRow(`SELECT COUNT(*) FROM group_messages_fts_docsize`).Scan(&ftsCount); err == nil && ftsCount == 0 {
			_, _ = db.Exec(`INSERT INTO group_messages_fts(group_messages_fts) VALUES('rebuild')`)
		}
	}
	return true
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

// ChatImageDir returns the local directory where uploaded chat message image files are stored.
func (s *Store) ChatImageDir() string {
	return filepath.Join(s.dataDir, "chat_images")
}

// ChatAudioDir returns the local directory where uploaded voice message WAV files are stored.
func (s *Store) ChatAudioDir() string {
	return filepath.Join(s.dataDir, "chat_audio")
}

// ChatVideoDir returns the local directory where uploaded chat video MP4/MOV files are stored.
func (s *Store) ChatVideoDir() string {
	return filepath.Join(s.dataDir, "chat_video")
}

// migrateCustomerColumns safely adds new columns to pre-existing SQLite databases.
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
	for _, tbl := range []string{"messages", "group_messages"} {
		_, err := db.Exec(fmt.Sprintf(`ALTER TABLE %s ADD COLUMN image_url TEXT NOT NULL DEFAULT ''`, tbl))
		if err != nil && !strings.Contains(strings.ToLower(err.Error()), "duplicate column name") {
			return err
		}
		_, err = db.Exec(fmt.Sprintf(`ALTER TABLE %s ADD COLUMN audio_url TEXT NOT NULL DEFAULT ''`, tbl))
		if err != nil && !strings.Contains(strings.ToLower(err.Error()), "duplicate column name") {
			return err
		}
		_, err = db.Exec(fmt.Sprintf(`ALTER TABLE %s ADD COLUMN video_url TEXT NOT NULL DEFAULT ''`, tbl))
		if err != nil && !strings.Contains(strings.ToLower(err.Error()), "duplicate column name") {
			return err
		}
		_, err = db.Exec(fmt.Sprintf(`ALTER TABLE %s ADD COLUMN external_id TEXT NOT NULL DEFAULT ''`, tbl))
		if err != nil && !strings.Contains(strings.ToLower(err.Error()), "duplicate column name") {
			return err
		}
	}
	syncIndexes := []string{
		`CREATE INDEX IF NOT EXISTS customers_owner_native_id_idx ON customers(owner_id, native_id) WHERE native_id <> ''`,
		`CREATE INDEX IF NOT EXISTS groups_owner_group_key_idx ON groups(owner_id, group_key) WHERE group_key <> ''`,
		`CREATE INDEX IF NOT EXISTS group_members_owner_group_native_idx ON group_members(owner_id, group_id, native_id) WHERE native_id <> ''`,
		`CREATE INDEX IF NOT EXISTS messages_owner_customer_sent_idx ON messages(owner_id, customer_id, sent_at DESC, id DESC)`,
		`CREATE INDEX IF NOT EXISTS group_messages_owner_group_sent_idx ON group_messages(owner_id, group_id, sent_at DESC, id DESC)`,
		`CREATE UNIQUE INDEX IF NOT EXISTS messages_owner_customer_ext_uidx ON messages(owner_id, customer_id, external_id) WHERE external_id <> ''`,
		`CREATE UNIQUE INDEX IF NOT EXISTS group_messages_owner_group_ext_uidx ON group_messages(owner_id, group_id, external_id) WHERE external_id <> ''`,
	}
	for _, stmt := range syncIndexes {
		if _, err := db.Exec(stmt); err != nil {
			return err
		}
	}
	if _, err := db.Exec(`ALTER TABLE groups ADD COLUMN my_nickname TEXT NOT NULL DEFAULT ''`); err != nil && !strings.Contains(strings.ToLower(err.Error()), "duplicate column name") {
		return err
	}
	if _, err := db.Exec(`ALTER TABLE groups ADD COLUMN avatar_url TEXT NOT NULL DEFAULT ''`); err != nil && !strings.Contains(strings.ToLower(err.Error()), "duplicate column name") {
		return err
	}
	memberExtraCols := []string{"wechat_remark", "gender", "avatar_url", "region", "phone", "signature", "source"}
	for _, col := range memberExtraCols {
		_, err := db.Exec(fmt.Sprintf(`ALTER TABLE group_members ADD COLUMN %s TEXT NOT NULL DEFAULT ''`, col))
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
