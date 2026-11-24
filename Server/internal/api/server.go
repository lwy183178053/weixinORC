package api

import (
	"encoding/json"
	"errors"
	"net/http"
	"strconv"
	"unicode/utf8"

	"github.com/gin-gonic/gin"
	"weixinorc/server/internal/auth"
	"weixinorc/server/internal/store"
)

const maxBodyBytes = 4 << 20 // 4 MB (supports base64 avatar upload up to 2 MB)

type Server struct {
	store    *store.Store
	verifier auth.Verifier
	sessions *auth.Sessions
}

func New(db *store.Store, verifier auth.Verifier, sessions *auth.Sessions) *gin.Engine {
	gin.SetMode(gin.ReleaseMode)
	r := gin.New()
	_ = r.SetTrustedProxies(nil)
	r.Use(gin.Recovery(), func(c *gin.Context) {
		c.Request.Body = http.MaxBytesReader(c.Writer, c.Request.Body, maxBodyBytes)
		c.Next()
	})

	s := &Server{store: db, verifier: verifier, sessions: sessions}

	r.GET("/healthz", func(c *gin.Context) {
		c.JSON(http.StatusOK, gin.H{"status": "ok"})
	})

	v1 := r.Group("/api/v1")
	v1.POST("/login", s.login)

	authed := v1.Group("", sessions.Middleware())
	authed.POST("/logout", s.logout)
	authed.GET("/me", s.me)

	// Customers & Avatars & Messages
	authed.GET("/customers", s.listCustomers)
	authed.POST("/customers", s.createCustomer)
	authed.GET("/customers/:id", s.getCustomer)
	authed.PATCH("/customers/:id", s.updateCustomer)
	authed.DELETE("/customers/:id", s.deleteCustomer)
	authed.POST("/customers/:id/avatar", s.uploadCustomerAvatar)
	authed.DELETE("/customers/:id/avatar", s.deleteCustomerAvatar)
	authed.GET("/avatars/:filename", s.getAvatar)
	authed.GET("/customers/:id/messages", s.listMessages)
	authed.POST("/customers/:id/messages", s.createMessage)

	// Groups & Members & Group Messages
	authed.GET("/groups", s.listGroups)
	authed.POST("/groups", s.createGroup)
	authed.GET("/groups/:id", s.getGroup)
	authed.PATCH("/groups/:id", s.updateGroup)
	authed.DELETE("/groups/:id", s.deleteGroup)
	authed.GET("/groups/:id/members", s.listGroupMembers)
	authed.POST("/groups/:id/members", s.createGroupMember)
	authed.PATCH("/groups/:id/members/:memberId", s.updateGroupMember)
	authed.DELETE("/groups/:id/members/:memberId", s.deleteGroupMember)
	authed.GET("/groups/:id/messages", s.listGroupMessages)
	authed.POST("/groups/:id/messages", s.createGroupMessage)

	// WeChat Accounts (1 employee <-> personal/enterprise WeChat binding)
	authed.GET("/wechat-accounts", s.listWeChatAccounts)
	authed.POST("/wechat-accounts", s.upsertWeChatAccount)
	authed.PATCH("/wechat-accounts/:id", s.updateWeChatAccount)
	authed.DELETE("/wechat-accounts/:id", s.deleteWeChatAccount)

	// Audit Logs
	authed.GET("/audit-logs", s.listAuditLogs)
	authed.POST("/audit-logs", s.createAuditLog)

	return r
}

func bindJSON(c *gin.Context, dst any) bool {
	dec := json.NewDecoder(c.Request.Body)
	dec.DisallowUnknownFields()
	if err := dec.Decode(dst); err != nil {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_json"})
		return false
	}
	return true
}

func parseID(raw string) (int64, bool) {
	id, err := strconv.ParseInt(raw, 10, 64)
	return id, err == nil && id > 0
}

func parsePagination(c *gin.Context) (cursor int64, limit int, ok bool) {
	limit = 50
	if raw := c.Query("limit"); raw != "" {
		n, err := strconv.Atoi(raw)
		if err != nil || n < 1 || n > 100 {
			c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_limit"})
			return 0, 0, false
		}
		limit = n
	}
	if raw := c.Query("cursor"); raw != "" {
		id, valid := parseID(raw)
		if !valid {
			c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_cursor"})
			return 0, 0, false
		}
		cursor = id
	}
	return cursor, limit, true
}

func validText(s string, maxRunes int) bool {
	return utf8.ValidString(s) && utf8.RuneCountInString(s) <= maxRunes
}

func writeStoreErr(c *gin.Context, err error) {
	switch {
	case errors.Is(err, store.ErrNotFound):
		c.JSON(http.StatusNotFound, gin.H{"error": "not_found"})
	case errors.Is(err, store.ErrConflict):
		c.JSON(http.StatusConflict, gin.H{"error": "conflict"})
	default:
		c.JSON(http.StatusInternalServerError, gin.H{"error": "internal_error"})
	}
}
