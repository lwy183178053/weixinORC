package api

import (
	"net/http"
	"strings"
	"time"

	"github.com/gin-gonic/gin"
	"weixinorc/server/internal/auth"
	"weixinorc/server/internal/store"
)

type createGroupRequest struct {
	GroupName   string `json:"group_name"`
	GroupKey    string `json:"group_key"`
	MemberCount int    `json:"member_count"`
	MembersText string `json:"members_text"`
	Note        string `json:"note"`
}

type patchGroupRequest struct {
	GroupName   *string `json:"group_name"`
	GroupKey    *string `json:"group_key"`
	MemberCount *int    `json:"member_count"`
	MembersText *string `json:"members_text"`
	Note        *string `json:"note"`
}

type createGroupMessageRequest struct {
	SenderName string `json:"sender_name"`
	Direction  string `json:"direction"`
	Content    string `json:"content"`
	SentAt     string `json:"sent_at"`
}

func (s *Server) listGroups(c *gin.Context) {
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

func (s *Server) createGroup(c *gin.Context) {
	var req createGroupRequest
	if !bindJSON(c, &req) {
		return
	}
	req.GroupName = strings.TrimSpace(req.GroupName)
	req.GroupKey = strings.TrimSpace(req.GroupKey)
	if req.GroupName == "" {
		c.JSON(http.StatusBadRequest, gin.H{"error": "group_name_required"})
		return
	}
	if req.MemberCount < 0 {
		req.MemberCount = 0
	}
	if !validText(req.GroupName, 128) || !validText(req.GroupKey, 128) ||
		!validText(req.MembersText, 4000) || !validText(req.Note, 2000) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "field_too_long"})
		return
	}

	ownerID := auth.IdentityFromContext(c).EmployeeID
	group, err := s.store.CreateGroup(c.Request.Context(), ownerID, store.GroupInput{
		GroupName:   req.GroupName,
		GroupKey:    req.GroupKey,
		MemberCount: req.MemberCount,
		MembersText: req.MembersText,
		Note:        req.Note,
	})
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusCreated, group)
}

func (s *Server) getGroup(c *gin.Context) {
	id, ok := parseID(c.Param("id"))
	if !ok {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_group_id"})
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

func (s *Server) updateGroup(c *gin.Context) {
	id, ok := parseID(c.Param("id"))
	if !ok {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_group_id"})
		return
	}
	var req patchGroupRequest
	if !bindJSON(c, &req) {
		return
	}
	if req.GroupName == nil && req.GroupKey == nil && req.MemberCount == nil && req.MembersText == nil && req.Note == nil {
		c.JSON(http.StatusBadRequest, gin.H{"error": "empty_patch"})
		return
	}
	if (req.GroupName != nil && !validText(*req.GroupName, 128)) ||
		(req.GroupKey != nil && !validText(*req.GroupKey, 128)) ||
		(req.MembersText != nil && !validText(*req.MembersText, 4000)) ||
		(req.Note != nil && !validText(*req.Note, 2000)) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "field_too_long"})
		return
	}

	ownerID := auth.IdentityFromContext(c).EmployeeID
	group, err := s.store.UpdateGroup(c.Request.Context(), ownerID, id, store.GroupPatch{
		GroupName:   req.GroupName,
		GroupKey:    req.GroupKey,
		MemberCount: req.MemberCount,
		MembersText: req.MembersText,
		Note:        req.Note,
	})
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusOK, group)
}

func (s *Server) deleteGroup(c *gin.Context) {
	id, ok := parseID(c.Param("id"))
	if !ok {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_group_id"})
		return
	}
	ownerID := auth.IdentityFromContext(c).EmployeeID
	if err := s.store.DeleteGroup(c.Request.Context(), ownerID, id); err != nil {
		writeStoreErr(c, err)
		return
	}
	c.Status(http.StatusNoContent)
}

func (s *Server) listGroupMessages(c *gin.Context) {
	groupID, ok := parseID(c.Param("id"))
	if !ok {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_group_id"})
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

func (s *Server) createGroupMessage(c *gin.Context) {
	groupID, ok := parseID(c.Param("id"))
	if !ok {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_group_id"})
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
	if strings.TrimSpace(req.Content) == "" || !validText(req.Content, 10000) || !validText(req.SenderName, 128) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_content"})
		return
	}

	sentAt := time.Now().UTC()
	if req.SentAt != "" {
		parsed, err := time.Parse(time.RFC3339, req.SentAt)
		if err != nil {
			c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_sent_at"})
			return
		}
		sentAt = parsed
	}

	ownerID := auth.IdentityFromContext(c).EmployeeID
	msg, err := s.store.CreateGroupMessage(c.Request.Context(), ownerID, groupID, store.GroupMessageInput{
		SenderName: strings.TrimSpace(req.SenderName),
		Direction:  req.Direction,
		Content:    req.Content,
		SentAt:     sentAt,
	})
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusCreated, msg)
}
