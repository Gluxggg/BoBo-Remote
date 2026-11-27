package main

import (
	"encoding/binary"
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

func TestUdpRelayEchoAndHealthPorts(t *testing.T) {
	echoAddr, stopEcho := startEchoServer(t)
	defer stopEcho()

	udpEcho, err := net.ListenUDP("udp", &net.UDPAddr{IP: net.ParseIP("127.0.0.1"), Port: 0})
	if err != nil {
		t.Fatal(err)
	}
	defer udpEcho.Close()
	go func() {
		buf := make([]byte, 2048)
		for {
			n, addr, rErr := udpEcho.ReadFromUDP(buf)
			if rErr != nil {
				return
			}
			_, _ = udpEcho.WriteToUDP(buf[:n], addr)
		}
	}()

	srv := NewRelayServer(RelayConfig{
		ListenAddr:       "127.0.0.1:0",
		TargetAddr:       echoAddr,
		UDPListenAddr:    "127.0.0.1:0",
		UDPTargetAddr:    udpEcho.LocalAddr().String(),
		STUNListenAddr:   "127.0.0.1:0",
		HealthListenAddr: "127.0.0.1:0",
		MaxSessions:      8,
		MaxBandwidthMbps: 100,
		Region:           "cn-udp",
	})
	if err := srv.Start(); err != nil {
		t.Fatal(err)
	}
	defer srv.Close()

	snap := fetchHealth(t, srv.HealthAddr())
	if snap.UDPPort <= 0 || snap.STUNPort <= 0 || !snap.OK {
		t.Fatalf("expected udpPort and stunPort > 0 in health snapshot, got %+v", snap)
	}

	client, err := net.DialTimeout("udp", srv.UdpAddr(), 2*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	defer client.Close()
	msg := []byte("BOBO-UDP-BLIND-RELAY-OK")
	if _, err := client.Write(msg); err != nil {
		t.Fatal(err)
	}
	_ = client.SetReadDeadline(time.Now().Add(2 * time.Second))
	buf := make([]byte, len(msg)+16)
	n, err := client.Read(buf)
	if err != nil {
		t.Fatal(err)
	}
	if string(buf[:n]) != string(msg) {
		t.Fatalf("udp relay echo mismatch: got %q want %q", string(buf[:n]), string(msg))
	}
}

func TestStunBindingResponse(t *testing.T) {
	echoAddr, stopEcho := startEchoServer(t)
	defer stopEcho()

	srv := NewRelayServer(RelayConfig{
		ListenAddr:       "127.0.0.1:0",
		TargetAddr:       echoAddr,
		STUNListenAddr:   "127.0.0.1:0",
		HealthListenAddr: "127.0.0.1:0",
		MaxSessions:      4,
	})
	if err := srv.Start(); err != nil {
		t.Fatal(err)
	}
	defer srv.Close()

	c, err := net.DialTimeout("udp", srv.StunAddr(), 2*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	defer c.Close()

	txId := []byte{1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12}
	req := make([]byte, 20)
	binary.BigEndian.PutUint16(req[0:2], 0x0001)
	binary.BigEndian.PutUint16(req[2:4], 0)
	binary.BigEndian.PutUint32(req[4:8], stunMagicCookie)
	copy(req[8:20], txId)

	if _, err := c.Write(req); err != nil {
		t.Fatal(err)
	}
	_ = c.SetReadDeadline(time.Now().Add(2 * time.Second))
	resp := make([]byte, 128)
	n, err := c.Read(resp)
	if err != nil {
		t.Fatal(err)
	}
	if n != 32 || binary.BigEndian.Uint16(resp[0:2]) != 0x0101 || binary.BigEndian.Uint32(resp[4:8]) != stunMagicCookie {
		t.Fatalf("unexpected STUN binding response len=%d head=%x", n, resp[:minInt(n, 20)])
	}
	for i := 0; i < 12; i++ {
		if resp[8+i] != txId[i] {
			t.Fatalf("transaction id mismatch at %d", i)
		}
	}
	if binary.BigEndian.Uint16(resp[20:22]) != 0x0020 || resp[25] != 0x01 {
		t.Fatalf("missing XOR-MAPPED-ADDRESS IPv4 attribute: %x", resp[20:n])
	}
	xorPort := binary.BigEndian.Uint16(resp[26:28]) ^ uint16(stunMagicCookie>>16)
	localPort := uint16(c.LocalAddr().(*net.UDPAddr).Port)
	if xorPort != localPort {
		t.Fatalf("mapped port mismatch: got %d want %d", xorPort, localPort)
	}
	mappedIp := net.IPv4(resp[28]^resp[4], resp[29]^resp[5], resp[30]^resp[6], resp[31]^resp[7])
	if !mappedIp.Equal(net.ParseIP("127.0.0.1")) {
		t.Fatalf("mapped ip mismatch: got %v want 127.0.0.1", mappedIp)
	}
}

func minInt(a, b int) int {
	if a < b {
		return a
	}
	return b
}
