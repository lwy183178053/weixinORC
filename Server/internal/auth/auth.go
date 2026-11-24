package auth

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/hex"
	"errors"
	"strings"
	"sync"
	"time"

	"github.com/gin-gonic/gin"
)

var (
	ErrInvalidCredentials = errors.New("invalid credentials")
	ErrUnavailable        = errors.New("authentication provider unavailable")
)

type Identity struct {
	EmployeeID  string `json:"employee_id"`
	Account     string `json:"account"`
	DisplayName string `json:"display_name"`
}

type Verifier interface {
	Verify(ctx context.Context, account, password string) (Identity, error)
}

type UnavailableVerifier struct{}

func (UnavailableVerifier) Verify(context.Context, string, string) (Identity, error) {
	return Identity{}, ErrUnavailable
}

type MockVerifier struct {
	account  string
	password string
	identity Identity
}

func NewMockVerifier(account, password string) *MockVerifier {
	return &MockVerifier{
		account:  account,
		password: password,
		identity: Identity{EmployeeID: "mock-employee", Account: account, DisplayName: "Mock Employee"},
	}
}

func (v *MockVerifier) Verify(_ context.Context, account, password string) (Identity, error) {
	accountOK := subtle.ConstantTimeCompare([]byte(account), []byte(v.account))
	passwordOK := subtle.ConstantTimeCompare([]byte(password), []byte(v.password))
	if accountOK != 1 || passwordOK != 1 {
		return Identity{}, ErrInvalidCredentials
	}
	return v.identity, nil
}

type session struct {
	identity Identity
	expires  time.Time
}

type Sessions struct {
	mu       sync.Mutex
	ttl      time.Duration
	sessions map[[32]byte]session
	now      func() time.Time
}

func NewSessions(ttl time.Duration) *Sessions {
	return &Sessions{ttl: ttl, sessions: make(map[[32]byte]session), now: time.Now}
}

func (s *Sessions) Create(identity Identity) (string, time.Time, error) {
	var raw [32]byte
	if _, err := rand.Read(raw[:]); err != nil {
		return "", time.Time{}, err
	}
	token := hex.EncodeToString(raw[:])
	expires := s.now().Add(s.ttl)
	s.mu.Lock()
	s.removeExpiredLocked()
	s.sessions[sha256.Sum256([]byte(token))] = session{identity: identity, expires: expires}
	s.mu.Unlock()
	return token, expires, nil
}

func (s *Sessions) Revoke(token string) {
	digest := sha256.Sum256([]byte(token))
	s.mu.Lock()
	delete(s.sessions, digest)
	s.mu.Unlock()
}

func (s *Sessions) Lookup(token string) (Identity, bool) {
	if token == "" {
		return Identity{}, false
	}
	digest := sha256.Sum256([]byte(token))
	s.mu.Lock()
	defer s.mu.Unlock()
	entry, ok := s.sessions[digest]
	if !ok {
		return Identity{}, false
	}
	if !s.now().Before(entry.expires) {
		delete(s.sessions, digest)
		return Identity{}, false
	}
	return entry.identity, true
}

func (s *Sessions) removeExpiredLocked() {
	now := s.now()
	for digest, entry := range s.sessions {
		if !now.Before(entry.expires) {
			delete(s.sessions, digest)
		}
	}
}

const identityKey = "authenticated_identity"

func (s *Sessions) Middleware() gin.HandlerFunc {
	return func(c *gin.Context) {
		header := c.GetHeader("Authorization")
		if !strings.HasPrefix(header, "Bearer ") {
			c.AbortWithStatusJSON(401, gin.H{"error": "unauthorized"})
			return
		}
		token := strings.TrimSpace(strings.TrimPrefix(header, "Bearer "))
		identity, ok := s.Lookup(token)
		if !ok {
			c.AbortWithStatusJSON(401, gin.H{"error": "unauthorized"})
			return
		}
		c.Set(identityKey, identity)
		c.Set("session_token", token)
		c.Next()
	}
}

func IdentityFromContext(c *gin.Context) Identity {
	identity, _ := c.Get(identityKey)
	return identity.(Identity)
}

func TokenFromContext(c *gin.Context) string {
	token, _ := c.Get("session_token")
	return token.(string)
}
