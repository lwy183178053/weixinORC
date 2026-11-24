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
	CreatedAt     string `json:"created_at"`
	UpdatedAt     string `json:"updated_at"`
}

type CustomerInput struct {
	WechatName    string
	WechatAccount string
	Note          string
}

type CustomerPatch struct {
	WechatName    *string
	WechatAccount *string
	Note          *string
}

func (s *Store) CreateCustomer(ctx context.Context, ownerID string, input CustomerInput) (Customer, error) {
	now := time.Now().UTC().Format(time.RFC3339Nano)
	result, err := s.db.ExecContext(ctx, `INSERT INTO customers(owner_id, wechat_name, wechat_account, note, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?)`, ownerID, input.WechatName, input.WechatAccount, input.Note, now, now)
	if err != nil {
		if isUniqueViolation(err) {
			return Customer{}, ErrConflict
		}
		return Customer{}, err
	}
	id, err := result.LastInsertId()
	if err != nil {
		return Customer{}, err
	}
	return s.GetCustomer(ctx, ownerID, id)
}

func (s *Store) GetCustomer(ctx context.Context, ownerID string, id int64) (Customer, error) {
	var customer Customer
	err := s.db.QueryRowContext(ctx, `SELECT id, wechat_name, wechat_account, note, created_at, updated_at FROM customers WHERE owner_id = ? AND id = ?`, ownerID, id).Scan(&customer.ID, &customer.WechatName, &customer.WechatAccount, &customer.Note, &customer.CreatedAt, &customer.UpdatedAt)
	if errors.Is(err, sql.ErrNoRows) {
		return Customer{}, ErrNotFound
	}
	return customer, err
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
	current.UpdatedAt = time.Now().UTC().Format(time.RFC3339Nano)
	_, err = s.db.ExecContext(ctx, `UPDATE customers SET wechat_name = ?, wechat_account = ?, note = ?, updated_at = ? WHERE owner_id = ? AND id = ?`, current.WechatName, current.WechatAccount, current.Note, current.UpdatedAt, ownerID, id)
	if err != nil {
		if isUniqueViolation(err) {
			return Customer{}, ErrConflict
		}
		return Customer{}, err
	}
	return current, nil
}

func (s *Store) ListCustomers(ctx context.Context, ownerID string, cursor int64, limit int) ([]Customer, int64, error) {
	query := `SELECT id, wechat_name, wechat_account, note, created_at, updated_at FROM customers WHERE owner_id = ?`
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

	items := make([]Customer, 0, limit)
	for rows.Next() {
		var customer Customer
		if err := rows.Scan(&customer.ID, &customer.WechatName, &customer.WechatAccount, &customer.Note, &customer.CreatedAt, &customer.UpdatedAt); err != nil {
			return nil, 0, err
		}
		items = append(items, customer)
	}
	if err := rows.Err(); err != nil {
		return nil, 0, err
	}
	var next int64
	if len(items) > limit {
		items = items[:limit]
		next = items[len(items)-1].ID
	}
	return items, next, nil
}
