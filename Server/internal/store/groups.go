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
	MyNickname    string `json:"my_nickname"`
	AvatarURL     string `json:"avatar_url"`
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
	MyNickname  string
	MemberCount int
	MembersText string
	Note        string
}

type GroupPatch struct {
	GroupName   *string
	GroupKey    *string
	MyNickname  *string
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
	ImageURL   string `json:"image_url"`
	AudioURL   string `json:"audio_url"`
	VideoURL   string `json:"video_url"`
	SentAt     string `json:"sent_at"`
	CreatedAt  string `json:"created_at"`
}

type GroupMessageInput struct {
	SenderName string
	Direction  string
	Content    string
	ImageURL   string
	SentAt     time.Time
}

func (s *Store) CreateGroup(ctx context.Context, ownerID string, input GroupInput) (Group, error) {
	now := time.Now().UTC().Format(time.RFC3339Nano)
	res, err := s.db.ExecContext(ctx, `
		INSERT INTO groups (owner_id, group_name, group_key, my_nickname, member_count, members_text, note, created_at, updated_at)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)`,
		ownerID, input.GroupName, input.GroupKey, input.MyNickname, input.MemberCount, input.MembersText, input.Note, now, now,
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
		MyNickname:  input.MyNickname,
		MemberCount: input.MemberCount,
		MembersText: input.MembersText,
		Note:        input.Note,
		CreatedAt:   now,
		UpdatedAt:   now,
	}, nil
}

const groupSelectCols = `
	g.id, g.group_name, g.group_key, g.my_nickname, g.avatar_url, g.member_count, g.members_text, g.note,
	COALESCE((SELECT CASE WHEN m.content <> '' THEN m.content WHEN m.image_url <> '' THEN '[图片]' ELSE '' END FROM group_messages m WHERE m.owner_id = g.owner_id AND m.group_id = g.id ORDER BY m.sent_at DESC, m.id DESC LIMIT 1), ''),
	COALESCE((SELECT m.sent_at FROM group_messages m WHERE m.owner_id = g.owner_id AND m.group_id = g.id ORDER BY m.sent_at DESC, m.id DESC LIMIT 1), ''),
	g.created_at, g.updated_at`

func scanGroup(scanner rowScanner) (Group, error) {
	var g Group
	err := scanner.Scan(
		&g.ID, &g.GroupName, &g.GroupKey, &g.MyNickname, &g.AvatarURL, &g.MemberCount, &g.MembersText, &g.Note,
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
		    my_nickname  = COALESCE(?, my_nickname),
		    member_count = COALESCE(?, member_count),
		    members_text = COALESCE(?, members_text),
		    note         = COALESCE(?, note),
		    updated_at   = ?
		WHERE owner_id = ? AND id = ?`,
		patch.GroupName, patch.GroupKey, patch.MyNickname, patch.MemberCount, patch.MembersText, patch.Note, now, ownerID, id,
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

// SetGroupAvatar atomically replaces group avatar_url and returns the previous avatar_url for cleanup.
func (s *Store) SetGroupAvatar(ctx context.Context, ownerID string, id int64, newAvatarURL string) (Group, string, error) {
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return Group{}, "", err
	}
	defer tx.Rollback()

	var prevURL string
	err = tx.QueryRowContext(ctx, `SELECT avatar_url FROM groups WHERE owner_id = ? AND id = ?`, ownerID, id).Scan(&prevURL)
	if errors.Is(err, sql.ErrNoRows) {
		return Group{}, "", ErrNotFound
	}
	if err != nil {
		return Group{}, "", err
	}

	now := time.Now().UTC().Format(time.RFC3339Nano)
	if _, err := tx.ExecContext(ctx,
		`UPDATE groups SET avatar_url = ?, updated_at = ? WHERE owner_id = ? AND id = ?`,
		newAvatarURL, now, ownerID, id,
	); err != nil {
		return Group{}, "", err
	}

	g, err := scanGroup(tx.QueryRowContext(
		ctx,
		`SELECT `+groupSelectCols+` FROM groups g WHERE g.owner_id = ? AND g.id = ?`,
		ownerID, id,
	))
	if err != nil {
		return Group{}, "", err
	}
	if err := tx.Commit(); err != nil {
		return Group{}, "", err
	}
	return g, prevURL, nil
}

func (s *Store) DeleteGroup(ctx context.Context, ownerID string, id int64) error {
	return s.deleteOne(ctx, `DELETE FROM groups WHERE owner_id = ? AND id = ?`, ownerID, id)
}

func (s *Store) DeleteGroupReturningAvatar(ctx context.Context, ownerID string, id int64) (avatarURL string, err error) {
	err = s.db.QueryRowContext(
		ctx,
		`DELETE FROM groups WHERE owner_id = ? AND id = ? RETURNING avatar_url`,
		ownerID, id,
	).Scan(&avatarURL)
	if errors.Is(err, sql.ErrNoRows) {
		return "", ErrNotFound
	}
	return avatarURL, err
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
		INSERT INTO group_messages (owner_id, group_id, sender_name, direction, content, image_url, sent_at, created_at)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
		ownerID, groupID, input.SenderName, input.Direction, input.Content, input.ImageURL, sentAt, createdAt,
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
		ImageURL:   input.ImageURL,
		SentAt:     sentAt,
		CreatedAt:  createdAt,
	}, nil
}

func (s *Store) UpdateGroupMessage(ctx context.Context, ownerID string, groupID, messageID int64, senderName, direction, content, imageURL string) (GroupMessage, error) {
	res, err := s.db.ExecContext(ctx, `
		UPDATE group_messages
		SET sender_name = ?, direction = ?, content = ?, image_url = ?
		WHERE owner_id = ? AND group_id = ? AND id = ?`,
		senderName, direction, content, imageURL, ownerID, groupID, messageID,
	)
	if err != nil {
		return GroupMessage{}, err
	}
	n, err := res.RowsAffected()
	if err != nil {
		return GroupMessage{}, err
	}
	if n == 0 {
		return GroupMessage{}, ErrNotFound
	}

	var m GroupMessage
	err = s.db.QueryRowContext(ctx, `
		SELECT id, group_id, sender_name, direction, content, image_url, audio_url, video_url, sent_at, created_at
		FROM group_messages WHERE owner_id = ? AND group_id = ? AND id = ?`,
		ownerID, groupID, messageID,
	).Scan(&m.ID, &m.GroupID, &m.SenderName, &m.Direction, &m.Content, &m.ImageURL, &m.AudioURL, &m.VideoURL, &m.SentAt, &m.CreatedAt)
	if errors.Is(err, sql.ErrNoRows) {
		return GroupMessage{}, ErrNotFound
	}
	return m, err
}

func (s *Store) ListGroupMessages(ctx context.Context, ownerID string, groupID, cursor int64, limit int) ([]GroupMessage, int64, error) {
	ok, err := s.groupExists(ctx, ownerID, groupID)
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
			SELECT id, group_id, sender_name, direction, content, image_url, audio_url, video_url, sent_at, created_at
			FROM group_messages
			WHERE owner_id = ? AND group_id = ?
			  AND (sent_at, id) < (
			      SELECT sent_at, id FROM group_messages WHERE owner_id = ? AND group_id = ? AND id = ?
			  )
			ORDER BY sent_at DESC, id DESC
			LIMIT ?`, ownerID, groupID, ownerID, groupID, cursor, limit+1)
	} else {
		rows, err = s.db.QueryContext(ctx, `
			SELECT id, group_id, sender_name, direction, content, image_url, audio_url, video_url, sent_at, created_at
			FROM group_messages
			WHERE owner_id = ? AND group_id = ?
			ORDER BY sent_at DESC, id DESC
			LIMIT ?`, ownerID, groupID, limit+1)
	}
	if err != nil {
		return nil, 0, err
	}
	defer rows.Close()

	items := make([]GroupMessage, 0, limit)
	for rows.Next() {
		var m GroupMessage
		if err := rows.Scan(&m.ID, &m.GroupID, &m.SenderName, &m.Direction, &m.Content, &m.ImageURL, &m.AudioURL, &m.VideoURL, &m.SentAt, &m.CreatedAt); err != nil {
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
