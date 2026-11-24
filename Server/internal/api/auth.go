package api

import (
	"net/http"
	"strings"

	"github.com/gin-gonic/gin"
	"weixinorc/server/internal/auth"
)

type loginRequest struct {
	Account  string `json:"account"`
	Password string `json:"password"`
}

func (s *Server) login(c *gin.Context) {
	var req loginRequest
	if !bindJSON(c, &req) {
		return
	}
	if strings.TrimSpace(req.Account) == "" || req.Password == "" || len(req.Account) > 256 || len(req.Password) > 1024 {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_credentials_format"})
		return
	}
	if s.verifier == nil {
		c.JSON(http.StatusServiceUnavailable, gin.H{"error": "authentication_unavailable"})
		return
	}

	identity, err := s.verifier.Verify(c.Request.Context(), req.Account, req.Password)
	if err != nil {
		c.JSON(http.StatusUnauthorized, gin.H{"error": "invalid_credentials"})
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

func (s *Server) logout(c *gin.Context) {
	id := auth.IdentityFromContext(c)
	_, _ = s.store.CreateAuditLog(c.Request.Context(), id.EmployeeID, "logout", "employee", id.EmployeeID, "account="+id.Account)
	s.sessions.Revoke(auth.TokenFromContext(c))
	c.Status(http.StatusNoContent)
}

func (s *Server) me(c *gin.Context) {
	c.JSON(http.StatusOK, gin.H{"employee": auth.IdentityFromContext(c)})
}
