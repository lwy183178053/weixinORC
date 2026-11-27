package api

import (
	"crypto/rand"
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/gin-gonic/gin"
	"weixinorc/server/internal/auth"
	"weixinorc/server/internal/store"
)

type createGroupRequest struct {
	OwnerWxid        string `json:"owner_wxid"`
	GroupName        string `json:"group_name"`
	GroupKey         string `json:"group_key"`
	MyNickname       string `json:"my_nickname"`
	MemberCount      int    `json:"member_count"`
	MembersText      string `json:"members_text"`
	Announcement     string `json:"announcement"`
	Note             string `json:"note"`
	AutoReplyEnabled bool   `json:"auto_reply_enabled"`
	WelcomeEnabled   bool   `json:"welcome_enabled"`
	WelcomeTemplate  string `json:"welcome_template"`
}

type patchGroupRequest struct {
	OwnerWxid        *string `json:"owner_wxid"`
	GroupName        *string `json:"group_name"`
	GroupKey         *string `json:"group_key"`
	MyNickname       *string `json:"my_nickname"`
	MemberCount      *int    `json:"member_count"`
	MembersText      *string `json:"members_text"`
	Announcement     *string `json:"announcement"`
	Note             *string `json:"note"`
	AutoReplyEnabled *bool   `json:"auto_reply_enabled"`
	WelcomeEnabled   *bool   `json:"welcome_enabled"`
	WelcomeTemplate  *string `json:"welcome_template"`
}

type createGroupMessageRequest struct {
	SenderName string `json:"sender_name"`
	Direction  string `json:"direction"`
	Content    string `json:"content"`
	ImageURL   string `json:"image_url"`
	SentAt     string `json:"sent_at"`
}

type updateGroupMessageRequest struct {
	SenderName string `json:"sender_name"`
	Direction  string `json:"direction"`
	Content    string `json:"content"`
	ImageURL   string `json:"image_url"`
}

func (s *server) listGroups(c *gin.Context) {
	cursor, limit, ok := parsePagination(c)
	if !ok {
		return
	}
	ownerID := auth.IdentityFromContext(c).EmployeeID
	items, next, err := s.store.ListGroups(c.Request.Context(), ownerID, cursor, limit)
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusOK, gin.H{"items": items, "next_cursor": next})
}

func (s *server) createGroup(c *gin.Context) {
	var req createGroupRequest
	if !bindJSON(c, &req) {
		return
	}
	req.OwnerWxid = strings.TrimSpace(req.OwnerWxid)
	req.GroupName = strings.TrimSpace(req.GroupName)
	req.GroupKey = strings.TrimSpace(req.GroupKey)
	req.MyNickname = strings.TrimSpace(req.MyNickname)
	if req.GroupName == "" {
		c.JSON(http.StatusBadRequest, gin.H{"error": "group_name_required"})
		return
	}
	if req.MemberCount < 0 {
		req.MemberCount = 0
	}
	if !validText(req.OwnerWxid, 128) || !validText(req.GroupName, 128) || !validText(req.GroupKey, 128) || !validText(req.MyNickname, 128) ||
		!validText(req.MembersText, 4000) || !validText(req.Announcement, 4000) || !validText(req.Note, 2000) || !validText(req.WelcomeTemplate, 1000) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "field_too_long"})
		return
	}

	ownerID := auth.IdentityFromContext(c).EmployeeID
	group, err := s.store.CreateGroup(c.Request.Context(), ownerID, store.GroupInput{
		OwnerWxid:        req.OwnerWxid,
		GroupName:        req.GroupName,
		GroupKey:         req.GroupKey,
		MyNickname:       req.MyNickname,
		MemberCount:      req.MemberCount,
		MembersText:      req.MembersText,
		Announcement:     req.Announcement,
		Note:             req.Note,
		AutoReplyEnabled: req.AutoReplyEnabled,
		WelcomeEnabled:   req.WelcomeEnabled,
		WelcomeTemplate:  req.WelcomeTemplate,
	})
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusCreated, group)
}

func (s *server) getGroup(c *gin.Context) {
	id, ok := parsePathID(c, "id", "invalid_group_id")
	if !ok {
		return
	}
	ownerID := auth.IdentityFromContext(c).EmployeeID
	group, err := s.store.GetGroup(c.Request.Context(), ownerID, id)
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusOK, group)
}

func (s *server) updateGroup(c *gin.Context) {
	id, ok := parsePathID(c, "id", "invalid_group_id")
	if !ok {
		return
	}
	var req patchGroupRequest
	if !bindJSON(c, &req) {
		return
	}
	if req.OwnerWxid == nil && req.GroupName == nil && req.GroupKey == nil && req.MyNickname == nil && req.MemberCount == nil && req.MembersText == nil && req.Announcement == nil && req.Note == nil && req.AutoReplyEnabled == nil && req.WelcomeEnabled == nil && req.WelcomeTemplate == nil {
		c.JSON(http.StatusBadRequest, gin.H{"error": "empty_patch"})
		return
	}
	if !trimAndCheck(&req.OwnerWxid, 128) || !trimAndCheck(&req.GroupName, 128) || !trimAndCheck(&req.GroupKey, 128) || !trimAndCheck(&req.MyNickname, 128) ||
		(req.MembersText != nil && !validText(*req.MembersText, 4000)) ||
		(req.Announcement != nil && !validText(*req.Announcement, 4000)) ||
		(req.Note != nil && !validText(*req.Note, 2000)) ||
		(req.WelcomeTemplate != nil && !validText(*req.WelcomeTemplate, 1000)) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "field_too_long"})
		return
	}
	if req.GroupName != nil && *req.GroupName == "" {
		c.JSON(http.StatusBadRequest, gin.H{"error": "group_name_required"})
		return
	}
	if req.MemberCount != nil && *req.MemberCount < 0 {
		zero := 0
		req.MemberCount = &zero
	}

	ownerID := auth.IdentityFromContext(c).EmployeeID
	group, err := s.store.UpdateGroup(c.Request.Context(), ownerID, id, store.GroupPatch{
		OwnerWxid:        req.OwnerWxid,
		GroupName:        req.GroupName,
		GroupKey:         req.GroupKey,
		MyNickname:       req.MyNickname,
		MemberCount:      req.MemberCount,
		MembersText:      req.MembersText,
		Announcement:     req.Announcement,
		Note:             req.Note,
		AutoReplyEnabled: req.AutoReplyEnabled,
		WelcomeEnabled:   req.WelcomeEnabled,
		WelcomeTemplate:  req.WelcomeTemplate,
	})
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusOK, group)
}

func (s *server) deleteGroup(c *gin.Context) {
	id, ok := parsePathID(c, "id", "invalid_group_id")
	if !ok {
		return
	}
	ownerID := auth.IdentityFromContext(c).EmployeeID
	avatarURL, err := s.store.DeleteGroupReturningAvatar(c.Request.Context(), ownerID, id)
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	s.removeAvatarFile(ownerID, avatarURL)
	c.Status(http.StatusNoContent)
}

func (s *server) uploadGroupAvatar(c *gin.Context) {
	id, ok := parsePathID(c, "id", "invalid_group_id")
	if !ok {
		return
	}
	var req uploadAvatarRequest
	if !bindJSON(c, &req) {
		return
	}
	b64 := strings.TrimSpace(req.ImageBase64)
	if idx := strings.Index(b64, ","); idx >= 0 && strings.HasPrefix(b64[:idx], "data:") {
		b64 = b64[idx+1:]
	}
	if b64 == "" {
		c.JSON(http.StatusBadRequest, gin.H{"error": "empty_avatar"})
		return
	}
	raw, err := base64.StdEncoding.DecodeString(b64)
	if err != nil || len(raw) == 0 || len(raw) > 2<<20 {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_avatar_image"})
		return
	}
	ext, _, validImg := detectImageExt(raw)
	if !validImg {
		c.JSON(http.StatusBadRequest, gin.H{"error": "unsupported_image_format"})
		return
	}

	ownerID := auth.IdentityFromContext(c).EmployeeID
	var nonce [6]byte
	if _, err := rand.Read(nonce[:]); err != nil {
		c.JSON(http.StatusInternalServerError, gin.H{"error": "avatar_save_failed"})
		return
	}
	filename := fmt.Sprintf("%sg%d_%d_%s%s", ownerAvatarPrefix(ownerID), id, time.Now().UnixNano(), hex.EncodeToString(nonce[:]), ext)
	fullPath := filepath.Join(s.store.AvatarDir(), filename)
	if err := os.WriteFile(fullPath, raw, 0o640); err != nil {
		c.JSON(http.StatusInternalServerError, gin.H{"error": "avatar_save_failed"})
		return
	}

	avatarURL := "/api/v1/avatars/" + filename
	updated, prevURL, err := s.store.SetGroupAvatar(c.Request.Context(), ownerID, id, avatarURL)
	if err != nil {
		_ = os.Remove(fullPath)
		writeStoreErr(c, err)
		return
	}
	if prevURL != "" && prevURL != avatarURL {
		s.removeAvatarFile(ownerID, prevURL)
	}
	c.JSON(http.StatusOK, updated)
}

func (s *server) deleteGroupAvatar(c *gin.Context) {
	id, ok := parsePathID(c, "id", "invalid_group_id")
	if !ok {
		return
	}
	ownerID := auth.IdentityFromContext(c).EmployeeID
	updated, prevURL, err := s.store.SetGroupAvatar(c.Request.Context(), ownerID, id, "")
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	s.removeAvatarFile(ownerID, prevURL)
	c.JSON(http.StatusOK, updated)
}

func (s *server) listGroupMessages(c *gin.Context) {
	groupID, ok := parsePathID(c, "id", "invalid_group_id")
	if !ok {
		return
	}
	cursor, limit, ok := parsePagination(c)
	if !ok {
		return
	}
	ownerID := auth.IdentityFromContext(c).EmployeeID
	items, next, err := s.store.ListGroupMessages(c.Request.Context(), ownerID, groupID, cursor, limit)
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusOK, gin.H{"items": items, "next_cursor": next})
}

func (s *server) createGroupMessage(c *gin.Context) {
	groupID, ok := parsePathID(c, "id", "invalid_group_id")
	if !ok {
		return
	}
	var req createGroupMessageRequest
	if !bindJSON(c, &req) {
		return
	}
	if req.Direction != "incoming" && req.Direction != "outgoing" {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_direction"})
		return
	}
	ownerID := auth.IdentityFromContext(c).EmployeeID
	req.ImageURL = strings.TrimSpace(req.ImageURL)
	if !validChatImageURL(ownerID, req.ImageURL) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_image_url"})
		return
	}
	if (strings.TrimSpace(req.Content) == "" && req.ImageURL == "") || !validText(req.Content, 10000) || !validText(req.SenderName, 128) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_content"})
		return
	}

	sentAt, ok := parseSentAt(c, req.SentAt)
	if !ok {
		return
	}

	msg, err := s.store.CreateGroupMessage(c.Request.Context(), ownerID, groupID, store.GroupMessageInput{
		SenderName: strings.TrimSpace(req.SenderName),
		Direction:  req.Direction,
		Content:    req.Content,
		ImageURL:   req.ImageURL,
		SentAt:     sentAt,
	})
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusCreated, msg)
}

func (s *server) updateGroupMessage(c *gin.Context) {
	groupID, ok := parsePathID(c, "id", "invalid_group_id")
	if !ok {
		return
	}
	msgID, ok := parsePathID(c, "msgId", "invalid_message_id")
	if !ok {
		return
	}
	var req updateGroupMessageRequest
	if !bindJSON(c, &req) {
		return
	}
	if req.Direction != "incoming" && req.Direction != "outgoing" {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_direction"})
		return
	}
	ownerID := auth.IdentityFromContext(c).EmployeeID
	req.ImageURL = strings.TrimSpace(req.ImageURL)
	if !validChatImageURL(ownerID, req.ImageURL) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_image_url"})
		return
	}
	if (strings.TrimSpace(req.Content) == "" && req.ImageURL == "") || !validText(req.Content, 10000) || !validText(req.SenderName, 128) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_content"})
		return
	}

	msg, err := s.store.UpdateGroupMessage(c.Request.Context(), ownerID, groupID, msgID, strings.TrimSpace(req.SenderName), req.Direction, req.Content, req.ImageURL)
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusOK, msg)
}
