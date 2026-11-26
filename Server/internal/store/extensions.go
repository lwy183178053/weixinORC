package store

import (
	"context"
	"database/sql"
	"errors"
	"time"
)

// ---- WeChat Accounts (1 employee <-> multiple personal / enterprise WeChat accounts) ----

type WeChatAccount struct {
	ID              int64  `json:"id"`
	AccountType     string `json:"account_type"`
	WechatID        string `json:"wechat_id"`
	Alias           string `json:"alias"`
	Nickname        string `json:"nickname"`
	AvatarURL       string `json:"avatar_url"`
	Region          string `json:"region"`
	Signature       string `json:"signature"`
	Status          string `json:"status"`
	CustomerCount   int    `json:"customer_count"`
	GroupCount      int    `json:"group_count"`
	LastHeartbeatAt string `json:"last_heartbeat_at"`
	CreatedAt       string `json:"created_at"`
	UpdatedAt       string `json:"updated_at"`
}

type WeChatAccountInput struct {
	AccountType string
	WechatID    string
	Alias       string
	Nickname    string
	Region      string
	Signature   string
	Status      string
}

type WeChatAccountPatch struct {
	WechatID  *string
	Alias     *string
	Nickname  *string
	Region    *string
	Signature *string
	Status    *string
	Heartbeat bool
}

const wechatAccountSelectCols = `
	w.id, w.account_type, w.wechat_id, w.alias, w.nickname, w.avatar_url, w.region, w.signature, w.status,
	COALESCE((SELECT COUNT(*) FROM customers c WHERE c.owner_id = w.owner_id AND c.owner_wxid = w.wechat_id), 0),
	COALESCE((SELECT COUNT(*) FROM groups g WHERE g.owner_id = w.owner_id AND g.owner_wxid = w.wechat_id), 0),
	w.last_heartbeat_at, w.created_at, w.updated_at`

func scanWeChatAccount(sc rowScanner) (WeChatAccount, error) {
	var a WeChatAccount
	err := sc.Scan(
		&a.ID, &a.AccountType, &a.WechatID, &a.Alias, &a.Nickname, &a.AvatarURL, &a.Region, &a.Signature, &a.Status,
		&a.CustomerCount, &a.GroupCount, &a.LastHeartbeatAt, &a.CreatedAt, &a.UpdatedAt,
	)
	return a, err
}

func (s *Store) ListWeChatAccounts(ctx context.Context, ownerID string) ([]WeChatAccount, error) {
	rows, err := s.db.QueryContext(ctx,
		`SELECT `+wechatAccountSelectCols+` FROM wechat_accounts w WHERE w.owner_id = ? ORDER BY CASE WHEN w.status = 'online' THEN 0 ELSE 1 END, w.updated_at DESC, w.id DESC`,
		ownerID,
	)
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
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return WeChatAccount{}, err
	}
	defer tx.Rollback()

	now := time.Now().UTC().Format(time.RFC3339Nano)
	status := input.Status
	if status == "" {
		status = "online"
	}

	if status == "online" && input.AccountType == "personal" {
		if _, err := tx.ExecContext(ctx,
			`UPDATE wechat_accounts SET status = 'offline', updated_at = ? WHERE owner_id = ? AND account_type = 'personal' AND wechat_id <> ? AND status = 'online'`,
			now, ownerID, input.WechatID,
		); err != nil {
			return WeChatAccount{}, err
		}
	}

	var id int64
	err = tx.QueryRowContext(ctx, `
		INSERT INTO wechat_accounts (owner_id, account_type, wechat_id, alias, nickname, avatar_url, region, signature, status, last_heartbeat_at, created_at, updated_at)
		VALUES (?, ?, ?, ?, ?, '', ?, ?, ?, ?, ?, ?)
		ON CONFLICT(owner_id, wechat_id) DO UPDATE SET
			account_type      = excluded.account_type,
			alias             = CASE WHEN excluded.alias <> '' THEN excluded.alias ELSE wechat_accounts.alias END,
			nickname          = CASE WHEN excluded.nickname <> '' THEN excluded.nickname ELSE wechat_accounts.nickname END,
			region            = CASE WHEN excluded.region <> '' THEN excluded.region ELSE wechat_accounts.region END,
			signature         = CASE WHEN excluded.signature <> '' THEN excluded.signature ELSE wechat_accounts.signature END,
			status            = excluded.status,
			last_heartbeat_at = excluded.last_heartbeat_at,
			updated_at        = excluded.updated_at
		RETURNING id`,
		ownerID, input.AccountType, input.WechatID, input.Alias, input.Nickname, input.Region, input.Signature, status, now, now, now,
	).Scan(&id)
	if err != nil {
		return WeChatAccount{}, err
	}

	acc, err := scanWeChatAccount(tx.QueryRowContext(ctx, `SELECT `+wechatAccountSelectCols+` FROM wechat_accounts w WHERE w.owner_id = ? AND w.id = ?`, ownerID, id))
	if err != nil {
		return WeChatAccount{}, err
	}
	if err := tx.Commit(); err != nil {
		return WeChatAccount{}, err
	}
	return acc, nil
}

func (s *Store) UpdateWeChatAccount(ctx context.Context, ownerID string, id int64, patch WeChatAccountPatch) (WeChatAccount, error) {
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return WeChatAccount{}, err
	}
	defer tx.Rollback()

	now := time.Now().UTC().Format(time.RFC3339Nano)
	var heartbeat *string
	if patch.Heartbeat {
		heartbeat = &now
	}

	res, err := tx.ExecContext(ctx, `
		UPDATE wechat_accounts
		SET wechat_id         = COALESCE(?, wechat_id),
		    alias             = COALESCE(?, alias),
		    nickname          = COALESCE(?, nickname),
		    region            = COALESCE(?, region),
		    signature         = COALESCE(?, signature),
		    status            = COALESCE(?, status),
		    last_heartbeat_at = COALESCE(?, last_heartbeat_at),
		    updated_at        = ?
		WHERE owner_id = ? AND id = ?`,
		patch.WechatID, patch.Alias, patch.Nickname, patch.Region, patch.Signature, patch.Status, heartbeat, now, ownerID, id,
	)
	if err != nil {
		return WeChatAccount{}, err
	}
	n, err := res.RowsAffected()
	if err != nil {
		return WeChatAccount{}, err
	}
	if n == 0 {
		return WeChatAccount{}, ErrNotFound
	}

	acc, err := scanWeChatAccount(tx.QueryRowContext(ctx, `SELECT `+wechatAccountSelectCols+` FROM wechat_accounts w WHERE w.owner_id = ? AND w.id = ?`, ownerID, id))
	if err != nil {
		return WeChatAccount{}, err
	}
	if err := tx.Commit(); err != nil {
		return WeChatAccount{}, err
	}
	return acc, nil
}

func (s *Store) SetWeChatAccountAvatar(ctx context.Context, ownerID string, id int64, newAvatarURL string) (WeChatAccount, string, error) {
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return WeChatAccount{}, "", err
	}
	defer tx.Rollback()

	var prevURL string
	err = tx.QueryRowContext(ctx, `SELECT avatar_url FROM wechat_accounts WHERE owner_id = ? AND id = ?`, ownerID, id).Scan(&prevURL)
	if errors.Is(err, sql.ErrNoRows) {
		return WeChatAccount{}, "", ErrNotFound
	}
	if err != nil {
		return WeChatAccount{}, "", err
	}

	now := time.Now().UTC().Format(time.RFC3339Nano)
	if _, err := tx.ExecContext(ctx,
		`UPDATE wechat_accounts SET avatar_url = ?, updated_at = ? WHERE owner_id = ? AND id = ?`,
		newAvatarURL, now, ownerID, id,
	); err != nil {
		return WeChatAccount{}, "", err
	}

	acc, err := scanWeChatAccount(tx.QueryRowContext(ctx, `SELECT `+wechatAccountSelectCols+` FROM wechat_accounts w WHERE w.owner_id = ? AND w.id = ?`, ownerID, id))
	if err != nil {
		return WeChatAccount{}, "", err
	}
	if err := tx.Commit(); err != nil {
		return WeChatAccount{}, "", err
	}
	return acc, prevURL, nil
}

func (s *Store) DeleteWeChatAccount(ctx context.Context, ownerID string, id int64) error {
	return s.deleteOne(ctx, `DELETE FROM wechat_accounts WHERE owner_id = ? AND id = ?`, ownerID, id)
}

func (s *Store) DeleteWeChatAccountReturningAvatar(ctx context.Context, ownerID string, id int64) (avatarURL string, err error) {
	err = s.db.QueryRowContext(
		ctx,
		`DELETE FROM wechat_accounts WHERE owner_id = ? AND id = ? RETURNING avatar_url`,
		ownerID, id,
	).Scan(&avatarURL)
	if errors.Is(err, sql.ErrNoRows) {
		return "", ErrNotFound
	}
	return avatarURL, err
}

// ---- Group Members ----

type GroupMember struct {
	ID            int64  `json:"id"`
	GroupID       int64  `json:"group_id"`
	LocalKey      string `json:"local_key"`
	NativeID      string `json:"native_id"`
	GroupNickname string `json:"group_nickname"`
	WechatName    string `json:"wechat_name"`
	WechatRemark  string `json:"wechat_remark"`
	WechatAccount string `json:"wechat_account"`
	Gender        string `json:"gender"`
	AvatarURL     string `json:"avatar_url"`
	Region        string `json:"region"`
	Phone         string `json:"phone"`
	Signature     string `json:"signature"`
	Source        string `json:"source"`
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
	WechatRemark  string
	WechatAccount string
	Gender        string
	AvatarURL     string
	Region        string
	Phone         string
	Signature     string
	Source        string
	Role          string
	Note          string
}

type GroupMemberPatch struct {
	LocalKey      *string
	NativeID      *string
	GroupNickname *string
	WechatName    *string
	WechatRemark  *string
	WechatAccount *string
	Gender        *string
	AvatarURL     *string
	Region        *string
	Phone         *string
	Signature     *string
	Source        *string
	Role          *string
	Note          *string
}

const groupMemberCols = `id, group_id, local_key, native_id, group_nickname, wechat_name, wechat_remark, wechat_account, gender, avatar_url, region, phone, signature, source, role, note, created_at, updated_at`

func scanGroupMember(sc rowScanner) (GroupMember, error) {
	var m GroupMember
	err := sc.Scan(
		&m.ID, &m.GroupID, &m.LocalKey, &m.NativeID, &m.GroupNickname,
		&m.WechatName, &m.WechatRemark, &m.WechatAccount, &m.Gender, &m.AvatarURL,
		&m.Region, &m.Phone, &m.Signature, &m.Source, &m.Role, &m.Note, &m.CreatedAt, &m.UpdatedAt,
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
		INSERT INTO group_members (owner_id, group_id, local_key, native_id, group_nickname, wechat_name, wechat_remark, wechat_account, gender, avatar_url, region, phone, signature, source, role, note, created_at, updated_at)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
		ownerID, groupID, input.LocalKey, input.NativeID, input.GroupNickname,
		input.WechatName, input.WechatRemark, input.WechatAccount, input.Gender, input.AvatarURL,
		input.Region, input.Phone, input.Signature, input.Source, input.Role, input.Note, now, now,
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
		WechatRemark:  input.WechatRemark,
		WechatAccount: input.WechatAccount,
		Gender:        input.Gender,
		AvatarURL:     input.AvatarURL,
		Region:        input.Region,
		Phone:         input.Phone,
		Signature:     input.Signature,
		Source:        input.Source,
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
		    wechat_remark  = COALESCE(?, wechat_remark),
		    wechat_account = COALESCE(?, wechat_account),
		    gender         = COALESCE(?, gender),
		    avatar_url     = COALESCE(?, avatar_url),
		    region         = COALESCE(?, region),
		    phone          = COALESCE(?, phone),
		    signature      = COALESCE(?, signature),
		    source         = COALESCE(?, source),
		    role           = COALESCE(?, role),
		    note           = COALESCE(?, note),
		    updated_at     = ?
		WHERE owner_id = ? AND group_id = ? AND id = ?
		RETURNING `+groupMemberCols,
		patch.LocalKey, patch.NativeID, patch.GroupNickname, patch.WechatName, patch.WechatRemark,
		patch.WechatAccount, patch.Gender, patch.AvatarURL, patch.Region, patch.Phone,
		patch.Signature, patch.Source, patch.Role, patch.Note, now, ownerID, groupID, memberID,
	))
	if errors.Is(err, sql.ErrNoRows) {
		return GroupMember{}, ErrNotFound
	}
	return m, err
}

func (s *Store) SetGroupMemberAvatar(ctx context.Context, ownerID string, groupID, memberID int64, newAvatarURL string) (GroupMember, string, error) {
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return GroupMember{}, "", err
	}
	defer tx.Rollback()

	var prevURL string
	err = tx.QueryRowContext(ctx, `SELECT avatar_url FROM group_members WHERE owner_id = ? AND group_id = ? AND id = ?`, ownerID, groupID, memberID).Scan(&prevURL)
	if errors.Is(err, sql.ErrNoRows) {
		return GroupMember{}, "", ErrNotFound
	}
	if err != nil {
		return GroupMember{}, "", err
	}

	now := time.Now().UTC().Format(time.RFC3339Nano)
	m, err := scanGroupMember(tx.QueryRowContext(ctx,
		`UPDATE group_members SET avatar_url = ?, updated_at = ? WHERE owner_id = ? AND group_id = ? AND id = ? RETURNING `+groupMemberCols,
		newAvatarURL, now, ownerID, groupID, memberID,
	))
	if err != nil {
		return GroupMember{}, "", err
	}
	if err := tx.Commit(); err != nil {
		return GroupMember{}, "", err
	}
	return m, prevURL, nil
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
