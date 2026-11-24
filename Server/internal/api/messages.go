package api

import (
	"net/http"
	"strings"
	"time"
	"unicode/utf8"

	"github.com/gin-gonic/gin"
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
		writeError(c, http.StatusBadRequest, "invalid_customer_id")
		return
	}
	cursor, limit, ok := pagination(c)
	if !ok {
		return
	}
	items, next, err := s.store.ListMessages(c.Request.Context(), currentIdentity(c).EmployeeID, customerID, cursor, limit)
	if err != nil {
		writeStoreError(c, err)
		return
	}
	c.JSON(http.StatusOK, gin.H{"items": items, "next_cursor": next})
}

func (s *Server) createMessage(c *gin.Context) {
	customerID, ok := parseID(c.Param("id"))
	if !ok {
		writeError(c, http.StatusBadRequest, "invalid_customer_id")
		return
	}
	var request createMessageRequest
	if !decodeJSON(c, &request) {
		return
	}
	if request.Direction != "incoming" && request.Direction != "outgoing" {
		writeError(c, http.StatusBadRequest, "invalid_direction")
		return
	}
	if strings.TrimSpace(request.Content) == "" || !utf8.ValidString(request.Content) || utf8.RuneCountInString(request.Content) > 10000 {
		writeError(c, http.StatusBadRequest, "invalid_content")
		return
	}
	sentAt := time.Now().UTC()
	if request.SentAt != "" {
		parsed, err := time.Parse(time.RFC3339, request.SentAt)
		if err != nil {
			writeError(c, http.StatusBadRequest, "invalid_sent_at")
			return
		}
		sentAt = parsed
	}
	message, err := s.store.CreateMessage(c.Request.Context(), currentIdentity(c).EmployeeID, customerID, store.MessageInput{Direction: request.Direction, Content: request.Content, SentAt: sentAt})
	if err != nil {
		writeStoreError(c, err)
		return
	}
	c.JSON(http.StatusCreated, message)
}
