package api

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"testing"
	"time"

	"weixinorc/server/internal/auth"
	"weixinorc/server/internal/store"
)

type testVerifier struct {
	identities map[string]auth.Identity
	password   string
}

func (v testVerifier) Verify(_ context.Context, account, password string) (auth.Identity, error) {
	identity, ok := v.identities[account]
	if !ok || password != v.password {
		return auth.Identity{}, auth.ErrInvalidCredentials
	}
	return identity, nil
}

type testApp struct {
	handler http.Handler
	db      *store.Store
}

func newTestApp(t *testing.T, verifier auth.Verifier) testApp {
	t.Helper()
	database, err := store.Open(filepath.Join(t.TempDir(), "test.db"))
	if err != nil {
		t.Fatal(err)
	}
	sessions := auth.NewSessions(time.Hour)
	return testApp{handler: New(database, verifier, sessions), db: database}
}

func (a testApp) request(t *testing.T, method, path, token string, body any) *httptest.ResponseRecorder {
	t.Helper()
	var payload bytes.Buffer
	if body != nil {
		if err := json.NewEncoder(&payload).Encode(body); err != nil {
			t.Fatal(err)
		}
	}
	req := httptest.NewRequest(method, path, &payload)
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	if token != "" {
		req.Header.Set("Authorization", "Bearer "+token)
	}
	response := httptest.NewRecorder()
	a.handler.ServeHTTP(response, req)
	return response
}

func (a testApp) login(t *testing.T, account string) string {
	t.Helper()
	response := a.request(t, http.MethodPost, "/api/v1/login", "", map[string]string{"account": account, "password": "secret"})
	if response.Code != http.StatusOK {
		t.Fatalf("login %s: got %d %s", account, response.Code, response.Body.String())
	}
	var result struct {
		Token string `json:"token"`
	}
	if err := json.Unmarshal(response.Body.Bytes(), &result); err != nil {
		t.Fatal(err)
	}
	if result.Token == "" {
		t.Fatal("login returned an empty token")
	}
	return result.Token
}

func TestLoginUnavailableByDefault(t *testing.T) {
	app := newTestApp(t, nil)
	defer app.db.Close()
	response := app.request(t, http.MethodPost, "/api/v1/login", "", map[string]string{"account": "a", "password": "b"})
	if response.Code != http.StatusServiceUnavailable {
		t.Fatalf("expected 503, got %d", response.Code)
	}
}

func TestInvalidCredentialsAndMissingToken(t *testing.T) {
	verifier := testVerifier{password: "secret", identities: map[string]auth.Identity{"alice": {EmployeeID: "employee-a", Account: "alice", DisplayName: "Alice"}}}
	app := newTestApp(t, verifier)
	defer app.db.Close()
	invalid := app.request(t, http.MethodPost, "/api/v1/login", "", map[string]string{"account": "alice", "password": "wrong"})
	if invalid.Code != http.StatusUnauthorized {
		t.Fatalf("invalid credentials expected 401, got %d", invalid.Code)
	}
	missing := app.request(t, http.MethodGet, "/api/v1/me", "", nil)
	if missing.Code != http.StatusUnauthorized {
		t.Fatalf("missing token expected 401, got %d", missing.Code)
	}
}

func TestCustomerMessageFlowsAndOwnerIsolation(t *testing.T) {
	verifier := testVerifier{
		password: "secret",
		identities: map[string]auth.Identity{
			"alice": {EmployeeID: "employee-a", Account: "alice", DisplayName: "Alice"},
			"bob":   {EmployeeID: "employee-b", Account: "bob", DisplayName: "Bob"},
		},
	}
	app := newTestApp(t, verifier)
	defer app.db.Close()
	alice := app.login(t, "alice")
	bob := app.login(t, "bob")

	created := app.request(t, http.MethodPost, "/api/v1/customers", alice, map[string]string{"wechat_remark": "RemarkName", "wechat_name": "Customer", "wechat_account": "wx-1", "note": "initial"})
	if created.Code != http.StatusCreated {
		t.Fatalf("create customer: got %d %s", created.Code, created.Body.String())
	}
	var customer struct {
		ID           int64  `json:"id"`
		WechatRemark string `json:"wechat_remark"`
		WechatName   string `json:"wechat_name"`
		Note         string `json:"note"`
	}
	if err := json.Unmarshal(created.Body.Bytes(), &customer); err != nil {
		t.Fatal(err)
	}
	if customer.WechatRemark != "RemarkName" || customer.WechatName != "Customer" || customer.Note != "initial" {
		t.Fatalf("unexpected customer fields: %+v", customer)
	}
	path := fmt.Sprintf("/api/v1/customers/%d", customer.ID)

	if response := app.request(t, http.MethodGet, path, bob, nil); response.Code != http.StatusNotFound {
		t.Fatalf("cross-owner get expected 404, got %d", response.Code)
	}
	if response := app.request(t, http.MethodPatch, path, bob, map[string]string{"note": "stolen"}); response.Code != http.StatusNotFound {
		t.Fatalf("cross-owner patch expected 404, got %d", response.Code)
	}
	if response := app.request(t, http.MethodPost, path+"/messages", bob, map[string]string{"direction": "incoming", "content": "hello"}); response.Code != http.StatusNotFound {
		t.Fatalf("cross-owner message expected 404, got %d", response.Code)
	}

	if response := app.request(t, http.MethodPost, "/api/v1/customers", alice, map[string]string{"wechat_account": "wx-1"}); response.Code != http.StatusConflict {
		t.Fatalf("duplicate account expected 409, got %d", response.Code)
	}
	patched := app.request(t, http.MethodPatch, path, alice, map[string]string{"note": ""})
	if patched.Code != http.StatusOK {
		t.Fatalf("clear note: got %d %s", patched.Code, patched.Body.String())
	}

	for _, content := range []string{"first", "second", "third"} {
		response := app.request(t, http.MethodPost, path+"/messages", alice, map[string]string{"direction": "incoming", "content": content})
		if response.Code != http.StatusCreated {
			t.Fatalf("create message: got %d %s", response.Code, response.Body.String())
		}
	}
	page := app.request(t, http.MethodGet, path+"/messages?limit=2", alice, nil)
	if page.Code != http.StatusOK {
		t.Fatalf("list messages: got %d %s", page.Code, page.Body.String())
	}
	var result struct {
		Items      []json.RawMessage `json:"items"`
		NextCursor int64             `json:"next_cursor"`
	}
	if err := json.Unmarshal(page.Body.Bytes(), &result); err != nil {
		t.Fatal(err)
	}
	if len(result.Items) != 2 || result.NextCursor == 0 {
		t.Fatalf("expected two messages and a cursor, got %d items, cursor %d", len(result.Items), result.NextCursor)
	}
	secondPage := app.request(t, http.MethodGet, fmt.Sprintf("%s/messages?limit=2&cursor=%d", path, result.NextCursor), alice, nil)
	if secondPage.Code != http.StatusOK {
		t.Fatalf("second page: got %d %s", secondPage.Code, secondPage.Body.String())
	}
}

func TestSessionRevocationAndRestart(t *testing.T) {
	path := filepath.Join(t.TempDir(), "persistent.db")
	database, err := store.Open(path)
	if err != nil {
		t.Fatal(err)
	}
	verifier := testVerifier{password: "secret", identities: map[string]auth.Identity{"alice": {EmployeeID: "employee-a", Account: "alice", DisplayName: "Alice"}}}
	sessions := auth.NewSessions(time.Hour)
	handler := New(database, verifier, sessions)
	app := testApp{handler: handler, db: database}
	token := app.login(t, "alice")
	created := app.request(t, http.MethodPost, "/api/v1/customers", token, map[string]string{"wechat_name": "Persistent"})
	if created.Code != http.StatusCreated {
		t.Fatalf("create customer: got %d %s", created.Code, created.Body.String())
	}
	var customer struct {
		ID int64 `json:"id"`
	}
	if err := json.Unmarshal(created.Body.Bytes(), &customer); err != nil {
		t.Fatal(err)
	}
	message := app.request(t, http.MethodPost, fmt.Sprintf("/api/v1/customers/%d/messages", customer.ID), token, map[string]string{"direction": "incoming", "content": "persisted message"})
	if message.Code != http.StatusCreated {
		t.Fatalf("create message: got %d %s", message.Code, message.Body.String())
	}
	if response := app.request(t, http.MethodPost, "/api/v1/logout", token, nil); response.Code != http.StatusNoContent {
		t.Fatalf("logout: got %d", response.Code)
	}
	if response := app.request(t, http.MethodGet, "/api/v1/me", token, nil); response.Code != http.StatusUnauthorized {
		t.Fatalf("revoked token expected 401, got %d", response.Code)
	}
	if err := database.Close(); err != nil {
		t.Fatal(err)
	}

	database, err = store.Open(path)
	if err != nil {
		t.Fatal(err)
	}
	defer database.Close()
	newApp := testApp{handler: New(database, verifier, auth.NewSessions(time.Hour)), db: database}
	newToken := newApp.login(t, "alice")
	list := newApp.request(t, http.MethodGet, "/api/v1/customers", newToken, nil)
	if list.Code != http.StatusOK {
		t.Fatalf("persistent customer list: got %d %s", list.Code, list.Body.String())
	}
	persistedMessages := newApp.request(t, http.MethodGet, fmt.Sprintf("/api/v1/customers/%d/messages", customer.ID), newToken, nil)
	if persistedMessages.Code != http.StatusOK || !bytes.Contains(persistedMessages.Body.Bytes(), []byte("persisted message")) {
		t.Fatalf("persistent messages: got %d %s", persistedMessages.Code, persistedMessages.Body.String())
	}
	if response := newApp.request(t, http.MethodGet, "/api/v1/me", token, nil); response.Code != http.StatusUnauthorized {
		t.Fatalf("pre-restart token expected 401, got %d", response.Code)
	}
}

func TestExtendedProfileAvatarAccountsMembersAndAuditLogs(t *testing.T) {
	verifier := testVerifier{
		password: "secret",
		identities: map[string]auth.Identity{
			"alice": {EmployeeID: "employee-a", Account: "alice", DisplayName: "Alice"},
			"bob":   {EmployeeID: "employee-b", Account: "bob", DisplayName: "Bob"},
		},
	}
	app := newTestApp(t, verifier)
	defer app.db.Close()
	alice := app.login(t, "alice")
	bob := app.login(t, "bob")

	// 1. Extended customer profile fields
	created := app.request(t, http.MethodPost, "/api/v1/customers", alice, map[string]string{
		"wechat_remark":  "张总",
		"wechat_name":    "阿良",
		"wechat_account": "wx_aliang",
		"gender":         "男",
		"description":    "华东区采购负责人",
		"tags":           "VIP,高意向",
		"follow_status":  "跟进中",
		"local_key":      "local_001",
		"native_id":      "",
	})
	if created.Code != http.StatusCreated {
		t.Fatalf("create extended customer: got %d %s", created.Code, created.Body.String())
	}
	var cust store.Customer
	if err := json.Unmarshal(created.Body.Bytes(), &cust); err != nil {
		t.Fatal(err)
	}
	if cust.Gender != "男" || cust.FollowStatus != "跟进中" || cust.Tags != "VIP,高意向" || cust.Description != "华东区采购负责人" {
		t.Fatalf("unexpected extended fields: %+v", cust)
	}

	// 2. Upload avatar (valid 1x1 PNG) and verify strict owner isolation
	tinyPngBase64 := "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="
	avatarResp := app.request(t, http.MethodPost, fmt.Sprintf("/api/v1/customers/%d/avatar", cust.ID), alice, map[string]string{
		"image_base64": tinyPngBase64,
	})
	if avatarResp.Code != http.StatusOK {
		t.Fatalf("upload avatar: got %d %s", avatarResp.Code, avatarResp.Body.String())
	}
	if err := json.Unmarshal(avatarResp.Body.Bytes(), &cust); err != nil {
		t.Fatal(err)
	}
	if cust.AvatarURL == "" {
		t.Fatal("expected non-empty avatar_url after upload")
	}
	if getOwn := app.request(t, http.MethodGet, cust.AvatarURL, alice, nil); getOwn.Code != http.StatusOK {
		t.Fatalf("alice get own avatar expected 200, got %d", getOwn.Code)
	}
	if getCross := app.request(t, http.MethodGet, cust.AvatarURL, bob, nil); getCross.Code != http.StatusNotFound {
		t.Fatalf("bob cross-owner get avatar expected 404, got %d", getCross.Code)
	}

	// 3. WeChat account binding
	bindResp := app.request(t, http.MethodPost, "/api/v1/wechat-accounts", alice, map[string]string{
		"account_type": "personal",
		"wechat_id":    "wx_emp_alice",
		"nickname":     "Alice工作号",
		"status":       "online",
	})
	if bindResp.Code != http.StatusOK {
		t.Fatalf("bind wechat account: got %d %s", bindResp.Code, bindResp.Body.String())
	}
	if listAcc := app.request(t, http.MethodGet, "/api/v1/wechat-accounts", alice, nil); listAcc.Code != http.StatusOK || !bytes.Contains(listAcc.Body.Bytes(), []byte("wx_emp_alice")) {
		t.Fatalf("list wechat accounts: got %d %s", listAcc.Code, listAcc.Body.String())
	}

	// 4. Group & Group Members (including extended fields + avatar upload)
	grpResp := app.request(t, http.MethodPost, "/api/v1/groups", alice, map[string]any{"group_name": "VIP客户群", "my_nickname": "客服小王", "member_count": 12})
	if grpResp.Code != http.StatusCreated {
		t.Fatalf("create group: got %d %s", grpResp.Code, grpResp.Body.String())
	}
	var grp store.Group
	if err := json.Unmarshal(grpResp.Body.Bytes(), &grp); err != nil {
		t.Fatal(err)
	}
	if grp.MyNickname != "客服小王" {
		t.Fatalf("expected my_nickname 客服小王, got %q", grp.MyNickname)
	}
	memResp := app.request(t, http.MethodPost, fmt.Sprintf("/api/v1/groups/%d/members", grp.ID), alice, map[string]string{
		"group_nickname": "张总-采购",
		"wechat_name":    "阿良",
		"wechat_remark":  "张总",
		"wechat_account": "wx_zhang",
		"gender":         "男",
		"region":         "上海",
		"phone":          "13800138000",
		"local_key":      "gm_01",
	})
	if memResp.Code != http.StatusCreated {
		t.Fatalf("create group member: got %d %s", memResp.Code, memResp.Body.String())
	}
	var mem store.GroupMember
	if err := json.Unmarshal(memResp.Body.Bytes(), &mem); err != nil || mem.Region != "上海" || mem.Phone != "13800138000" {
		t.Fatalf("unexpected group member fields: %+v", mem)
	}
	memAvatarResp := app.request(t, http.MethodPost, fmt.Sprintf("/api/v1/groups/%d/members/%d/avatar", grp.ID, mem.ID), alice, map[string]string{
		"image_base64": tinyPngBase64,
	})
	if memAvatarResp.Code != http.StatusOK {
		t.Fatalf("upload group member avatar: got %d %s", memAvatarResp.Code, memAvatarResp.Body.String())
	}
	if err := json.Unmarshal(memAvatarResp.Body.Bytes(), &mem); err != nil || mem.AvatarURL == "" {
		t.Fatalf("expected non-empty member avatar_url: %s", memAvatarResp.Body.String())
	}
	if getOwnMemAv := app.request(t, http.MethodGet, mem.AvatarURL, alice, nil); getOwnMemAv.Code != http.StatusOK {
		t.Fatalf("alice get member avatar expected 200, got %d", getOwnMemAv.Code)
	}
	if getCrossMemAv := app.request(t, http.MethodGet, mem.AvatarURL, bob, nil); getCrossMemAv.Code != http.StatusNotFound {
		t.Fatalf("bob cross-owner member avatar expected 404, got %d", getCrossMemAv.Code)
	}
	if crossMem := app.request(t, http.MethodGet, fmt.Sprintf("/api/v1/groups/%d/members", grp.ID), bob, nil); crossMem.Code != http.StatusNotFound {
		t.Fatalf("cross-owner list group members expected 404, got %d", crossMem.Code)
	}

	// 5. Audit logs (auto-recorded + client-posted)
	if postAudit := app.request(t, http.MethodPost, "/api/v1/audit-logs", alice, map[string]string{
		"action": "qr_cleanup", "target_type": "login_qr", "detail": "deleted temp qr file",
	}); postAudit.Code != http.StatusCreated {
		t.Fatalf("create audit log: got %d %s", postAudit.Code, postAudit.Body.String())
	}
	logsResp := app.request(t, http.MethodGet, "/api/v1/audit-logs", alice, nil)
	if logsResp.Code != http.StatusOK || !bytes.Contains(logsResp.Body.Bytes(), []byte("qr_cleanup")) || !bytes.Contains(logsResp.Body.Bytes(), []byte("upload_avatar")) {
		t.Fatalf("list audit logs: got %d %s", logsResp.Code, logsResp.Body.String())
	}

	// 6. Chat image upload, owner isolation, image-only message creation & PATCH update
	imgUpload := app.request(t, http.MethodPost, "/api/v1/chat-images", alice, map[string]string{
		"image_base64": "data:image/png;base64," + tinyPngBase64,
	})
	if imgUpload.Code != http.StatusCreated {
		t.Fatalf("upload chat image: got %d %s", imgUpload.Code, imgUpload.Body.String())
	}
	var uploaded struct {
		ImageURL string `json:"image_url"`
	}
	if err := json.Unmarshal(imgUpload.Body.Bytes(), &uploaded); err != nil || uploaded.ImageURL == "" {
		t.Fatalf("unexpected upload response: %s", imgUpload.Body.String())
	}
	if getOwnImg := app.request(t, http.MethodGet, uploaded.ImageURL, alice, nil); getOwnImg.Code != http.StatusOK {
		t.Fatalf("alice get own chat image expected 200, got %d", getOwnImg.Code)
	}
	if getCrossImg := app.request(t, http.MethodGet, uploaded.ImageURL, bob, nil); getCrossImg.Code != http.StatusNotFound {
		t.Fatalf("bob cross-owner chat image expected 404, got %d", getCrossImg.Code)
	}

	// Image-only customer message
	imgMsgResp := app.request(t, http.MethodPost, fmt.Sprintf("/api/v1/customers/%d/messages", cust.ID), alice, map[string]string{
		"direction": "outgoing",
		"content":   "",
		"image_url": uploaded.ImageURL,
	})
	if imgMsgResp.Code != http.StatusCreated {
		t.Fatalf("create image-only customer message: got %d %s", imgMsgResp.Code, imgMsgResp.Body.String())
	}
	var imgMsg store.Message
	if err := json.Unmarshal(imgMsgResp.Body.Bytes(), &imgMsg); err != nil || imgMsg.ImageURL != uploaded.ImageURL {
		t.Fatalf("unexpected customer image message: %+v", imgMsg)
	}

	// PATCH customer message to add text caption
	patchMsgResp := app.request(t, http.MethodPatch, fmt.Sprintf("/api/v1/customers/%d/messages/%d", cust.ID, imgMsg.ID), alice, map[string]string{
		"direction": "incoming",
		"content":   "产品实拍图",
		"image_url": uploaded.ImageURL,
	})
	if patchMsgResp.Code != http.StatusOK {
		t.Fatalf("patch customer message: got %d %s", patchMsgResp.Code, patchMsgResp.Body.String())
	}

	// Image-only group message
	grpImgMsgResp := app.request(t, http.MethodPost, fmt.Sprintf("/api/v1/groups/%d/messages", grp.ID), alice, map[string]string{
		"sender_name": "张总",
		"direction":   "incoming",
		"content":     "",
		"image_url":   uploaded.ImageURL,
	})
	if grpImgMsgResp.Code != http.StatusCreated {
		t.Fatalf("create image-only group message: got %d %s", grpImgMsgResp.Code, grpImgMsgResp.Body.String())
	}
}

func TestChatSearchFTS5TrigramAndTriggers(t *testing.T) {
	verifier := testVerifier{
		password: "secret",
		identities: map[string]auth.Identity{
			"alice": {EmployeeID: "emp-fts", Account: "alice", DisplayName: "Alice"},
		},
	}
	app := newTestApp(t, verifier)
	defer app.db.Close()
	ctx := context.Background()

	cust, err := app.db.CreateCustomer(ctx, "emp-fts", store.CustomerInput{
		WechatName: "林女士",
		Tags:       "VIP",
	})
	if err != nil {
		t.Fatal(err)
	}
	msg1, err := app.db.CreateMessage(ctx, "emp-fts", cust.ID, store.MessageInput{
		Direction: "incoming",
		Content:   "请问超声炮全脸套餐下周三上午可以预约吗？",
		SentAt:    time.Now().UTC(),
	})
	if err != nil {
		t.Fatal(err)
	}

	grp, err := app.db.CreateGroup(ctx, "emp-fts", store.GroupInput{GroupName: "VIP客服群"})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := app.db.CreateGroupMessage(ctx, "emp-fts", grp.ID, store.GroupMessageInput{
		SenderName: "陈经理",
		Direction:  "incoming",
		Content:    "热玛吉五代和超声炮联合方案已经发群文件了",
		SentAt:     time.Now().UTC(),
	}); err != nil {
		t.Fatal(err)
	}

	// 3+ rune Chinese query exercises SQLite FTS5 trigram index across both customer & group messages
	hits, err := app.db.SearchChatMessages(ctx, "emp-fts", "超声炮", "all", "", 0, 0, "", "", 20)
	if err != nil {
		t.Fatalf("fts5 search failed: %v", err)
	}
	if len(hits) != 2 {
		t.Fatalf("expected 2 hits for 超声炮, got %d: %+v", len(hits), hits)
	}

	// 2-rune short query exercises LIKE fallback seamlessly
	shortHits, err := app.db.SearchChatMessages(ctx, "emp-fts", "预约", "customer", "", 0, 0, "", "", 20)
	if err != nil || len(shortHits) != 1 {
		t.Fatalf("short query expected 1 hit, got %d (err=%v)", len(shortHits), err)
	}

	// UPDATE trigger keeps FTS5 index synchronized
	if _, err := app.db.UpdateMessage(ctx, "emp-fts", cust.ID, msg1.ID, "incoming", "改约光子嫩肤项目了", ""); err != nil {
		t.Fatal(err)
	}
	afterUpdateOld, err := app.db.SearchChatMessages(ctx, "emp-fts", "超声炮", "customer", "", 0, 0, "", "", 20)
	if err != nil || len(afterUpdateOld) != 0 {
		t.Fatalf("expected 0 customer hits after update, got %d (err=%v)", len(afterUpdateOld), err)
	}
	afterUpdateNew, err := app.db.SearchChatMessages(ctx, "emp-fts", "光子嫩肤", "customer", "", 0, 0, "", "", 20)
	if err != nil || len(afterUpdateNew) != 1 {
		t.Fatalf("expected 1 customer hit for 光子嫩肤 after update, got %d (err=%v)", len(afterUpdateNew), err)
	}
}
