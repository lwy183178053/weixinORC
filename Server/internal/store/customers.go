package store

import (
	"context"
	"database/sql"
	"errors"
	"time"
)

type Customer struct {
	ID            int64  `json:"id"`
	WechatName    string `json:"wechat_name"`
	WechatAccount string `json:"wechat_account"`
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
	WechatName    string
	WechatAccount string
	Note          string
	Region        string
	Phone         string
	Signature     string
	Source        string
	AddedDate     string
}

type CustomerPatch struct {
	WechatName    *string
	WechatAccount *string
	Note          *string
	Region        *string
	Phone         *string
	Signature     *string
	Source        *string
	AddedDate     *string
}

func (s *Store) CreateCustomer(ctx context.Context, ownerID string, input CustomerInput) (Customer, error) {
	now := time.Now().UTC().Format(time.RFC3339Nano)
	res, err := s.db.ExecContext(ctx, `
		INSERT INTO customers (owner_id, wechat_name, wechat_account, note, region, phone, signature, source, added_date, created_at, updated_at)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
		ownerID, input.WechatName, input.WechatAccount, input.Note,
		input.Region, input.Phone, input.Signature, input.Source, input.AddedDate,
		now, now,
	)
	if err != nil {
		if isUniqueViolation(err) {
			return Customer{}, ErrConflict
		}
		return Customer{}, err
	}

	id, err := res.LastInsertId()
	if err != nil {
		return Customer{}, err
	}

	return Customer{
		ID:            id,
		WechatName:    input.WechatName,
		WechatAccount: input.WechatAccount,
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

func (s *Store) GetCustomer(ctx context.Context, ownerID string, id int64) (Customer, error) {
	var c Customer
	err := s.db.QueryRowContext(ctx, `
		SELECT c.id, c.wechat_name, c.wechat_account, c.note,
		       c.region, c.phone, c.signature, c.source, c.added_date,
		       COALESCE((SELECT m.content FROM messages m WHERE m.owner_id = c.owner_id AND m.customer_id = c.id ORDER BY m.id DESC LIMIT 1), ''),
		       COALESCE((SELECT m.sent_at FROM messages m WHERE m.owner_id = c.owner_id AND m.customer_id = c.id ORDER BY m.id DESC LIMIT 1), ''),
		       c.created_at, c.updated_at
		FROM customers c
		WHERE c.owner_id = ? AND c.id = ?`,
		ownerID, id,
	).Scan(
		&c.ID, &c.WechatName, &c.WechatAccount, &c.Note,
		&c.Region, &c.Phone, &c.Signature, &c.Source, &c.AddedDate,
		&c.LastMessage, &c.LastMessageAt,
		&c.CreatedAt, &c.UpdatedAt,
	)
	if errors.Is(err, sql.ErrNoRows) {
		return Customer{}, ErrNotFound
	}
	return c, err
}

func (s *Store) UpdateCustomer(ctx context.Context, ownerID string, id int64, patch CustomerPatch) (Customer, error) {
	current, err := s.GetCustomer(ctx, ownerID, id)
	if err != nil {
		return Customer{}, err
	}

	if patch.WechatName != nil {
		current.WechatName = *patch.WechatName
	}
	if patch.WechatAccount != nil {
		current.WechatAccount = *patch.WechatAccount
	}
	if patch.Note != nil {
		current.Note = *patch.Note
	}
	if patch.Region != nil {
		current.Region = *patch.Region
	}
	if patch.Phone != nil {
		current.Phone = *patch.Phone
	}
	if patch.Signature != nil {
		current.Signature = *patch.Signature
	}
	if patch.Source != nil {
		current.Source = *patch.Source
	}
	if patch.AddedDate != nil {
		current.AddedDate = *patch.AddedDate
	}
	current.UpdatedAt = time.Now().UTC().Format(time.RFC3339Nano)

	_, err = s.db.ExecContext(ctx, `
		UPDATE customers
		SET wechat_name = ?, wechat_account = ?, note = ?,
		    region = ?, phone = ?, signature = ?, source = ?, added_date = ?,
		    updated_at = ?
		WHERE owner_id = ? AND id = ?`,
		current.WechatName, current.WechatAccount, current.Note,
		current.Region, current.Phone, current.Signature, current.Source, current.AddedDate,
		current.UpdatedAt, ownerID, id,
	)
	if err != nil {
		if isUniqueViolation(err) {
			return Customer{}, ErrConflict
		}
		return Customer{}, err
	}
	return current, nil
}

func (s *Store) DeleteCustomer(ctx context.Context, ownerID string, id int64) error {
	res, err := s.db.ExecContext(ctx, `DELETE FROM customers WHERE owner_id = ? AND id = ?`, ownerID, id)
	if err != nil {
		return err
	}
	if n, _ := res.RowsAffected(); n == 0 {
		return ErrNotFound
	}
	return nil
}

func (s *Store) ListCustomers(ctx context.Context, ownerID string, cursor int64, limit int) ([]Customer, int64, error) {
	query := `
		SELECT c.id, c.wechat_name, c.wechat_account, c.note,
		       c.region, c.phone, c.signature, c.source, c.added_date,
		       COALESCE((SELECT m.content FROM messages m WHERE m.owner_id = c.owner_id AND m.customer_id = c.id ORDER BY m.id DESC LIMIT 1), ''),
		       COALESCE((SELECT m.sent_at FROM messages m WHERE m.owner_id = c.owner_id AND m.customer_id = c.id ORDER BY m.id DESC LIMIT 1), ''),
		       c.created_at, c.updated_at
		FROM customers c
		WHERE c.owner_id = ?`
	args := []any{ownerID}
	if cursor > 0 {
		query += ` AND c.id < ?`
		args = append(args, cursor)
	}
	query += ` ORDER BY c.id DESC LIMIT ?`
	args = append(args, limit+1)

	rows, err := s.db.QueryContext(ctx, query, args...)
	if err != nil {
		return nil, 0, err
	}
	defer rows.Close()

	items := make([]Customer, 0, limit)
	for rows.Next() {
		var c Customer
		if err := rows.Scan(
			&c.ID, &c.WechatName, &c.WechatAccount, &c.Note,
			&c.Region, &c.Phone, &c.Signature, &c.Source, &c.AddedDate,
			&c.LastMessage, &c.LastMessageAt,
			&c.CreatedAt, &c.UpdatedAt,
		); err != nil {
			return nil, 0, err
		}
		items = append(items, c)
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
