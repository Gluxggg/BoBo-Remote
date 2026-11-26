package main

import (
	"encoding/json"
	"io"
	"net"
	"net/http"
	"testing"
	"time"
)

func startEchoServer(t *testing.T) (string, func()) {
	t.Helper()
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	go func() {
		for {
			c, err := ln.Accept()
			if err != nil {
				return
			}
			go func(conn net.Conn) {
				defer conn.Close()
				_, _ = io.Copy(conn, conn)
			}(c)
		}
	}()
	return ln.Addr().String(), func() { _ = ln.Close() }
}

func fetchHealth(t *testing.T, addr string) HealthSnapshot {
	t.Helper()
	resp, err := http.Get("http://" + addr + "/bobo-relay/health")
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("unexpected health status: %d", resp.StatusCode)
	}
	var snap HealthSnapshot
	if err := json.NewDecoder(resp.Body).Decode(&snap); err != nil {
		t.Fatal(err)
	}
	return snap
}

func TestRelayEchoAndHealthCounts(t *testing.T) {
	echoAddr, stopEcho := startEchoServer(t)
	defer stopEcho()

	srv := NewRelayServer(RelayConfig{
		ListenAddr:       "127.0.0.1:0",
		TargetAddr:       echoAddr,
		HealthListenAddr: "127.0.0.1:0",
		MaxSessions:      2,
		MaxBandwidthMbps: 100,
		Region:           "cn-test",
	})
	if err := srv.Start(); err != nil {
		t.Fatal(err)
	}
	defer srv.Close()

	snapIdle := fetchHealth(t, srv.HealthAddr())
	if !snapIdle.OK || !snapIdle.Accepting || snapIdle.ActiveSessions != 0 || snapIdle.Region != "cn-test" {
		t.Fatalf("unexpected idle health snapshot: %+v", snapIdle)
	}

	conn, err := net.DialTimeout("tcp", srv.RelayAddr(), 2*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	payload := []byte("BOBO-RELAY-TLS-BLIND")
	if _, err := conn.Write(payload); err != nil {
		t.Fatal(err)
	}
	buf := make([]byte, len(payload))
	if _, err := io.ReadFull(conn, buf); err != nil {
		t.Fatal(err)
	}
	if string(buf) != string(payload) {
		t.Fatalf("echo mismatch: got %q, want %q", string(buf), string(payload))
	}

	snapBusy := fetchHealth(t, srv.HealthAddr())
	if snapBusy.ActiveSessions != 1 || snapBusy.Load < 0.5 {
		t.Fatalf("expected activeSessions=1 and load>=0.5, got %+v", snapBusy)
	}

	_ = conn.Close()
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		if s := fetchHealth(t, srv.HealthAddr()); s.ActiveSessions == 0 {
			return
		}
		time.Sleep(25 * time.Millisecond)
	}
	t.Fatalf("activeSessions did not return to 0 after close: %+v", fetchHealth(t, srv.HealthAddr()))
}

func TestRelaySaturationAndDrain(t *testing.T) {
	echoAddr, stopEcho := startEchoServer(t)
	defer stopEcho()

	srv := NewRelayServer(RelayConfig{
		ListenAddr:       "127.0.0.1:0",
		TargetAddr:       echoAddr,
		HealthListenAddr: "127.0.0.1:0",
		MaxSessions:      1,
		MaxBandwidthMbps: 100,
	})
	if err := srv.Start(); err != nil {
		t.Fatal(err)
	}
	defer srv.Close()

	c1, err := net.DialTimeout("tcp", srv.RelayAddr(), 2*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	defer c1.Close()

	// Wait until c1 is accepted and tracked
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		s := fetchHealth(t, srv.HealthAddr())
		if s.ActiveSessions == 1 {
			if s.Accepting || s.Load < 0.99 {
				t.Fatalf("expected saturated snapshot accepting=false load=1.0, got %+v", s)
			}
			break
		}
		time.Sleep(20 * time.Millisecond)
	}

	// Second connection should be rejected quickly by saturation gate
	c2, err := net.DialTimeout("tcp", srv.RelayAddr(), 2*time.Second)
	if err == nil {
		_ = c2.SetReadDeadline(time.Now().Add(500 * time.Millisecond))
		one := make([]byte, 1)
		if _, rErr := c2.Read(one); rErr == nil {
			_ = c2.Close()
			t.Fatalf("expected 2nd connection to be closed when at max-sessions=1")
		}
		_ = c2.Close()
	}

	// Test explicit drain flag
	_ = c1.Close()
	srv.SetAccepting(false)
	drainSnap := fetchHealth(t, srv.HealthAddr())
	if !drainSnap.OK || drainSnap.Accepting {
		t.Fatalf("expected ok=true accepting=false during drain, got %+v", drainSnap)
	}
}

func TestNormalizeHealthMath(t *testing.T) {
	if got := computeNormalizedLoad(32, 64, 15.0, 20.0, 200.0); got != 0.5 {
		t.Fatalf("expected 0.5 load, got %v", got)
	}
	if got := computeNormalizedLoad(1, 100, 92.5, 10.0, 200.0); got != 0.925 {
		t.Fatalf("expected 0.925 cpu load, got %v", got)
	}
	if got := computeNormalizedLoad(100, 64, 10.0, 400.0, 200.0); got != 1.0 {
		t.Fatalf("expected clamped 1.0 load, got %v", got)
	}
}
