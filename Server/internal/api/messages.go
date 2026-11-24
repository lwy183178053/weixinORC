package api

import (
	"net/http"
	"strings"
	"time"

	"github.com/gin-gonic/gin"
	"weixinorc/server/internal/auth"
	"weixinorc/server/internal/store"
)

type createMessageRequest struct {
	Direction string `json:"direction"`
	Content   string `json:"content"`
	SentAt    string `json:"sent_at"`
}

func (s *Server) listMessages(c *gin.Context) {
	customerID, ok := parseID(c.Param("id"))
	if !ok {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_customer_id"})
		return
	}
	cursor, limit, ok := parsePagination(c)
	if !ok {
		return
	}

	ownerID := auth.IdentityFromContext(c).EmployeeID
	items, next, err := s.store.ListMessages(c.Request.Context(), ownerID, customerID, cursor, limit)
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusOK, gin.H{"items": items, "next_cursor": next})
}

func (s *Server) createMessage(c *gin.Context) {
	customerID, ok := parseID(c.Param("id"))
	if !ok {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_customer_id"})
		return
	}

	var req createMessageRequest
	if !bindJSON(c, &req) {
		return
	}
	if req.Direction != "incoming" && req.Direction != "outgoing" {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_direction"})
		return
	}
	if strings.TrimSpace(req.Content) == "" || !validText(req.Content, 10000) {
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
	msg, err := s.store.CreateMessage(c.Request.Context(), ownerID, customerID, store.MessageInput{
		Direction: req.Direction,
		Content:   req.Content,
		SentAt:    sentAt,
	})
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusCreated, msg)
}
