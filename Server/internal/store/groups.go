package store

import (
	"context"
	"database/sql"
	"errors"
	"time"
)

type Group struct {
	ID            int64  `json:"id"`
	GroupName     string `json:"group_name"`
	GroupKey      string `json:"group_key"`
	MemberCount   int    `json:"member_count"`
	MembersText   string `json:"members_text"`
	Note          string `json:"note"`
	LastMessage   string `json:"last_message"`
	LastMessageAt string `json:"last_message_at"`
	CreatedAt     string `json:"created_at"`
	UpdatedAt     string `json:"updated_at"`
}

type GroupInput struct {
	GroupName   string
	GroupKey    string
	MemberCount int
	MembersText string
	Note        string
}

type GroupPatch struct {
	GroupName   *string
	GroupKey    *string
	MemberCount *int
	MembersText *string
	Note        *string
}

type GroupMessage struct {
	ID         int64  `json:"id"`
	GroupID    int64  `json:"group_id"`
	SenderName string `json:"sender_name"`
	Direction  string `json:"direction"`
	Content    string `json:"content"`
	SentAt     string `json:"sent_at"`
	CreatedAt  string `json:"created_at"`
}

type GroupMessageInput struct {
	SenderName string
	Direction  string
	Content    string
	SentAt     time.Time
}

func (s *Store) CreateGroup(ctx context.Context, ownerID string, input GroupInput) (Group, error) {
	now := time.Now().UTC().Format(time.RFC3339Nano)
	res, err := s.db.ExecContext(ctx, `
		INSERT INTO groups (owner_id, group_name, group_key, member_count, members_text, note, created_at, updated_at)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
		ownerID, input.GroupName, input.GroupKey, input.MemberCount, input.MembersText, input.Note, now, now,
	)
	if err != nil {
		return Group{}, err
	}

	id, err := res.LastInsertId()
	if err != nil {
		return Group{}, err
	}

	return Group{
		ID:          id,
		GroupName:   input.GroupName,
		GroupKey:    input.GroupKey,
		MemberCount: input.MemberCount,
		MembersText: input.MembersText,
		Note:        input.Note,
		CreatedAt:   now,
		UpdatedAt:   now,
	}, nil
}

func (s *Store) GetGroup(ctx context.Context, ownerID string, id int64) (Group, error) {
	var g Group
	err := s.db.QueryRowContext(ctx, `
		SELECT g.id, g.group_name, g.group_key, g.member_count, g.members_text, g.note,
		       COALESCE((SELECT m.content FROM group_messages m WHERE m.owner_id = g.owner_id AND m.group_id = g.id ORDER BY m.id DESC LIMIT 1), ''),
		       COALESCE((SELECT m.sent_at FROM group_messages m WHERE m.owner_id = g.owner_id AND m.group_id = g.id ORDER BY m.id DESC LIMIT 1), ''),
		       g.created_at, g.updated_at
		FROM groups g
		WHERE g.owner_id = ? AND g.id = ?`,
		ownerID, id,
	).Scan(
		&g.ID, &g.GroupName, &g.GroupKey, &g.MemberCount, &g.MembersText, &g.Note,
		&g.LastMessage, &g.LastMessageAt, &g.CreatedAt, &g.UpdatedAt,
	)
	if errors.Is(err, sql.ErrNoRows) {
		return Group{}, ErrNotFound
	}
	return g, err
}

func (s *Store) UpdateGroup(ctx context.Context, ownerID string, id int64, patch GroupPatch) (Group, error) {
	current, err := s.GetGroup(ctx, ownerID, id)
	if err != nil {
		return Group{}, err
	}

	if patch.GroupName != nil {
		current.GroupName = *patch.GroupName
	}
	if patch.GroupKey != nil {
		current.GroupKey = *patch.GroupKey
	}
	if patch.MemberCount != nil {
		current.MemberCount = *patch.MemberCount
	}
	if patch.MembersText != nil {
		current.MembersText = *patch.MembersText
	}
	if patch.Note != nil {
		current.Note = *patch.Note
	}
	current.UpdatedAt = time.Now().UTC().Format(time.RFC3339Nano)

	_, err = s.db.ExecContext(ctx, `
		UPDATE groups
		SET group_name = ?, group_key = ?, member_count = ?, members_text = ?, note = ?, updated_at = ?
		WHERE owner_id = ? AND id = ?`,
		current.GroupName, current.GroupKey, current.MemberCount, current.MembersText, current.Note, current.UpdatedAt, ownerID, id,
	)
	if err != nil {
		return Group{}, err
	}
	return current, nil
}

func (s *Store) DeleteGroup(ctx context.Context, ownerID string, id int64) error {
	res, err := s.db.ExecContext(ctx, `DELETE FROM groups WHERE owner_id = ? AND id = ?`, ownerID, id)
	if err != nil {
		return err
	}
	if n, _ := res.RowsAffected(); n == 0 {
		return ErrNotFound
	}
	return nil
}

func (s *Store) ListGroups(ctx context.Context, ownerID string, cursor int64, limit int) ([]Group, int64, error) {
	query := `
		SELECT g.id, g.group_name, g.group_key, g.member_count, g.members_text, g.note,
		       COALESCE((SELECT m.content FROM group_messages m WHERE m.owner_id = g.owner_id AND m.group_id = g.id ORDER BY m.id DESC LIMIT 1), ''),
		       COALESCE((SELECT m.sent_at FROM group_messages m WHERE m.owner_id = g.owner_id AND m.group_id = g.id ORDER BY m.id DESC LIMIT 1), ''),
		       g.created_at, g.updated_at
		FROM groups g
		WHERE g.owner_id = ?`
	args := []any{ownerID}
	if cursor > 0 {
		query += ` AND g.id < ?`
		args = append(args, cursor)
	}
	query += ` ORDER BY g.id DESC LIMIT ?`
	args = append(args, limit+1)

	rows, err := s.db.QueryContext(ctx, query, args...)
	if err != nil {
		return nil, 0, err
	}
	defer rows.Close()

	items := make([]Group, 0, limit)
	for rows.Next() {
		var g Group
		if err := rows.Scan(
			&g.ID, &g.GroupName, &g.GroupKey, &g.MemberCount, &g.MembersText, &g.Note,
			&g.LastMessage, &g.LastMessageAt, &g.CreatedAt, &g.UpdatedAt,
		); err != nil {
			return nil, 0, err
		}
		items = append(items, g)
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

func (s *Store) CreateGroupMessage(ctx context.Context, ownerID string, groupID int64, input GroupMessageInput) (GroupMessage, error) {
	ok, err := s.groupExists(ctx, ownerID, groupID)
	if err != nil {
		return GroupMessage{}, err
	}
	if !ok {
		return GroupMessage{}, ErrNotFound
	}

	sentAt := input.SentAt.UTC().Format(time.RFC3339Nano)
	createdAt := time.Now().UTC().Format(time.RFC3339Nano)

	res, err := s.db.ExecContext(ctx, `
		INSERT INTO group_messages (owner_id, group_id, sender_name, direction, content, sent_at, created_at)
		VALUES (?, ?, ?, ?, ?, ?, ?)`,
		ownerID, groupID, input.SenderName, input.Direction, input.Content, sentAt, createdAt,
	)
	if err != nil {
		return GroupMessage{}, err
	}

	id, err := res.LastInsertId()
	if err != nil {
		return GroupMessage{}, err
	}

	return GroupMessage{
		ID:         id,
		GroupID:    groupID,
		SenderName: input.SenderName,
		Direction:  input.Direction,
		Content:    input.Content,
		SentAt:     sentAt,
		CreatedAt:  createdAt,
	}, nil
}

func (s *Store) ListGroupMessages(ctx context.Context, ownerID string, groupID, cursor int64, limit int) ([]GroupMessage, int64, error) {
	ok, err := s.groupExists(ctx, ownerID, groupID)
	if err != nil {
		return nil, 0, err
	}
	if !ok {
		return nil, 0, ErrNotFound
	}

	query := `
		SELECT id, group_id, sender_name, direction, content, sent_at, created_at
		FROM group_messages
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

	items := make([]GroupMessage, 0, limit)
	for rows.Next() {
		var m GroupMessage
		if err := rows.Scan(&m.ID, &m.GroupID, &m.SenderName, &m.Direction, &m.Content, &m.SentAt, &m.CreatedAt); err != nil {
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
