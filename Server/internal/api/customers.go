package api

import (
	"net/http"
	"strings"

	"github.com/gin-gonic/gin"
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
	cursor, limit, ok := pagination(c)
	if !ok {
		return
	}
	items, next, err := s.store.ListCustomers(c.Request.Context(), currentIdentity(c).EmployeeID, cursor, limit)
	if err != nil {
		writeStoreError(c, err)
		return
	}
	c.JSON(http.StatusOK, gin.H{"items": items, "next_cursor": next})
}

func (s *Server) createCustomer(c *gin.Context) {
	var request createCustomerRequest
	if !decodeJSON(c, &request) {
		return
	}
	request.WechatName = strings.TrimSpace(request.WechatName)
	request.WechatAccount = strings.TrimSpace(request.WechatAccount)
	if !checkLength(request.WechatName, 128) || !checkLength(request.WechatAccount, 128) || !checkLength(request.Note, 2000) {
		writeError(c, http.StatusBadRequest, "field_too_long")
		return
	}
	customer, err := s.store.CreateCustomer(c.Request.Context(), currentIdentity(c).EmployeeID, store.CustomerInput{WechatName: request.WechatName, WechatAccount: request.WechatAccount, Note: request.Note})
	if err != nil {
		writeStoreError(c, err)
		return
	}
	c.JSON(http.StatusCreated, customer)
}

func (s *Server) getCustomer(c *gin.Context) {
	id, ok := parseID(c.Param("id"))
	if !ok {
		writeError(c, http.StatusBadRequest, "invalid_customer_id")
		return
	}
	customer, err := s.store.GetCustomer(c.Request.Context(), currentIdentity(c).EmployeeID, id)
	if err != nil {
		writeStoreError(c, err)
		return
	}
	c.JSON(http.StatusOK, customer)
}

func (s *Server) updateCustomer(c *gin.Context) {
	id, ok := parseID(c.Param("id"))
	if !ok {
		writeError(c, http.StatusBadRequest, "invalid_customer_id")
		return
	}
	var request patchCustomerRequest
	if !decodeJSON(c, &request) {
		return
	}
	if request.WechatName == nil && request.WechatAccount == nil && request.Note == nil {
		writeError(c, http.StatusBadRequest, "empty_patch")
		return
	}
	if (request.WechatName != nil && !checkLength(*request.WechatName, 128)) || (request.WechatAccount != nil && !checkLength(*request.WechatAccount, 128)) || (request.Note != nil && !checkLength(*request.Note, 2000)) {
		writeError(c, http.StatusBadRequest, "field_too_long")
		return
	}
	if request.WechatName != nil {
		*request.WechatName = strings.TrimSpace(*request.WechatName)
	}
	if request.WechatAccount != nil {
		*request.WechatAccount = strings.TrimSpace(*request.WechatAccount)
	}
	customer, err := s.store.UpdateCustomer(c.Request.Context(), currentIdentity(c).EmployeeID, id, store.CustomerPatch{WechatName: request.WechatName, WechatAccount: request.WechatAccount, Note: request.Note})
	if err != nil {
		writeStoreError(c, err)
		return
	}
	c.JSON(http.StatusOK, customer)
}
