package api

import (
	"errors"
	"log"
	"net/http"
	"strings"

	"github.com/gin-gonic/gin"
	"weixinorc/server/internal/auth"
)

type loginRequest struct {
	Account  string `json:"account"`
	Password string `json:"password"`
}

func (s *server) login(c *gin.Context) {
	var req loginRequest
	if !bindJSON(c, &req) {
		return
	}
	req.Account = strings.TrimSpace(req.Account)
	if req.Account == "" || req.Password == "" || len(req.Account) > 256 || len(req.Password) > 1024 {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_credentials_format"})
		return
	}
	if s.verifier == nil {
		c.JSON(http.StatusServiceUnavailable, gin.H{"error": "authentication_unavailable"})
		return
	}

	identity, err := s.verifier.Verify(c.Request.Context(), req.Account, req.Password)
	if err != nil {
		if errors.Is(err, auth.ErrInvalidCredentials) {
			c.JSON(http.StatusUnauthorized, gin.H{"error": "invalid_credentials"})
			return
		}
		log.Printf("auth verify error for account=%q: %v", req.Account, err)
		c.JSON(http.StatusServiceUnavailable, gin.H{"error": "authentication_unavailable"})
		return
	}
	if identity.EmployeeID == "" {
		c.JSON(http.StatusServiceUnavailable, gin.H{"error": "authentication_unavailable"})
		return
	}

	token, expiresAt, err := s.sessions.Create(identity)
	if err != nil {
		c.JSON(http.StatusInternalServerError, gin.H{"error": "internal_error"})
		return
	}

	_, _ = s.store.CreateAuditLog(c.Request.Context(), identity.EmployeeID, "login", "employee", identity.EmployeeID, "account="+identity.Account)

	c.JSON(http.StatusOK, gin.H{
		"token":      token,
		"expires_at": expiresAt.UTC(),
		"employee":   identity,
	})
}

func (s *server) logout(c *gin.Context) {
	id := auth.IdentityFromContext(c)
	_, _ = s.store.CreateAuditLog(c.Request.Context(), id.EmployeeID, "logout", "employee", id.EmployeeID, "account="+id.Account)
	s.sessions.Revoke(auth.TokenFromContext(c))
	c.Status(http.StatusNoContent)
}

func (s *server) me(c *gin.Context) {
	c.JSON(http.StatusOK, gin.H{"employee": auth.IdentityFromContext(c)})
}
