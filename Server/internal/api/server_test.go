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
