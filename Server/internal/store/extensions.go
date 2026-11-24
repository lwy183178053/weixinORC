package store

import (
	"context"
	"database/sql"
	"errors"
	"time"
)

// ---- WeChat Accounts (1 employee <-> personal / enterprise WeChat binding) ----

type WeChatAccount struct {
	ID              int64  `json:"id"`
	AccountType     string `json:"account_type"`
	WechatID        string `json:"wechat_id"`
	Nickname        string `json:"nickname"`
	Status          string `json:"status"`
	LastHeartbeatAt string `json:"last_heartbeat_at"`
	CreatedAt       string `json:"created_at"`
	UpdatedAt       string `json:"updated_at"`
}

type WeChatAccountInput struct {
	AccountType string
	WechatID    string
	Nickname    string
	Status      string
}

type WeChatAccountPatch struct {
	WechatID  *string
	Nickname  *string
	Status    *string
	Heartbeat bool
}

const wechatAccountCols = `id, account_type, wechat_id, nickname, status, last_heartbeat_at, created_at, updated_at`

func scanWeChatAccount(sc rowScanner) (WeChatAccount, error) {
	var a WeChatAccount
	err := sc.Scan(&a.ID, &a.AccountType, &a.WechatID, &a.Nickname, &a.Status, &a.LastHeartbeatAt, &a.CreatedAt, &a.UpdatedAt)
	return a, err
}

func (s *Store) ListWeChatAccounts(ctx context.Context, ownerID string) ([]WeChatAccount, error) {
	rows, err := s.db.QueryContext(ctx, `SELECT `+wechatAccountCols+` FROM wechat_accounts WHERE owner_id = ? ORDER BY id DESC`, ownerID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	items := make([]WeChatAccount, 0)
	for rows.Next() {
		a, err := scanWeChatAccount(rows)
		if err != nil {
			return nil, err
		}
		items = append(items, a)
	}
	return items, rows.Err()
}

func (s *Store) UpsertWeChatAccount(ctx context.Context, ownerID string, input WeChatAccountInput) (WeChatAccount, error) {
	now := time.Now().UTC().Format(time.RFC3339Nano)
	status := input.Status
	if status == "" {
		status = "online"
	}

	return scanWeChatAccount(s.db.QueryRowContext(ctx, `
		INSERT INTO wechat_accounts (owner_id, account_type, wechat_id, nickname, status, last_heartbeat_at, created_at, updated_at)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?)
		ON CONFLICT(owner_id, account_type) DO UPDATE SET
			wechat_id = excluded.wechat_id,
			nickname = excluded.nickname,
			status = excluded.status,
			last_heartbeat_at = excluded.last_heartbeat_at,
			updated_at = excluded.updated_at
		RETURNING `+wechatAccountCols,
		ownerID, input.AccountType, input.WechatID, input.Nickname, status, now, now, now,
	))
}

func (s *Store) UpdateWeChatAccount(ctx context.Context, ownerID string, id int64, patch WeChatAccountPatch) (WeChatAccount, error) {
	now := time.Now().UTC().Format(time.RFC3339Nano)
	var heartbeat *string
	if patch.Heartbeat {
		heartbeat = &now
	}

	a, err := scanWeChatAccount(s.db.QueryRowContext(ctx, `
		UPDATE wechat_accounts
		SET wechat_id         = COALESCE(?, wechat_id),
		    nickname          = COALESCE(?, nickname),
		    status            = COALESCE(?, status),
		    last_heartbeat_at = COALESCE(?, last_heartbeat_at),
		    updated_at        = ?
		WHERE owner_id = ? AND id = ?
		RETURNING `+wechatAccountCols,
		patch.WechatID, patch.Nickname, patch.Status, heartbeat, now, ownerID, id,
	))
	if errors.Is(err, sql.ErrNoRows) {
		return WeChatAccount{}, ErrNotFound
	}
	return a, err
}

func (s *Store) DeleteWeChatAccount(ctx context.Context, ownerID string, id int64) error {
	return s.deleteOne(ctx, `DELETE FROM wechat_accounts WHERE owner_id = ? AND id = ?`, ownerID, id)
}

// ---- Group Members ----

type GroupMember struct {
	ID            int64  `json:"id"`
	GroupID       int64  `json:"group_id"`
	LocalKey      string `json:"local_key"`
	NativeID      string `json:"native_id"`
	GroupNickname string `json:"group_nickname"`
	WechatName    string `json:"wechat_name"`
	WechatAccount string `json:"wechat_account"`
	Role          string `json:"role"`
	Note          string `json:"note"`
	CreatedAt     string `json:"created_at"`
	UpdatedAt     string `json:"updated_at"`
}

type GroupMemberInput struct {
	LocalKey      string
	NativeID      string
	GroupNickname string
	WechatName    string
	WechatAccount string
	Role          string
	Note          string
}

type GroupMemberPatch struct {
	LocalKey      *string
	NativeID      *string
	GroupNickname *string
	WechatName    *string
	WechatAccount *string
	Role          *string
	Note          *string
}

const groupMemberCols = `id, group_id, local_key, native_id, group_nickname, wechat_name, wechat_account, role, note, created_at, updated_at`

func scanGroupMember(sc rowScanner) (GroupMember, error) {
	var m GroupMember
	err := sc.Scan(
		&m.ID, &m.GroupID, &m.LocalKey, &m.NativeID, &m.GroupNickname,
		&m.WechatName, &m.WechatAccount, &m.Role, &m.Note, &m.CreatedAt, &m.UpdatedAt,
	)
	return m, err
}

func (s *Store) ListGroupMembers(ctx context.Context, ownerID string, groupID, cursor int64, limit int) ([]GroupMember, int64, error) {
	ok, err := s.groupExists(ctx, ownerID, groupID)
	if err != nil {
		return nil, 0, err
	}
	if !ok {
		return nil, 0, ErrNotFound
	}

	return listPage(
		ctx, s.db,
		`SELECT `+groupMemberCols+` FROM group_members WHERE owner_id = ? AND group_id = ?`,
		[]any{ownerID, groupID},
		"id", cursor, limit,
		scanGroupMember,
		func(m GroupMember) int64 { return m.ID },
	)
}

func (s *Store) CreateGroupMember(ctx context.Context, ownerID string, groupID int64, input GroupMemberInput) (GroupMember, error) {
	now := time.Now().UTC().Format(time.RFC3339Nano)
	res, err := s.db.ExecContext(ctx, `
		INSERT INTO group_members (owner_id, group_id, local_key, native_id, group_nickname, wechat_name, wechat_account, role, note, created_at, updated_at)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
		ownerID, groupID, input.LocalKey, input.NativeID, input.GroupNickname,
		input.WechatName, input.WechatAccount, input.Role, input.Note, now, now,
	)
	if err != nil {
		if isForeignKeyViolation(err) {
			return GroupMember{}, ErrNotFound
		}
		return GroupMember{}, err
	}
	id, err := res.LastInsertId()
	if err != nil {
		return GroupMember{}, err
	}
	return GroupMember{
		ID:            id,
		GroupID:       groupID,
		LocalKey:      input.LocalKey,
		NativeID:      input.NativeID,
		GroupNickname: input.GroupNickname,
		WechatName:    input.WechatName,
		WechatAccount: input.WechatAccount,
		Role:          input.Role,
		Note:          input.Note,
		CreatedAt:     now,
		UpdatedAt:     now,
	}, nil
}

func (s *Store) UpdateGroupMember(ctx context.Context, ownerID string, groupID, memberID int64, patch GroupMemberPatch) (GroupMember, error) {
	now := time.Now().UTC().Format(time.RFC3339Nano)
	m, err := scanGroupMember(s.db.QueryRowContext(ctx, `
		UPDATE group_members
		SET local_key      = COALESCE(?, local_key),
		    native_id      = COALESCE(?, native_id),
		    group_nickname = COALESCE(?, group_nickname),
		    wechat_name    = COALESCE(?, wechat_name),
		    wechat_account = COALESCE(?, wechat_account),
		    role           = COALESCE(?, role),
		    note           = COALESCE(?, note),
		    updated_at     = ?
		WHERE owner_id = ? AND group_id = ? AND id = ?
		RETURNING `+groupMemberCols,
		patch.LocalKey, patch.NativeID, patch.GroupNickname, patch.WechatName, patch.WechatAccount, patch.Role, patch.Note, now, ownerID, groupID, memberID,
	))
	if errors.Is(err, sql.ErrNoRows) {
		return GroupMember{}, ErrNotFound
	}
	return m, err
}

func (s *Store) DeleteGroupMember(ctx context.Context, ownerID string, groupID, memberID int64) error {
	return s.deleteOne(ctx, `DELETE FROM group_members WHERE owner_id = ? AND group_id = ? AND id = ?`, ownerID, groupID, memberID)
}

// ---- Audit Logs ----

type AuditLog struct {
	ID         int64  `json:"id"`
	Action     string `json:"action"`
	TargetType string `json:"target_type"`
	TargetID   string `json:"target_id"`
	Detail     string `json:"detail"`
	CreatedAt  string `json:"created_at"`
}

func (s *Store) CreateAuditLog(ctx context.Context, ownerID, action, targetType, targetID, detail string) (AuditLog, error) {
	now := time.Now().UTC().Format(time.RFC3339Nano)
	res, err := s.db.ExecContext(ctx, `
		INSERT INTO audit_logs (owner_id, action, target_type, target_id, detail, created_at)
		VALUES (?, ?, ?, ?, ?, ?)`,
		ownerID, action, targetType, targetID, detail, now,
	)
	if err != nil {
		return AuditLog{}, err
	}
	id, err := res.LastInsertId()
	if err != nil {
		return AuditLog{}, err
	}
	return AuditLog{
		ID:         id,
		Action:     action,
		TargetType: targetType,
		TargetID:   targetID,
		Detail:     detail,
		CreatedAt:  now,
	}, nil
}

func (s *Store) ListAuditLogs(ctx context.Context, ownerID string, cursor int64, limit int) ([]AuditLog, int64, error) {
	return listPage(
		ctx, s.db,
		`SELECT id, action, target_type, target_id, detail, created_at FROM audit_logs WHERE owner_id = ?`,
		[]any{ownerID},
		"id", cursor, limit,
		func(sc rowScanner) (AuditLog, error) {
			var l AuditLog
			err := sc.Scan(&l.ID, &l.Action, &l.TargetType, &l.TargetID, &l.Detail, &l.CreatedAt)
			return l, err
		},
		func(l AuditLog) int64 { return l.ID },
	)
}
