package api

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"

	"weixinorc/server/internal/auth"
)

func TestAdminUIAndAIConfigAndKnowledgeAndAgentSkills(t *testing.T) {
	verifier := testVerifier{
		password: "secret",
		identities: map[string]auth.Identity{
			"alice": {EmployeeID: "emp-alice", Account: "alice", DisplayName: "Alice"},
			"bob":   {EmployeeID: "emp-bob", Account: "bob", DisplayName: "Bob"},
		},
	}
	app := newTestApp(t, verifier)
	defer app.db.Close()

	// 1. Web Admin HTML served at /admin
	adminResp := app.request(t, http.MethodGet, "/admin", "", nil)
	if adminResp.Code != http.StatusOK || !strings.Contains(adminResp.Body.String(), "智美员工工作台 · 服务端配置管理台") {
		t.Fatalf("GET /admin failed: code=%d", adminResp.Code)
	}

	aliceToken := app.login(t, "alice")
	bobToken := app.login(t, "bob")

	// 2. Initial AI status is unconfigured and returns 6 builtin skills
	statusResp := app.request(t, http.MethodGet, "/api/v1/ai/status", aliceToken, nil)
	if statusResp.Code != http.StatusOK {
		t.Fatalf("GET /api/v1/ai/status: got %d %s", statusResp.Code, statusResp.Body.String())
	}
	var statusBody struct {
		Configured bool `json:"configured"`
		Skills     []struct {
			ID string `json:"id"`
		} `json:"skills"`
	}
	if err := json.Unmarshal(statusResp.Body.Bytes(), &statusBody); err != nil {
		t.Fatal(err)
	}
	if statusBody.Configured {
		t.Fatal("expected initial AI status configured=false")
	}
	if len(statusBody.Skills) != 6 {
		t.Fatalf("expected 6 builtin skills, got %d", len(statusBody.Skills))
	}

	// 3. Seed one customer for Alice and one for Bob to verify owner-scoped Skills
	cAliceResp := app.request(t, http.MethodPost, "/api/v1/customers", aliceToken, map[string]any{
		"wechat_account": "wx_alice_vip",
		"wechat_name":    "王女士",
		"tags":           "高意向,医美",
		"follow_status":  "报价中",
		"note":           "重点关注热玛吉价格与恢复期",
	})
	if cAliceResp.Code != http.StatusCreated {
		t.Fatalf("create alice customer: got %d %s", cAliceResp.Code, cAliceResp.Body.String())
	}
	var cAlice struct {
		ID int64 `json:"id"`
	}
	_ = json.Unmarshal(cAliceResp.Body.Bytes(), &cAlice)

	_ = app.request(t, http.MethodPost, "/api/v1/customers", bobToken, map[string]any{
		"wechat_account": "wx_bob_secret",
		"wechat_name":    "Bob机密客户",
		"tags":           "机密",
	})

	// 4. Knowledge base CRUD
	kbCreate := app.request(t, http.MethodPost, "/api/v1/admin/knowledge", aliceToken, map[string]any{
		"title":          "热玛吉标准报价与红线",
		"category":       "报价口径",
		"tags":           "热玛吉,价格",
		"content":        "热玛吉单次体验价 9800 元，严禁承诺100%无痛或永久有效。",
		"always_include": true,
	})
	if kbCreate.Code != http.StatusCreated {
		t.Fatalf("create knowledge: got %d %s", kbCreate.Code, kbCreate.Body.String())
	}

	// 5. Mock OpenAI-compatible server supporting both direct chat and 2-round Function Calling
	var agentRound atomic.Int32
	mockLLM := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/v1/chat/completions" {
			http.Error(w, "not found", http.StatusNotFound)
			return
		}
		if r.Header.Get("Authorization") != "Bearer sk-test-secret-123456" {
			http.Error(w, "bad auth", http.StatusUnauthorized)
			return
		}

		var reqBody struct {
			Model    string `json:"model"`
			Tools    []any  `json:"tools"`
			Messages []struct {
				Role    string `json:"role"`
				Content string `json:"content"`
			} `json:"messages"`
		}
		if err := json.NewDecoder(r.Body).Decode(&reqBody); err != nil {
			http.Error(w, err.Error(), http.StatusBadRequest)
			return
		}

		w.Header().Set("Content-Type", "application/json")

		// Agent call (has tools): Round 1 invokes list_customers; Round 2 verifies Alice only sees her own customer
		if len(reqBody.Tools) > 0 {
			round := agentRound.Add(1)
			if round == 1 {
				_ = json.NewEncoder(w).Encode(map[string]any{
					"choices": []map[string]any{
						{
							"message": map[string]any{
								"role":    "assistant",
								"content": "",
								"tool_calls": []map[string]any{
									{
										"id":   "call_1",
										"type": "function",
										"function": map[string]any{
											"name":      "list_customers",
											"arguments": `{"query":"","limit":20}`,
										},
									},
								},
							},
						},
					},
				})
				return
			}

			// Verify tool response includes Alice's customer and NEVER includes Bob's customer
			joined := ""
			for _, m := range reqBody.Messages {
				if m.Role == "tool" {
					joined += m.Content
				}
			}
			if !strings.Contains(joined, "王女士") {
				t.Errorf("expected tool output to contain Alice's customer 王女士, got: %s", joined)
			}
			if strings.Contains(joined, "Bob机密客户") {
				t.Errorf("owner isolation breached! tool output leaked Bob's customer: %s", joined)
			}

			_ = json.NewEncoder(w).Encode(map[string]any{
				"choices": []map[string]any{
					{
						"message": map[string]any{
							"role":    "assistant",
							"content": "已为您检索到 1 位重点客户：王女士（高意向，报价中）。",
						},
					},
				},
			})
			return
		}

		// Direct completion (test connection / customer summary / customer chat)
		_ = json.NewEncoder(w).Encode(map[string]any{
			"choices": []map[string]any{
				{
					"message": map[string]any{
						"role":    "assistant",
						"content": "【客户画像】王女士意向较高，建议按9800元体验价跟进。",
					},
				},
			},
		})
	}))
	defer mockLLM.Close()

	// 6. Save AI config via Admin API and confirm key is masked on GET
	saveCfgResp := app.request(t, http.MethodPut, "/api/v1/admin/ai-config", aliceToken, map[string]any{
		"enabled":     true,
		"base_url":    mockLLM.URL + "/v1",
		"api_key":     "sk-test-secret-123456",
		"model":       "mock-gpt",
		"temperature": 0.4,
		"max_tokens":  1024,
		"timeout_sec": 30,
	})
	if saveCfgResp.Code != http.StatusOK {
		t.Fatalf("PUT /api/v1/admin/ai-config: got %d %s", saveCfgResp.Code, saveCfgResp.Body.String())
	}

	getCfgResp := app.request(t, http.MethodGet, "/api/v1/admin/ai-config", aliceToken, nil)
	if strings.Contains(getCfgResp.Body.String(), "sk-test-secret-123456") {
		t.Fatalf("GET /api/v1/admin/ai-config leaked raw api key: %s", getCfgResp.Body.String())
	}

	// 7. Test connectivity endpoint
	testConnResp := app.request(t, http.MethodPost, "/api/v1/admin/ai-test", aliceToken, map[string]any{})
	if testConnResp.Code != http.StatusOK || !strings.Contains(testConnResp.Body.String(), `"ok":true`) {
		t.Fatalf("test connection failed: %d %s", testConnResp.Code, testConnResp.Body.String())
	}

	// 8. Test per-customer summary & chat
	sumResp := app.request(t, http.MethodPost, "/api/v1/ai/customer-summary", aliceToken, map[string]any{
		"customer_id": cAlice.ID,
	})
	if sumResp.Code != http.StatusOK || !strings.Contains(sumResp.Body.String(), "王女士") {
		t.Fatalf("customer-summary failed: %d %s", sumResp.Code, sumResp.Body.String())
	}

	chatResp := app.request(t, http.MethodPost, "/api/v1/ai/customer-chat", aliceToken, map[string]any{
		"customer_id": cAlice.ID,
		"history":     []map[string]string{},
		"message":     "帮我写一句报价跟进话术",
	})
	if chatResp.Code != http.StatusOK || !strings.Contains(chatResp.Body.String(), `"reply"`) {
		t.Fatalf("customer-chat failed: %d %s", chatResp.Code, chatResp.Body.String())
	}

	// 9. Test Agent Chat with Function Calling & tool_trace
	agentResp := app.request(t, http.MethodPost, "/api/v1/ai/agent-chat", aliceToken, map[string]any{
		"history": []map[string]string{},
		"message": "帮我列出名下所有重点客户",
	})
	if agentResp.Code != http.StatusOK {
		t.Fatalf("agent-chat failed: %d %s", agentResp.Code, agentResp.Body.String())
	}
	var agentResult struct {
		Reply     string `json:"reply"`
		ToolTrace []struct {
			SkillID string `json:"skill_id"`
			OK      bool   `json:"ok"`
		} `json:"tool_trace"`
	}
	if err := json.Unmarshal(agentResp.Body.Bytes(), &agentResult); err != nil {
		t.Fatal(err)
	}
	if len(agentResult.ToolTrace) != 1 || agentResult.ToolTrace[0].SkillID != "list_customers" || !agentResult.ToolTrace[0].OK {
		t.Fatalf("expected list_customers tool_trace, got %+v", agentResult.ToolTrace)
	}
	if !strings.Contains(agentResult.Reply, "王女士") {
		t.Fatalf("unexpected agent reply: %s", agentResult.Reply)
	}
}
