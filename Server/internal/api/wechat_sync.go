package api

import (
	"net/http"
	"strings"
	"time"
	"unicode/utf8"

	"github.com/gin-gonic/gin"
	"weixinorc/server/internal/auth"
	"weixinorc/server/internal/store"
)

type syncCustomersRequest struct {
	Items []store.WeChatSyncCustomerItem `json:"items"`
}

type syncGroupsRequest struct {
	Items []store.WeChatSyncGroupItem `json:"items"`
}

type syncMessagePayload struct {
	ExternalID string `json:"external_id"`
	SenderName string `json:"sender_name"`
	Direction  string `json:"direction"`
	Content    string `json:"content"`
	ImageURL   string `json:"image_url"`
	AudioURL   string `json:"audio_url"`
	VideoURL   string `json:"video_url"`
	SentAt     string `json:"sent_at"`
}

type syncMessagesRequest struct {
	Items []syncMessagePayload `json:"items"`
}

type existingExternalIDsRequest struct {
	Scope       string   `json:"scope"` // "customer" or "group"
	TargetID    int64    `json:"target_id"`
	ExternalIDs []string `json:"external_ids"`
}

func truncateRunes(s string, max int) string {
	s = strings.TrimSpace(s)
	if !utf8.ValidString(s) {
		s = strings.ToValidUTF8(s, "")
	}
	if utf8.RuneCountInString(s) <= max {
		return s
	}
	runes := []rune(s)
	return string(runes[:max])
}

func (s *server) syncWeChatCustomers(c *gin.Context) {
	var req syncCustomersRequest
	if !bindJSON(c, &req) {
		return
	}
	if len(req.Items) > 2000 {
		c.JSON(http.StatusBadRequest, gin.H{"error": "batch_too_large"})
		return
	}

	sanitized := make([]store.WeChatSyncCustomerItem, 0, len(req.Items))
	for _, it := range req.Items {
		item := store.WeChatSyncCustomerItem{
			NativeID:      truncateRunes(it.NativeID, 128),
			LocalKey:      truncateRunes(it.LocalKey, 128),
			WechatRemark:  truncateRunes(it.WechatRemark, 128),
			WechatName:    truncateRunes(it.WechatName, 128),
			WechatAccount: truncateRunes(it.WechatAccount, 128),
			Gender:        truncateRunes(it.Gender, 16),
			Description:   truncateRunes(it.Description, 2000),
			Tags:          truncateRunes(it.Tags, 256),
			Region:        truncateRunes(it.Region, 128),
			Phone:         truncateRunes(it.Phone, 64),
			Signature:     truncateRunes(it.Signature, 500),
			Source:        truncateRunes(it.Source, 128),
		}
		if item.NativeID == "" && item.WechatAccount == "" && item.WechatName == "" && item.WechatRemark == "" {
			continue
		}
		sanitized = append(sanitized, item)
	}

	ownerID := auth.IdentityFromContext(c).EmployeeID
	results, created, updated, err := s.store.SyncWeChatCustomers(c.Request.Context(), ownerID, sanitized)
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusOK, gin.H{
		"items":   results,
		"created": created,
		"updated": updated,
	})
}

func (s *server) syncWeChatGroups(c *gin.Context) {
	var req syncGroupsRequest
	if !bindJSON(c, &req) {
		return
	}
	if len(req.Items) > 500 {
		c.JSON(http.StatusBadRequest, gin.H{"error": "batch_too_large"})
		return
	}

	sanitized := make([]store.WeChatSyncGroupItem, 0, len(req.Items))
	for _, g := range req.Items {
		groupName := truncateRunes(g.GroupName, 128)
		groupKey := truncateRunes(g.GroupKey, 128)
		if groupName == "" {
			groupName = groupKey
		}
		if groupName == "" {
			continue
		}
		memberCount := g.MemberCount
		if memberCount < 0 {
			memberCount = 0
		}
		members := make([]store.WeChatSyncGroupMemberItem, 0, len(g.Members))
		for _, m := range g.Members {
			if len(members) >= 1000 {
				break
			}
			mem := store.WeChatSyncGroupMemberItem{
				NativeID:      truncateRunes(m.NativeID, 128),
				LocalKey:      truncateRunes(m.LocalKey, 128),
				GroupNickname: truncateRunes(m.GroupNickname, 128),
				WechatName:    truncateRunes(m.WechatName, 128),
				WechatRemark:  truncateRunes(m.WechatRemark, 128),
				WechatAccount: truncateRunes(m.WechatAccount, 128),
				Gender:        truncateRunes(m.Gender, 16),
				Region:        truncateRunes(m.Region, 128),
				Signature:     truncateRunes(m.Signature, 500),
				Role:          truncateRunes(m.Role, 64),
			}
			if mem.NativeID == "" && mem.WechatName == "" && mem.GroupNickname == "" {
				continue
			}
			members = append(members, mem)
		}
		sanitized = append(sanitized, store.WeChatSyncGroupItem{
			GroupKey:    groupKey,
			GroupName:   groupName,
			MyNickname:  truncateRunes(g.MyNickname, 128),
			MemberCount: memberCount,
			MembersText: truncateRunes(g.MembersText, 4000),
			Members:     members,
		})
	}

	ownerID := auth.IdentityFromContext(c).EmployeeID
	results, created, updated, err := s.store.SyncWeChatGroups(c.Request.Context(), ownerID, sanitized)
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusOK, gin.H{
		"items":   results,
		"created": created,
		"updated": updated,
	})
}

func parseBatchMessages(ownerID string, raw []syncMessagePayload, includeSender bool) ([]store.WeChatSyncMessageItem, bool) {
	items := make([]store.WeChatSyncMessageItem, 0, len(raw))
	for _, m := range raw {
		dir := strings.TrimSpace(m.Direction)
		if dir != "incoming" && dir != "outgoing" {
			return nil, false
		}
		imgURL := strings.TrimSpace(m.ImageURL)
		if !validChatImageURL(ownerID, imgURL) {
			return nil, false
		}
		audioURL := strings.TrimSpace(m.AudioURL)
		if !validChatAudioURL(ownerID, audioURL) {
			return nil, false
		}
		videoURL := strings.TrimSpace(m.VideoURL)
		if !validChatVideoURL(ownerID, videoURL) {
			return nil, false
		}
		content := truncateRunes(m.Content, 10000)
		if content == "" && imgURL == "" && audioURL == "" && videoURL == "" {
			continue
		}
		sentAt := time.Now().UTC()
		if strings.TrimSpace(m.SentAt) != "" {
			parsed, err := time.Parse(time.RFC3339, strings.TrimSpace(m.SentAt))
			if err != nil {
				return nil, false
			}
			sentAt = parsed
		}
		item := store.WeChatSyncMessageItem{
			ExternalID: truncateRunes(m.ExternalID, 128),
			Direction:  dir,
			Content:    content,
			ImageURL:   imgURL,
			AudioURL:   audioURL,
			VideoURL:   videoURL,
			SentAt:     sentAt,
		}
		if includeSender {
			item.SenderName = truncateRunes(m.SenderName, 128)
		}
		items = append(items, item)
	}
	return items, true
}

func (s *server) syncCustomerMessages(c *gin.Context) {
	customerID, ok := parsePathID(c, "id", "invalid_customer_id")
	if !ok {
		return
	}
	var req syncMessagesRequest
	if !bindJSON(c, &req) {
		return
	}
	if len(req.Items) > 5000 {
		c.JSON(http.StatusBadRequest, gin.H{"error": "batch_too_large"})
		return
	}
	ownerID := auth.IdentityFromContext(c).EmployeeID
	items, valid := parseBatchMessages(ownerID, req.Items, false)
	if !valid {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_message_item"})
		return
	}
	summary, err := s.store.SyncCustomerMessages(c.Request.Context(), ownerID, customerID, items)
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusOK, summary)
}

func (s *server) syncGroupMessages(c *gin.Context) {
	groupID, ok := parsePathID(c, "id", "invalid_group_id")
	if !ok {
		return
	}
	var req syncMessagesRequest
	if !bindJSON(c, &req) {
		return
	}
	if len(req.Items) > 5000 {
		c.JSON(http.StatusBadRequest, gin.H{"error": "batch_too_large"})
		return
	}
	ownerID := auth.IdentityFromContext(c).EmployeeID
	items, valid := parseBatchMessages(ownerID, req.Items, true)
	if !valid {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_message_item"})
		return
	}
	summary, err := s.store.SyncGroupMessages(c.Request.Context(), ownerID, groupID, items)
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusOK, summary)
}

func (s *server) checkExistingMessageIDs(c *gin.Context) {
	var req existingExternalIDsRequest
	if !bindJSON(c, &req) {
		return
	}
	if req.Scope != "customer" && req.Scope != "group" {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_scope"})
		return
	}
	if req.TargetID <= 0 {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_target_id"})
		return
	}
	if len(req.ExternalIDs) > 5000 {
		c.JSON(http.StatusBadRequest, gin.H{"error": "batch_too_large"})
		return
	}

	ownerID := auth.IdentityFromContext(c).EmployeeID
	existing, err := s.store.FindExistingMessageExternalIDs(c.Request.Context(), ownerID, req.Scope, req.TargetID, req.ExternalIDs)
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusOK, gin.H{"existing_ids": existing})
}
