package api

import (
	"errors"
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
	var request loginRequest
	if !decodeJSON(c, &request) {
		return
	}
	if strings.TrimSpace(request.Account) == "" || request.Password == "" || len(request.Account) > 256 || len(request.Password) > 1024 {
		writeError(c, http.StatusBadRequest, "invalid_credentials_format")
		return
	}
	identity, err := s.verifier.Verify(c.Request.Context(), request.Account, request.Password)
	if err != nil {
		if errors.Is(err, auth.ErrUnavailable) {
			writeError(c, http.StatusServiceUnavailable, "authentication_unavailable")
			return
		}
		writeError(c, http.StatusUnauthorized, "invalid_credentials")
		return
	}
	if identity.EmployeeID == "" {
		writeError(c, http.StatusServiceUnavailable, "authentication_unavailable")
		return
	}
	token, expiresAt, err := s.sessions.Create(identity)
	if err != nil {
		writeError(c, http.StatusInternalServerError, "internal_error")
		return
	}
	c.JSON(http.StatusOK, gin.H{"token": token, "expires_at": expiresAt.UTC(), "employee": identity})
}

func (s *Server) logout(c *gin.Context) {
	s.sessions.Revoke(auth.TokenFromContext(c))
	c.Status(http.StatusNoContent)
}

func (s *Server) me(c *gin.Context) {
	c.JSON(http.StatusOK, gin.H{"employee": currentIdentity(c)})
}
