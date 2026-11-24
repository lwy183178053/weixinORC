package auth

import (
	"testing"
	"time"
)

func TestSessionExpires(t *testing.T) {
	now := time.Date(2026, 11, 24, 12, 0, 0, 0, time.UTC)
	sessions := NewSessions(time.Minute)
	sessions.now = func() time.Time { return now }
	identity := Identity{EmployeeID: "employee-1", Account: "test", DisplayName: "Test"}
	token, expires, err := sessions.Create(identity)
	if err != nil {
		t.Fatal(err)
	}
	if !expires.Equal(now.Add(time.Minute)) {
		t.Fatalf("unexpected session expiry: %s", expires)
	}
	if got, ok := sessions.Lookup(token); !ok || got.EmployeeID != identity.EmployeeID {
		t.Fatal("session should be valid before expiry")
	}
	now = expires
	if _, ok := sessions.Lookup(token); ok {
		t.Fatal("session should be invalid at expiry")
	}
}
