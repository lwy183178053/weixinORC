package store

import (
	"context"
	"sort"
	"strings"
)

type ChatSearchHit struct {
	Scope       string `json:"scope"` // "customer" or "group"
	TargetID    int64  `json:"target_id"`
	TargetName  string `json:"target_name"`
	Tags        string `json:"tags,omitempty"`
	MessageID   int64  `json:"message_id"`
	Direction   string `json:"direction"`
	SenderName  string `json:"sender_name"`
	Content     string `json:"content"`
	HasImage    bool   `json:"has_image"`
	SentAt      string `json:"sent_at"`
}

type RecentChatSummary struct {
	Scope         string `json:"scope"` // "customer" or "group"
	TargetID      int64  `json:"target_id"`
	TargetName    string `json:"target_name"`
	Tags          string `json:"tags,omitempty"`
	MessageCount  int    `json:"message_count"`
	LastDirection string `json:"last_direction"`
	LastSender    string `json:"last_sender"`
	LastMessage   string `json:"last_message"`
	LastMessageAt string `json:"last_message_at"`
	NeedsReply    bool   `json:"needs_reply"`
}

// SearchChatMessages searches across customer private chats and/or group chats by keyword, tag,
// specific customer/group, and optional ISO-8601 time range.
func (s *Store) SearchChatMessages(
	ctx context.Context,
	ownerID string,
	query string,
	scope string, // "all", "customer", "group"
	tag string,
	customerID int64,
	groupID int64,
	sinceISO string,
	untilISO string,
	limit int,
) ([]ChatSearchHit, error) {
	if limit <= 0 {
		limit = 40
	}
	if limit > 100 {
		limit = 100
	}
	scope = strings.ToLower(strings.TrimSpace(scope))
	if scope == "" {
		scope = "all"
	}
	trimmedQuery := strings.TrimSpace(query)
	likeQuery := "%" + trimmedQuery + "%"
	likeTag := "%" + strings.TrimSpace(tag) + "%"
	useFTS := s.hasFTS5 && len([]rune(trimmedQuery)) >= 3
	ftsMatch := `"` + strings.ReplaceAll(trimmedQuery, `"`, `""`) + `"`
	results := make([]ChatSearchHit, 0, limit)

	if (scope == "all" || scope == "customer") && groupID == 0 {
		q := `
			SELECT c.id,
			       COALESCE(NULLIF(c.wechat_remark, ''), NULLIF(c.wechat_name, ''), NULLIF(c.wechat_account, ''), '客户#' || c.id),
			       c.tags,
			       m.id, m.direction, m.content, m.image_url, m.sent_at
			FROM messages m
			JOIN customers c ON c.id = m.customer_id AND c.owner_id = m.owner_id`
		if useFTS {
			q += ` JOIN messages_fts fts ON fts.rowid = m.id`
		}
		q += ` WHERE m.owner_id = ?`
		args := []any{ownerID}
		if customerID > 0 {
			q += ` AND c.id = ?`
			args = append(args, customerID)
		}
		if strings.TrimSpace(tag) != "" {
			q += ` AND LOWER(c.tags) LIKE LOWER(?)`
			args = append(args, likeTag)
		}
		if trimmedQuery != "" {
			if useFTS {
				q += ` AND messages_fts MATCH ?`
				args = append(args, ftsMatch)
			} else {
				q += ` AND LOWER(m.content) LIKE LOWER(?)`
				args = append(args, likeQuery)
			}
		}
		if strings.TrimSpace(sinceISO) != "" {
			q += ` AND m.sent_at >= ?`
			args = append(args, strings.TrimSpace(sinceISO))
		}
		if strings.TrimSpace(untilISO) != "" {
			q += ` AND m.sent_at <= ?`
			args = append(args, strings.TrimSpace(untilISO))
		}
		q += ` ORDER BY m.sent_at DESC, m.id DESC LIMIT ?`
		args = append(args, limit)

		rows, err := s.db.QueryContext(ctx, q, args...)
		if err != nil {
			return nil, err
		}
		for rows.Next() {
			var hit ChatSearchHit
			var imgURL string
			if err := rows.Scan(&hit.TargetID, &hit.TargetName, &hit.Tags, &hit.MessageID, &hit.Direction, &hit.Content, &imgURL, &hit.SentAt); err != nil {
				rows.Close()
				return nil, err
			}
			hit.Scope = "customer"
			hit.HasImage = strings.TrimSpace(imgURL) != ""
			if hit.Direction == "outgoing" {
				hit.SenderName = "我方"
			} else {
				hit.SenderName = hit.TargetName
			}
			if strings.TrimSpace(hit.Content) == "" && hit.HasImage {
				hit.Content = "[图片]"
			}
			results = append(results, hit)
		}
		rows.Close()
	}

	if (scope == "all" || scope == "group") && customerID == 0 && strings.TrimSpace(tag) == "" {
		rem := limit - len(results)
		if rem < 20 && scope == "all" {
			rem = 20
		}
		if rem > 0 {
			q := `
				SELECT g.id, g.group_name, m.id, m.direction, m.sender_name, m.content, m.image_url, m.sent_at
				FROM group_messages m
				JOIN groups g ON g.id = m.group_id AND g.owner_id = m.owner_id`
			if useFTS {
				q += ` JOIN group_messages_fts fts ON fts.rowid = m.id`
			}
			q += ` WHERE m.owner_id = ?`
			args := []any{ownerID}
			if groupID > 0 {
				q += ` AND g.id = ?`
				args = append(args, groupID)
			}
			if trimmedQuery != "" {
				if useFTS {
					q += ` AND group_messages_fts MATCH ?`
					args = append(args, ftsMatch)
				} else {
					q += ` AND (LOWER(m.content) LIKE LOWER(?) OR LOWER(m.sender_name) LIKE LOWER(?))`
					args = append(args, likeQuery, likeQuery)
				}
			}
			if strings.TrimSpace(sinceISO) != "" {
				q += ` AND m.sent_at >= ?`
				args = append(args, strings.TrimSpace(sinceISO))
			}
			if strings.TrimSpace(untilISO) != "" {
				q += ` AND m.sent_at <= ?`
				args = append(args, strings.TrimSpace(untilISO))
			}
			q += ` ORDER BY m.sent_at DESC, m.id DESC LIMIT ?`
			args = append(args, rem)

			rows, err := s.db.QueryContext(ctx, q, args...)
			if err != nil {
				return nil, err
			}
			for rows.Next() {
				var hit ChatSearchHit
				var imgURL string
				if err := rows.Scan(&hit.TargetID, &hit.TargetName, &hit.MessageID, &hit.Direction, &hit.SenderName, &hit.Content, &imgURL, &hit.SentAt); err != nil {
					rows.Close()
					return nil, err
				}
				hit.Scope = "group"
				hit.HasImage = strings.TrimSpace(imgURL) != ""
				if strings.TrimSpace(hit.SenderName) == "" {
					if hit.Direction == "outgoing" {
						hit.SenderName = "我方"
					} else {
						hit.SenderName = "群成员"
					}
				}
				if strings.TrimSpace(hit.Content) == "" && hit.HasImage {
					hit.Content = "[图片]"
				}
				results = append(results, hit)
			}
			rows.Close()
		}
	}

	sort.Slice(results, func(i, j int) bool {
		if results[i].SentAt != results[j].SentAt {
			return results[i].SentAt > results[j].SentAt
		}
		return results[i].MessageID > results[j].MessageID
	})

	if len(results) > limit {
		results = results[:limit]
	}
	return results, nil
}

// ListRecentActiveChats lists the most recently active customer & group conversations
// along with message counts and whether the last message was incoming (needs reply).
func (s *Store) ListRecentActiveChats(ctx context.Context, ownerID, scope string, limit int) ([]RecentChatSummary, error) {
	if limit <= 0 {
		limit = 20
	}
	if limit > 50 {
		limit = 50
	}
	scope = strings.ToLower(strings.TrimSpace(scope))
	if scope == "" {
		scope = "all"
	}

	out := make([]RecentChatSummary, 0, limit)

	if scope == "all" || scope == "customer" {
		rows, err := s.db.QueryContext(ctx, `
			SELECT c.id,
			       COALESCE(NULLIF(c.wechat_remark, ''), NULLIF(c.wechat_name, ''), NULLIF(c.wechat_account, ''), '客户#' || c.id),
			       c.tags,
			       (SELECT COUNT(*) FROM messages mc WHERE mc.owner_id = c.owner_id AND mc.customer_id = c.id) AS cnt,
			       m.direction,
			       CASE WHEN m.content <> '' THEN m.content WHEN m.image_url <> '' THEN '[图片]' ELSE '' END,
			       m.sent_at
			FROM customers c
			JOIN messages m ON m.id = (
				SELECT m2.id FROM messages m2
				WHERE m2.owner_id = c.owner_id AND m2.customer_id = c.id
				ORDER BY m2.sent_at DESC, m2.id DESC LIMIT 1
			)
			WHERE c.owner_id = ?
			ORDER BY m.sent_at DESC, m.id DESC
			LIMIT ?`, ownerID, limit)
		if err != nil {
			return nil, err
		}
		for rows.Next() {
			var item RecentChatSummary
			if err := rows.Scan(&item.TargetID, &item.TargetName, &item.Tags, &item.MessageCount, &item.LastDirection, &item.LastMessage, &item.LastMessageAt); err != nil {
				rows.Close()
				return nil, err
			}
			item.Scope = "customer"
			item.NeedsReply = item.LastDirection == "incoming"
			if item.LastDirection == "outgoing" {
				item.LastSender = "我方"
			} else {
				item.LastSender = item.TargetName
			}
			out = append(out, item)
		}
		rows.Close()
	}

	if scope == "all" || scope == "group" {
		rows, err := s.db.QueryContext(ctx, `
			SELECT g.id,
			       g.group_name,
			       (SELECT COUNT(*) FROM group_messages mc WHERE mc.owner_id = g.owner_id AND mc.group_id = g.id) AS cnt,
			       m.direction,
			       m.sender_name,
			       CASE WHEN m.content <> '' THEN m.content WHEN m.image_url <> '' THEN '[图片]' ELSE '' END,
			       m.sent_at
			FROM groups g
			JOIN group_messages m ON m.id = (
				SELECT m2.id FROM group_messages m2
				WHERE m2.owner_id = g.owner_id AND m2.group_id = g.id
				ORDER BY m2.sent_at DESC, m2.id DESC LIMIT 1
			)
			WHERE g.owner_id = ?
			ORDER BY m.sent_at DESC, m.id DESC
			LIMIT ?`, ownerID, limit)
		if err != nil {
			return nil, err
		}
		for rows.Next() {
			var item RecentChatSummary
			if err := rows.Scan(&item.TargetID, &item.TargetName, &item.MessageCount, &item.LastDirection, &item.LastSender, &item.LastMessage, &item.LastMessageAt); err != nil {
				rows.Close()
				return nil, err
			}
			item.Scope = "group"
			item.NeedsReply = item.LastDirection == "incoming"
			if strings.TrimSpace(item.LastSender) == "" {
				if item.LastDirection == "outgoing" {
					item.LastSender = "我方"
				} else {
					item.LastSender = "群成员"
				}
			}
			out = append(out, item)
		}
		rows.Close()
	}

	return out, nil
}
