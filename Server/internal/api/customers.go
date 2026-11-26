package api

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"github.com/gin-gonic/gin"
	"weixinorc/server/internal/auth"
	"weixinorc/server/internal/store"
)

type createCustomerRequest struct {
	OwnerWxid      string `json:"owner_wxid"`
	WechatRemark   string `json:"wechat_remark"`
	WechatName     string `json:"wechat_name"`
	WechatAccount  string `json:"wechat_account"`
	Gender         string `json:"gender"`
	Description    string `json:"description"`
	Tags           string `json:"tags"`
	FollowStatus   string `json:"follow_status"`
	LocalKey       string `json:"local_key"`
	NativeID       string `json:"native_id"`
	Note           string `json:"note"`
	Region         string `json:"region"`
	Phone          string `json:"phone"`
	Signature      string `json:"signature"`
	Source         string `json:"source"`
	AddedDate      string `json:"added_date"`
	AllowDuplicate bool   `json:"allow_duplicate"`
}

type patchCustomerRequest struct {
	OwnerWxid     *string `json:"owner_wxid"`
	WechatRemark  *string `json:"wechat_remark"`
	WechatName    *string `json:"wechat_name"`
	WechatAccount *string `json:"wechat_account"`
	Gender        *string `json:"gender"`
	Description   *string `json:"description"`
	Tags          *string `json:"tags"`
	FollowStatus  *string `json:"follow_status"`
	LocalKey      *string `json:"local_key"`
	NativeID      *string `json:"native_id"`
	Note          *string `json:"note"`
	Region        *string `json:"region"`
	Phone         *string `json:"phone"`
	Signature     *string `json:"signature"`
	Source        *string `json:"source"`
	AddedDate     *string `json:"added_date"`
}

type uploadAvatarRequest struct {
	ImageBase64 string `json:"image_base64"`
}

func (s *server) listCustomers(c *gin.Context) {
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

func (s *server) createCustomer(c *gin.Context) {
	var req createCustomerRequest
	if !bindJSON(c, &req) {
		return
	}
	req.OwnerWxid = strings.TrimSpace(req.OwnerWxid)
	req.WechatRemark = strings.TrimSpace(req.WechatRemark)
	req.WechatName = strings.TrimSpace(req.WechatName)
	req.WechatAccount = strings.TrimSpace(req.WechatAccount)
	req.Gender = strings.TrimSpace(req.Gender)
	req.Description = strings.TrimSpace(req.Description)
	req.Tags = strings.TrimSpace(req.Tags)
	req.FollowStatus = strings.TrimSpace(req.FollowStatus)
	req.LocalKey = strings.TrimSpace(req.LocalKey)
	req.NativeID = strings.TrimSpace(req.NativeID)
	req.Region = strings.TrimSpace(req.Region)
	req.Phone = strings.TrimSpace(req.Phone)
	req.Signature = strings.TrimSpace(req.Signature)
	req.Source = strings.TrimSpace(req.Source)
	req.AddedDate = strings.TrimSpace(req.AddedDate)

	if !validText(req.OwnerWxid, 128) || !validText(req.WechatRemark, 128) || !validText(req.WechatName, 128) || !validText(req.WechatAccount, 128) ||
		!validText(req.Gender, 16) || !validText(req.Description, 2000) ||
		!validText(req.Tags, 256) || !validText(req.FollowStatus, 32) || !validText(req.LocalKey, 128) ||
		!validText(req.NativeID, 128) || !validText(req.Note, 2000) || !validText(req.Region, 128) ||
		!validText(req.Phone, 64) || !validText(req.Signature, 500) ||
		!validText(req.Source, 128) || !validText(req.AddedDate, 64) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "field_too_long"})
		return
	}

	ownerID := auth.IdentityFromContext(c).EmployeeID
	customer, err := s.store.CreateCustomer(c.Request.Context(), ownerID, store.CustomerInput{
		OwnerWxid:      req.OwnerWxid,
		WechatRemark:   req.WechatRemark,
		WechatName:     req.WechatName,
		WechatAccount:  req.WechatAccount,
		Gender:         req.Gender,
		Description:    req.Description,
		Tags:           req.Tags,
		FollowStatus:   req.FollowStatus,
		LocalKey:       req.LocalKey,
		NativeID:       req.NativeID,
		Note:           req.Note,
		Region:         req.Region,
		Phone:          req.Phone,
		Signature:      req.Signature,
		Source:         req.Source,
		AddedDate:      req.AddedDate,
		AllowDuplicate: req.AllowDuplicate,
	})
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusCreated, customer)
}

func (s *server) getCustomer(c *gin.Context) {
	id, ok := parsePathID(c, "id", "invalid_customer_id")
	if !ok {
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

func (s *server) updateCustomer(c *gin.Context) {
	id, ok := parsePathID(c, "id", "invalid_customer_id")
	if !ok {
		return
	}

	var req patchCustomerRequest
	if !bindJSON(c, &req) {
		return
	}
	if req.OwnerWxid == nil && req.WechatRemark == nil && req.WechatName == nil && req.WechatAccount == nil &&
		req.Gender == nil && req.Description == nil &&
		req.Tags == nil && req.FollowStatus == nil && req.LocalKey == nil && req.NativeID == nil &&
		req.Note == nil && req.Region == nil && req.Phone == nil && req.Signature == nil &&
		req.Source == nil && req.AddedDate == nil {
		c.JSON(http.StatusBadRequest, gin.H{"error": "empty_patch"})
		return
	}

	if !trimAndCheck(&req.OwnerWxid, 128) || !trimAndCheck(&req.WechatRemark, 128) || !trimAndCheck(&req.WechatName, 128) || !trimAndCheck(&req.WechatAccount, 128) ||
		!trimAndCheck(&req.Gender, 16) || !trimAndCheck(&req.Description, 2000) || !trimAndCheck(&req.Tags, 256) ||
		!trimAndCheck(&req.FollowStatus, 32) || !trimAndCheck(&req.LocalKey, 128) || !trimAndCheck(&req.NativeID, 128) ||
		!trimAndCheck(&req.Region, 128) || !trimAndCheck(&req.Phone, 64) ||
		!trimAndCheck(&req.Signature, 500) || !trimAndCheck(&req.Source, 128) ||
		!trimAndCheck(&req.AddedDate, 64) ||
		(req.Note != nil && !validText(*req.Note, 2000)) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "field_too_long"})
		return
	}

	ownerID := auth.IdentityFromContext(c).EmployeeID
	customer, err := s.store.UpdateCustomer(c.Request.Context(), ownerID, id, store.CustomerPatch{
		OwnerWxid:     req.OwnerWxid,
		WechatRemark:  req.WechatRemark,
		WechatName:    req.WechatName,
		WechatAccount: req.WechatAccount,
		Gender:        req.Gender,
		Description:   req.Description,
		Tags:          req.Tags,
		FollowStatus:  req.FollowStatus,
		LocalKey:      req.LocalKey,
		NativeID:      req.NativeID,
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

func (s *server) deleteCustomer(c *gin.Context) {
	id, ok := parsePathID(c, "id", "invalid_customer_id")
	if !ok {
		return
	}
	ownerID := auth.IdentityFromContext(c).EmployeeID
	avatarURL, wechatName, err := s.store.DeleteCustomerReturningMeta(c.Request.Context(), ownerID, id)
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	s.removeAvatarFile(ownerID, avatarURL)
	_, _ = s.store.CreateAuditLog(c.Request.Context(), ownerID, "delete_customer", "customer", strconv.FormatInt(id, 10), wechatName)
	c.Status(http.StatusNoContent)
}

// ---- Customer Avatar Upload / Fetch / Delete (strictly isolated per owner_id) ----

func ownerAvatarPrefix(ownerID string) string {
	sum := sha256.Sum256([]byte(ownerID))
	return hex.EncodeToString(sum[:8]) + "_"
}

func validAvatarFilename(ownerID, name string) bool {
	if name == "" || name == "." || name == ".." || name != filepath.Base(name) {
		return false
	}
	if strings.ContainsAny(name, `/\:`) || !strings.HasPrefix(name, ownerAvatarPrefix(ownerID)) {
		return false
	}
	for _, r := range name {
		if !((r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9') || r == '_' || r == '-' || r == '.') {
			return false
		}
	}
	ext := strings.ToLower(filepath.Ext(name))
	return ext == ".png" || ext == ".jpg" || ext == ".webp"
}

func detectImageExt(raw []byte) (ext string, contentType string, ok bool) {
	if len(raw) >= 8 &&
		raw[0] == 0x89 && raw[1] == 0x50 && raw[2] == 0x4E && raw[3] == 0x47 &&
		raw[4] == 0x0D && raw[5] == 0x0A && raw[6] == 0x1A && raw[7] == 0x0A {
		return ".png", "image/png", true
	}
	if len(raw) >= 3 && raw[0] == 0xFF && raw[1] == 0xD8 && raw[2] == 0xFF {
		return ".jpg", "image/jpeg", true
	}
	if len(raw) >= 12 && string(raw[0:4]) == "RIFF" && string(raw[8:12]) == "WEBP" {
		return ".webp", "image/webp", true
	}
	return "", "", false
}

func (s *server) uploadCustomerAvatar(c *gin.Context) {
	id, ok := parsePathID(c, "id", "invalid_customer_id")
	if !ok {
		return
	}

	var req uploadAvatarRequest
	if !bindJSON(c, &req) {
		return
	}

	b64 := strings.TrimSpace(req.ImageBase64)
	if idx := strings.Index(b64, ","); idx >= 0 && strings.HasPrefix(b64[:idx], "data:") {
		b64 = b64[idx+1:]
	}
	if b64 == "" {
		c.JSON(http.StatusBadRequest, gin.H{"error": "empty_avatar"})
		return
	}

	raw, err := base64.StdEncoding.DecodeString(b64)
	if err != nil || len(raw) == 0 || len(raw) > 2<<20 {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_avatar_image"})
		return
	}

	ext, _, validImg := detectImageExt(raw)
	if !validImg {
		c.JSON(http.StatusBadRequest, gin.H{"error": "unsupported_image_format"})
		return
	}

	ownerID := auth.IdentityFromContext(c).EmployeeID
	var nonce [6]byte
	if _, err := rand.Read(nonce[:]); err != nil {
		c.JSON(http.StatusInternalServerError, gin.H{"error": "avatar_save_failed"})
		return
	}
	filename := fmt.Sprintf("%sc%d_%d_%s%s", ownerAvatarPrefix(ownerID), id, time.Now().UnixNano(), hex.EncodeToString(nonce[:]), ext)
	fullPath := filepath.Join(s.store.AvatarDir(), filename)
	if err := os.WriteFile(fullPath, raw, 0o640); err != nil {
		c.JSON(http.StatusInternalServerError, gin.H{"error": "avatar_save_failed"})
		return
	}

	avatarURL := "/api/v1/avatars/" + filename
	updated, prevURL, err := s.store.SetCustomerAvatar(c.Request.Context(), ownerID, id, avatarURL)
	if err != nil {
		_ = os.Remove(fullPath)
		writeStoreErr(c, err)
		return
	}

	if prevURL != "" && prevURL != avatarURL {
		s.removeAvatarFile(ownerID, prevURL)
	}
	_, _ = s.store.CreateAuditLog(c.Request.Context(), ownerID, "upload_avatar", "customer", strconv.FormatInt(id, 10), filename)
	c.JSON(http.StatusOK, updated)
}

func (s *server) deleteCustomerAvatar(c *gin.Context) {
	id, ok := parsePathID(c, "id", "invalid_customer_id")
	if !ok {
		return
	}
	ownerID := auth.IdentityFromContext(c).EmployeeID
	updated, prevURL, err := s.store.SetCustomerAvatar(c.Request.Context(), ownerID, id, "")
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	s.removeAvatarFile(ownerID, prevURL)
	c.JSON(http.StatusOK, updated)
}

func (s *server) getAvatar(c *gin.Context) {
	rawName := c.Param("filename")
	ownerID := auth.IdentityFromContext(c).EmployeeID

	if !validAvatarFilename(ownerID, rawName) {
		c.JSON(http.StatusNotFound, gin.H{"error": "not_found"})
		return
	}

	fullPath := filepath.Join(s.store.AvatarDir(), rawName)
	raw, err := os.ReadFile(fullPath)
	if err != nil {
		c.JSON(http.StatusNotFound, gin.H{"error": "not_found"})
		return
	}
	_, contentType, ok := detectImageExt(raw)
	if !ok {
		contentType = "application/octet-stream"
	}
	c.Header("X-Content-Type-Options", "nosniff")
	c.Header("Cache-Control", "private, max-age=3600")
	c.Data(http.StatusOK, contentType, raw)
}

func (s *server) removeAvatarFile(ownerID, avatarURL string) {
	const routePrefix = "/api/v1/avatars/"
	if !strings.HasPrefix(avatarURL, routePrefix) {
		return
	}
	filename := strings.TrimPrefix(avatarURL, routePrefix)
	if !validAvatarFilename(ownerID, filename) {
		return
	}
	_ = os.Remove(filepath.Join(s.store.AvatarDir(), filename))
}
