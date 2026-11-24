package auth

import (
	"bytes"
	"context"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"strings"
	"sync"
	"time"

	"github.com/gin-gonic/gin"
)

var ErrInvalidCredentials = errors.New("invalid credentials")

// Identity represents an authenticated employee.
type Identity struct {
	EmployeeID  string `json:"employee_id"`
	Account     string `json:"account"`
	DisplayName string `json:"display_name"`
}

// Verifier validates employee credentials against an external auth provider.
type Verifier interface {
	Verify(ctx context.Context, account, password string) (Identity, error)
}

// MockVerifier is a single-account verifier for local development only.
type MockVerifier struct {
	account  string
	password string
	identity Identity
}

func NewMockVerifier(account, password string) *MockVerifier {
	return &MockVerifier{
		account:  account,
		password: password,
		identity: Identity{
			EmployeeID:  "mock-employee",
			Account:     account,
			DisplayName: "Mock Employee",
		},
	}
}

func (v *MockVerifier) Verify(_ context.Context, account, password string) (Identity, error) {
	okAccount := subtle.ConstantTimeCompare([]byte(account), []byte(v.account)) == 1
	okPassword := subtle.ConstantTimeCompare([]byte(password), []byte(v.password)) == 1
	if !okAccount || !okPassword {
		return Identity{}, ErrInvalidCredentials
	}
	return v.identity, nil
}

// HTTPVerifier delegates employee verification to an external HTTP endpoint (e.g. 智美员工Bot).
type HTTPVerifier struct {
	endpoint string
	apiToken string
	client   *http.Client
}

func NewHTTPVerifier(endpoint, apiToken string, timeout time.Duration) *HTTPVerifier {
	if timeout <= 0 {
		timeout = 10 * time.Second
	}
	return &HTTPVerifier{
		endpoint: strings.TrimSpace(endpoint),
		apiToken: strings.TrimSpace(apiToken),
		client:   &http.Client{Timeout: timeout},
	}
}

func (v *HTTPVerifier) Verify(ctx context.Context, account, password string) (Identity, error) {
	payload, err := json.Marshal(map[string]string{
		"account":  account,
		"password": password,
	})
	if err != nil {
		return Identity{}, err
	}

	req, err := http.NewRequestWithContext(ctx, http.MethodPost, v.endpoint, bytes.NewReader(payload))
	if err != nil {
		return Identity{}, err
	}
	req.Header.Set("Content-Type", "application/json")
	if v.apiToken != "" {
		req.Header.Set("Authorization", "Bearer "+v.apiToken)
	}

	resp, err := v.client.Do(req)
	if err != nil {
		return Identity{}, err
	}
	defer resp.Body.Close()

	if resp.StatusCode == http.StatusUnauthorized || resp.StatusCode == http.StatusForbidden {
		return Identity{}, ErrInvalidCredentials
	}
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return Identity{}, fmt.Errorf("external auth returned status %d", resp.StatusCode)
	}

	var raw struct {
		EmployeeID  string    `json:"employee_id"`
		Account     string    `json:"account"`
		DisplayName string    `json:"display_name"`
		Employee    *Identity `json:"employee"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&raw); err != nil {
		return Identity{}, err
	}

	id := Identity{
		EmployeeID:  strings.TrimSpace(raw.EmployeeID),
		Account:     strings.TrimSpace(raw.Account),
		DisplayName: strings.TrimSpace(raw.DisplayName),
	}
	if raw.Employee != nil && strings.TrimSpace(raw.Employee.EmployeeID) != "" {
		id = Identity{
			EmployeeID:  strings.TrimSpace(raw.Employee.EmployeeID),
			Account:     strings.TrimSpace(raw.Employee.Account),
			DisplayName: strings.TrimSpace(raw.Employee.DisplayName),
		}
	}
	if id.EmployeeID == "" {
		return Identity{}, ErrInvalidCredentials
	}
	if id.Account == "" {
		id.Account = account
	}
	if id.DisplayName == "" {
		id.DisplayName = id.Account
	}
	return id, nil
}

type sessionEntry struct {
	identity Identity
	expires  time.Time
}

// Sessions manages in-memory Bearer tokens keyed by SHA-256 hash.
type Sessions struct {
	mu      sync.Mutex
	ttl     time.Duration
	entries map[[32]byte]sessionEntry
	now     func() time.Time
}

func NewSessions(ttl time.Duration) *Sessions {
	return &Sessions{
		ttl:     ttl,
		entries: make(map[[32]byte]sessionEntry),
		now:     time.Now,
	}
}

func (s *Sessions) Create(identity Identity) (string, time.Time, error) {
	var raw [32]byte
	if _, err := rand.Read(raw[:]); err != nil {
		return "", time.Time{}, err
	}

	token := hex.EncodeToString(raw[:])
	expires := s.now().Add(s.ttl)

	s.mu.Lock()
	defer s.mu.Unlock()

	now := s.now()
	for key, entry := range s.entries {
		if !now.Before(entry.expires) {
			delete(s.entries, key)
		}
	}
	s.entries[sha256.Sum256([]byte(token))] = sessionEntry{
		identity: identity,
		expires:  expires,
	}
	return token, expires, nil
}

func (s *Sessions) Lookup(token string) (Identity, bool) {
	if token == "" {
		return Identity{}, false
	}

	key := sha256.Sum256([]byte(token))
	s.mu.Lock()
	defer s.mu.Unlock()

	entry, ok := s.entries[key]
	if !ok {
		return Identity{}, false
	}
	if !s.now().Before(entry.expires) {
		delete(s.entries, key)
		return Identity{}, false
	}
	return entry.identity, true
}

func (s *Sessions) Revoke(token string) {
	s.mu.Lock()
	delete(s.entries, sha256.Sum256([]byte(token)))
	s.mu.Unlock()
}

const (
	ctxIdentityKey = "auth_identity"
	ctxTokenKey    = "auth_token"
)

func (s *Sessions) Middleware() gin.HandlerFunc {
	return func(c *gin.Context) {
		token, ok := strings.CutPrefix(c.GetHeader("Authorization"), "Bearer ")
		token = strings.TrimSpace(token)
		if !ok || token == "" {
			c.AbortWithStatusJSON(http.StatusUnauthorized, gin.H{"error": "unauthorized"})
			return
		}

		identity, valid := s.Lookup(token)
		if !valid {
			c.AbortWithStatusJSON(http.StatusUnauthorized, gin.H{"error": "unauthorized"})
			return
		}

		c.Set(ctxIdentityKey, identity)
		c.Set(ctxTokenKey, token)
		c.Next()
	}
}

func IdentityFromContext(c *gin.Context) Identity {
	v, _ := c.Get(ctxIdentityKey)
	id, _ := v.(Identity)
	return id
}

func TokenFromContext(c *gin.Context) string {
	v, _ := c.Get(ctxTokenKey)
	token, _ := v.(string)
	return token
}
