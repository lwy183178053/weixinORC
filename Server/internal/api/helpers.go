package api

import (
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"strconv"
	"unicode/utf8"

	"github.com/gin-gonic/gin"
	"weixinorc/server/internal/auth"
	"weixinorc/server/internal/store"
)

const maxRequestBody = 1 << 20

func limitBody() gin.HandlerFunc {
	return func(c *gin.Context) {
		c.Request.Body = http.MaxBytesReader(c.Writer, c.Request.Body, maxRequestBody)
		c.Next()
	}
}

func decodeJSON(c *gin.Context, target any) bool {
	decoder := json.NewDecoder(c.Request.Body)
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(target); err != nil {
		writeError(c, http.StatusBadRequest, "invalid_json")
		return false
	}
	var extra any
	if err := decoder.Decode(&extra); err != io.EOF {
		writeError(c, http.StatusBadRequest, "invalid_json")
		return false
	}
	return true
}

func writeError(c *gin.Context, status int, code string) {
	c.JSON(status, gin.H{"error": code})
}

func currentIdentity(c *gin.Context) auth.Identity {
	return auth.IdentityFromContext(c)
}

func parseID(raw string) (int64, bool) {
	id, err := strconv.ParseInt(raw, 10, 64)
	return id, err == nil && id > 0
}

func pagination(c *gin.Context) (int64, int, bool) {
	limit := 20
	if raw := c.Query("limit"); raw != "" {
		parsed, err := strconv.Atoi(raw)
		if err != nil || parsed < 1 || parsed > 100 {
			writeError(c, http.StatusBadRequest, "invalid_limit")
			return 0, 0, false
		}
		limit = parsed
	}
	var cursor int64
	if raw := c.Query("cursor"); raw != "" {
		parsed, ok := parseID(raw)
		if !ok {
			writeError(c, http.StatusBadRequest, "invalid_cursor")
			return 0, 0, false
		}
		cursor = parsed
	}
	return cursor, limit, true
}

func checkLength(value string, max int) bool {
	return utf8.ValidString(value) && utf8.RuneCountInString(value) <= max
}

func writeStoreError(c *gin.Context, err error) {
	switch {
	case errors.Is(err, store.ErrNotFound):
		writeError(c, http.StatusNotFound, "not_found")
	case errors.Is(err, store.ErrConflict):
		writeError(c, http.StatusConflict, "conflict")
	default:
		writeError(c, http.StatusInternalServerError, "internal_error")
	}
}
