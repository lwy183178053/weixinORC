package auth

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/hex"
	"errors"
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
