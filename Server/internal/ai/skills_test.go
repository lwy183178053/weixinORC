package ai

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"weixinorc/server/internal/store"
)

func TestChatLookupSkills(t *testing.T) {
	st, err := store.Open(filepath.Join(t.TempDir(), "skills.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer st.Close()

	ctx := context.Background()
	owner := "emp-1"

	custRes, _, _, err := st.SyncWeChatCustomers(ctx, owner, []store.WeChatSyncCustomerItem{
		{NativeID: "wxid_vip1", WechatName: "张总", WechatRemark: "张总-VIP", Tags: "VIP客户, 重点跟进"},
		{NativeID: "wxid_other", WechatName: "李小姐", Tags: "普通客户"},
	})
	if err != nil {
		t.Fatal(err)
	}
	vipID := custRes[0].ID

	t1 := time.Date(2026, 11, 25, 10, 0, 0, 0, time.UTC)
	t2 := time.Date(2026, 11, 25, 10, 5, 0, 0, time.UTC)
	_, err = st.SyncCustomerMessages(ctx, owner, vipID, []store.WeChatSyncMessageItem{
		{ExternalID: "m1", Direction: "outgoing", Content: "张总您好，这是本周最新报价单", SentAt: t1},
		{ExternalID: "m2", Direction: "incoming", Content: "收到，下周三帮我安排热玛吉体验 🙂", SentAt: t2},
	})
	if err != nil {
		t.Fatal(err)
	}

	grpRes, _, _, err := st.SyncWeChatGroups(ctx, owner, []store.WeChatSyncGroupItem{
		{GroupKey: "888@chatroom", GroupName: "VIP服务群", MemberCount: 2},
	})
	if err != nil {
		t.Fatal(err)
	}
	grpID := grpRes[0].ID
	_, err = st.SyncGroupMessages(ctx, owner, grpID, []store.WeChatSyncMessageItem{
		{ExternalID: "gm1", SenderName: "张总-VIP", Direction: "incoming", Content: "群里发一下热玛吉术前注意事项", SentAt: t2.Add(time.Minute)},
	})
	if err != nil {
		t.Fatal(err)
	}

	svc := NewService(st)

	// 1. search_chat_messages across private & group chats
	raw, target, _, ok := svc.executeSkill(ctx, owner, "search_chat_messages", `{"query":"热玛吉"}`)
	if !ok || target == "" {
		t.Fatalf("search_chat_messages failed: target=%q raw=%s", target, raw)
	}
	var searchOut struct {
		Count int `json:"count"`
	}
	if err := json.Unmarshal([]byte(raw), &searchOut); err != nil || searchOut.Count != 2 {
		t.Fatalf("expected 2 hits for 热玛吉 across customer+group, got %s", raw)
	}

	// 2. get_recent_conversations with only_needs_reply=true
	raw, target, _, ok = svc.executeSkill(ctx, owner, "get_recent_conversations", `{"only_needs_reply":true}`)
	if !ok || target == "" {
		t.Fatalf("get_recent_conversations failed: target=%q raw=%s", target, raw)
	}
	var recentOut struct {
		Count int `json:"count"`
	}
	if err := json.Unmarshal([]byte(raw), &recentOut); err != nil || recentOut.Count != 2 {
		t.Fatalf("expected 2 needs-reply active sessions, got %s", raw)
	}

	// 3. get_tag_customers_chats
	raw, target, _, ok = svc.executeSkill(ctx, owner, "get_tag_customers_chats", `{"tag":"VIP客户"}`)
	if !ok || target != "#VIP客户" {
		t.Fatalf("get_tag_customers_chats failed: target=%q raw=%s", target, raw)
	}
	var tagOut struct {
		CustomerCount int `json:"customer_count"`
	}
	if err := json.Unmarshal([]byte(raw), &tagOut); err != nil || tagOut.CustomerCount != 1 {
		t.Fatalf("expected 1 VIP customer with chats, got %s", raw)
	}

	// 4. get_customer_detail pagination with before_message_id
	raw, target, _, ok = svc.executeSkill(ctx, owner, "get_customer_detail", `{"keyword":"张总","message_limit":1}`)
	if !ok || target == "" {
		t.Fatalf("get_customer_detail failed: target=%q raw=%s", target, raw)
	}
	var detailOut struct {
		HasOlder        bool  `json:"has_older"`
		NextBeforeMsgID int64 `json:"next_before_msg_id"`
	}
	if err := json.Unmarshal([]byte(raw), &detailOut); err != nil || !detailOut.HasOlder || detailOut.NextBeforeMsgID <= 0 {
		t.Fatalf("expected has_older=true with cursor, got %s", raw)
	}

	// 5. Shared time tool
	raw, _, _, ok = svc.executeSkill(ctx, owner, "get_current_time", `{"offset_days":1}`)
	if !ok || !strings.Contains(raw, "target_weekday") || !strings.Contains(raw, `"offset_days":1`) {
		t.Fatalf("get_current_time failed: ok=%v raw=%s", ok, raw)
	}

	// 6. Shared Zhimei knowledge catalog + full detail tools
	kb, err := st.CreateKnowledge(ctx, store.KnowledgeInput{
		Title:    "智美超光子嫩肤项目说明书",
		Category: "产品介绍",
		Tags:     "超光子,嫩肤,智美",
		Content:  "智美超光子采用双波段脉冲光技术，主打提亮肤色与改善泛红，单次护理约30分钟，术后注意保湿防晒。",
	})
	if err != nil {
		t.Fatal(err)
	}
	raw, _, _, ok = svc.executeSkill(ctx, owner, "list_knowledge_catalog", `{"category":"产品介绍"}`)
	if !ok || !strings.Contains(raw, "智美超光子嫩肤项目说明书") {
		t.Fatalf("list_knowledge_catalog failed: ok=%v raw=%s", ok, raw)
	}
	raw, _, _, ok = svc.executeSkill(ctx, owner, "get_knowledge_detail", mustJSON(map[string]any{"id": kb.ID}))
	if !ok || !strings.Contains(raw, "双波段脉冲光技术") {
		t.Fatalf("get_knowledge_detail failed: ok=%v raw=%s", ok, raw)
	}

	// 7. Auto-reply scope isolation: allows get_current_group_history and blocks private customer lookup
	raw, _, _, ok = svc.executeSkillScoped(ctx, owner, "get_current_group_history", `{"message_limit":10}`, grpID)
	if !ok || !strings.Contains(raw, "热玛吉术前注意事项") {
		t.Fatalf("get_current_group_history failed: ok=%v raw=%s", ok, raw)
	}
	raw, _, _, ok = svc.executeSkillScoped(ctx, owner, "list_customers", `{}`, grpID)
	if ok || !strings.Contains(raw, "禁止访问") {
		t.Fatalf("expected auto-reply scope to block list_customers, got ok=%v raw=%s", ok, raw)
	}

	// 8. Lightweight free no-key web_search tool (primary Bing RSS XML + HTML fallback)
	mockSearch := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Query().Get("format") == "rss" {
			w.Header().Set("Content-Type", "application/rss+xml; charset=utf-8")
			_, _ = w.Write([]byte(`<?xml version="1.0" encoding="utf-8"?><rss version="2.0"><channel>
				<item>
					<title>智美护肤科普指南</title>
					<link>https://example.com/zhimei</link>
					<description>超光子术后一周内建议温和洁面并做好防晒保湿。</description>
				</item>
			</channel></rss>`))
			return
		}
		w.Header().Set("Content-Type", "text/html; charset=utf-8")
		_, _ = w.Write([]byte(`<html><body><ol id="b_results">
			<li class="b_algo">
				<h2><a href="https://example.com/zhimei">智美护肤科普指南</a></h2>
				<div class="b_caption"><p>超光子术后一周内建议温和洁面并做好防晒保湿。</p></div>
			</li>
		</ol></body></html>`))
	}))
	defer mockSearch.Close()

	prevBing := bingSearchBaseURL
	bingSearchBaseURL = mockSearch.URL
	defer func() { bingSearchBaseURL = prevBing }()

	raw, _, _, ok = svc.executeSkill(ctx, owner, "web_search", `{"query":"超光子术后护理","limit":3}`)
	if !ok || !strings.Contains(raw, `"source":"bing_rss"`) || !strings.Contains(raw, "智美护肤科普指南") || !strings.Contains(raw, "温和洁面") {
		t.Fatalf("web_search failed: ok=%v raw=%s", ok, raw)
	}

	// 9. Baidu Baike fallback when Bing returns empty/error (fast China-friendly fallback without DuckDuckGo hang)
	mockEmptyBing := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusServiceUnavailable)
	}))
	defer mockEmptyBing.Close()
	mockBaike := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"title":"黎曼猜想","desc":"数学猜想","abstract":"黎曼猜想是关于黎曼ζ函数非平凡零点分布的著名数学猜想。","url":"https://baike.baidu.com/item/rh"}`))
	}))
	defer mockBaike.Close()

	prevBaike := baikeSearchBaseURL
	bingSearchBaseURL = mockEmptyBing.URL
	baikeSearchBaseURL = mockBaike.URL
	defer func() { baikeSearchBaseURL = prevBaike }()

	raw, _, _, ok = svc.executeSkill(ctx, owner, "web_search", `{"query":"黎曼猜想 证明","limit":3}`)
	if !ok || !strings.Contains(raw, `"source":"baidu_baike"`) || !strings.Contains(raw, "黎曼猜想") {
		t.Fatalf("baike fallback failed: ok=%v raw=%s", ok, raw)
	}
}

func TestGroupAutoReplyAnnouncementAndMultimodalFallback(t *testing.T) {
	st, err := store.Open(filepath.Join(t.TempDir(), "multimodal.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer st.Close()

	ctx := context.Background()
	owner := "emp-mm"

	// 1. Sync group with announcement and update welcome settings
	grpRes, _, _, err := st.SyncWeChatGroups(ctx, owner, []store.WeChatSyncGroupItem{
		{
			GroupKey:     "mm999@chatroom",
			GroupName:    "智美VIP答疑群",
			MemberCount:  12,
			Announcement: "本群服务时间每日9:00-21:00，本月超光子体验价680元。",
		},
	})
	if err != nil {
		t.Fatal(err)
	}
	grpID := grpRes[0].ID

	welcomeOn := true
	welcomeTpl := "欢迎 {新人昵称} 加入 {群名}！"
	updatedGrp, err := st.UpdateGroup(ctx, owner, grpID, store.GroupPatch{
		WelcomeEnabled:  &welcomeOn,
		WelcomeTemplate: &welcomeTpl,
	})
	if err != nil {
		t.Fatal(err)
	}
	if !updatedGrp.WelcomeEnabled || updatedGrp.WelcomeTemplate != welcomeTpl || !strings.Contains(updatedGrp.Announcement, "680元") {
		t.Fatalf("unexpected updated group: %+v", updatedGrp)
	}

	// 2. Mock OpenAI server that rejects image_url on first attempt (simulating text-only model)
	//    and verifies announcement injection + text-only fallback retry.
	var calls int
	var sawImageURL bool
	var sawAnnouncement bool
	mockAI := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls++
		var req struct {
			Messages []struct {
				Role    string          `json:"role"`
				Content json.RawMessage `json:"content"`
			} `json:"messages"`
		}
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			http.Error(w, err.Error(), http.StatusBadRequest)
			return
		}
		for _, m := range req.Messages {
			raw := string(m.Content)
			if strings.Contains(raw, "本月超光子体验价680元") {
				sawAnnouncement = true
			}
			if strings.Contains(raw, `"image_url"`) {
				sawImageURL = true
				w.WriteHeader(http.StatusBadRequest)
				_, _ = w.Write([]byte(`{"error":{"message":"This model does not support image_url input"}}`))
				return
			}
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"choices":[{"message":{"role":"assistant","content":"您好，本月超光子体验价是680元，服务时间为每日9:00-21:00。"}}]}`))
	}))
	defer mockAI.Close()

	_, err = st.SaveAISettings(ctx, store.AISettingsUpdate{
		Enabled: true,
		BaseURL: mockAI.URL,
		APIKey:  "sk-test",
		Model:   "text-only-mock",
	})
	if err != nil {
		t.Fatal(err)
	}

	svc := NewService(st)
	res, err := svc.GroupAutoReply(ctx, owner, GroupAutoReplyInput{
		GroupID:        grpID,
		SenderName:     "王小姐",
		TriggerMessage: "请问这张图里的项目多少钱？",
		Images:         []string{"data:image/jpeg;base64,/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAA=="},
	})
	if err != nil {
		t.Fatalf("expected graceful fallback to text-only, got err: %v", err)
	}
	if !sawImageURL {
		t.Fatalf("expected initial request to include multimodal image_url")
	}
	if !sawAnnouncement {
		t.Fatalf("expected group announcement to be injected into system prompt")
	}
	if calls != 2 {
		t.Fatalf("expected 2 calls (multimodal attempt + text-only fallback), got %d", calls)
	}
	if !strings.Contains(res.Reply, "680元") {
		t.Fatalf("unexpected reply: %q", res.Reply)
	}
}
