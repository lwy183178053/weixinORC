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

const groupSelectCols = `
	g.id, g.group_name, g.group_key, g.member_count, g.members_text, g.note,
	COALESCE((SELECT m.content FROM group_messages m WHERE m.owner_id = g.owner_id AND m.group_id = g.id ORDER BY m.id DESC LIMIT 1), ''),
	COALESCE((SELECT m.sent_at FROM group_messages m WHERE m.owner_id = g.owner_id AND m.group_id = g.id ORDER BY m.id DESC LIMIT 1), ''),
	g.created_at, g.updated_at`

func scanGroup(scanner rowScanner) (Group, error) {
	var g Group
	err := scanner.Scan(
		&g.ID, &g.GroupName, &g.GroupKey, &g.MemberCount, &g.MembersText, &g.Note,
		&g.LastMessage, &g.LastMessageAt, &g.CreatedAt, &g.UpdatedAt,
	)
	return g, err
}

func (s *Store) GetGroup(ctx context.Context, ownerID string, id int64) (Group, error) {
	g, err := scanGroup(s.db.QueryRowContext(
		ctx,
		`SELECT `+groupSelectCols+` FROM groups g WHERE g.owner_id = ? AND g.id = ?`,
		ownerID, id,
	))
	if errors.Is(err, sql.ErrNoRows) {
		return Group{}, ErrNotFound
	}
	return g, err
}

func (s *Store) UpdateGroup(ctx context.Context, ownerID string, id int64, patch GroupPatch) (Group, error) {
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return Group{}, err
	}
	defer tx.Rollback()

	now := time.Now().UTC().Format(time.RFC3339Nano)
	res, err := tx.ExecContext(ctx, `
		UPDATE groups
		SET group_name   = COALESCE(?, group_name),
		    group_key    = COALESCE(?, group_key),
		    member_count = COALESCE(?, member_count),
		    members_text = COALESCE(?, members_text),
		    note         = COALESCE(?, note),
		    updated_at   = ?
		WHERE owner_id = ? AND id = ?`,
		patch.GroupName, patch.GroupKey, patch.MemberCount, patch.MembersText, patch.Note, now, ownerID, id,
	)
	if err != nil {
		return Group{}, err
	}
	n, err := res.RowsAffected()
	if err != nil {
		return Group{}, err
	}
	if n == 0 {
		return Group{}, ErrNotFound
	}

	g, err := scanGroup(tx.QueryRowContext(
		ctx,
		`SELECT `+groupSelectCols+` FROM groups g WHERE g.owner_id = ? AND g.id = ?`,
		ownerID, id,
	))
	if err != nil {
		return Group{}, err
	}
	if err := tx.Commit(); err != nil {
		return Group{}, err
	}
	return g, nil
}

func (s *Store) DeleteGroup(ctx context.Context, ownerID string, id int64) error {
	return s.deleteOne(ctx, `DELETE FROM groups WHERE owner_id = ? AND id = ?`, ownerID, id)
}

func (s *Store) ListGroups(ctx context.Context, ownerID string, cursor int64, limit int) ([]Group, int64, error) {
	return listPage(
		ctx, s.db,
		`SELECT `+groupSelectCols+` FROM groups g WHERE g.owner_id = ?`,
		[]any{ownerID},
		"g.id", cursor, limit,
		scanGroup,
		func(g Group) int64 { return g.ID },
	)
}

func (s *Store) CreateGroupMessage(ctx context.Context, ownerID string, groupID int64, input GroupMessageInput) (GroupMessage, error) {
	sentAt := input.SentAt.UTC().Format(time.RFC3339Nano)
	createdAt := time.Now().UTC().Format(time.RFC3339Nano)

	res, err := s.db.ExecContext(ctx, `
		INSERT INTO group_messages (owner_id, group_id, sender_name, direction, content, sent_at, created_at)
		VALUES (?, ?, ?, ?, ?, ?, ?)`,
		ownerID, groupID, input.SenderName, input.Direction, input.Content, sentAt, createdAt,
	)
	if err != nil {
		if isForeignKeyViolation(err) {
			return GroupMessage{}, ErrNotFound
		}
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

	return listPage(
		ctx, s.db,
		`SELECT id, group_id, sender_name, direction, content, sent_at, created_at FROM group_messages WHERE owner_id = ? AND group_id = ?`,
		[]any{ownerID, groupID},
		"id", cursor, limit,
		func(sc rowScanner) (GroupMessage, error) {
			var m GroupMessage
			err := sc.Scan(&m.ID, &m.GroupID, &m.SenderName, &m.Direction, &m.Content, &m.SentAt, &m.CreatedAt)
			return m, err
		},
		func(m GroupMessage) int64 { return m.ID },
	)
}
