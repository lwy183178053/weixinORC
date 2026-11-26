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
	ImageURL   string `json:"image_url"`
	AudioURL   string `json:"audio_url"`
	VideoURL   string `json:"video_url"`
	SentAt     string `json:"sent_at"`
	CreatedAt  string `json:"created_at"`
}

type MessageInput struct {
	Direction string
	Content   string
	ImageURL  string
	SentAt    time.Time
}

func (s *Store) CreateMessage(ctx context.Context, ownerID string, customerID int64, input MessageInput) (Message, error) {
	sentAt := input.SentAt.UTC().Format(time.RFC3339Nano)
	createdAt := time.Now().UTC().Format(time.RFC3339Nano)

	res, err := s.db.ExecContext(ctx, `
		INSERT INTO messages (owner_id, customer_id, direction, content, image_url, sent_at, created_at)
		VALUES (?, ?, ?, ?, ?, ?, ?)`,
		ownerID, customerID, input.Direction, input.Content, input.ImageURL, sentAt, createdAt,
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
		ImageURL:   input.ImageURL,
		SentAt:     sentAt,
		CreatedAt:  createdAt,
	}, nil
}

func (s *Store) UpdateMessage(ctx context.Context, ownerID string, customerID, messageID int64, direction, content, imageURL string) (Message, error) {
	res, err := s.db.ExecContext(ctx, `
		UPDATE messages
		SET direction = ?, content = ?, image_url = ?
		WHERE owner_id = ? AND customer_id = ? AND id = ?`,
		direction, content, imageURL, ownerID, customerID, messageID,
	)
	if err != nil {
		return Message{}, err
	}
	n, err := res.RowsAffected()
	if err != nil {
		return Message{}, err
	}
	if n == 0 {
		return Message{}, ErrNotFound
	}

	var m Message
	err = s.db.QueryRowContext(ctx, `
		SELECT id, customer_id, direction, content, image_url, audio_url, video_url, sent_at, created_at
		FROM messages WHERE owner_id = ? AND customer_id = ? AND id = ?`,
		ownerID, customerID, messageID,
	).Scan(&m.ID, &m.CustomerID, &m.Direction, &m.Content, &m.ImageURL, &m.AudioURL, &m.VideoURL, &m.SentAt, &m.CreatedAt)
	if errors.Is(err, sql.ErrNoRows) {
		return Message{}, ErrNotFound
	}
	return m, err
}

func (s *Store) ListMessages(ctx context.Context, ownerID string, customerID, cursor int64, limit int) ([]Message, int64, error) {
	ok, err := s.customerExists(ctx, ownerID, customerID)
	if err != nil {
		return nil, 0, err
	}
	if !ok {
		return nil, 0, ErrNotFound
	}
	if limit <= 0 {
		limit = 50
	}

	var rows *sql.Rows
	if cursor > 0 {
		rows, err = s.db.QueryContext(ctx, `
			SELECT id, customer_id, direction, content, image_url, audio_url, video_url, sent_at, created_at
			FROM messages
			WHERE owner_id = ? AND customer_id = ?
			  AND (sent_at, id) < (
			      SELECT sent_at, id FROM messages WHERE owner_id = ? AND customer_id = ? AND id = ?
			  )
			ORDER BY sent_at DESC, id DESC
			LIMIT ?`, ownerID, customerID, ownerID, customerID, cursor, limit+1)
	} else {
		rows, err = s.db.QueryContext(ctx, `
			SELECT id, customer_id, direction, content, image_url, audio_url, video_url, sent_at, created_at
			FROM messages
			WHERE owner_id = ? AND customer_id = ?
			ORDER BY sent_at DESC, id DESC
			LIMIT ?`, ownerID, customerID, limit+1)
	}
	if err != nil {
		return nil, 0, err
	}
	defer rows.Close()

	items := make([]Message, 0, limit)
	for rows.Next() {
		var m Message
		if err := rows.Scan(&m.ID, &m.CustomerID, &m.Direction, &m.Content, &m.ImageURL, &m.AudioURL, &m.VideoURL, &m.SentAt, &m.CreatedAt); err != nil {
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
