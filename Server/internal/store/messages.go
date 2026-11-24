package store

import (
	"context"
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
	sentAt := input.SentAt.UTC().Format(time.RFC3339Nano)
	createdAt := time.Now().UTC().Format(time.RFC3339Nano)

	res, err := s.db.ExecContext(ctx, `
		INSERT INTO messages (owner_id, customer_id, direction, content, sent_at, created_at)
		VALUES (?, ?, ?, ?, ?, ?)`,
		ownerID, customerID, input.Direction, input.Content, sentAt, createdAt,
	)
	if err != nil {
		if isForeignKeyViolation(err) {
			return Message{}, ErrNotFound
		}
		return Message{}, err
	}

	id, err := res.LastInsertId()
	if err != nil {
		return Message{}, err
	}

	return Message{
		ID:         id,
		CustomerID: customerID,
		Direction:  input.Direction,
		Content:    input.Content,
		SentAt:     sentAt,
		CreatedAt:  createdAt,
	}, nil
}

func (s *Store) ListMessages(ctx context.Context, ownerID string, customerID, cursor int64, limit int) ([]Message, int64, error) {
	ok, err := s.customerExists(ctx, ownerID, customerID)
	if err != nil {
		return nil, 0, err
	}
	if !ok {
		return nil, 0, ErrNotFound
	}

	return listPage(
		ctx, s.db,
		`SELECT id, customer_id, direction, content, sent_at, created_at FROM messages WHERE owner_id = ? AND customer_id = ?`,
		[]any{ownerID, customerID},
		"id", cursor, limit,
		func(sc rowScanner) (Message, error) {
			var m Message
			err := sc.Scan(&m.ID, &m.CustomerID, &m.Direction, &m.Content, &m.SentAt, &m.CreatedAt)
			return m, err
		},
		func(m Message) int64 { return m.ID },
	)
}
