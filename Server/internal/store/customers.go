package store

import (
	"context"
	"database/sql"
	"errors"
	"time"
)

type Customer struct {
	ID            int64  `json:"id"`
	WechatRemark  string `json:"wechat_remark"`
	WechatName    string `json:"wechat_name"`
	WechatAccount string `json:"wechat_account"`
	Gender        string `json:"gender"`
	AvatarURL     string `json:"avatar_url"`
	Description   string `json:"description"`
	Tags          string `json:"tags"`
	FollowStatus  string `json:"follow_status"`
	LocalKey      string `json:"local_key"`
	NativeID      string `json:"native_id"`
	Note          string `json:"note"`
	Region        string `json:"region"`
	Phone         string `json:"phone"`
	Signature     string `json:"signature"`
	Source        string `json:"source"`
	AddedDate     string `json:"added_date"`
	LastMessage   string `json:"last_message"`
	LastMessageAt string `json:"last_message_at"`
	CreatedAt     string `json:"created_at"`
	UpdatedAt     string `json:"updated_at"`
}

type CustomerInput struct {
	WechatRemark   string
	WechatName     string
	WechatAccount  string
	Gender         string
	AvatarURL      string
	Description    string
	Tags           string
	FollowStatus   string
	LocalKey       string
	NativeID       string
	Note           string
	Region         string
	Phone          string
	Signature      string
	Source         string
	AddedDate      string
	AllowDuplicate bool
}

type CustomerPatch struct {
	WechatRemark  *string
	WechatName    *string
	WechatAccount *string
	Gender        *string
	AvatarURL     *string
	Description   *string
	Tags          *string
	FollowStatus  *string
	LocalKey      *string
	NativeID      *string
	Note          *string
	Region        *string
	Phone         *string
	Signature     *string
	Source        *string
	AddedDate     *string
}

func (s *Store) CreateCustomer(ctx context.Context, ownerID string, input CustomerInput) (Customer, error) {
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return Customer{}, err
	}
	defer tx.Rollback()

	if !input.AllowDuplicate && input.WechatAccount != "" {
		var exists int
		err := tx.QueryRowContext(
			ctx,
			`SELECT 1 FROM customers WHERE owner_id = ? AND wechat_account = ? LIMIT 1`,
			ownerID, input.WechatAccount,
		).Scan(&exists)
		if err == nil {
			return Customer{}, ErrConflict
		}
		if !errors.Is(err, sql.ErrNoRows) {
			return Customer{}, err
		}
	}

	now := time.Now().UTC().Format(time.RFC3339Nano)
	res, err := tx.ExecContext(ctx, `
		INSERT INTO customers (
			owner_id, wechat_remark, wechat_name, wechat_account,
			gender, avatar_url, description, tags, follow_status, local_key, native_id,
			note, region, phone, signature, source, added_date,
			created_at, updated_at
		) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
		ownerID, input.WechatRemark, input.WechatName, input.WechatAccount,
		input.Gender, input.AvatarURL, input.Description, input.Tags, input.FollowStatus, input.LocalKey, input.NativeID,
		input.Note, input.Region, input.Phone, input.Signature, input.Source, input.AddedDate,
		now, now,
	)
	if err != nil {
		return Customer{}, err
	}

	id, err := res.LastInsertId()
	if err != nil {
		return Customer{}, err
	}
	if err := tx.Commit(); err != nil {
		return Customer{}, err
	}

	return Customer{
		ID:            id,
		WechatRemark:  input.WechatRemark,
		WechatName:    input.WechatName,
		WechatAccount: input.WechatAccount,
		Gender:        input.Gender,
		AvatarURL:     input.AvatarURL,
		Description:   input.Description,
		Tags:          input.Tags,
		FollowStatus:  input.FollowStatus,
		LocalKey:      input.LocalKey,
		NativeID:      input.NativeID,
		Note:          input.Note,
		Region:        input.Region,
		Phone:         input.Phone,
		Signature:     input.Signature,
		Source:        input.Source,
		AddedDate:     input.AddedDate,
		CreatedAt:     now,
		UpdatedAt:     now,
	}, nil
}

const customerSelectCols = `
	c.id, c.wechat_remark, c.wechat_name, c.wechat_account,
	c.gender, c.avatar_url, c.description, c.tags, c.follow_status, c.local_key, c.native_id,
	c.note, c.region, c.phone, c.signature, c.source, c.added_date,
	COALESCE((SELECT CASE WHEN m.content <> '' THEN m.content WHEN m.image_url <> '' THEN '[图片]' ELSE '' END FROM messages m WHERE m.owner_id = c.owner_id AND m.customer_id = c.id ORDER BY m.sent_at DESC, m.id DESC LIMIT 1), ''),
	COALESCE((SELECT m.sent_at FROM messages m WHERE m.owner_id = c.owner_id AND m.customer_id = c.id ORDER BY m.sent_at DESC, m.id DESC LIMIT 1), ''),
	c.created_at, c.updated_at`

func scanCustomer(scanner rowScanner) (Customer, error) {
	var c Customer
	err := scanner.Scan(
		&c.ID, &c.WechatRemark, &c.WechatName, &c.WechatAccount,
		&c.Gender, &c.AvatarURL, &c.Description, &c.Tags, &c.FollowStatus, &c.LocalKey, &c.NativeID,
		&c.Note, &c.Region, &c.Phone, &c.Signature, &c.Source, &c.AddedDate,
		&c.LastMessage, &c.LastMessageAt,
		&c.CreatedAt, &c.UpdatedAt,
	)
	return c, err
}

func (s *Store) GetCustomer(ctx context.Context, ownerID string, id int64) (Customer, error) {
	row := s.db.QueryRowContext(
		ctx,
		`SELECT `+customerSelectCols+` FROM customers c WHERE c.owner_id = ? AND c.id = ?`,
		ownerID, id,
	)
	c, err := scanCustomer(row)
	if errors.Is(err, sql.ErrNoRows) {
		return Customer{}, ErrNotFound
	}
	return c, err
}

func (s *Store) UpdateCustomer(ctx context.Context, ownerID string, id int64, patch CustomerPatch) (Customer, error) {
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return Customer{}, err
	}
	defer tx.Rollback()

	now := time.Now().UTC().Format(time.RFC3339Nano)
	res, err := tx.ExecContext(ctx, `
		UPDATE customers
		SET wechat_remark  = COALESCE(?, wechat_remark),
		    wechat_name    = COALESCE(?, wechat_name),
		    wechat_account = COALESCE(?, wechat_account),
		    gender         = COALESCE(?, gender),
		    avatar_url     = COALESCE(?, avatar_url),
		    description    = COALESCE(?, description),
		    tags           = COALESCE(?, tags),
		    follow_status  = COALESCE(?, follow_status),
		    local_key      = COALESCE(?, local_key),
		    native_id      = COALESCE(?, native_id),
		    note           = COALESCE(?, note),
		    region         = COALESCE(?, region),
		    phone          = COALESCE(?, phone),
		    signature      = COALESCE(?, signature),
		    source         = COALESCE(?, source),
		    added_date     = COALESCE(?, added_date),
		    updated_at     = ?
		WHERE owner_id = ? AND id = ?`,
		patch.WechatRemark, patch.WechatName, patch.WechatAccount,
		patch.Gender, patch.AvatarURL, patch.Description, patch.Tags, patch.FollowStatus, patch.LocalKey, patch.NativeID,
		patch.Note, patch.Region, patch.Phone, patch.Signature, patch.Source, patch.AddedDate,
		now, ownerID, id,
	)
	if err != nil {
		return Customer{}, err
	}
	n, err := res.RowsAffected()
	if err != nil {
		return Customer{}, err
	}
	if n == 0 {
		return Customer{}, ErrNotFound
	}

	c, err := scanCustomer(tx.QueryRowContext(
		ctx,
		`SELECT `+customerSelectCols+` FROM customers c WHERE c.owner_id = ? AND c.id = ?`,
		ownerID, id,
	))
	if err != nil {
		return Customer{}, err
	}
	if err := tx.Commit(); err != nil {
		return Customer{}, err
	}
	return c, nil
}

// SetCustomerAvatar atomically replaces avatar_url and returns the previous avatar_url for cleanup.
func (s *Store) SetCustomerAvatar(ctx context.Context, ownerID string, id int64, newAvatarURL string) (Customer, string, error) {
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return Customer{}, "", err
	}
	defer tx.Rollback()

	var prevURL string
	err = tx.QueryRowContext(ctx, `SELECT avatar_url FROM customers WHERE owner_id = ? AND id = ?`, ownerID, id).Scan(&prevURL)
	if errors.Is(err, sql.ErrNoRows) {
		return Customer{}, "", ErrNotFound
	}
	if err != nil {
		return Customer{}, "", err
	}

	now := time.Now().UTC().Format(time.RFC3339Nano)
	if _, err := tx.ExecContext(ctx,
		`UPDATE customers SET avatar_url = ?, updated_at = ? WHERE owner_id = ? AND id = ?`,
		newAvatarURL, now, ownerID, id,
	); err != nil {
		return Customer{}, "", err
	}

	c, err := scanCustomer(tx.QueryRowContext(
		ctx,
		`SELECT `+customerSelectCols+` FROM customers c WHERE c.owner_id = ? AND c.id = ?`,
		ownerID, id,
	))
	if err != nil {
		return Customer{}, "", err
	}
	if err := tx.Commit(); err != nil {
		return Customer{}, "", err
	}
	return c, prevURL, nil
}

func (s *Store) DeleteCustomer(ctx context.Context, ownerID string, id int64) error {
	return s.deleteOne(ctx, `DELETE FROM customers WHERE owner_id = ? AND id = ?`, ownerID, id)
}

func (s *Store) DeleteCustomerReturningMeta(ctx context.Context, ownerID string, id int64) (avatarURL, wechatName string, err error) {
	err = s.db.QueryRowContext(
		ctx,
		`DELETE FROM customers WHERE owner_id = ? AND id = ? RETURNING avatar_url, wechat_name`,
		ownerID, id,
	).Scan(&avatarURL, &wechatName)
	if errors.Is(err, sql.ErrNoRows) {
		return "", "", ErrNotFound
	}
	return avatarURL, wechatName, err
}

func (s *Store) ListCustomers(ctx context.Context, ownerID string, cursor int64, limit int) ([]Customer, int64, error) {
	return listPage(
		ctx, s.db,
		`SELECT `+customerSelectCols+` FROM customers c WHERE c.owner_id = ?`,
		[]any{ownerID},
		"c.id", cursor, limit,
		scanCustomer,
		func(c Customer) int64 { return c.ID },
	)
}
