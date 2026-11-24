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

func (s *Store) ListWeChatAccounts(ctx context.Context, ownerID string) ([]WeChatAccount, error) {
	rows, err := s.db.QueryContext(ctx, `
		SELECT id, account_type, wechat_id, nickname, status, last_heartbeat_at, created_at, updated_at
		FROM wechat_accounts
		WHERE owner_id = ?
		ORDER BY id DESC`, ownerID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	items := make([]WeChatAccount, 0)
	for rows.Next() {
		var a WeChatAccount
		if err := rows.Scan(&a.ID, &a.AccountType, &a.WechatID, &a.Nickname, &a.Status, &a.LastHeartbeatAt, &a.CreatedAt, &a.UpdatedAt); err != nil {
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

	_, err := s.db.ExecContext(ctx, `
		INSERT INTO wechat_accounts (owner_id, account_type, wechat_id, nickname, status, last_heartbeat_at, created_at, updated_at)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?)
		ON CONFLICT(owner_id, account_type) DO UPDATE SET
			wechat_id = excluded.wechat_id,
			nickname = excluded.nickname,
			status = excluded.status,
			last_heartbeat_at = excluded.last_heartbeat_at,
			updated_at = excluded.updated_at`,
		ownerID, input.AccountType, input.WechatID, input.Nickname, status, now, now, now,
	)
	if err != nil {
		return WeChatAccount{}, err
	}

	var a WeChatAccount
	err = s.db.QueryRowContext(ctx, `
		SELECT id, account_type, wechat_id, nickname, status, last_heartbeat_at, created_at, updated_at
		FROM wechat_accounts
		WHERE owner_id = ? AND account_type = ?`,
		ownerID, input.AccountType,
	).Scan(&a.ID, &a.AccountType, &a.WechatID, &a.Nickname, &a.Status, &a.LastHeartbeatAt, &a.CreatedAt, &a.UpdatedAt)
	return a, err
}

func (s *Store) UpdateWeChatAccount(ctx context.Context, ownerID string, id int64, patch WeChatAccountPatch) (WeChatAccount, error) {
	var a WeChatAccount
	err := s.db.QueryRowContext(ctx, `
		SELECT id, account_type, wechat_id, nickname, status, last_heartbeat_at, created_at, updated_at
		FROM wechat_accounts
		WHERE owner_id = ? AND id = ?`, ownerID, id,
	).Scan(&a.ID, &a.AccountType, &a.WechatID, &a.Nickname, &a.Status, &a.LastHeartbeatAt, &a.CreatedAt, &a.UpdatedAt)
	if errors.Is(err, sql.ErrNoRows) {
		return WeChatAccount{}, ErrNotFound
	}
	if err != nil {
		return WeChatAccount{}, err
	}

	now := time.Now().UTC().Format(time.RFC3339Nano)
	if patch.WechatID != nil {
		a.WechatID = *patch.WechatID
	}
	if patch.Nickname != nil {
		a.Nickname = *patch.Nickname
	}
	if patch.Status != nil {
		a.Status = *patch.Status
	}
	if patch.Heartbeat {
		a.LastHeartbeatAt = now
	}
	a.UpdatedAt = now

	_, err = s.db.ExecContext(ctx, `
		UPDATE wechat_accounts
		SET wechat_id = ?, nickname = ?, status = ?, last_heartbeat_at = ?, updated_at = ?
		WHERE owner_id = ? AND id = ?`,
		a.WechatID, a.Nickname, a.Status, a.LastHeartbeatAt, a.UpdatedAt, ownerID, id,
	)
	return a, err
}

func (s *Store) DeleteWeChatAccount(ctx context.Context, ownerID string, id int64) error {
	res, err := s.db.ExecContext(ctx, `DELETE FROM wechat_accounts WHERE owner_id = ? AND id = ?`, ownerID, id)
	if err != nil {
		return err
	}
	if n, _ := res.RowsAffected(); n == 0 {
		return ErrNotFound
	}
	return nil
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

func (s *Store) ListGroupMembers(ctx context.Context, ownerID string, groupID, cursor int64, limit int) ([]GroupMember, int64, error) {
	ok, err := s.groupExists(ctx, ownerID, groupID)
	if err != nil {
		return nil, 0, err
	}
	if !ok {
		return nil, 0, ErrNotFound
	}

	query := `
		SELECT id, group_id, local_key, native_id, group_nickname, wechat_name, wechat_account, role, note, created_at, updated_at
		FROM group_members
		WHERE owner_id = ? AND group_id = ?`
	args := []any{ownerID, groupID}
	if cursor > 0 {
		query += ` AND id < ?`
		args = append(args, cursor)
	}
	query += ` ORDER BY id DESC LIMIT ?`
	args = append(args, limit+1)

	rows, err := s.db.QueryContext(ctx, query, args...)
	if err != nil {
		return nil, 0, err
	}
	defer rows.Close()

	items := make([]GroupMember, 0, limit)
	for rows.Next() {
		var m GroupMember
		if err := rows.Scan(
			&m.ID, &m.GroupID, &m.LocalKey, &m.NativeID, &m.GroupNickname,
			&m.WechatName, &m.WechatAccount, &m.Role, &m.Note, &m.CreatedAt, &m.UpdatedAt,
		); err != nil {
			return nil, 0, err
		}
		items = append(items, m)
	}
	if err := rows.Err(); err != nil {
		return nil, 0, err
	}

	var nextCursor int64
	if len(items) > limit {
		items = items[:limit]
		nextCursor = items[len(items)-1].ID
	}
	return items, nextCursor, nil
}

func (s *Store) CreateGroupMember(ctx context.Context, ownerID string, groupID int64, input GroupMemberInput) (GroupMember, error) {
	ok, err := s.groupExists(ctx, ownerID, groupID)
	if err != nil {
		return GroupMember{}, err
	}
	if !ok {
		return GroupMember{}, ErrNotFound
	}

	now := time.Now().UTC().Format(time.RFC3339Nano)
	res, err := s.db.ExecContext(ctx, `
		INSERT INTO group_members (owner_id, group_id, local_key, native_id, group_nickname, wechat_name, wechat_account, role, note, created_at, updated_at)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
		ownerID, groupID, input.LocalKey, input.NativeID, input.GroupNickname,
		input.WechatName, input.WechatAccount, input.Role, input.Note, now, now,
	)
	if err != nil {
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
	var m GroupMember
	err := s.db.QueryRowContext(ctx, `
		SELECT id, group_id, local_key, native_id, group_nickname, wechat_name, wechat_account, role, note, created_at, updated_at
		FROM group_members
		WHERE owner_id = ? AND group_id = ? AND id = ?`, ownerID, groupID, memberID,
	).Scan(
		&m.ID, &m.GroupID, &m.LocalKey, &m.NativeID, &m.GroupNickname,
		&m.WechatName, &m.WechatAccount, &m.Role, &m.Note, &m.CreatedAt, &m.UpdatedAt,
	)
	if errors.Is(err, sql.ErrNoRows) {
		return GroupMember{}, ErrNotFound
	}
	if err != nil {
		return GroupMember{}, err
	}

	if patch.LocalKey != nil {
		m.LocalKey = *patch.LocalKey
	}
	if patch.NativeID != nil {
		m.NativeID = *patch.NativeID
	}
	if patch.GroupNickname != nil {
		m.GroupNickname = *patch.GroupNickname
	}
	if patch.WechatName != nil {
		m.WechatName = *patch.WechatName
	}
	if patch.WechatAccount != nil {
		m.WechatAccount = *patch.WechatAccount
	}
	if patch.Role != nil {
		m.Role = *patch.Role
	}
	if patch.Note != nil {
		m.Note = *patch.Note
	}
	m.UpdatedAt = time.Now().UTC().Format(time.RFC3339Nano)

	_, err = s.db.ExecContext(ctx, `
		UPDATE group_members
		SET local_key = ?, native_id = ?, group_nickname = ?, wechat_name = ?, wechat_account = ?, role = ?, note = ?, updated_at = ?
		WHERE owner_id = ? AND group_id = ? AND id = ?`,
		m.LocalKey, m.NativeID, m.GroupNickname, m.WechatName, m.WechatAccount, m.Role, m.Note, m.UpdatedAt, ownerID, groupID, memberID,
	)
	return m, err
}

func (s *Store) DeleteGroupMember(ctx context.Context, ownerID string, groupID, memberID int64) error {
	res, err := s.db.ExecContext(ctx, `DELETE FROM group_members WHERE owner_id = ? AND group_id = ? AND id = ?`, ownerID, groupID, memberID)
	if err != nil {
		return err
	}
	if n, _ := res.RowsAffected(); n == 0 {
		return ErrNotFound
	}
	return nil
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
	query := `SELECT id, action, target_type, target_id, detail, created_at FROM audit_logs WHERE owner_id = ?`
	args := []any{ownerID}
	if cursor > 0 {
		query += ` AND id < ?`
		args = append(args, cursor)
	}
	query += ` ORDER BY id DESC LIMIT ?`
	args = append(args, limit+1)

	rows, err := s.db.QueryContext(ctx, query, args...)
	if err != nil {
		return nil, 0, err
	}
	defer rows.Close()

	items := make([]AuditLog, 0, limit)
	for rows.Next() {
		var l AuditLog
		if err := rows.Scan(&l.ID, &l.Action, &l.TargetType, &l.TargetID, &l.Detail, &l.CreatedAt); err != nil {
			return nil, 0, err
		}
		items = append(items, l)
	}
	if err := rows.Err(); err != nil {
		return nil, 0, err
	}

	var nextCursor int64
	if len(items) > limit {
		items = items[:limit]
		nextCursor = items[len(items)-1].ID
	}
	return items, nextCursor, nil
}
