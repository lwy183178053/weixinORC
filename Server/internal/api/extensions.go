package api

import (
	"crypto/rand"
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"github.com/gin-gonic/gin"
	"weixinorc/server/internal/auth"
	"weixinorc/server/internal/store"
)

// ---- WeChat Accounts ----

type upsertWeChatAccountRequest struct {
	AccountType string `json:"account_type"`
	WechatID    string `json:"wechat_id"`
	Alias       string `json:"alias"`
	Nickname    string `json:"nickname"`
	Region      string `json:"region"`
	Signature   string `json:"signature"`
	Status      string `json:"status"`
}

type patchWeChatAccountRequest struct {
	WechatID  *string `json:"wechat_id"`
	Alias     *string `json:"alias"`
	Nickname  *string `json:"nickname"`
	Region    *string `json:"region"`
	Signature *string `json:"signature"`
	Status    *string `json:"status"`
	Heartbeat bool    `json:"heartbeat"`
}

func validWeChatStatus(s string) bool {
	return s == "online" || s == "offline" || s == "disconnected" || s == "paused"
}

func (s *server) listWeChatAccounts(c *gin.Context) {
	ownerID := auth.IdentityFromContext(c).EmployeeID
	items, err := s.store.ListWeChatAccounts(c.Request.Context(), ownerID)
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusOK, gin.H{"items": items})
}

func (s *server) upsertWeChatAccount(c *gin.Context) {
	var req upsertWeChatAccountRequest
	if !bindJSON(c, &req) {
		return
	}
	req.AccountType = strings.TrimSpace(req.AccountType)
	req.WechatID = strings.TrimSpace(req.WechatID)
	req.Alias = strings.TrimSpace(req.Alias)
	req.Nickname = strings.TrimSpace(req.Nickname)
	req.Region = strings.TrimSpace(req.Region)
	req.Signature = strings.TrimSpace(req.Signature)
	req.Status = strings.TrimSpace(req.Status)
	if req.Status == "" {
		req.Status = "online"
	}

	if req.AccountType != "personal" && req.AccountType != "enterprise" {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_account_type"})
		return
	}
	if req.WechatID == "" {
		c.JSON(http.StatusBadRequest, gin.H{"error": "wechat_id_required"})
		return
	}
	if !validWeChatStatus(req.Status) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_status"})
		return
	}
	if !validText(req.WechatID, 128) || !validText(req.Alias, 128) || !validText(req.Nickname, 128) ||
		!validText(req.Region, 128) || !validText(req.Signature, 500) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "field_too_long"})
		return
	}

	ownerID := auth.IdentityFromContext(c).EmployeeID
	acc, err := s.store.UpsertWeChatAccount(c.Request.Context(), ownerID, store.WeChatAccountInput{
		AccountType: req.AccountType,
		WechatID:    req.WechatID,
		Alias:       req.Alias,
		Nickname:    req.Nickname,
		Region:      req.Region,
		Signature:   req.Signature,
		Status:      req.Status,
	})
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	_, _ = s.store.CreateAuditLog(c.Request.Context(), ownerID, "bind_wechat_account", "wechat_account", strconv.FormatInt(acc.ID, 10), req.AccountType+":"+req.WechatID)
	c.JSON(http.StatusOK, acc)
}

func (s *server) updateWeChatAccount(c *gin.Context) {
	id, ok := parsePathID(c, "id", "invalid_account_id")
	if !ok {
		return
	}
	var req patchWeChatAccountRequest
	if !bindJSON(c, &req) {
		return
	}
	if req.WechatID == nil && req.Alias == nil && req.Nickname == nil && req.Region == nil && req.Signature == nil && req.Status == nil && !req.Heartbeat {
		c.JSON(http.StatusBadRequest, gin.H{"error": "empty_patch"})
		return
	}
	if !trimAndCheck(&req.WechatID, 128) || !trimAndCheck(&req.Alias, 128) || !trimAndCheck(&req.Nickname, 128) ||
		!trimAndCheck(&req.Region, 128) || !trimAndCheck(&req.Signature, 500) || !trimAndCheck(&req.Status, 32) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "field_too_long"})
		return
	}
	if req.Status != nil && !validWeChatStatus(*req.Status) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_status"})
		return
	}

	ownerID := auth.IdentityFromContext(c).EmployeeID
	acc, err := s.store.UpdateWeChatAccount(c.Request.Context(), ownerID, id, store.WeChatAccountPatch{
		WechatID:  req.WechatID,
		Alias:     req.Alias,
		Nickname:  req.Nickname,
		Region:    req.Region,
		Signature: req.Signature,
		Status:    req.Status,
		Heartbeat: req.Heartbeat,
	})
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusOK, acc)
}

func (s *server) uploadWeChatAccountAvatar(c *gin.Context) {
	id, ok := parsePathID(c, "id", "invalid_account_id")
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
	filename := fmt.Sprintf("%swa%d_%d_%s%s", ownerAvatarPrefix(ownerID), id, time.Now().UnixNano(), hex.EncodeToString(nonce[:]), ext)
	fullPath := filepath.Join(s.store.AvatarDir(), filename)
	if err := os.WriteFile(fullPath, raw, 0o640); err != nil {
		c.JSON(http.StatusInternalServerError, gin.H{"error": "avatar_save_failed"})
		return
	}

	avatarURL := "/api/v1/avatars/" + filename
	updated, prevURL, err := s.store.SetWeChatAccountAvatar(c.Request.Context(), ownerID, id, avatarURL)
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

func (s *server) deleteWeChatAccount(c *gin.Context) {
	id, ok := parsePathID(c, "id", "invalid_account_id")
	if !ok {
		return
	}
	ownerID := auth.IdentityFromContext(c).EmployeeID
	avatarURL, err := s.store.DeleteWeChatAccountReturningAvatar(c.Request.Context(), ownerID, id)
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	s.removeAvatarFile(ownerID, avatarURL)
	_, _ = s.store.CreateAuditLog(c.Request.Context(), ownerID, "unbind_wechat_account", "wechat_account", strconv.FormatInt(id, 10), "")
	c.Status(http.StatusNoContent)
}

// ---- Group Members ----

type createGroupMemberRequest struct {
	LocalKey      string `json:"local_key"`
	NativeID      string `json:"native_id"`
	GroupNickname string `json:"group_nickname"`
	WechatName    string `json:"wechat_name"`
	WechatRemark  string `json:"wechat_remark"`
	WechatAccount string `json:"wechat_account"`
	Gender        string `json:"gender"`
	Region        string `json:"region"`
	Phone         string `json:"phone"`
	Signature     string `json:"signature"`
	Source        string `json:"source"`
	Role          string `json:"role"`
	Note          string `json:"note"`
}

type patchGroupMemberRequest struct {
	LocalKey      *string `json:"local_key"`
	NativeID      *string `json:"native_id"`
	GroupNickname *string `json:"group_nickname"`
	WechatName    *string `json:"wechat_name"`
	WechatRemark  *string `json:"wechat_remark"`
	WechatAccount *string `json:"wechat_account"`
	Gender        *string `json:"gender"`
	Region        *string `json:"region"`
	Phone         *string `json:"phone"`
	Signature     *string `json:"signature"`
	Source        *string `json:"source"`
	Role          *string `json:"role"`
	Note          *string `json:"note"`
}

func (s *server) listGroupMembers(c *gin.Context) {
	groupID, ok := parsePathID(c, "id", "invalid_group_id")
	if !ok {
		return
	}
	cursor, limit, ok := parsePagination(c)
	if !ok {
		return
	}
	ownerID := auth.IdentityFromContext(c).EmployeeID
	items, next, err := s.store.ListGroupMembers(c.Request.Context(), ownerID, groupID, cursor, limit)
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusOK, gin.H{"items": items, "next_cursor": next})
}

func (s *server) createGroupMember(c *gin.Context) {
	groupID, ok := parsePathID(c, "id", "invalid_group_id")
	if !ok {
		return
	}
	var req createGroupMemberRequest
	if !bindJSON(c, &req) {
		return
	}
	req.LocalKey = strings.TrimSpace(req.LocalKey)
	req.NativeID = strings.TrimSpace(req.NativeID)
	req.GroupNickname = strings.TrimSpace(req.GroupNickname)
	req.WechatName = strings.TrimSpace(req.WechatName)
	req.WechatRemark = strings.TrimSpace(req.WechatRemark)
	req.WechatAccount = strings.TrimSpace(req.WechatAccount)
	req.Gender = strings.TrimSpace(req.Gender)
	req.Region = strings.TrimSpace(req.Region)
	req.Phone = strings.TrimSpace(req.Phone)
	req.Signature = strings.TrimSpace(req.Signature)
	req.Source = strings.TrimSpace(req.Source)
	req.Role = strings.TrimSpace(req.Role)
	if req.GroupNickname == "" && req.WechatName == "" && req.WechatRemark == "" && req.WechatAccount == "" {
		c.JSON(http.StatusBadRequest, gin.H{"error": "member_name_required"})
		return
	}
	if !validText(req.LocalKey, 128) || !validText(req.NativeID, 128) || !validText(req.GroupNickname, 128) ||
		!validText(req.WechatName, 128) || !validText(req.WechatRemark, 128) || !validText(req.WechatAccount, 128) ||
		!validText(req.Gender, 16) || !validText(req.Region, 128) || !validText(req.Phone, 64) ||
		!validText(req.Signature, 500) || !validText(req.Source, 128) || !validText(req.Role, 32) || !validText(req.Note, 1000) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "field_too_long"})
		return
	}

	ownerID := auth.IdentityFromContext(c).EmployeeID
	member, err := s.store.CreateGroupMember(c.Request.Context(), ownerID, groupID, store.GroupMemberInput{
		LocalKey:      req.LocalKey,
		NativeID:      req.NativeID,
		GroupNickname: req.GroupNickname,
		WechatName:    req.WechatName,
		WechatRemark:  req.WechatRemark,
		WechatAccount: req.WechatAccount,
		Gender:        req.Gender,
		Region:        req.Region,
		Phone:         req.Phone,
		Signature:     req.Signature,
		Source:        req.Source,
		Role:          req.Role,
		Note:          req.Note,
	})
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusCreated, member)
}

func (s *server) updateGroupMember(c *gin.Context) {
	groupID, ok := parsePathID(c, "id", "invalid_group_id")
	if !ok {
		return
	}
	memberID, ok := parsePathID(c, "memberId", "invalid_member_id")
	if !ok {
		return
	}
	var req patchGroupMemberRequest
	if !bindJSON(c, &req) {
		return
	}
	if req.LocalKey == nil && req.NativeID == nil && req.GroupNickname == nil &&
		req.WechatName == nil && req.WechatRemark == nil && req.WechatAccount == nil &&
		req.Gender == nil && req.Region == nil && req.Phone == nil &&
		req.Signature == nil && req.Source == nil && req.Role == nil && req.Note == nil {
		c.JSON(http.StatusBadRequest, gin.H{"error": "empty_patch"})
		return
	}
	if !trimAndCheck(&req.LocalKey, 128) || !trimAndCheck(&req.NativeID, 128) || !trimAndCheck(&req.GroupNickname, 128) ||
		!trimAndCheck(&req.WechatName, 128) || !trimAndCheck(&req.WechatRemark, 128) || !trimAndCheck(&req.WechatAccount, 128) ||
		!trimAndCheck(&req.Gender, 16) || !trimAndCheck(&req.Region, 128) || !trimAndCheck(&req.Phone, 64) ||
		!trimAndCheck(&req.Signature, 500) || !trimAndCheck(&req.Source, 128) || !trimAndCheck(&req.Role, 32) ||
		(req.Note != nil && !validText(*req.Note, 1000)) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "field_too_long"})
		return
	}

	ownerID := auth.IdentityFromContext(c).EmployeeID
	member, err := s.store.UpdateGroupMember(c.Request.Context(), ownerID, groupID, memberID, store.GroupMemberPatch{
		LocalKey:      req.LocalKey,
		NativeID:      req.NativeID,
		GroupNickname: req.GroupNickname,
		WechatName:    req.WechatName,
		WechatRemark:  req.WechatRemark,
		WechatAccount: req.WechatAccount,
		Gender:        req.Gender,
		Region:        req.Region,
		Phone:         req.Phone,
		Signature:     req.Signature,
		Source:        req.Source,
		Role:          req.Role,
		Note:          req.Note,
	})
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusOK, member)
}

func (s *server) uploadGroupMemberAvatar(c *gin.Context) {
	groupID, ok := parsePathID(c, "id", "invalid_group_id")
	if !ok {
		return
	}
	memberID, ok := parsePathID(c, "memberId", "invalid_member_id")
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
	filename := fmt.Sprintf("%sgm%d_%d_%d_%s%s", ownerAvatarPrefix(ownerID), groupID, memberID, time.Now().UnixNano(), hex.EncodeToString(nonce[:]), ext)
	fullPath := filepath.Join(s.store.AvatarDir(), filename)
	if err := os.WriteFile(fullPath, raw, 0o640); err != nil {
		c.JSON(http.StatusInternalServerError, gin.H{"error": "avatar_save_failed"})
		return
	}

	avatarURL := "/api/v1/avatars/" + filename
	updated, prevURL, err := s.store.SetGroupMemberAvatar(c.Request.Context(), ownerID, groupID, memberID, avatarURL)
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

func (s *server) deleteGroupMember(c *gin.Context) {
	groupID, ok := parsePathID(c, "id", "invalid_group_id")
	if !ok {
		return
	}
	memberID, ok := parsePathID(c, "memberId", "invalid_member_id")
	if !ok {
		return
	}
	ownerID := auth.IdentityFromContext(c).EmployeeID
	if err := s.store.DeleteGroupMember(c.Request.Context(), ownerID, groupID, memberID); err != nil {
		writeStoreErr(c, err)
		return
	}
	c.Status(http.StatusNoContent)
}

// ---- Audit Logs ----

type createAuditLogRequest struct {
	Action     string `json:"action"`
	TargetType string `json:"target_type"`
	TargetID   string `json:"target_id"`
	Detail     string `json:"detail"`
}

func (s *server) listAuditLogs(c *gin.Context) {
	cursor, limit, ok := parsePagination(c)
	if !ok {
		return
	}
	ownerID := auth.IdentityFromContext(c).EmployeeID
	items, next, err := s.store.ListAuditLogs(c.Request.Context(), ownerID, cursor, limit)
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusOK, gin.H{"items": items, "next_cursor": next})
}

func (s *server) createAuditLog(c *gin.Context) {
	var req createAuditLogRequest
	if !bindJSON(c, &req) {
		return
	}
	req.Action = strings.TrimSpace(req.Action)
	req.TargetType = strings.TrimSpace(req.TargetType)
	req.TargetID = strings.TrimSpace(req.TargetID)
	req.Detail = strings.TrimSpace(req.Detail)
	if req.Action == "" || !validText(req.Action, 64) || !validText(req.TargetType, 64) ||
		!validText(req.TargetID, 128) || !validText(req.Detail, 2000) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_audit_log"})
		return
	}
	ownerID := auth.IdentityFromContext(c).EmployeeID
	entry, err := s.store.CreateAuditLog(c.Request.Context(), ownerID, req.Action, req.TargetType, req.TargetID, req.Detail)
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusCreated, entry)
}
