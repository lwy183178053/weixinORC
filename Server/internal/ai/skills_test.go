package ai

import (
	"context"
	"encoding/json"
	"path/filepath"
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
}
