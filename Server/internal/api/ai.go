package api

import (
	_ "embed"
	"errors"
	"net/http"
	"strings"

	"github.com/gin-gonic/gin"
	"weixinorc/server/internal/ai"
	"weixinorc/server/internal/auth"
	"weixinorc/server/internal/store"
)

//go:embed admin_ui.html
var adminUIHTML []byte

func (s *server) serveAdminUI(c *gin.Context) {
	c.Data(http.StatusOK, "text/html; charset=utf-8", adminUIHTML)
}

func (s *server) getAIStatus(c *gin.Context) {
	cfg, err := s.store.GetAISettings(c.Request.Context())
	if err != nil {
		c.JSON(http.StatusInternalServerError, gin.H{"error": "internal_error"})
		return
	}
	configured := cfg.Enabled && strings.TrimSpace(cfg.BaseURL) != "" && strings.TrimSpace(cfg.Model) != ""
	c.JSON(http.StatusOK, gin.H{
		"enabled":    cfg.Enabled,
		"configured": configured,
		"model":      cfg.Model,
		"base_url":   cfg.BaseURL,
		"skills":     ai.BuiltinSkills(),
	})
}

func (s *server) getAdminAIConfig(c *gin.Context) {
	cfg, err := s.store.GetAISettings(c.Request.Context())
	if err != nil {
		c.JSON(http.StatusInternalServerError, gin.H{"error": "internal_error"})
		return
	}
	c.JSON(http.StatusOK, cfg)
}

type updateAIConfigRequest struct {
	Enabled      bool    `json:"enabled"`
	BaseURL      string  `json:"base_url"`
	APIKey       string  `json:"api_key"`
	ClearAPIKey  bool    `json:"clear_api_key"`
	Model        string  `json:"model"`
	SystemPrompt string  `json:"system_prompt"`
	Temperature  float64 `json:"temperature"`
	MaxTokens    int     `json:"max_tokens"`
	TimeoutSec   int     `json:"timeout_sec"`
}

func (s *server) updateAdminAIConfig(c *gin.Context) {
	var req updateAIConfigRequest
	if !bindJSON(c, &req) {
		return
	}
	if strings.TrimSpace(req.BaseURL) != "" {
		if _, err := ai.ResolveChatEndpoint(req.BaseURL); err != nil {
			c.JSON(http.StatusBadRequest, gin.H{"error": err.Error()})
			return
		}
	}

	updated, err := s.store.SaveAISettings(c.Request.Context(), store.AISettingsUpdate{
		Enabled:      req.Enabled,
		BaseURL:      req.BaseURL,
		APIKey:       req.APIKey,
		ClearAPIKey:  req.ClearAPIKey,
		Model:        req.Model,
		SystemPrompt: req.SystemPrompt,
		Temperature:  req.Temperature,
		MaxTokens:    req.MaxTokens,
		TimeoutSec:   req.TimeoutSec,
	})
	if err != nil {
		c.JSON(http.StatusInternalServerError, gin.H{"error": "internal_error"})
		return
	}

	id := auth.IdentityFromContext(c)
	_, _ = s.store.CreateAuditLog(c.Request.Context(), id.EmployeeID, "ai_config_update", "ai_settings", "1", "model="+updated.Model)
	c.JSON(http.StatusOK, updated)
}

type testAIConfigRequest struct {
	BaseURL    string `json:"base_url"`
	APIKey     string `json:"api_key"`
	Model      string `json:"model"`
	TimeoutSec int    `json:"timeout_sec"`
}

func (s *server) testAdminAIConfig(c *gin.Context) {
	var req testAIConfigRequest
	var override *store.AISettings
	if c.Request.ContentLength > 0 {
		if !bindJSON(c, &req) {
			return
		}
		if strings.TrimSpace(req.BaseURL) != "" || strings.TrimSpace(req.Model) != "" || strings.TrimSpace(req.APIKey) != "" {
			override = &store.AISettings{
				BaseURL:    req.BaseURL,
				APIKey:     req.APIKey,
				Model:      req.Model,
				TimeoutSec: req.TimeoutSec,
			}
		}
	}
	res := s.ai.TestConnection(c.Request.Context(), override)
	c.JSON(http.StatusOK, res)
}

// ---- Knowledge Base Admin Endpoints ----

type knowledgeRequest struct {
	Title         string `json:"title"`
	Category      string `json:"category"`
	Tags          string `json:"tags"`
	Content       string `json:"content"`
	AlwaysInclude bool   `json:"always_include"`
}

func (s *server) listAdminKnowledge(c *gin.Context) {
	items, err := s.store.ListKnowledge(c.Request.Context())
	if err != nil {
		c.JSON(http.StatusInternalServerError, gin.H{"error": "internal_error"})
		return
	}
	c.JSON(http.StatusOK, gin.H{"items": items})
}

func (s *server) createAdminKnowledge(c *gin.Context) {
	var req knowledgeRequest
	if !bindJSON(c, &req) {
		return
	}
	if strings.TrimSpace(req.Title) == "" || strings.TrimSpace(req.Content) == "" {
		c.JSON(http.StatusBadRequest, gin.H{"error": "标题与内容不能为空"})
		return
	}
	item, err := s.store.CreateKnowledge(c.Request.Context(), store.KnowledgeInput{
		Title:         req.Title,
		Category:      req.Category,
		Tags:          req.Tags,
		Content:       req.Content,
		AlwaysInclude: req.AlwaysInclude,
	})
	if err != nil {
		c.JSON(http.StatusInternalServerError, gin.H{"error": "internal_error"})
		return
	}
	c.JSON(http.StatusCreated, item)
}

func (s *server) updateAdminKnowledge(c *gin.Context) {
	id, ok := parsePathID(c, "id", "invalid_knowledge_id")
	if !ok {
		return
	}
	var req knowledgeRequest
	if !bindJSON(c, &req) {
		return
	}
	if strings.TrimSpace(req.Title) == "" || strings.TrimSpace(req.Content) == "" {
		c.JSON(http.StatusBadRequest, gin.H{"error": "标题与内容不能为空"})
		return
	}
	item, err := s.store.UpdateKnowledge(c.Request.Context(), id, store.KnowledgeInput{
		Title:         req.Title,
		Category:      req.Category,
		Tags:          req.Tags,
		Content:       req.Content,
		AlwaysInclude: req.AlwaysInclude,
	})
	if errors.Is(err, store.ErrNotFound) {
		c.JSON(http.StatusNotFound, gin.H{"error": "knowledge_not_found"})
		return
	}
	if err != nil {
		c.JSON(http.StatusInternalServerError, gin.H{"error": "internal_error"})
		return
	}
	c.JSON(http.StatusOK, item)
}

func (s *server) deleteAdminKnowledge(c *gin.Context) {
	id, ok := parsePathID(c, "id", "invalid_knowledge_id")
	if !ok {
		return
	}
	err := s.store.DeleteKnowledge(c.Request.Context(), id)
	if errors.Is(err, store.ErrNotFound) {
		c.JSON(http.StatusNotFound, gin.H{"error": "knowledge_not_found"})
		return
	}
	if err != nil {
		c.JSON(http.StatusInternalServerError, gin.H{"error": "internal_error"})
		return
	}
	c.Status(http.StatusNoContent)
}

// ---- Employee AI Endpoints ----

type customerSummaryRequest struct {
	CustomerID int64 `json:"customer_id"`
}

func (s *server) aiCustomerSummary(c *gin.Context) {
	var req customerSummaryRequest
	if !bindJSON(c, &req) {
		return
	}
	if req.CustomerID <= 0 {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_customer_id"})
		return
	}
	id := auth.IdentityFromContext(c)
	res, err := s.ai.SummarizeCustomer(c.Request.Context(), id.EmployeeID, req.CustomerID)
	if err != nil {
		writeAIError(c, err)
		return
	}
	_, _ = s.store.CreateAuditLog(c.Request.Context(), id.EmployeeID, "ai_customer_summary", "customer", "", "model="+res.Model)
	c.JSON(http.StatusOK, res)
}

type customerChatRequest struct {
	CustomerID int64         `json:"customer_id"`
	History    []ai.ChatTurn `json:"history"`
	Message    string        `json:"message"`
	Images     []string      `json:"images,omitempty"`
}

func (s *server) aiCustomerChat(c *gin.Context) {
	var req customerChatRequest
	if !bindJSON(c, &req) {
		return
	}
	if req.CustomerID <= 0 || (strings.TrimSpace(req.Message) == "" && len(req.Images) == 0) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_request"})
		return
	}
	id := auth.IdentityFromContext(c)
	res, err := s.ai.CustomerChat(c.Request.Context(), id.EmployeeID, req.CustomerID, req.History, req.Message, req.Images)
	if err != nil {
		writeAIError(c, err)
		return
	}
	_, _ = s.store.CreateAuditLog(c.Request.Context(), id.EmployeeID, "ai_customer_chat", "customer", "", "model="+res.Model)
	c.JSON(http.StatusOK, res)
}

type agentChatRequest struct {
	History []ai.ChatTurn `json:"history"`
	Message string        `json:"message"`
	Images  []string      `json:"images,omitempty"`
}

func (s *server) aiAgentChat(c *gin.Context) {
	var req agentChatRequest
	if !bindJSON(c, &req) {
		return
	}
	if strings.TrimSpace(req.Message) == "" && len(req.Images) == 0 {
		c.JSON(http.StatusBadRequest, gin.H{"error": "empty_message"})
		return
	}
	id := auth.IdentityFromContext(c)
	res, err := s.ai.AgentChat(c.Request.Context(), id.EmployeeID, req.History, req.Message, req.Images)
	if err != nil {
		writeAIError(c, err)
		return
	}
	_, _ = s.store.CreateAuditLog(c.Request.Context(), id.EmployeeID, "ai_agent_chat", "agent", "", "model="+res.Model)
	c.JSON(http.StatusOK, res)
}

func writeAIError(c *gin.Context, err error) {
	if errors.Is(err, ai.ErrAINotConfigured) {
		c.JSON(http.StatusServiceUnavailable, gin.H{"error": err.Error()})
		return
	}
	if errors.Is(err, store.ErrNotFound) {
		c.JSON(http.StatusNotFound, gin.H{"error": "客户档案不存在"})
		return
	}
	c.JSON(http.StatusBadGateway, gin.H{"error": err.Error()})
}
