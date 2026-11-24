package api

import (
	"net/http"
	"strconv"
	"strings"

	"github.com/gin-gonic/gin"
	"weixinorc/server/internal/auth"
	"weixinorc/server/internal/store"
)

// ---- WeChat Accounts ----

type upsertWeChatAccountRequest struct {
	AccountType string `json:"account_type"`
	WechatID    string `json:"wechat_id"`
	Nickname    string `json:"nickname"`
	Status      string `json:"status"`
}

type patchWeChatAccountRequest struct {
	WechatID  *string `json:"wechat_id"`
	Nickname  *string `json:"nickname"`
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
	req.Nickname = strings.TrimSpace(req.Nickname)
	req.Status = strings.TrimSpace(req.Status)
	if req.Status == "" {
		req.Status = "online"
	}

	if req.AccountType != "personal" && req.AccountType != "enterprise" {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_account_type"})
		return
	}
	if !validWeChatStatus(req.Status) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_status"})
		return
	}
	if !validText(req.WechatID, 128) || !validText(req.Nickname, 128) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "field_too_long"})
		return
	}

	ownerID := auth.IdentityFromContext(c).EmployeeID
	acc, err := s.store.UpsertWeChatAccount(c.Request.Context(), ownerID, store.WeChatAccountInput{
		AccountType: req.AccountType,
		WechatID:    req.WechatID,
		Nickname:    req.Nickname,
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
	if req.WechatID == nil && req.Nickname == nil && req.Status == nil && !req.Heartbeat {
		c.JSON(http.StatusBadRequest, gin.H{"error": "empty_patch"})
		return
	}
	if !trimAndCheck(&req.WechatID, 128) || !trimAndCheck(&req.Nickname, 128) || !trimAndCheck(&req.Status, 32) {
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
		Nickname:  req.Nickname,
		Status:    req.Status,
		Heartbeat: req.Heartbeat,
	})
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusOK, acc)
}

func (s *server) deleteWeChatAccount(c *gin.Context) {
	id, ok := parsePathID(c, "id", "invalid_account_id")
	if !ok {
		return
	}
	ownerID := auth.IdentityFromContext(c).EmployeeID
	if err := s.store.DeleteWeChatAccount(c.Request.Context(), ownerID, id); err != nil {
		writeStoreErr(c, err)
		return
	}
	_, _ = s.store.CreateAuditLog(c.Request.Context(), ownerID, "unbind_wechat_account", "wechat_account", strconv.FormatInt(id, 10), "")
	c.Status(http.StatusNoContent)
}

// ---- Group Members ----

type createGroupMemberRequest struct {
	LocalKey      string `json:"local_key"`
	NativeID      string `json:"native_id"`
	GroupNickname string `json:"group_nickname"`
	WechatName    string `json:"wechat_name"`
	WechatAccount string `json:"wechat_account"`
	Role          string `json:"role"`
	Note          string `json:"note"`
}

type patchGroupMemberRequest struct {
	LocalKey      *string `json:"local_key"`
	NativeID      *string `json:"native_id"`
	GroupNickname *string `json:"group_nickname"`
	WechatName    *string `json:"wechat_name"`
	WechatAccount *string `json:"wechat_account"`
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
	req.WechatAccount = strings.TrimSpace(req.WechatAccount)
	req.Role = strings.TrimSpace(req.Role)
	if req.GroupNickname == "" && req.WechatName == "" && req.WechatAccount == "" {
		c.JSON(http.StatusBadRequest, gin.H{"error": "member_name_required"})
		return
	}
	if !validText(req.LocalKey, 128) || !validText(req.NativeID, 128) || !validText(req.GroupNickname, 128) ||
		!validText(req.WechatName, 128) || !validText(req.WechatAccount, 128) || !validText(req.Role, 32) || !validText(req.Note, 1000) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "field_too_long"})
		return
	}

	ownerID := auth.IdentityFromContext(c).EmployeeID
	member, err := s.store.CreateGroupMember(c.Request.Context(), ownerID, groupID, store.GroupMemberInput{
		LocalKey:      req.LocalKey,
		NativeID:      req.NativeID,
		GroupNickname: req.GroupNickname,
		WechatName:    req.WechatName,
		WechatAccount: req.WechatAccount,
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
		req.WechatName == nil && req.WechatAccount == nil && req.Role == nil && req.Note == nil {
		c.JSON(http.StatusBadRequest, gin.H{"error": "empty_patch"})
		return
	}
	if !trimAndCheck(&req.LocalKey, 128) || !trimAndCheck(&req.NativeID, 128) || !trimAndCheck(&req.GroupNickname, 128) ||
		!trimAndCheck(&req.WechatName, 128) || !trimAndCheck(&req.WechatAccount, 128) || !trimAndCheck(&req.Role, 32) ||
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
		WechatAccount: req.WechatAccount,
		Role:          req.Role,
		Note:          req.Note,
	})
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusOK, member)
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
