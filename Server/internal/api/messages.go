package api

import (
	"net/http"
	"strings"

	"github.com/gin-gonic/gin"
	"weixinorc/server/internal/auth"
	"weixinorc/server/internal/store"
)

type createMessageRequest struct {
	Direction string `json:"direction"`
	Content   string `json:"content"`
	SentAt    string `json:"sent_at"`
}

func (s *server) listMessages(c *gin.Context) {
	customerID, ok := parsePathID(c, "id", "invalid_customer_id")
	if !ok {
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

func (s *server) createMessage(c *gin.Context) {
	customerID, ok := parsePathID(c, "id", "invalid_customer_id")
	if !ok {
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

	sentAt, ok := parseSentAt(c, req.SentAt)
	if !ok {
		return
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
