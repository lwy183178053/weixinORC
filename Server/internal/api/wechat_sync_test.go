package api

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"testing"

	"weixinorc/server/internal/auth"
)

func TestWeChatSyncIdempotentUpsertAndDeduplication(t *testing.T) {
	app := newTestApp(t, testVerifier{
		password: "secret",
		identities: map[string]auth.Identity{
			"emp1": {EmployeeID: "emp-1", Account: "emp1", DisplayName: "员工一"},
		},
	})
	defer app.db.Close()
	tok := app.login(t, "emp1")

	// 1. Sync customers first time
	w := app.request(t, http.MethodPost, "/api/v1/wechat-sync/customers", tok, map[string]any{
		"items": []map[string]any{
			{
				"native_id":      "wxid_alice001",
				"wechat_name":    "Alice",
				"wechat_remark":  "王总",
				"wechat_account": "alice_wx",
				"tags":           "VIP客户",
				"region":         "广东 深圳",
			},
		},
	})
	if w.Code != http.StatusOK {
		t.Fatalf("sync customers 1st status = %d body=%s", w.Code, w.Body.String())
	}
	var custResp struct {
		Created int `json:"created"`
		Updated int `json:"updated"`
		Items   []struct {
			NativeID string `json:"native_id"`
			ID       int64  `json:"id"`
			Created  bool   `json:"created"`
		} `json:"items"`
	}
	if err := json.Unmarshal(w.Body.Bytes(), &custResp); err != nil {
		t.Fatal(err)
	}
	if custResp.Created != 1 || custResp.Updated != 0 || len(custResp.Items) != 1 {
		t.Fatalf("unexpected 1st customer sync resp: %+v", custResp)
	}
	customerID := custResp.Items[0].ID

	// Employee adds manual note
	w = app.request(t, http.MethodPatch, fmt.Sprintf("/api/v1/customers/%d", customerID), tok, map[string]any{
		"note": "重要意向客户，周五回访",
	})
	if w.Code != http.StatusOK {
		t.Fatalf("patch customer note status = %d body=%s", w.Code, w.Body.String())
	}

	// 2. Sync same customer again with updated nickname — should update without overwriting employee note
	w = app.request(t, http.MethodPost, "/api/v1/wechat-sync/customers", tok, map[string]any{
		"items": []map[string]any{
			{
				"native_id":      "wxid_alice001",
				"wechat_name":    "Alice New",
				"wechat_remark":  "王总(重点)",
				"wechat_account": "alice_wx",
				"tags":           "VIP客户, 已签约",
			},
		},
	})
	if w.Code != http.StatusOK {
		t.Fatalf("sync customers 2nd status = %d body=%s", w.Code, w.Body.String())
	}
	if err := json.Unmarshal(w.Body.Bytes(), &custResp); err != nil {
		t.Fatal(err)
	}
	if custResp.Created != 0 || custResp.Updated != 1 || custResp.Items[0].ID != customerID {
		t.Fatalf("unexpected 2nd customer sync resp: %+v", custResp)
	}

	w = app.request(t, http.MethodGet, fmt.Sprintf("/api/v1/customers/%d", customerID), tok, nil)
	var fetched struct {
		WechatName   string `json:"wechat_name"`
		WechatRemark string `json:"wechat_remark"`
		Note         string `json:"note"`
		Tags         string `json:"tags"`
	}
	if err := json.Unmarshal(w.Body.Bytes(), &fetched); err != nil {
		t.Fatal(err)
	}
	if fetched.WechatName != "Alice New" || fetched.Note != "重要意向客户，周五回访" || fetched.Tags != "VIP客户, 已签约" {
		t.Fatalf("expected updated name/tags and preserved note, got %+v", fetched)
	}

	// 3. Sync customer messages twice — verify external_id deduplication
	msgPayload := map[string]any{
		"items": []map[string]any{
			{
				"external_id": "wx4:srv:10001",
				"direction":   "incoming",
				"content":     "你好，咨询一下套餐",
				"sent_at":     "2026-11-25T10:00:00Z",
			},
			{
				"external_id": "wx4:srv:10002",
				"direction":   "outgoing",
				"content":     "好的，马上发您资料",
				"sent_at":     "2026-11-25T10:01:00Z",
			},
		},
	}
	w = app.request(t, http.MethodPost, fmt.Sprintf("/api/v1/wechat-sync/customers/%d/messages", customerID), tok, msgPayload)
	if w.Code != http.StatusOK {
		t.Fatalf("sync messages 1st status = %d body=%s", w.Code, w.Body.String())
	}
	var msgSummary struct {
		Inserted int `json:"inserted"`
		Skipped  int `json:"skipped"`
	}
	if err := json.Unmarshal(w.Body.Bytes(), &msgSummary); err != nil {
		t.Fatal(err)
	}
	if msgSummary.Inserted != 2 || msgSummary.Skipped != 0 {
		t.Fatalf("unexpected 1st msg sync summary: %+v", msgSummary)
	}

	w = app.request(t, http.MethodPost, fmt.Sprintf("/api/v1/wechat-sync/customers/%d/messages", customerID), tok, msgPayload)
	if w.Code != http.StatusOK {
		t.Fatalf("sync messages 2nd status = %d body=%s", w.Code, w.Body.String())
	}
	if err := json.Unmarshal(w.Body.Bytes(), &msgSummary); err != nil {
		t.Fatal(err)
	}
	if msgSummary.Inserted != 0 || msgSummary.Skipped != 2 {
		t.Fatalf("expected full deduplication on 2nd sync, got %+v", msgSummary)
	}

	// 3b. Verify existing-ids lookup and chronological ordering when backfilling older history
	w = app.request(t, http.MethodPost, "/api/v1/wechat-sync/existing-ids", tok, map[string]any{
		"scope":        "customer",
		"target_id":    customerID,
		"external_ids": []string{"wx4:srv:10001", "wx4:srv:99999"},
	})
	if w.Code != http.StatusOK {
		t.Fatalf("existing-ids status = %d body=%s", w.Code, w.Body.String())
	}
	var existingResp struct {
		ExistingIDs []string `json:"existing_ids"`
	}
	if err := json.Unmarshal(w.Body.Bytes(), &existingResp); err != nil {
		t.Fatal(err)
	}
	if len(existingResp.ExistingIDs) != 1 || existingResp.ExistingIDs[0] != "wx4:srv:10001" {
		t.Fatalf("unexpected existing_ids: %+v", existingResp.ExistingIDs)
	}

	// Backfill an older message; ListMessages must still return the newest sent_at first!
	w = app.request(t, http.MethodPost, fmt.Sprintf("/api/v1/wechat-sync/customers/%d/messages", customerID), tok, map[string]any{
		"items": []map[string]any{
			{
				"external_id": "wx4:srv:09000",
				"direction":   "incoming",
				"content":     "更早之前的咨询记录",
				"sent_at":     "2026-10-01T08:00:00Z",
			},
		},
	})
	if w.Code != http.StatusOK {
		t.Fatalf("backfill older message status = %d", w.Code)
	}
	w = app.request(t, http.MethodGet, fmt.Sprintf("/api/v1/customers/%d/messages?limit=10", customerID), tok, nil)
	var listMsgs struct {
		Items []struct {
			Content string `json:"content"`
		} `json:"items"`
	}
	if err := json.Unmarshal(w.Body.Bytes(), &listMsgs); err != nil {
		t.Fatal(err)
	}
	if len(listMsgs.Items) != 3 || listMsgs.Items[0].Content != "好的，马上发您资料" || listMsgs.Items[2].Content != "更早之前的咨询记录" {
		t.Fatalf("expected messages sorted by sent_at DESC, got %+v", listMsgs.Items)
	}

	// 4. Sync groups, members, and group messages with deduplication
	w = app.request(t, http.MethodPost, "/api/v1/wechat-sync/groups", tok, map[string]any{
		"items": []map[string]any{
			{
				"group_key":    "123456@chatroom",
				"group_name":   "核心客户交流群",
				"my_nickname":  "顾问小陈",
				"member_count": 2,
				"members_text": "王总、顾问小陈",
				"members": []map[string]any{
					{"native_id": "wxid_alice001", "wechat_name": "Alice New", "wechat_remark": "王总"},
					{"native_id": "wxid_self001", "wechat_name": "小陈", "group_nickname": "顾问小陈"},
				},
			},
		},
	})
	if w.Code != http.StatusOK {
		t.Fatalf("sync groups status = %d body=%s", w.Code, w.Body.String())
	}
	var grpResp struct {
		Created int `json:"created"`
		Items   []struct {
			ID      int64 `json:"id"`
			Members []struct {
				ID int64 `json:"id"`
			} `json:"members"`
		} `json:"items"`
	}
	if err := json.Unmarshal(w.Body.Bytes(), &grpResp); err != nil {
		t.Fatal(err)
	}
	if grpResp.Created != 1 || len(grpResp.Items) != 1 || len(grpResp.Items[0].Members) != 2 {
		t.Fatalf("unexpected group sync resp: %+v", grpResp)
	}
	groupID := grpResp.Items[0].ID

	grpMsgPayload := map[string]any{
		"items": []map[string]any{
			{
				"external_id": "wx4:srv:90001",
				"sender_name": "王总",
				"direction":   "incoming",
				"content":     "群里发一下报价单",
				"sent_at":     "2026-11-25T11:00:00Z",
			},
		},
	}
	w = app.request(t, http.MethodPost, fmt.Sprintf("/api/v1/wechat-sync/groups/%d/messages", groupID), tok, grpMsgPayload)
	if w.Code != http.StatusOK {
		t.Fatalf("sync group msgs 1st status = %d body=%s", w.Code, w.Body.String())
	}
	if err := json.Unmarshal(w.Body.Bytes(), &msgSummary); err != nil {
		t.Fatal(err)
	}
	if msgSummary.Inserted != 1 || msgSummary.Skipped != 0 {
		t.Fatalf("unexpected 1st group msg sync: %+v", msgSummary)
	}
	w = app.request(t, http.MethodPost, fmt.Sprintf("/api/v1/wechat-sync/groups/%d/messages", groupID), tok, grpMsgPayload)
	if err := json.Unmarshal(w.Body.Bytes(), &msgSummary); err != nil {
		t.Fatal(err)
	}
	if msgSummary.Inserted != 0 || msgSummary.Skipped != 1 {
		t.Fatalf("expected group msg deduplication, got %+v", msgSummary)
	}

	// 5. Upload group avatar and verify persistence + sync return
	// 1x1 valid PNG
	const tinyPngB64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="
	w = app.request(t, http.MethodPost, fmt.Sprintf("/api/v1/groups/%d/avatar", groupID), tok, map[string]any{
		"image_base64": tinyPngB64,
	})
	if w.Code != http.StatusOK {
		t.Fatalf("upload group avatar status = %d body=%s", w.Code, w.Body.String())
	}
	var grpWithAvatar struct {
		ID        int64  `json:"id"`
		AvatarURL string `json:"avatar_url"`
	}
	if err := json.Unmarshal(w.Body.Bytes(), &grpWithAvatar); err != nil {
		t.Fatal(err)
	}
	if grpWithAvatar.AvatarURL == "" {
		t.Fatalf("expected non-empty group avatar_url after upload")
	}
	w = app.request(t, http.MethodGet, grpWithAvatar.AvatarURL, tok, nil)
	if w.Code != http.StatusOK {
		t.Fatalf("fetch group avatar status = %d", w.Code)
	}

	// 6. Upload WAV voice audio + sync voice message & backfill missing audio_url on existing message
	// Minimal 44-byte valid RIFF/WAVE header + 4 bytes silence
	const tinyWavB64 = "UklGRigAAABXQVZFZm10IBAAAAABAAEAwF0AAIC7AAACABAAZGF0YQQAAAAAAAAA"
	w = app.request(t, http.MethodPost, "/api/v1/chat-audio", tok, map[string]any{
		"audio_base64": tinyWavB64,
	})
	if w.Code != http.StatusCreated {
		t.Fatalf("upload chat audio status = %d body=%s", w.Code, w.Body.String())
	}
	var audioResp struct {
		AudioURL string `json:"audio_url"`
	}
	if err := json.Unmarshal(w.Body.Bytes(), &audioResp); err != nil {
		t.Fatal(err)
	}
	if audioResp.AudioURL == "" {
		t.Fatalf("expected non-empty audio_url")
	}
	w = app.request(t, http.MethodGet, audioResp.AudioURL, tok, nil)
	if w.Code != http.StatusOK {
		t.Fatalf("fetch chat audio status = %d", w.Code)
	}

	// First sync voice without audio_url, then re-sync with audio_url and verify backfill
	w = app.request(t, http.MethodPost, fmt.Sprintf("/api/v1/wechat-sync/customers/%d/messages", customerID), tok, map[string]any{
		"items": []map[string]any{
			{
				"external_id": "wx4:voice:1",
				"direction":   "incoming",
				"content":     "🎤 [语音 5\"]",
				"sent_at":     "2026-11-26T10:00:00Z",
			},
		},
	})
	if w.Code != http.StatusOK {
		t.Fatalf("sync voice 1st status = %d", w.Code)
	}
	w = app.request(t, http.MethodPost, fmt.Sprintf("/api/v1/wechat-sync/customers/%d/messages", customerID), tok, map[string]any{
		"items": []map[string]any{
			{
				"external_id": "wx4:voice:1",
				"direction":   "incoming",
				"content":     "🎤 [语音 5\"]",
				"audio_url":   audioResp.AudioURL,
				"sent_at":     "2026-11-26T10:00:00Z",
			},
		},
	})
	if w.Code != http.StatusOK {
		t.Fatalf("sync voice backfill status = %d", w.Code)
	}
	w = app.request(t, http.MethodGet, fmt.Sprintf("/api/v1/customers/%d/messages?limit=1", customerID), tok, nil)
	var latestMsg struct {
		Items []struct {
			AudioURL string `json:"audio_url"`
		} `json:"items"`
	}
	if err := json.Unmarshal(w.Body.Bytes(), &latestMsg); err != nil {
		t.Fatal(err)
	}
	if len(latestMsg.Items) != 1 || latestMsg.Items[0].AudioURL != audioResp.AudioURL {
		t.Fatalf("expected backfilled audio_url=%s, got %+v", audioResp.AudioURL, latestMsg.Items)
	}

	// 7. Test legacy malformed Konata WAV repair ("RIFF(\x00\x00\x00WAVEfmt\x00...")
	brokenKonataWav := make([]byte, 42+480)
	copy(brokenKonataWav[0:4], "RIFF")
	brokenKonataWav[4] = 0x28
	copy(brokenKonataWav[8:12], "WAVE")
	copy(brokenKonataWav[12:16], "fmt\x00")
	brokenKonataWav[16] = 0x10
	brokenKonataWav[20] = 1
	brokenKonataWav[22] = 1
	brokenKonataWav[24] = 0xc0
	brokenKonataWav[25] = 0x5d // 24000 Hz
	copy(brokenKonataWav[34:38], "data")
	repaired := repairLegacyMalformedWav(brokenKonataWav)
	if len(repaired) != 44+480 || string(repaired[12:16]) != "fmt " || string(repaired[36:40]) != "data" {
		t.Fatalf("unexpected repaired wav len=%d hdr=%q", len(repaired), repaired[:44])
	}

	// 8. Test raw binary video upload (/api/v1/chat-video) and video_url backfill
	tinyMp4 := []byte{
		0x00, 0x00, 0x00, 0x18, 'f', 't', 'y', 'p',
		'i', 's', 'o', 'm', 0x00, 0x00, 0x02, 0x00,
		'i', 's', 'o', 'm', 'm', 'p', '4', '1',
	}
	vidReq := httptest.NewRequest(http.MethodPost, "/api/v1/chat-video", bytes.NewReader(tinyMp4))
	vidReq.Header.Set("Authorization", "Bearer "+tok)
	vidReq.Header.Set("Content-Type", "application/octet-stream")
	vidW := httptest.NewRecorder()
	app.handler.ServeHTTP(vidW, vidReq)
	if vidW.Code != http.StatusCreated {
		t.Fatalf("upload chat video status = %d body=%s", vidW.Code, vidW.Body.String())
	}
	var vidResp struct {
		VideoURL string `json:"video_url"`
	}
	if err := json.Unmarshal(vidW.Body.Bytes(), &vidResp); err != nil || vidResp.VideoURL == "" {
		t.Fatalf("unexpected video upload resp: %s", vidW.Body.String())
	}
	w = app.request(t, http.MethodGet, vidResp.VideoURL, tok, nil)
	if w.Code != http.StatusOK || len(w.Body.Bytes()) != len(tinyMp4) {
		t.Fatalf("fetch chat video status = %d len=%d", w.Code, len(w.Body.Bytes()))
	}

	// First sync video placeholder, then backfill video_url
	w = app.request(t, http.MethodPost, fmt.Sprintf("/api/v1/wechat-sync/customers/%d/messages", customerID), tok, map[string]any{
		"items": []map[string]any{
			{
				"external_id": "wx4:video:1",
				"direction":   "incoming",
				"content":     "🎬 [视频 93\"]",
				"sent_at":     "2026-11-26T10:05:00Z",
			},
		},
	})
	if w.Code != http.StatusOK {
		t.Fatalf("sync video 1st status = %d", w.Code)
	}
	w = app.request(t, http.MethodPost, fmt.Sprintf("/api/v1/wechat-sync/customers/%d/messages", customerID), tok, map[string]any{
		"items": []map[string]any{
			{
				"external_id": "wx4:video:1",
				"direction":   "incoming",
				"content":     "🎬 [视频 93\"]",
				"video_url":   vidResp.VideoURL,
				"sent_at":     "2026-11-26T10:05:00Z",
			},
		},
	})
	if w.Code != http.StatusOK {
		t.Fatalf("sync video backfill status = %d", w.Code)
	}
	w = app.request(t, http.MethodGet, fmt.Sprintf("/api/v1/customers/%d/messages?limit=1", customerID), tok, nil)
	var latestVidMsg struct {
		Items []struct {
			VideoURL string `json:"video_url"`
		} `json:"items"`
	}
	if err := json.Unmarshal(w.Body.Bytes(), &latestVidMsg); err != nil {
		t.Fatal(err)
	}
	if len(latestVidMsg.Items) != 1 || latestVidMsg.Items[0].VideoURL != vidResp.VideoURL {
		t.Fatalf("expected backfilled video_url=%s, got %+v", vidResp.VideoURL, latestVidMsg.Items)
	}
}

func TestMultiWeChatAccountsPerEmployeeIsolation(t *testing.T) {
	app := newTestApp(t, testVerifier{
		password: "secret",
		identities: map[string]auth.Identity{
			"alice": {EmployeeID: "emp-alice", Account: "alice", DisplayName: "Alice"},
		},
	})
	defer app.db.Close()
	tok := app.login(t, "alice")

	// 1. Bind two personal WeChat accounts under the same employee without overwriting each other
	w := app.request(t, http.MethodPost, "/api/v1/wechat-accounts", tok, map[string]any{
		"account_type": "personal",
		"wechat_id":    "wxid_work_a",
		"alias":        "work_a_alias",
		"nickname":     "工作主号A",
		"region":       "上海",
		"signature":    "主号签名",
		"status":       "online",
	})
	if w.Code != http.StatusOK {
		t.Fatalf("bind A status=%d body=%s", w.Code, w.Body.String())
	}
	var accA struct {
		ID int64 `json:"id"`
	}
	_ = json.Unmarshal(w.Body.Bytes(), &accA)

	// Upload avatar for WeChat A
	pngBytes := []byte{0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D}
	w = app.request(t, http.MethodPost, fmt.Sprintf("/api/v1/wechat-accounts/%d/avatar", accA.ID), tok, map[string]any{
		"image_base64": base64.StdEncoding.EncodeToString(pngBytes),
	})
	if w.Code != http.StatusOK {
		t.Fatalf("upload account A avatar status=%d body=%s", w.Code, w.Body.String())
	}

	// Switch to WeChat B (online -> should auto-mark A offline while preserving A's row)
	w = app.request(t, http.MethodPost, "/api/v1/wechat-accounts", tok, map[string]any{
		"account_type": "personal",
		"wechat_id":    "wxid_work_b",
		"alias":        "work_b_alias",
		"nickname":     "备用小号B",
		"status":       "online",
	})
	if w.Code != http.StatusOK {
		t.Fatalf("bind B status=%d body=%s", w.Code, w.Body.String())
	}

	// 2. Sync a legacy customer with no owner_wxid, then sync under WeChat A (should adopt), then sync SAME native_id under WeChat B (should create separate customer!)
	w = app.request(t, http.MethodPost, "/api/v1/wechat-sync/customers", tok, map[string]any{
		"items": []map[string]any{
			{"native_id": "wxid_shared_client", "wechat_name": "张老板", "wechat_account": "boss_zhang"},
		},
	})
	if w.Code != http.StatusOK {
		t.Fatalf("sync legacy status=%d body=%s", w.Code, w.Body.String())
	}
	var syncLegacy struct {
		Items []struct {
			ID int64 `json:"id"`
		} `json:"items"`
	}
	_ = json.Unmarshal(w.Body.Bytes(), &syncLegacy)
	legacyID := syncLegacy.Items[0].ID

	// WeChat A adopts the legacy row
	w = app.request(t, http.MethodPost, "/api/v1/wechat-sync/customers", tok, map[string]any{
		"owner_wxid": "wxid_work_a",
		"items": []map[string]any{
			{"native_id": "wxid_shared_client", "wechat_name": "张老板", "wechat_remark": "张总(主号)", "wechat_account": "boss_zhang"},
		},
	})
	if w.Code != http.StatusOK {
		t.Fatalf("sync A status=%d body=%s", w.Code, w.Body.String())
	}
	var syncA struct {
		Items []struct {
			ID      int64 `json:"id"`
			Created bool  `json:"created"`
		} `json:"items"`
	}
	_ = json.Unmarshal(w.Body.Bytes(), &syncA)
	if syncA.Items[0].ID != legacyID || syncA.Items[0].Created {
		t.Fatalf("expected WeChat A to adopt legacy customer #%d, got %+v", legacyID, syncA.Items[0])
	}

	// WeChat B syncs the exact same friend -> must create a NEW isolated customer record!
	w = app.request(t, http.MethodPost, "/api/v1/wechat-sync/customers", tok, map[string]any{
		"owner_wxid": "wxid_work_b",
		"items": []map[string]any{
			{"native_id": "wxid_shared_client", "wechat_name": "张老板", "wechat_remark": "张总(小号)", "wechat_account": "boss_zhang"},
		},
	})
	if w.Code != http.StatusOK {
		t.Fatalf("sync B status=%d body=%s", w.Code, w.Body.String())
	}
	var syncB struct {
		Items []struct {
			ID      int64 `json:"id"`
			Created bool  `json:"created"`
		} `json:"items"`
	}
	_ = json.Unmarshal(w.Body.Bytes(), &syncB)
	if !syncB.Items[0].Created || syncB.Items[0].ID == legacyID {
		t.Fatalf("expected WeChat B to create separate customer record, got %+v", syncB.Items[0])
	}

	// Verify /wechat-accounts returns BOTH accounts with B online, A offline, avatar & customer_count=1 each
	w = app.request(t, http.MethodGet, "/api/v1/wechat-accounts", tok, nil)
	if w.Code != http.StatusOK {
		t.Fatalf("list accounts status=%d body=%s", w.Code, w.Body.String())
	}
	var accList struct {
		Items []struct {
			WechatID      string `json:"wechat_id"`
			Alias         string `json:"alias"`
			Nickname      string `json:"nickname"`
			AvatarURL     string `json:"avatar_url"`
			Status        string `json:"status"`
			CustomerCount int    `json:"customer_count"`
		} `json:"items"`
	}
	_ = json.Unmarshal(w.Body.Bytes(), &accList)
	if len(accList.Items) != 2 {
		t.Fatalf("expected 2 wechat accounts, got %d: %+v", len(accList.Items), accList.Items)
	}
	if accList.Items[0].WechatID != "wxid_work_b" || accList.Items[0].Status != "online" || accList.Items[0].CustomerCount != 1 {
		t.Fatalf("expected online B first with customer_count=1, got %+v", accList.Items[0])
	}
	if accList.Items[1].WechatID != "wxid_work_a" || accList.Items[1].Status != "offline" || accList.Items[1].AvatarURL == "" || accList.Items[1].CustomerCount != 1 {
		t.Fatalf("expected offline A second with avatar and customer_count=1, got %+v", accList.Items[1])
	}
}
