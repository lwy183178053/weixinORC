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

	// 4. Group & Group Members
	grpResp := app.request(t, http.MethodPost, "/api/v1/groups", alice, map[string]any{"group_name": "VIP客户群", "member_count": 12})
	if grpResp.Code != http.StatusCreated {
		t.Fatalf("create group: got %d %s", grpResp.Code, grpResp.Body.String())
	}
	var grp store.Group
	if err := json.Unmarshal(grpResp.Body.Bytes(), &grp); err != nil {
		t.Fatal(err)
	}
	memResp := app.request(t, http.MethodPost, fmt.Sprintf("/api/v1/groups/%d/members", grp.ID), alice, map[string]string{
		"group_nickname": "张总-采购",
		"wechat_name":    "阿良",
		"local_key":      "gm_01",
	})
	if memResp.Code != http.StatusCreated {
		t.Fatalf("create group member: got %d %s", memResp.Code, memResp.Body.String())
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
}
