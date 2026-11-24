package store

import (
	"context"
	"database/sql"
	"errors"
	"time"
)

type Message struct {
	ID         int64  `json:"id"`
	CustomerID int64  `json:"customer_id"`
	Direction  string `json:"direction"`
	Content    string `json:"content"`
	SentAt     string `json:"sent_at"`
	CreatedAt  string `json:"created_at"`
}

type MessageInput struct {
	Direction string
	Content   string
	SentAt    time.Time
}

func (s *Store) CreateMessage(ctx context.Context, ownerID string, customerID int64, input MessageInput) (Message, error) {
	var exists int
	err := s.db.QueryRowContext(ctx, `SELECT 1 FROM customers WHERE owner_id = ? AND id = ?`, ownerID, customerID).Scan(&exists)
	if errors.Is(err, sql.ErrNoRows) {
		return Message{}, ErrNotFound
	}
	if err != nil {
		return Message{}, err
	}
	now := time.Now().UTC()
	result, err := s.db.ExecContext(ctx, `INSERT INTO messages(owner_id, customer_id, direction, content, sent_at, created_at) VALUES (?, ?, ?, ?, ?, ?)`, ownerID, customerID, input.Direction, input.Content, input.SentAt.UTC().Format(time.RFC3339Nano), now.Format(time.RFC3339Nano))
	if err != nil {
		return Message{}, err
	}
	id, err := result.LastInsertId()
	if err != nil {
		return Message{}, err
	}
	return Message{ID: id, CustomerID: customerID, Direction: input.Direction, Content: input.Content, SentAt: input.SentAt.UTC().Format(time.RFC3339Nano), CreatedAt: now.Format(time.RFC3339Nano)}, nil
}

func (s *Store) ListMessages(ctx context.Context, ownerID string, customerID, cursor int64, limit int) ([]Message, int64, error) {
	var exists int
	err := s.db.QueryRowContext(ctx, `SELECT 1 FROM customers WHERE owner_id = ? AND id = ?`, ownerID, customerID).Scan(&exists)
	if errors.Is(err, sql.ErrNoRows) {
		return nil, 0, ErrNotFound
	}
	if err != nil {
		return nil, 0, err
	}
	query := `SELECT id, customer_id, direction, content, sent_at, created_at FROM messages WHERE owner_id = ? AND customer_id = ?`
	args := []any{ownerID, customerID}
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

	items := make([]Message, 0, limit)
	for rows.Next() {
		var message Message
		if err := rows.Scan(&message.ID, &message.CustomerID, &message.Direction, &message.Content, &message.SentAt, &message.CreatedAt); err != nil {
			return nil, 0, err
		}
		items = append(items, message)
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
