package api

import (
	"crypto/rand"
	"encoding/base64"
	"encoding/binary"
	"encoding/hex"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/gin-gonic/gin"
	"weixinorc/server/internal/auth"
	"weixinorc/server/internal/store"
)

const chatImageRoutePrefix = "/api/v1/chat-images/"
const chatAudioRoutePrefix = "/api/v1/chat-audio/"
const chatVideoRoutePrefix = "/api/v1/chat-video/"
const maxVideoBodyBytes = 80 << 20 // 80 MB raw binary video upload limit

type createMessageRequest struct {
	Direction string `json:"direction"`
	Content   string `json:"content"`
	ImageURL  string `json:"image_url"`
	SentAt    string `json:"sent_at"`
}

type updateMessageRequest struct {
	Direction string `json:"direction"`
	Content   string `json:"content"`
	ImageURL  string `json:"image_url"`
}

type uploadChatImageRequest struct {
	ImageBase64 string `json:"image_base64"`
}

type uploadChatAudioRequest struct {
	AudioBase64 string `json:"audio_base64"`
}

func validChatAudioFilename(ownerID, name string) bool {
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
	return ext == ".wav" || ext == ".mp3"
}

func validChatAudioURL(ownerID, audioURL string) bool {
	if audioURL == "" {
		return true
	}
	if !strings.HasPrefix(audioURL, chatAudioRoutePrefix) {
		return false
	}
	filename := strings.TrimPrefix(audioURL, chatAudioRoutePrefix)
	return validChatAudioFilename(ownerID, filename)
}

func detectAudioExt(raw []byte) (ext string, contentType string, ok bool) {
	if len(raw) >= 12 && string(raw[0:4]) == "RIFF" && string(raw[8:12]) == "WAVE" {
		return ".wav", "audio/wav", true
	}
	if len(raw) >= 3 && (string(raw[0:3]) == "ID3" || (raw[0] == 0xFF && (raw[1]&0xE0) == 0xE0)) {
		return ".mp3", "audio/mpeg", true
	}
	return "", "", false
}

func (s *server) uploadChatAudio(c *gin.Context) {
	var req uploadChatAudioRequest
	if !bindJSON(c, &req) {
		return
	}
	b64 := strings.TrimSpace(req.AudioBase64)
	if idx := strings.Index(b64, ","); idx >= 0 && strings.HasPrefix(b64[:idx], "data:") {
		b64 = b64[idx+1:]
	}
	if b64 == "" {
		c.JSON(http.StatusBadRequest, gin.H{"error": "empty_audio"})
		return
	}
	raw, err := base64.StdEncoding.DecodeString(b64)
	if err != nil || len(raw) == 0 || len(raw) > 8<<20 {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_chat_audio"})
		return
	}
	ext, _, validAudio := detectAudioExt(raw)
	if !validAudio {
		c.JSON(http.StatusBadRequest, gin.H{"error": "unsupported_audio_format"})
		return
	}

	ownerID := auth.IdentityFromContext(c).EmployeeID
	var nonce [6]byte
	if _, err := rand.Read(nonce[:]); err != nil {
		c.JSON(http.StatusInternalServerError, gin.H{"error": "audio_save_failed"})
		return
	}
	filename := fmt.Sprintf("%svoice_%d_%s%s", ownerAvatarPrefix(ownerID), time.Now().UnixNano(), hex.EncodeToString(nonce[:]), ext)
	fullPath := filepath.Join(s.store.ChatAudioDir(), filename)
	if err := os.WriteFile(fullPath, raw, 0o640); err != nil {
		c.JSON(http.StatusInternalServerError, gin.H{"error": "audio_save_failed"})
		return
	}
	c.JSON(http.StatusCreated, gin.H{"audio_url": chatAudioRoutePrefix + filename})
}

func (s *server) getChatAudio(c *gin.Context) {
	rawName := c.Param("filename")
	ownerID := auth.IdentityFromContext(c).EmployeeID
	if !validChatAudioFilename(ownerID, rawName) {
		c.JSON(http.StatusNotFound, gin.H{"error": "not_found"})
		return
	}
	fullPath := filepath.Join(s.store.ChatAudioDir(), rawName)
	raw, err := os.ReadFile(fullPath)
	if err != nil {
		c.JSON(http.StatusNotFound, gin.H{"error": "not_found"})
		return
	}
	_, contentType, ok := detectAudioExt(raw)
	if !ok {
		contentType = "application/octet-stream"
	}
	raw = repairLegacyMalformedWav(raw)
	c.Header("X-Content-Type-Options", "nosniff")
	c.Header("Cache-Control", "private, max-age=3600")
	c.Data(http.StatusOK, contentType, raw)
}

// repairLegacyMalformedWav repairs 42-byte broken Konata WAV headers ("RIFF(\x00\x00\x00WAVEfmt\x00...")
// into a standard 44-byte RIFF/WAVE 24kHz 16-bit mono PCM header on the fly so previously synced voices play cleanly.
func repairLegacyMalformedWav(raw []byte) []byte {
	if len(raw) <= 42 ||
		string(raw[0:4]) != "RIFF" ||
		string(raw[8:12]) != "WAVE" ||
		string(raw[12:16]) != "fmt\x00" {
		return raw
	}
	pcmOffset := 42
	if len(raw) >= 42 && string(raw[34:38]) == "data" {
		pcmOffset = 42
	} else {
		return raw
	}
	pcm := raw[pcmOffset:]
	if len(pcm) == 0 {
		return raw
	}
	sampleRate := binary.LittleEndian.Uint32(raw[24:28])
	if sampleRate < 8000 || sampleRate > 48000 {
		sampleRate = 24000
	}
	channels := binary.LittleEndian.Uint16(raw[22:24])
	if channels < 1 || channels > 2 {
		channels = 1
	}
	const bitsPerSample uint16 = 16
	blockAlign := channels * (bitsPerSample / 8)
	byteRate := sampleRate * uint32(blockAlign)

	out := make([]byte, 44+len(pcm))
	copy(out[0:4], "RIFF")
	binary.LittleEndian.PutUint32(out[4:8], uint32(36+len(pcm)))
	copy(out[8:12], "WAVE")
	copy(out[12:16], "fmt ")
	binary.LittleEndian.PutUint32(out[16:20], 16)
	binary.LittleEndian.PutUint16(out[20:22], 1)
	binary.LittleEndian.PutUint16(out[22:24], channels)
	binary.LittleEndian.PutUint32(out[24:28], sampleRate)
	binary.LittleEndian.PutUint32(out[28:32], byteRate)
	binary.LittleEndian.PutUint16(out[32:34], blockAlign)
	binary.LittleEndian.PutUint16(out[34:36], bitsPerSample)
	copy(out[36:40], "data")
	binary.LittleEndian.PutUint32(out[40:44], uint32(len(pcm)))
	copy(out[44:], pcm)
	return out
}

func validChatVideoFilename(ownerID, name string) bool {
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
	return ext == ".mp4" || ext == ".mov" || ext == ".webm"
}

func validChatVideoURL(ownerID, videoURL string) bool {
	if videoURL == "" {
		return true
	}
	if !strings.HasPrefix(videoURL, chatVideoRoutePrefix) {
		return false
	}
	filename := strings.TrimPrefix(videoURL, chatVideoRoutePrefix)
	return validChatVideoFilename(ownerID, filename)
}

func detectVideoExt(head []byte) (ext string, contentType string, ok bool) {
	if len(head) >= 12 && string(head[4:8]) == "ftyp" {
		if string(head[8:12]) == "qt  " {
			return ".mov", "video/quicktime", true
		}
		return ".mp4", "video/mp4", true
	}
	if len(head) >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3 {
		return ".webm", "video/webm", true
	}
	return "", "", false
}

func (s *server) uploadChatVideo(c *gin.Context) {
	c.Request.Body = http.MaxBytesReader(c.Writer, c.Request.Body, maxVideoBodyBytes)
	raw, err := io.ReadAll(c.Request.Body)
	if err != nil || len(raw) < 16 {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_chat_video"})
		return
	}
	ext, _, validVid := detectVideoExt(raw)
	if !validVid {
		c.JSON(http.StatusBadRequest, gin.H{"error": "unsupported_video_format"})
		return
	}

	ownerID := auth.IdentityFromContext(c).EmployeeID
	var nonce [6]byte
	if _, err := rand.Read(nonce[:]); err != nil {
		c.JSON(http.StatusInternalServerError, gin.H{"error": "video_save_failed"})
		return
	}
	filename := fmt.Sprintf("%svideo_%d_%s%s", ownerAvatarPrefix(ownerID), time.Now().UnixNano(), hex.EncodeToString(nonce[:]), ext)
	fullPath := filepath.Join(s.store.ChatVideoDir(), filename)
	if err := os.WriteFile(fullPath, raw, 0o640); err != nil {
		c.JSON(http.StatusInternalServerError, gin.H{"error": "video_save_failed"})
		return
	}
	c.JSON(http.StatusCreated, gin.H{"video_url": chatVideoRoutePrefix + filename})
}

func (s *server) getChatVideo(c *gin.Context) {
	rawName := c.Param("filename")
	ownerID := auth.IdentityFromContext(c).EmployeeID
	if !validChatVideoFilename(ownerID, rawName) {
		c.JSON(http.StatusNotFound, gin.H{"error": "not_found"})
		return
	}
	fullPath := filepath.Join(s.store.ChatVideoDir(), rawName)
	if _, err := os.Stat(fullPath); err != nil {
		c.JSON(http.StatusNotFound, gin.H{"error": "not_found"})
		return
	}
	c.Header("X-Content-Type-Options", "nosniff")
	c.Header("Cache-Control", "private, max-age=3600")
	http.ServeFile(c.Writer, c.Request, fullPath)
}

func validChatImageURL(ownerID, imageURL string) bool {
	if imageURL == "" {
		return true
	}
	if !strings.HasPrefix(imageURL, chatImageRoutePrefix) {
		return false
	}
	filename := strings.TrimPrefix(imageURL, chatImageRoutePrefix)
	return validAvatarFilename(ownerID, filename)
}

func (s *server) uploadChatImage(c *gin.Context) {
	var req uploadChatImageRequest
	if !bindJSON(c, &req) {
		return
	}

	b64 := strings.TrimSpace(req.ImageBase64)
	if idx := strings.Index(b64, ","); idx >= 0 && strings.HasPrefix(b64[:idx], "data:") {
		b64 = b64[idx+1:]
	}
	if b64 == "" {
		c.JSON(http.StatusBadRequest, gin.H{"error": "empty_image"})
		return
	}

	raw, err := base64.StdEncoding.DecodeString(b64)
	if err != nil || len(raw) == 0 || len(raw) > 8<<20 {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_chat_image"})
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
		c.JSON(http.StatusInternalServerError, gin.H{"error": "image_save_failed"})
		return
	}

	filename := fmt.Sprintf("%smsg_%d_%s%s", ownerAvatarPrefix(ownerID), time.Now().UnixNano(), hex.EncodeToString(nonce[:]), ext)
	fullPath := filepath.Join(s.store.ChatImageDir(), filename)
	if err := os.WriteFile(fullPath, raw, 0o640); err != nil {
		c.JSON(http.StatusInternalServerError, gin.H{"error": "image_save_failed"})
		return
	}

	c.JSON(http.StatusCreated, gin.H{"image_url": chatImageRoutePrefix + filename})
}

func (s *server) getChatImage(c *gin.Context) {
	rawName := c.Param("filename")
	ownerID := auth.IdentityFromContext(c).EmployeeID

	if !validAvatarFilename(ownerID, rawName) {
		c.JSON(http.StatusNotFound, gin.H{"error": "not_found"})
		return
	}

	fullPath := filepath.Join(s.store.ChatImageDir(), rawName)
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

func (s *server) listMessages(c *gin.Context) {
	customerID, ok := parsePathID(c, "id", "invalid_customer_id")
	if !ok {
		return
	}
	cursor, limit, ok := parsePagination(c)
	if !ok {
		return
	}

	ownerID := auth.IdentityFromContext(c).EmployeeID
	items, next, err := s.store.ListMessages(c.Request.Context(), ownerID, customerID, cursor, limit)
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusOK, gin.H{"items": items, "next_cursor": next})
}

func (s *server) createMessage(c *gin.Context) {
	customerID, ok := parsePathID(c, "id", "invalid_customer_id")
	if !ok {
		return
	}

	var req createMessageRequest
	if !bindJSON(c, &req) {
		return
	}
	if req.Direction != "incoming" && req.Direction != "outgoing" {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_direction"})
		return
	}
	ownerID := auth.IdentityFromContext(c).EmployeeID
	req.ImageURL = strings.TrimSpace(req.ImageURL)
	if !validChatImageURL(ownerID, req.ImageURL) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_image_url"})
		return
	}
	if (strings.TrimSpace(req.Content) == "" && req.ImageURL == "") || !validText(req.Content, 10000) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_content"})
		return
	}

	sentAt, ok := parseSentAt(c, req.SentAt)
	if !ok {
		return
	}

	msg, err := s.store.CreateMessage(c.Request.Context(), ownerID, customerID, store.MessageInput{
		Direction: req.Direction,
		Content:   req.Content,
		ImageURL:  req.ImageURL,
		SentAt:    sentAt,
	})
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusCreated, msg)
}

func (s *server) updateMessage(c *gin.Context) {
	customerID, ok := parsePathID(c, "id", "invalid_customer_id")
	if !ok {
		return
	}
	msgID, ok := parsePathID(c, "msgId", "invalid_message_id")
	if !ok {
		return
	}

	var req updateMessageRequest
	if !bindJSON(c, &req) {
		return
	}
	if req.Direction != "incoming" && req.Direction != "outgoing" {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_direction"})
		return
	}
	ownerID := auth.IdentityFromContext(c).EmployeeID
	req.ImageURL = strings.TrimSpace(req.ImageURL)
	if !validChatImageURL(ownerID, req.ImageURL) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_image_url"})
		return
	}
	if (strings.TrimSpace(req.Content) == "" && req.ImageURL == "") || !validText(req.Content, 10000) {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid_content"})
		return
	}

	msg, err := s.store.UpdateMessage(c.Request.Context(), ownerID, customerID, msgID, req.Direction, req.Content, req.ImageURL)
	if err != nil {
		writeStoreErr(c, err)
		return
	}
	c.JSON(http.StatusOK, msg)
}
