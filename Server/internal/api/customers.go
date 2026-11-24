package api

import (
	"net/http"
	"strings"

	"github.com/gin-gonic/gin"
	"weixinorc/server/internal/auth"
	"weixinorc/server/internal/store"
)

type createCustomerRequest struct {
	WechatName    string `json:"wechat_name"`
	WechatAccount string `json:"wechat_account"`
	Note          string `json:"note"`
}

type patchCustomerRequest struct {
	WechatName    *string `json:"wechat_name"`
	WechatAccount *string `json:"wechat_account"`
	Note          *string `json:"note"`
}

func (s *Server) listCustomers(c *gin.Context) {
	cursor, limit, ok := parsePagination(c)
	if !ok {
		return
	}
	ownerID := auth.IdentityFromContext(c).EmployeeID
	items, next, err := s.store.ListCustomers(c.Request.Context(), ownerID, cursor, limit)
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusOK, gin.H{"items": items, "next_cursor": next})
}

func (s *Server) createCustomer(c *gin.Context) {
	var req createCustomerRequest
	if !bindJSON(c, &req) {
		return
	}
	req.WechatName = strings.TrimSpace(req.WechatName)
	req.WechatAccount = strings.TrimSpace(req.WechatAccount)

	if !validText(req.WechatName, 128) || !validText(req.WechatAccount, 128) || !validText(req.Note, 2000) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "field_too_long"})
		return
	}

	ownerID := auth.IdentityFromContext(c).EmployeeID
	customer, err := s.store.CreateCustomer(c.Request.Context(), ownerID, store.CustomerInput{
		WechatName:    req.WechatName,
		WechatAccount: req.WechatAccount,
		Note:          req.Note,
	})
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusCreated, customer)
}

func (s *Server) getCustomer(c *gin.Context) {
	id, ok := parseID(c.Param("id"))
	if !ok {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_customer_id"})
		return
	}
	ownerID := auth.IdentityFromContext(c).EmployeeID
	customer, err := s.store.GetCustomer(c.Request.Context(), ownerID, id)
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusOK, customer)
}

func (s *Server) updateCustomer(c *gin.Context) {
	id, ok := parseID(c.Param("id"))
	if !ok {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_customer_id"})
		return
	}

	var req patchCustomerRequest
	if !bindJSON(c, &req) {
		return
	}
	if req.WechatName == nil && req.WechatAccount == nil && req.Note == nil {
		c.JSON(http.StatusBadRequest, gin.H{"error": "empty_patch"})
		return
	}

	if req.WechatName != nil {
		trimmed := strings.TrimSpace(*req.WechatName)
		if !validText(trimmed, 128) {
			c.JSON(http.StatusBadRequest, gin.H{"error": "field_too_long"})
			return
		}
		req.WechatName = &trimmed
	}
	if req.WechatAccount != nil {
		trimmed := strings.TrimSpace(*req.WechatAccount)
		if !validText(trimmed, 128) {
			c.JSON(http.StatusBadRequest, gin.H{"error": "field_too_long"})
			return
		}
		req.WechatAccount = &trimmed
	}
	if req.Note != nil && !validText(*req.Note, 2000) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "field_too_long"})
		return
	}

	ownerID := auth.IdentityFromContext(c).EmployeeID
	customer, err := s.store.UpdateCustomer(c.Request.Context(), ownerID, id, store.CustomerPatch{
		WechatName:    req.WechatName,
		WechatAccount: req.WechatAccount,
		Note:          req.Note,
	})
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusOK, customer)
}
