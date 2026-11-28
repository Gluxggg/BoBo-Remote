package main

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestSanitizeHostname(t *testing.T) {
	cases := map[string]string{
		"":                    "bobo-node",
		"WORKSTATION-01":      "bobo-workstation-01",
		"BoBo-Laptop":         "bobo-laptop",
		"My_Home PC.local":    "bobo-my-home-pc-local",
		"VeryLongComputerNameThatExceedsMaximumTailscaleLength": "bobo-verylongcomputernamethatexce",
	}
	for input, want := range cases {
		if len(want) > 32 {
			want = want[:32]
		}
		got := sanitizeHostname(input)
		if got != want {
			t.Fatalf("sanitizeHostname(%q) = %q, want %q", input, got, want)
		}
	}
}

func TestAllowedTailnetDestination(t *testing.T) {
	known := map[string]bool{
		"home-pc":                  true,
		"home-pc.tailnet.ts.net":   true,
	}
	allowed := []string{
		"100.64.0.12",
		"100.127.255.254",
		"fd7a:115c:a1e0::1234",
		"[fd7a:115c:a1e0::1234]",
		"laptop.example.ts.net",
		"laptop.example.ts.net.",
		"HOME-PC",
	}
	for _, h := range allowed {
		if !isAllowedTailnetHost(h, known) {
			t.Fatalf("expected tailnet host %q to be allowed", h)
		}
	}
	blocked := []string{
		"",
		"127.0.0.1",
		"192.168.1.5",
		"10.0.0.1",
		"8.8.8.8",
		"google.com",
		"unknown-host",
	}
	for _, h := range blocked {
		if isAllowedTailnetHost(h, known) {
			t.Fatalf("expected non-tailnet host %q to be rejected", h)
		}
	}
}

func TestSocks5RejectsBadTokenAndNonTailnet(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	port, ln, err := startSocks5Server(
		ctx,
		"good-token",
		func(context.Context, string) (net.Conn, error) {
			return nil, fmt.Errorf("unexpected dial")
		},
		func(h string) bool { return isAllowedTailnetHost(h, nil) },
	)
	if err != nil {
		t.Fatal(err)
	}
	defer ln.Close()

	// 1. Bad token must fail subnegotiation
	c1, err := net.DialTimeout("tcp", fmt.Sprintf("127.0.0.1:%d", port), 2*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	defer c1.Close()
	_, _ = c1.Write([]byte{0x05, 0x01, 0x02})
	buf := make([]byte, 2)
	if _, err := io.ReadFull(c1, buf); err != nil || buf[1] != 0x02 {
		t.Fatalf("unexpected method reply: %v %x", err, buf)
	}
	badAuth := []byte{0x01, 0x04, 'b', 'o', 'b', 'o', 0x03, 'b', 'a', 'd'}
	_, _ = c1.Write(badAuth)
	if _, err := io.ReadFull(c1, buf); err != nil || buf[1] == 0x00 {
		t.Fatalf("expected auth rejection, got %x err=%v", buf, err)
	}

	// 2. Valid token + non-tailnet IP must be denied (reply 0x02)
	c2, err := net.DialTimeout("tcp", fmt.Sprintf("127.0.0.1:%d", port), 2*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	defer c2.Close()
	_, _ = c2.Write([]byte{0x05, 0x01, 0x02})
	if _, err := io.ReadFull(c2, buf); err != nil {
		t.Fatal(err)
	}
	goodAuth := append([]byte{0x01, 0x04, 'b', 'o', 'b', 'o', byte(len("good-token"))}, []byte("good-token")...)
	_, _ = c2.Write(goodAuth)
	if _, err := io.ReadFull(c2, buf); err != nil || buf[1] != 0x00 {
		t.Fatalf("expected auth success, got %x", buf)
	}
	_, _ = c2.Write([]byte{0x05, 0x01, 0x00, 0x01, 192, 168, 1, 50, 0x1F, 0x90})
	rep := make([]byte, 10)
	if _, err := io.ReadFull(c2, rep); err != nil || rep[1] != 0x02 {
		t.Fatalf("expected non-tailnet denial 0x02, got %x err=%v", rep, err)
	}
}

func TestRunSelfTest(t *testing.T) {
	if err := runSelfTest(); err != nil {
		t.Fatalf("runSelfTest failed: %v", err)
	}
}

func TestControlAuthAndStatusEncoding(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	mgr := newTsManager(ctx, cancel, "", "my-pc", defaultForwardTarget, "tok123")
	mgr.socksPort = 51234
	mgr.controlPort = 51235
	mux := buildControlMux("tok123", mgr, func(context.Context) StatusDTO {
		return StatusDTO{
			BackendState:   "NeedsLogin",
			AuthURL:        "https://login.tailscale.com/a/example",
			SelfHostName:   "bobo-my-pc",
			ControlPort:    51235,
			SocksPort:      51234,
			Listening49810: false,
			Peers: []PeerDTO{
				{ID: "p1", HostName: "macbook", DNSName: "macbook.ts.net", OS: "macOS", IPv4: "100.64.0.99", Online: true},
			},
		}
	})

	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer ln.Close()
	srv := &http.Server{Handler: mux}
	go func() { _ = srv.Serve(ln) }()
	defer srv.Close()

	req, _ := http.NewRequest(http.MethodGet, "http://"+ln.Addr().String()+"/status", nil)
	req.Header.Set("X-BoBo-Token", "tok123")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	raw, _ := io.ReadAll(resp.Body)
	if !strings.Contains(string(raw), "login.tailscale.com") {
		t.Fatalf("missing authUrl in response: %s", string(raw))
	}
	var decoded StatusDTO
	if err := json.NewDecoder(bytes.NewReader(raw)).Decode(&decoded); err != nil {
		t.Fatal(err)
	}
	if decoded.BackendState != "NeedsLogin" || len(decoded.Peers) != 1 || decoded.Peers[0].OS != "macOS" {
		t.Fatalf("unexpected decoded status: %+v", decoded)
	}
}

func TestHasExistingState(t *testing.T) {
	dir := t.TempDir()
	if hasExistingState(dir) {
		t.Fatalf("expected empty dir to report false")
	}
	stateFile := filepath.Join(dir, "tailscaled.state")
	if err := os.WriteFile(stateFile, []byte("small"), 0o600); err != nil {
		t.Fatal(err)
	}
	if hasExistingState(dir) {
		t.Fatalf("expected tiny state file to report false")
	}
	if err := os.WriteFile(stateFile, []byte("valid-saved-tsnet-state-payload"), 0o600); err != nil {
		t.Fatal(err)
	}
	if !hasExistingState(dir) {
		t.Fatalf("expected valid state file to report true")
	}
}

func TestControlLogoutClearsStateDir(t *testing.T) {
	stateDir := t.TempDir()
	if err := os.WriteFile(filepath.Join(stateDir, "tailscaled.state"), []byte("saved-state-data-012345"), 0o600); err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	mgr := newTsManager(ctx, cancel, stateDir, "my-pc", defaultForwardTarget, "tok123")
	mux := buildControlMux("tok123", mgr, nil)

	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer ln.Close()
	srv := &http.Server{Handler: mux}
	go func() { _ = srv.Serve(ln) }()
	defer srv.Close()

	req, _ := http.NewRequest(http.MethodPost, "http://"+ln.Addr().String()+"/logout", nil)
	req.Header.Set("X-BoBo-Token", "tok123")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("expected 200 OK from /logout, got %d", resp.StatusCode)
	}
	var out map[string]any
	if err := json.NewDecoder(resp.Body).Decode(&out); err != nil {
		t.Fatal(err)
	}
	if okVal, _ := out["ok"].(bool); !okVal {
		t.Fatalf("expected ok=true from /logout, got %v", out)
	}
	if hasExistingState(stateDir) {
		t.Fatalf("expected stateDir to be cleared after /logout")
	}
}

func TestDeriveUdpForwardTarget(t *testing.T) {
	if got := deriveUdpForwardTarget("127.0.0.1:49810"); got != "127.0.0.1:49811" {
		t.Fatalf("expected 127.0.0.1:49811, got %q", got)
	}
	if got := deriveUdpForwardTarget("[::1]:50000"); got != "[::1]:50001" {
		t.Fatalf("expected [::1]:50001, got %q", got)
	}
}

func TestUdpBridgeRelayAndAuth(t *testing.T) {
	echoUdp, err := net.ListenUDP("udp", &net.UDPAddr{IP: net.ParseIP("127.0.0.1"), Port: 0})
	if err != nil {
		t.Fatal(err)
	}
	defer echoUdp.Close()
	go func() {
		buf := make([]byte, 2048)
		for {
			n, peer, rErr := echoUdp.ReadFromUDP(buf)
			if rErr != nil {
				return
			}
			_, _ = echoUdp.WriteToUDP(buf[:n], peer)
		}
	}()

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	mgr := newTsManager(ctx, cancel, "", "udp-pc", defaultForwardTarget, "tok-udp")
	mgr.udpDialFn = func(_ context.Context, _ string) (net.Conn, error) {
		return net.Dial("udp", echoUdp.LocalAddr().String())
	}
	mux := buildControlMux("tok-udp", mgr, nil)
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer ln.Close()
	srv := &http.Server{Handler: mux}
	go func() { _ = srv.Serve(ln) }()
	defer srv.Close()

	// 1. Non-tailnet destination must return 403 Forbidden
	badReq, _ := http.NewRequest(http.MethodPost,
		"http://"+ln.Addr().String()+"/udp-bridge?peerIp=192.168.1.50&remotePort=49811&localPort=40000", nil)
	badReq.Header.Set("X-BoBo-Token", "tok-udp")
	badResp, err := http.DefaultClient.Do(badReq)
	if err != nil {
		t.Fatal(err)
	}
	_ = badResp.Body.Close()
	if badResp.StatusCode != http.StatusForbidden {
		t.Fatalf("expected 403 Forbidden for LAN peerIp, got %d", badResp.StatusCode)
	}

	// 2. Valid tailnet peer relays UDP packets bidirectionally via local bridgePort
	clientUdp, err := net.ListenUDP("udp", &net.UDPAddr{IP: net.ParseIP("127.0.0.1"), Port: 0})
	if err != nil {
		t.Fatal(err)
	}
	defer clientUdp.Close()
	clientPort := clientUdp.LocalAddr().(*net.UDPAddr).Port

	openReq, _ := http.NewRequest(http.MethodPost,
		fmt.Sprintf("http://%s/udp-bridge?peerIp=100.64.0.12&remotePort=49811&localPort=%d", ln.Addr().String(), clientPort), nil)
	openReq.Header.Set("X-BoBo-Token", "tok-udp")
	openResp, err := http.DefaultClient.Do(openReq)
	if err != nil {
		t.Fatal(err)
	}
	defer openResp.Body.Close()
	if openResp.StatusCode != http.StatusOK {
		t.Fatalf("expected 200 OK from POST /udp-bridge, got %d", openResp.StatusCode)
	}
	var openOut struct {
		OK         bool `json:"ok"`
		BridgePort int  `json:"bridgePort"`
	}
	if err := json.NewDecoder(openResp.Body).Decode(&openOut); err != nil {
		t.Fatal(err)
	}
	if !openOut.OK || openOut.BridgePort <= 0 {
		t.Fatalf("unexpected openOut: %+v", openOut)
	}

	bridgeAddr := &net.UDPAddr{IP: net.ParseIP("127.0.0.1"), Port: openOut.BridgePort}
	want := []byte("BOBO-TSNET-UDP-OK")
	if _, err := clientUdp.WriteToUDP(want, bridgeAddr); err != nil {
		t.Fatal(err)
	}
	_ = clientUdp.SetReadDeadline(time.Now().Add(2 * time.Second))
	got := make([]byte, 128)
	n, _, err := clientUdp.ReadFromUDP(got)
	if err != nil {
		t.Fatal(err)
	}
	if string(got[:n]) != string(want) {
		t.Fatalf("udp bridge echo mismatch: got %q want %q", string(got[:n]), string(want))
	}

	// 3. DELETE /udp-bridge closes cleanly
	delReq, _ := http.NewRequest(http.MethodDelete,
		fmt.Sprintf("http://%s/udp-bridge?port=%d", ln.Addr().String(), openOut.BridgePort), nil)
	delReq.Header.Set("X-BoBo-Token", "tok-udp")
	delResp, err := http.DefaultClient.Do(delReq)
	if err != nil {
		t.Fatal(err)
	}
	_ = delResp.Body.Close()
	if delResp.StatusCode != http.StatusOK {
		t.Fatalf("expected 200 OK from DELETE /udp-bridge, got %d", delResp.StatusCode)
	}
}

func TestPickPreferredWireGuardPortAndPeerAddrsJson(t *testing.T) {
	port := pickPreferredWireGuardPort()
	if port != 0 && port != 49813 {
		t.Fatalf("unexpected preferred WireGuard port: %d", port)
	}
	dto := PeerDTO{
		ID:       "n1",
		HostName: "host",
		Addrs:    []string{"[2409:8a00::1]:49813", "192.168.1.10:49813"},
	}
	raw, err := json.Marshal(dto)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(raw), `"addrs":["[2409:8a00::1]:49813","192.168.1.10:49813"]`) {
		t.Fatalf("PeerDTO addrs missing in JSON: %s", string(raw))
	}
}

func TestControlMuxForwardTargetUpdate(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	mgr := newTsManager(ctx, cancel, "", "fwd-pc", defaultForwardTarget, "tok-fwd")
	mux := buildControlMux("tok-fwd", mgr, nil)

	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer ln.Close()
	srv := &http.Server{Handler: mux}
	go func() { _ = srv.Serve(ln) }()
	defer srv.Close()

	baseURL := "http://" + ln.Addr().String()

	// 1. Unauthorized must fail with 401
	unauthReq, _ := http.NewRequest(http.MethodPost, baseURL+"/forward-target?port=49823", nil)
	unauthResp, err := http.DefaultClient.Do(unauthReq)
	if err != nil {
		t.Fatal(err)
	}
	_ = unauthResp.Body.Close()
	if unauthResp.StatusCode != http.StatusUnauthorized {
		t.Fatalf("expected 401 without token, got %d", unauthResp.StatusCode)
	}

	// 2. Invalid port must return 400
	badReq, _ := http.NewRequest(http.MethodPost, baseURL+"/forward-target?port=0", nil)
	badReq.Header.Set("X-BoBo-Token", "tok-fwd")
	badResp, err := http.DefaultClient.Do(badReq)
	if err != nil {
		t.Fatal(err)
	}
	_ = badResp.Body.Close()
	if badResp.StatusCode != http.StatusBadRequest {
		t.Fatalf("expected 400 for invalid port, got %d", badResp.StatusCode)
	}

	// 3. Valid port updates both TCP and UDP targets cleanly
	okReq, _ := http.NewRequest(http.MethodPost, baseURL+"/forward-target?port=49823", nil)
	okReq.Header.Set("X-BoBo-Token", "tok-fwd")
	okResp, err := http.DefaultClient.Do(okReq)
	if err != nil {
		t.Fatal(err)
	}
	defer okResp.Body.Close()
	if okResp.StatusCode != http.StatusOK {
		t.Fatalf("expected 200 OK from /forward-target, got %d", okResp.StatusCode)
	}
	var out struct {
		OK               bool   `json:"ok"`
		ForwardTarget    string `json:"forwardTarget"`
		UdpForwardTarget string `json:"udpForwardTarget"`
	}
	if err := json.NewDecoder(okResp.Body).Decode(&out); err != nil {
		t.Fatal(err)
	}
	if !out.OK || out.ForwardTarget != "127.0.0.1:49823" || out.UdpForwardTarget != "127.0.0.1:49824" {
		t.Fatalf("unexpected /forward-target output: %+v", out)
	}
	gotTcp, gotUdp := mgr.getForwardTargets()
	if gotTcp != "127.0.0.1:49823" || gotUdp != "127.0.0.1:49824" {
		t.Fatalf("manager forward targets not updated: tcp=%q udp=%q", gotTcp, gotUdp)
	}
}
