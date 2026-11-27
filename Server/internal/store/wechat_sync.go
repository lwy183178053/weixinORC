package store

import (
	"context"
	"database/sql"
	"errors"
	"strings"
	"time"
)

type WeChatSyncCustomerItem struct {
	OwnerWxid     string `json:"owner_wxid"`
	NativeID      string `json:"native_id"`
	LocalKey      string `json:"local_key"`
	WechatRemark  string `json:"wechat_remark"`
	WechatName    string `json:"wechat_name"`
	WechatAccount string `json:"wechat_account"`
	Gender        string `json:"gender"`
	Description   string `json:"description"`
	Tags          string `json:"tags"`
	Region        string `json:"region"`
	Phone         string `json:"phone"`
	Signature     string `json:"signature"`
	Source        string `json:"source"`
	AddedDate     string `json:"added_date"`
}

type WeChatSyncCustomerResult struct {
	NativeID  string `json:"native_id"`
	ID        int64  `json:"id"`
	Created   bool   `json:"created"`
	AvatarURL string `json:"avatar_url"`
}

type WeChatSyncGroupMemberItem struct {
	NativeID      string `json:"native_id"`
	LocalKey      string `json:"local_key"`
	GroupNickname string `json:"group_nickname"`
	WechatName    string `json:"wechat_name"`
	WechatRemark  string `json:"wechat_remark"`
	WechatAccount string `json:"wechat_account"`
	Gender        string `json:"gender"`
	Region        string `json:"region"`
	Signature     string `json:"signature"`
	Role          string `json:"role"`
}

type WeChatSyncGroupItem struct {
	OwnerWxid    string                      `json:"owner_wxid"`
	GroupKey     string                      `json:"group_key"`
	GroupName    string                      `json:"group_name"`
	MyNickname   string                      `json:"my_nickname"`
	MemberCount  int                         `json:"member_count"`
	MembersText  string                      `json:"members_text"`
	Announcement string                      `json:"announcement"`
	Members      []WeChatSyncGroupMemberItem `json:"members"`
}

type WeChatSyncGroupMemberResult struct {
	NativeID  string `json:"native_id"`
	ID        int64  `json:"id"`
	Created   bool   `json:"created"`
	AvatarURL string `json:"avatar_url"`
}

type WeChatSyncGroupResult struct {
	GroupKey  string                        `json:"group_key"`
	ID        int64                         `json:"id"`
	Created   bool                          `json:"created"`
	AvatarURL string                        `json:"avatar_url"`
	Members   []WeChatSyncGroupMemberResult `json:"members"`
}

type WeChatSyncMessageItem struct {
	ExternalID string    `json:"external_id"`
	SenderName string    `json:"sender_name,omitempty"`
	Direction  string    `json:"direction"`
	Content    string    `json:"content"`
	ImageURL   string    `json:"image_url"`
	AudioURL   string    `json:"audio_url"`
	VideoURL   string    `json:"video_url"`
	SentAt     time.Time `json:"-"`
}

type WeChatSyncMessagesSummary struct {
	Inserted int `json:"inserted"`
	Skipped  int `json:"skipped"`
}

func nonEmptyOr(incoming, existing string) string {
	if strings.TrimSpace(incoming) != "" {
		return incoming
	}
	return existing
}

func (s *Store) SyncWeChatCustomers(ctx context.Context, ownerID string, items []WeChatSyncCustomerItem) ([]WeChatSyncCustomerResult, int, int, error) {
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return nil, 0, 0, err
	}
	defer tx.Rollback()

	now := time.Now().UTC().Format(time.RFC3339Nano)
	results := make([]WeChatSyncCustomerResult, 0, len(items))
	createdCount := 0
	updatedCount := 0

	for _, item := range items {
		var existingID int64
		var curOwnerWxid, curRemark, curName, curAccount, curGender, curAvatar, curDesc, curTags, curLocalKey, curNativeID, curRegion, curPhone, curSig, curSource, curAddedDate string
		found := false

		queryCustomer := func(where string, args ...any) (bool, error) {
			err := tx.QueryRowContext(ctx, `
				SELECT id, owner_wxid, wechat_remark, wechat_name, wechat_account, gender, avatar_url,
				       description, tags, local_key, native_id, region, phone, signature, source, added_date
				FROM customers
				WHERE `+where+`
				LIMIT 1`, args...,
			).Scan(&existingID, &curOwnerWxid, &curRemark, &curName, &curAccount, &curGender, &curAvatar,
				&curDesc, &curTags, &curLocalKey, &curNativeID, &curRegion, &curPhone, &curSig, &curSource, &curAddedDate)
			if err == nil {
				return true, nil
			}
			if errors.Is(err, sql.ErrNoRows) {
				return false, nil
			}
			return false, err
		}

		// Multi-WeChat aware lookup:
		// 1. Exact match within the same owner_wxid (so same contact across WeChat A & B stays separate).
		// 2. Adopt legacy rows with owner_wxid = '' so existing pre-upgrade archives seamlessly bind to the active account.
		if item.OwnerWxid != "" {
			if item.NativeID != "" {
				found, err = queryCustomer(`owner_id = ? AND owner_wxid = ? AND native_id = ?`, ownerID, item.OwnerWxid, item.NativeID)
				if err != nil {
					return nil, 0, 0, err
				}
			}
			if !found && item.WechatAccount != "" {
				found, err = queryCustomer(`owner_id = ? AND owner_wxid = ? AND wechat_account = ?`, ownerID, item.OwnerWxid, item.WechatAccount)
				if err != nil {
					return nil, 0, 0, err
				}
			}
			if !found && item.NativeID != "" {
				found, err = queryCustomer(`owner_id = ? AND owner_wxid = '' AND native_id = ?`, ownerID, item.NativeID)
				if err != nil {
					return nil, 0, 0, err
				}
			}
			if !found && item.WechatAccount != "" {
				found, err = queryCustomer(`owner_id = ? AND owner_wxid = '' AND wechat_account = ?`, ownerID, item.WechatAccount)
				if err != nil {
					return nil, 0, 0, err
				}
			}
		} else {
			if item.NativeID != "" {
				found, err = queryCustomer(`owner_id = ? AND native_id = ?`, ownerID, item.NativeID)
				if err != nil {
					return nil, 0, 0, err
				}
			}
			if !found && item.WechatAccount != "" {
				found, err = queryCustomer(`owner_id = ? AND wechat_account = ?`, ownerID, item.WechatAccount)
				if err != nil {
					return nil, 0, 0, err
				}
			}
		}

		if found {
			// Update WeChat-sourced profile fields while preserving employee-authored note, follow_status, and avatar_url.
			_, err := tx.ExecContext(ctx, `
				UPDATE customers
				SET owner_wxid     = ?,
				    wechat_remark  = ?,
				    wechat_name    = ?,
				    wechat_account = ?,
				    gender         = ?,
				    description    = ?,
				    tags           = ?,
				    local_key      = ?,
				    native_id      = ?,
				    region         = ?,
				    phone          = ?,
				    signature      = ?,
				    source         = ?,
				    added_date     = ?,
				    updated_at     = ?
				WHERE owner_id = ? AND id = ?`,
				nonEmptyOr(item.OwnerWxid, curOwnerWxid),
				nonEmptyOr(item.WechatRemark, curRemark),
				nonEmptyOr(item.WechatName, curName),
				nonEmptyOr(item.WechatAccount, curAccount),
				nonEmptyOr(item.Gender, curGender),
				nonEmptyOr(item.Description, curDesc),
				nonEmptyOr(item.Tags, curTags),
				nonEmptyOr(item.LocalKey, curLocalKey),
				nonEmptyOr(item.NativeID, curNativeID),
				nonEmptyOr(item.Region, curRegion),
				nonEmptyOr(item.Phone, curPhone),
				nonEmptyOr(item.Signature, curSig),
				nonEmptyOr(item.Source, curSource),
				nonEmptyOr(item.AddedDate, curAddedDate),
				now, ownerID, existingID,
			)
			if err != nil {
				return nil, 0, 0, err
			}
			updatedCount++
			results = append(results, WeChatSyncCustomerResult{
				NativeID:  item.NativeID,
				ID:        existingID,
				Created:   false,
				AvatarURL: curAvatar,
			})
			continue
		}

		res, err := tx.ExecContext(ctx, `
			INSERT INTO customers (
				owner_id, owner_wxid, wechat_remark, wechat_name, wechat_account,
				gender, avatar_url, description, tags, follow_status, local_key, native_id,
				note, region, phone, signature, source, added_date,
				created_at, updated_at
			) VALUES (?, ?, ?, ?, ?, ?, '', ?, ?, '', ?, ?, '', ?, ?, ?, ?, ?, ?, ?)`,
			ownerID, item.OwnerWxid, item.WechatRemark, item.WechatName, item.WechatAccount,
			item.Gender, item.Description, item.Tags, item.LocalKey, item.NativeID,
			item.Region, item.Phone, item.Signature, item.Source, item.AddedDate,
			now, now,
		)
		if err != nil {
			return nil, 0, 0, err
		}
		id, err := res.LastInsertId()
		if err != nil {
			return nil, 0, 0, err
		}
		createdCount++
		results = append(results, WeChatSyncCustomerResult{
			NativeID:  item.NativeID,
			ID:        id,
			Created:   true,
			AvatarURL: "",
		})
	}

	if err := tx.Commit(); err != nil {
		return nil, 0, 0, err
	}
	return results, createdCount, updatedCount, nil
}

func (s *Store) SyncWeChatGroups(ctx context.Context, ownerID string, items []WeChatSyncGroupItem) ([]WeChatSyncGroupResult, int, int, error) {
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return nil, 0, 0, err
	}
	defer tx.Rollback()

	now := time.Now().UTC().Format(time.RFC3339Nano)
	results := make([]WeChatSyncGroupResult, 0, len(items))
	createdCount := 0
	updatedCount := 0

	for _, item := range items {
		var groupID int64
		var curOwnerWxid, curName, curMyNick, curAvatar, curMembersText, curAnnouncement string
		var curMemberCount int
		found := false

		queryGroup := func(where string, args ...any) (bool, error) {
			err := tx.QueryRowContext(ctx, `
				SELECT id, owner_wxid, group_name, my_nickname, avatar_url, member_count, members_text, announcement
				FROM groups
				WHERE `+where+`
				LIMIT 1`, args...,
			).Scan(&groupID, &curOwnerWxid, &curName, &curMyNick, &curAvatar, &curMemberCount, &curMembersText, &curAnnouncement)
			if err == nil {
				return true, nil
			}
			if errors.Is(err, sql.ErrNoRows) {
				return false, nil
			}
			return false, err
		}

		if item.GroupKey != "" {
			if item.OwnerWxid != "" {
				found, err = queryGroup(`owner_id = ? AND owner_wxid = ? AND group_key = ?`, ownerID, item.OwnerWxid, item.GroupKey)
				if err != nil {
					return nil, 0, 0, err
				}
				if !found {
					found, err = queryGroup(`owner_id = ? AND owner_wxid = '' AND group_key = ?`, ownerID, item.GroupKey)
					if err != nil {
						return nil, 0, 0, err
					}
				}
			} else {
				found, err = queryGroup(`owner_id = ? AND group_key = ?`, ownerID, item.GroupKey)
				if err != nil {
					return nil, 0, 0, err
				}
			}
		}

		groupCreated := false
		if found {
			memberCount := item.MemberCount
			if memberCount <= 0 {
				memberCount = curMemberCount
			}
			_, err := tx.ExecContext(ctx, `
				UPDATE groups
				SET owner_wxid   = ?,
				    group_name   = ?,
				    my_nickname  = ?,
				    member_count = ?,
				    members_text = ?,
				    announcement = ?,
				    updated_at   = ?
				WHERE owner_id = ? AND id = ?`,
				nonEmptyOr(item.OwnerWxid, curOwnerWxid),
				nonEmptyOr(item.GroupName, curName),
				nonEmptyOr(item.MyNickname, curMyNick),
				memberCount,
				nonEmptyOr(item.MembersText, curMembersText),
				nonEmptyOr(item.Announcement, curAnnouncement),
				now, ownerID, groupID,
			)
			if err != nil {
				return nil, 0, 0, err
			}
			updatedCount++
		} else {
			res, err := tx.ExecContext(ctx, `
				INSERT INTO groups (owner_id, owner_wxid, group_name, group_key, my_nickname, member_count, members_text, announcement, note, created_at, updated_at)
				VALUES (?, ?, ?, ?, ?, ?, ?, ?, '', ?, ?)`,
				ownerID, item.OwnerWxid, item.GroupName, item.GroupKey, item.MyNickname, item.MemberCount, item.MembersText, item.Announcement, now, now,
			)
			if err != nil {
				return nil, 0, 0, err
			}
			groupID, err = res.LastInsertId()
			if err != nil {
				return nil, 0, 0, err
			}
			createdCount++
			groupCreated = true
		}

		memberResults := make([]WeChatSyncGroupMemberResult, 0, len(item.Members))
		for _, m := range item.Members {
			var memID int64
			var curGroupNick, curWechatName, curRemark, curAccount, curGender, curAvatar, curRegion, curSig, curRole string
			memFound := false

			if m.NativeID != "" {
				err := tx.QueryRowContext(ctx, `
					SELECT id, group_nickname, wechat_name, wechat_remark, wechat_account, gender, avatar_url, region, signature, role
					FROM group_members
					WHERE owner_id = ? AND group_id = ? AND native_id = ?
					LIMIT 1`, ownerID, groupID, m.NativeID,
				).Scan(&memID, &curGroupNick, &curWechatName, &curRemark, &curAccount, &curGender, &curAvatar, &curRegion, &curSig, &curRole)
				if err == nil {
					memFound = true
				} else if !errors.Is(err, sql.ErrNoRows) {
					return nil, 0, 0, err
				}
			}

			if memFound {
				_, err := tx.ExecContext(ctx, `
					UPDATE group_members
					SET group_nickname = ?,
					    wechat_name    = ?,
					    wechat_remark  = ?,
					    wechat_account = ?,
					    gender         = ?,
					    region         = ?,
					    signature      = ?,
					    role           = ?,
					    updated_at     = ?
					WHERE owner_id = ? AND group_id = ? AND id = ?`,
					nonEmptyOr(m.GroupNickname, curGroupNick),
					nonEmptyOr(m.WechatName, curWechatName),
					nonEmptyOr(m.WechatRemark, curRemark),
					nonEmptyOr(m.WechatAccount, curAccount),
					nonEmptyOr(m.Gender, curGender),
					nonEmptyOr(m.Region, curRegion),
					nonEmptyOr(m.Signature, curSig),
					nonEmptyOr(m.Role, curRole),
					now, ownerID, groupID, memID,
				)
				if err != nil {
					return nil, 0, 0, err
				}
				memberResults = append(memberResults, WeChatSyncGroupMemberResult{
					NativeID:  m.NativeID,
					ID:        memID,
					Created:   false,
					AvatarURL: curAvatar,
				})
				continue
			}

			res, err := tx.ExecContext(ctx, `
				INSERT INTO group_members (
					owner_id, group_id, local_key, native_id, group_nickname,
					wechat_name, wechat_remark, wechat_account, gender, avatar_url,
					region, phone, signature, source, role, note, created_at, updated_at
				) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, '', ?, '', ?, '', ?, '', ?, ?)`,
				ownerID, groupID, m.LocalKey, m.NativeID, m.GroupNickname,
				m.WechatName, m.WechatRemark, m.WechatAccount, m.Gender,
				m.Region, m.Signature, m.Role, now, now,
			)
			if err != nil {
				return nil, 0, 0, err
			}
			memID, err = res.LastInsertId()
			if err != nil {
				return nil, 0, 0, err
			}
			memberResults = append(memberResults, WeChatSyncGroupMemberResult{
				NativeID:  m.NativeID,
				ID:        memID,
				Created:   true,
				AvatarURL: "",
			})
		}

		results = append(results, WeChatSyncGroupResult{
			GroupKey:  item.GroupKey,
			ID:        groupID,
			Created:   groupCreated,
			AvatarURL: curAvatar,
			Members:   memberResults,
		})
	}

	if err := tx.Commit(); err != nil {
		return nil, 0, 0, err
	}
	return results, createdCount, updatedCount, nil
}

func (s *Store) SyncCustomerMessages(ctx context.Context, ownerID string, customerID int64, items []WeChatSyncMessageItem) (WeChatSyncMessagesSummary, error) {
	ok, err := s.customerExists(ctx, ownerID, customerID)
	if err != nil {
		return WeChatSyncMessagesSummary{}, err
	}
	if !ok {
		return WeChatSyncMessagesSummary{}, ErrNotFound
	}

	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return WeChatSyncMessagesSummary{}, err
	}
	defer tx.Rollback()

	stmt, err := tx.PrepareContext(ctx, `
		INSERT OR IGNORE INTO messages (owner_id, customer_id, direction, content, image_url, audio_url, video_url, sent_at, created_at, external_id)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`)
	if err != nil {
		return WeChatSyncMessagesSummary{}, err
	}
	defer stmt.Close()

	backfillStmt, err := tx.PrepareContext(ctx, `
		UPDATE messages
		SET image_url = CASE WHEN image_url = '' AND ? <> '' THEN ? ELSE image_url END,
		    audio_url = CASE WHEN audio_url = '' AND ? <> '' THEN ? ELSE audio_url END,
		    video_url = CASE WHEN video_url = '' AND ? <> '' THEN ? ELSE video_url END,
		    content   = CASE
		                  WHEN image_url = '' AND ? <> '' AND content LIKE '%[图片]%' THEN ?
		                  WHEN content LIKE '%[链接]%' AND content NOT LIKE '%http%' AND ? LIKE '%http%' THEN ?
		                  ELSE content
		                END
		WHERE owner_id = ? AND customer_id = ? AND external_id = ? AND external_id <> ''`)
	if err != nil {
		return WeChatSyncMessagesSummary{}, err
	}
	defer backfillStmt.Close()

	createdAt := time.Now().UTC().Format(time.RFC3339Nano)
	summary := WeChatSyncMessagesSummary{}
	for _, item := range items {
		sentAt := item.SentAt.UTC().Format(time.RFC3339Nano)
		res, err := stmt.ExecContext(ctx, ownerID, customerID, item.Direction, item.Content, item.ImageURL, item.AudioURL, item.VideoURL, sentAt, createdAt, item.ExternalID)
		if err != nil {
			return WeChatSyncMessagesSummary{}, err
		}
		n, err := res.RowsAffected()
		if err != nil {
			return WeChatSyncMessagesSummary{}, err
		}
		if n > 0 {
			summary.Inserted++
		} else {
			summary.Skipped++
			if item.ExternalID != "" && (item.ImageURL != "" || item.AudioURL != "" || item.VideoURL != "" || strings.Contains(item.Content, "http")) {
				_, _ = backfillStmt.ExecContext(ctx,
					item.ImageURL, item.ImageURL,
					item.AudioURL, item.AudioURL,
					item.VideoURL, item.VideoURL,
					item.ImageURL, item.Content,
					item.Content, item.Content,
					ownerID, customerID, item.ExternalID)
			}
		}
	}

	if err := tx.Commit(); err != nil {
		return WeChatSyncMessagesSummary{}, err
	}
	return summary, nil
}

func (s *Store) SyncGroupMessages(ctx context.Context, ownerID string, groupID int64, items []WeChatSyncMessageItem) (WeChatSyncMessagesSummary, error) {
	ok, err := s.groupExists(ctx, ownerID, groupID)
	if err != nil {
		return WeChatSyncMessagesSummary{}, err
	}
	if !ok {
		return WeChatSyncMessagesSummary{}, ErrNotFound
	}

	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return WeChatSyncMessagesSummary{}, err
	}
	defer tx.Rollback()

	stmt, err := tx.PrepareContext(ctx, `
		INSERT OR IGNORE INTO group_messages (owner_id, group_id, sender_name, direction, content, image_url, audio_url, video_url, sent_at, created_at, external_id)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`)
	if err != nil {
		return WeChatSyncMessagesSummary{}, err
	}
	defer stmt.Close()

	backfillStmt, err := tx.PrepareContext(ctx, `
		UPDATE group_messages
		SET image_url = CASE WHEN image_url = '' AND ? <> '' THEN ? ELSE image_url END,
		    audio_url = CASE WHEN audio_url = '' AND ? <> '' THEN ? ELSE audio_url END,
		    video_url = CASE WHEN video_url = '' AND ? <> '' THEN ? ELSE video_url END,
		    content   = CASE
		                  WHEN image_url = '' AND ? <> '' AND content LIKE '%[图片]%' THEN ?
		                  WHEN content LIKE '%[链接]%' AND content NOT LIKE '%http%' AND ? LIKE '%http%' THEN ?
		                  ELSE content
		                END
		WHERE owner_id = ? AND group_id = ? AND external_id = ? AND external_id <> ''`)
	if err != nil {
		return WeChatSyncMessagesSummary{}, err
	}
	defer backfillStmt.Close()

	createdAt := time.Now().UTC().Format(time.RFC3339Nano)
	summary := WeChatSyncMessagesSummary{}
	for _, item := range items {
		sentAt := item.SentAt.UTC().Format(time.RFC3339Nano)
		res, err := stmt.ExecContext(ctx, ownerID, groupID, item.SenderName, item.Direction, item.Content, item.ImageURL, item.AudioURL, item.VideoURL, sentAt, createdAt, item.ExternalID)
		if err != nil {
			return WeChatSyncMessagesSummary{}, err
		}
		n, err := res.RowsAffected()
		if err != nil {
			return WeChatSyncMessagesSummary{}, err
		}
		if n > 0 {
			summary.Inserted++
		} else {
			summary.Skipped++
			if item.ExternalID != "" && (item.ImageURL != "" || item.AudioURL != "" || item.VideoURL != "" || strings.Contains(item.Content, "http")) {
				_, _ = backfillStmt.ExecContext(ctx,
					item.ImageURL, item.ImageURL,
					item.AudioURL, item.AudioURL,
					item.VideoURL, item.VideoURL,
					item.ImageURL, item.Content,
					item.Content, item.Content,
					ownerID, groupID, item.ExternalID)
			}
		}
	}

	if err := tx.Commit(); err != nil {
		return WeChatSyncMessagesSummary{}, err
	}
	return summary, nil
}

// FindExistingMessageExternalIDs returns the subset of externalIDs that already exist
// for a customer or group, so clients can skip redundant media decoding and image uploads.
func (s *Store) FindExistingMessageExternalIDs(ctx context.Context, ownerID, scope string, targetID int64, externalIDs []string) ([]string, error) {
	if len(externalIDs) == 0 {
		return []string{}, nil
	}
	table := "messages"
	targetCol := "customer_id"
	if scope == "group" {
		table = "group_messages"
		targetCol = "group_id"
	}

	found := make([]string, 0, len(externalIDs))
	const chunkSize = 400
	for start := 0; start < len(externalIDs); start += chunkSize {
		end := start + chunkSize
		if end > len(externalIDs) {
			end = len(externalIDs)
		}
		chunk := externalIDs[start:end]
		placeholders := strings.Repeat("?,", len(chunk))
		placeholders = placeholders[:len(placeholders)-1]

		args := make([]any, 0, len(chunk)+2)
		args = append(args, ownerID, targetID)
		for _, id := range chunk {
			args = append(args, id)
		}

		rows, err := s.db.QueryContext(ctx,
			`SELECT external_id FROM `+table+`
			 WHERE owner_id = ? AND `+targetCol+` = ? AND external_id IN (`+placeholders+`)
			   AND NOT (image_url = '' AND (content LIKE '%[图片]%' OR content LIKE '%[动画表情%'))
			   AND NOT (audio_url = '' AND content LIKE '%[语音%')
			   AND NOT (image_url = '' AND content LIKE '%[视频%')
			   AND NOT (content LIKE '%[链接]%' AND content NOT LIKE '%http%')`,
			args...,
		)
		if err != nil {
			return nil, err
		}
		for rows.Next() {
			var extID string
			if err := rows.Scan(&extID); err != nil {
				rows.Close()
				return nil, err
			}
			found = append(found, extID)
		}
		rows.Close()
	}
	return found, nil
}
