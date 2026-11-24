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
	Region        string `json:"region"`
	Phone         string `json:"phone"`
	Signature     string `json:"signature"`
	Source        string `json:"source"`
	AddedDate     string `json:"added_date"`
}

type patchCustomerRequest struct {
	WechatName    *string `json:"wechat_name"`
	WechatAccount *string `json:"wechat_account"`
	Note          *string `json:"note"`
	Region        *string `json:"region"`
	Phone         *string `json:"phone"`
	Signature     *string `json:"signature"`
	Source        *string `json:"source"`
	AddedDate     *string `json:"added_date"`
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
	req.Region = strings.TrimSpace(req.Region)
	req.Phone = strings.TrimSpace(req.Phone)
	req.Signature = strings.TrimSpace(req.Signature)
	req.Source = strings.TrimSpace(req.Source)
	req.AddedDate = strings.TrimSpace(req.AddedDate)

	if !validText(req.WechatName, 128) || !validText(req.WechatAccount, 128) ||
		!validText(req.Note, 2000) || !validText(req.Region, 128) ||
		!validText(req.Phone, 64) || !validText(req.Signature, 500) ||
		!validText(req.Source, 128) || !validText(req.AddedDate, 64) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "field_too_long"})
		return
	}

	ownerID := auth.IdentityFromContext(c).EmployeeID
	customer, err := s.store.CreateCustomer(c.Request.Context(), ownerID, store.CustomerInput{
		WechatName:    req.WechatName,
		WechatAccount: req.WechatAccount,
		Note:          req.Note,
		Region:        req.Region,
		Phone:         req.Phone,
		Signature:     req.Signature,
		Source:        req.Source,
		AddedDate:     req.AddedDate,
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
	if req.WechatName == nil && req.WechatAccount == nil && req.Note == nil &&
		req.Region == nil && req.Phone == nil && req.Signature == nil &&
		req.Source == nil && req.AddedDate == nil {
		c.JSON(http.StatusBadRequest, gin.H{"error": "empty_patch"})
		return
	}

	trimAndCheck := func(p **string, max int) bool {
		if *p == nil {
			return true
		}
		trimmed := strings.TrimSpace(**p)
		if !validText(trimmed, max) {
			return false
		}
		*p = &trimmed
		return true
	}

	if !trimAndCheck(&req.WechatName, 128) || !trimAndCheck(&req.WechatAccount, 128) ||
		!trimAndCheck(&req.Region, 128) || !trimAndCheck(&req.Phone, 64) ||
		!trimAndCheck(&req.Signature, 500) || !trimAndCheck(&req.Source, 128) ||
		!trimAndCheck(&req.AddedDate, 64) || (req.Note != nil && !validText(*req.Note, 2000)) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "field_too_long"})
		return
	}

	ownerID := auth.IdentityFromContext(c).EmployeeID
	customer, err := s.store.UpdateCustomer(c.Request.Context(), ownerID, id, store.CustomerPatch{
		WechatName:    req.WechatName,
		WechatAccount: req.WechatAccount,
		Note:          req.Note,
		Region:        req.Region,
		Phone:         req.Phone,
		Signature:     req.Signature,
		Source:        req.Source,
		AddedDate:     req.AddedDate,
	})
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusOK, customer)
}

func (s *Server) deleteCustomer(c *gin.Context) {
	id, ok := parseID(c.Param("id"))
	if !ok {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_customer_id"})
		return
	}
	ownerID := auth.IdentityFromContext(c).EmployeeID
	if err := s.store.DeleteCustomer(c.Request.Context(), ownerID, id); err != nil {
		writeStoreErr(c, err)
		return
	}
	c.Status(http.StatusNoContent)
}
