package main

import (
	"context"
	"crypto/subtle"
	"encoding/binary"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"net/netip"
	"os"
	"os/signal"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
	"time"

	"tailscale.com/client/local"
	"tailscale.com/ipn"
	"tailscale.com/tailcfg"
	"tailscale.com/tsnet"
)

const (
	defaultForwardTarget = "127.0.0.1:49810"
	socksUserName        = "bobo"
)

var (
	tsnetIPv4Prefix = netip.MustParsePrefix("100.64.0.0/10")
	tsnetIPv6Prefix = netip.MustParsePrefix("fd7a:115c:a1e0::/48")
)

type PeerDTO struct {
	ID        string `json:"id"`
	HostName  string `json:"hostName"`
	DNSName   string `json:"dnsName"`
	OS        string `json:"os"`
	IPv4      string `json:"ip4"`
	IPv6      string `json:"ip6"`
	Online    bool   `json:"online"`
	Active    bool   `json:"active"`
	CurAddr   string `json:"curAddr"`
	Relay     string `json:"relay"`
	PeerRelay string   `json:"peerRelay"`
	LastSeen  string   `json:"lastSeen"`
	Addrs     []string `json:"addrs,omitempty"`
}

type StatusDTO struct {
	BackendState   string    `json:"backendState"`
	AuthURL        string    `json:"authUrl"`
	SelfIPv4       string    `json:"selfIp4"`
	SelfIPv6       string    `json:"selfIp6"`
	SelfDNSName    string    `json:"selfDnsName"`
	SelfHostName   string    `json:"selfHostName"`
	LoginName      string    `json:"loginName"`
	DisplayName    string    `json:"displayName"`
	TailnetName    string    `json:"tailnetName"`
	ControlPort    int       `json:"controlPort"`
	SocksPort      int       `json:"socksPort"`
	Listening49810 bool      `json:"listening49810"`
	Listening49811 bool      `json:"listening49811"`
	Error          string    `json:"error"`
	Peers          []PeerDTO `json:"peers"`
}

type PingDTO struct {
	OK             bool    `json:"ok"`
	Direct         bool    `json:"direct"`
	Endpoint       string  `json:"endpoint"`
	DERPRegionCode string  `json:"derpRegionCode"`
	PeerRelay      string  `json:"peerRelay"`
	LatencyMs      float64 `json:"latencyMs"`
	Error          string  `json:"error"`
}

type tsManager struct {
	stateDir         string
	hostname         string
	forwardTarget    string
	udpForwardTarget string
	token            string
	controlPort      int
	socksPort        int

	appCtx    context.Context
	appCancel context.CancelFunc

	mu             sync.Mutex
	srv            *tsnet.Server
	lc             *local.Client
	hostLn         net.Listener
	hostUdpLn      net.PacketConn
	authURL        string
	lastState      string
	lastErr        string
	knownPeers     map[string]bool
	loginWaitCh    chan struct{}

	udpMu          sync.Mutex
	udpBridges     map[int]*udpBridgeSession
	udpDialFn      func(context.Context, string) (net.Conn, error)
}

func deriveUdpForwardTarget(tcpTarget string) string {
	host, portStr, err := net.SplitHostPort(strings.TrimSpace(tcpTarget))
	if err != nil {
		return "127.0.0.1:49811"
	}
	port, err := strconv.Atoi(portStr)
	if err != nil || port <= 0 || port >= 65535 {
		return net.JoinHostPort(host, "49811")
	}
	return net.JoinHostPort(host, strconv.Itoa(port+1))
}

func newTsManager(ctx context.Context, cancel context.CancelFunc, stateDir, hostname, forwardTarget, token string) *tsManager {
	return &tsManager{
		stateDir:         stateDir,
		hostname:         sanitizeHostname(hostname),
		forwardTarget:    forwardTarget,
		udpForwardTarget: deriveUdpForwardTarget(forwardTarget),
		token:            token,
		appCtx:           ctx,
		appCancel:        cancel,
		lastState:        "Stopped",
		knownPeers:       make(map[string]bool),
		loginWaitCh:      make(chan struct{}, 1),
		udpBridges:       make(map[int]*udpBridgeSession),
	}
}

func sanitizeHostname(raw string) string {
	raw = strings.ToLower(strings.TrimSpace(raw))
	var b strings.Builder
	lastDash := false
	for i := 0; i < len(raw); i++ {
		c := raw[i]
		if (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') {
			b.WriteByte(c)
			lastDash = false
		} else if c == '-' || c == '_' || c == ' ' || c == '.' {
			if b.Len() > 0 && !lastDash {
				b.WriteByte('-')
				lastDash = true
			}
		}
	}
	clean := strings.Trim(b.String(), "-")
	if clean == "" {
		return "bobo-node"
	}
	if !strings.HasPrefix(clean, "bobo-") {
		clean = "bobo-" + clean
	}
	if len(clean) > 32 {
		clean = strings.TrimRight(clean[:32], "-")
	}
	if clean == "" {
		return "bobo-node"
	}
	return clean
}

func hasExistingState(stateDir string) bool {
	if strings.TrimSpace(stateDir) == "" {
		return false
	}
	info, err := os.Stat(filepath.Join(stateDir, "tailscaled.state"))
	return err == nil && !info.IsDir() && info.Size() > 16
}

func isAllowedTailnetHost(host string, knownPeers map[string]bool) bool {
	h := strings.TrimSpace(strings.TrimSuffix(host, "."))
	h = strings.TrimPrefix(strings.TrimSuffix(h, "]"), "[")
	if h == "" {
		return false
	}
	if addr, err := netip.ParseAddr(h); err == nil {
		addr = addr.Unmap()
		return tsnetIPv4Prefix.Contains(addr) || tsnetIPv6Prefix.Contains(addr)
	}
	lower := strings.ToLower(h)
	if strings.HasSuffix(lower, ".ts.net") && len(lower) > len(".ts.net") {
		return true
	}
	if knownPeers != nil && knownPeers[lower] {
		return true
	}
	return false
}

func (m *tsManager) isHostAllowed(host string) bool {
	m.mu.Lock()
	peersCopy := make(map[string]bool, len(m.knownPeers))
	for k, v := range m.knownPeers {
		peersCopy[k] = v
	}
	m.mu.Unlock()
	return isAllowedTailnetHost(host, peersCopy)
}

func (m *tsManager) signalWaiter() {
	select {
	case m.loginWaitCh <- struct{}{}:
	default:
	}
}

func pickPreferredWireGuardPort() uint16 {
	ln, err := net.ListenPacket("udp", ":49813")
	if err != nil {
		return 0
	}
	_ = ln.Close()
	return 49813
}

func (m *tsManager) ensureStarted() (*tsnet.Server, *local.Client, error) {
	m.mu.Lock()
	if m.srv != nil && m.lc != nil {
		srv, lc := m.srv, m.lc
		m.mu.Unlock()
		return srv, lc, nil
	}
	srv := &tsnet.Server{
		Dir:      m.stateDir,
		Hostname: m.hostname,
		Port:     pickPreferredWireGuardPort(),
		UserLogf: func(format string, args ...any) {
			msg := fmt.Sprintf(format, args...)
			if idx := strings.Index(msg, "https://"); idx >= 0 {
				urlStr := strings.Fields(msg[idx:])[0]
				m.mu.Lock()
				if m.lastState != "Running" && strings.Contains(urlStr, "login.tailscale.com") {
					m.authURL = urlStr
					m.signalWaiter()
				}
				m.mu.Unlock()
			}
		},
		Logf: func(string, ...any) {},
	}
	if err := srv.Start(); err != nil {
		m.lastErr = err.Error()
		m.mu.Unlock()
		return nil, nil, err
	}
	lc, err := srv.LocalClient()
	if err != nil {
		_ = srv.Close()
		m.lastErr = err.Error()
		m.mu.Unlock()
		return nil, nil, err
	}
	m.srv = srv
	m.lc = lc
	m.lastErr = ""
	m.mu.Unlock()

	go m.watchBus(srv, lc)
	return srv, lc, nil
}

type udpBridgeSession struct {
	localConn  *net.UDPConn
	remoteConn net.Conn
	closeOnce  sync.Once
}

func (u *udpBridgeSession) close() {
	u.closeOnce.Do(func() {
		if u.localConn != nil {
			_ = u.localConn.Close()
		}
		if u.remoteConn != nil {
			_ = u.remoteConn.Close()
		}
	})
}

type hostUdpPeerForward struct {
	remoteAddr net.Addr
	dstConn    *net.UDPConn
	lastUnix   atomic.Int64
	closeOnce  sync.Once
}

func (f *hostUdpPeerForward) close() {
	f.closeOnce.Do(func() {
		if f.dstConn != nil {
			_ = f.dstConn.Close()
		}
	})
}

func (m *tsManager) ensureHostListener(srv *tsnet.Server) {
	m.mu.Lock()
	needTcp := (m.hostLn == nil && m.srv == srv)
	m.mu.Unlock()

	if needTcp {
		ln, err := srv.Listen("tcp", ":49810")
		if err != nil {
			m.mu.Lock()
			m.lastErr = "listen :49810 failed: " + err.Error()
			m.mu.Unlock()
			return
		}

		m.mu.Lock()
		if m.srv != srv || m.hostLn != nil {
			m.mu.Unlock()
			_ = ln.Close()
		} else {
			m.hostLn = ln
			m.mu.Unlock()

			go func() {
				defer ln.Close()
				for {
					conn, err := ln.Accept()
					if err != nil {
						return
					}
					go forwardConn(conn, m.forwardTarget)
				}
			}()
		}
	}

	m.ensureHostUdpListener(srv)
}

func (m *tsManager) ensureHostUdpListener(srv *tsnet.Server) {
	m.mu.Lock()
	if m.hostUdpLn != nil || m.srv != srv || m.lc == nil {
		m.mu.Unlock()
		return
	}
	lc := m.lc
	m.mu.Unlock()

	ctx, cancel := context.WithTimeout(m.appCtx, 1500*time.Millisecond)
	st, err := lc.StatusWithoutPeers(ctx)
	cancel()
	if err != nil || st == nil {
		return
	}
	var selfIp4, selfIp6 string
	for _, addr := range st.TailscaleIPs {
		a := addr.Unmap()
		if a.Is4() && selfIp4 == "" {
			selfIp4 = a.String()
		} else if a.Is6() && selfIp6 == "" {
			selfIp6 = a.String()
		}
	}
	var pc net.PacketConn
	if selfIp4 != "" {
		if p, lErr := srv.ListenPacket("udp4", net.JoinHostPort(selfIp4, "49811")); lErr == nil {
			pc = p
		}
	}
	if pc == nil && selfIp6 != "" {
		if p, lErr := srv.ListenPacket("udp6", net.JoinHostPort(selfIp6, "49811")); lErr == nil {
			pc = p
		}
	}
	if pc == nil {
		return
	}

	m.mu.Lock()
	if m.srv != srv || m.hostUdpLn != nil {
		m.mu.Unlock()
		_ = pc.Close()
		return
	}
	m.hostUdpLn = pc
	m.mu.Unlock()

	go m.serveHostUdp(pc)
}

func tuneLocalUdpConn(c *net.UDPConn) {
	if c == nil {
		return
	}
	_ = c.SetReadBuffer(4 * 1024 * 1024)
	_ = c.SetWriteBuffer(4 * 1024 * 1024)
}

func (m *tsManager) serveHostUdp(pc net.PacketConn) {
	defer pc.Close()
	targetAddr, err := net.ResolveUDPAddr("udp", m.udpForwardTarget)
	if err != nil {
		return
	}
	var peerMu sync.Mutex
	peers := make(map[string]*hostUdpPeerForward)
	doneCh := make(chan struct{})
	defer func() {
		close(doneCh)
		peerMu.Lock()
		for k, entry := range peers {
			entry.close()
			delete(peers, k)
		}
		peerMu.Unlock()
	}()

	go func() {
		ticker := time.NewTicker(15 * time.Second)
		defer ticker.Stop()
		for {
			select {
			case <-doneCh:
				return
			case <-ticker.C:
				cutoff := time.Now().Unix() - 90
				peerMu.Lock()
				for k, entry := range peers {
					if entry.lastUnix.Load() < cutoff {
						entry.close()
						delete(peers, k)
					}
				}
				peerMu.Unlock()
			}
		}
	}()

	buf := make([]byte, 4096)
	for {
		n, remoteAddr, err := pc.ReadFrom(buf)
		if err != nil {
			return
		}
		if n <= 0 || remoteAddr == nil {
			continue
		}
		key := remoteAddr.String()
		nowUnix := time.Now().Unix()
		peerMu.Lock()
		entry := peers[key]
		if entry == nil {
			if len(peers) >= 64 {
				peerMu.Unlock()
				continue
			}
			dstConn, dErr := net.DialUDP("udp", nil, targetAddr)
			if dErr != nil {
				peerMu.Unlock()
				continue
			}
			tuneLocalUdpConn(dstConn)
			entry = &hostUdpPeerForward{
				remoteAddr: remoteAddr,
				dstConn:    dstConn,
			}
			entry.lastUnix.Store(nowUnix)
			peers[key] = entry
			peerMu.Unlock()

			go func(k string, ent *hostUdpPeerForward) {
				defer func() {
					ent.close()
					peerMu.Lock()
					if peers[k] == ent {
						delete(peers, k)
					}
					peerMu.Unlock()
				}()
				rbuf := make([]byte, 4096)
				for {
					rn, rErr := ent.dstConn.Read(rbuf)
					if rErr != nil {
						return
					}
					if rn > 0 {
						ent.lastUnix.Store(time.Now().Unix())
						if _, wErr := pc.WriteTo(rbuf[:rn], ent.remoteAddr); wErr != nil {
							return
						}
					}
				}
			}(key, entry)
		} else {
			entry.lastUnix.Store(nowUnix)
			peerMu.Unlock()
		}
		_, _ = entry.dstConn.Write(buf[:n])
	}
}

func (m *tsManager) closeHostListener() {
	m.mu.Lock()
	ln := m.hostLn
	m.hostLn = nil
	udpLn := m.hostUdpLn
	m.hostUdpLn = nil
	m.mu.Unlock()
	if ln != nil {
		_ = ln.Close()
	}
	if udpLn != nil {
		_ = udpLn.Close()
	}
	m.closeAllUdpBridges()
}

func (m *tsManager) closeAllUdpBridges() {
	m.udpMu.Lock()
	items := make([]*udpBridgeSession, 0, len(m.udpBridges))
	for k, s := range m.udpBridges {
		items = append(items, s)
		delete(m.udpBridges, k)
	}
	m.udpMu.Unlock()
	for _, s := range items {
		s.close()
	}
}

func (m *tsManager) dialTailnetUdp(ctx context.Context, addr string) (net.Conn, error) {
	if m.udpDialFn != nil {
		return m.udpDialFn(ctx, addr)
	}
	_, _, err := m.ensureStarted()
	if err != nil {
		return nil, err
	}
	m.mu.Lock()
	srv := m.srv
	m.mu.Unlock()
	if srv == nil {
		return nil, errors.New("tailnet server not ready")
	}
	return srv.Dial(ctx, "udp", addr)
}

func (m *tsManager) openUdpBridge(ctx context.Context, peerIp string, remotePort, localPort int) (int, error) {
	dialCtx, cancel := context.WithTimeout(ctx, 4*time.Second)
	defer cancel()
	remoteConn, err := m.dialTailnetUdp(dialCtx, net.JoinHostPort(peerIp, strconv.Itoa(remotePort)))
	if err != nil {
		return 0, err
	}
	localConn, err := net.ListenUDP("udp", &net.UDPAddr{IP: net.ParseIP("127.0.0.1"), Port: 0})
	if err != nil {
		_ = remoteConn.Close()
		return 0, err
	}
	tuneLocalUdpConn(localConn)
	bridgePort := localConn.LocalAddr().(*net.UDPAddr).Port
	clientAddr := &net.UDPAddr{IP: net.ParseIP("127.0.0.1"), Port: localPort}
	sess := &udpBridgeSession{
		localConn:  localConn,
		remoteConn: remoteConn,
	}

	m.udpMu.Lock()
	if len(m.udpBridges) >= 32 {
		m.udpMu.Unlock()
		sess.close()
		return 0, errors.New("too many active udp bridges")
	}
	m.udpBridges[bridgePort] = sess
	m.udpMu.Unlock()

	cleanup := func() {
		sess.close()
		m.udpMu.Lock()
		if m.udpBridges[bridgePort] == sess {
			delete(m.udpBridges, bridgePort)
		}
		m.udpMu.Unlock()
	}

	go func() {
		defer cleanup()
		buf := make([]byte, 4096)
		for {
			_ = localConn.SetReadDeadline(time.Now().Add(180 * time.Second))
			n, src, rErr := localConn.ReadFromUDP(buf)
			if rErr != nil {
				return
			}
			if n <= 0 || src == nil || !src.IP.IsLoopback() || src.Port != localPort {
				continue
			}
			if _, wErr := remoteConn.Write(buf[:n]); wErr != nil {
				return
			}
		}
	}()

	go func() {
		defer cleanup()
		buf := make([]byte, 4096)
		for {
			_ = remoteConn.SetReadDeadline(time.Now().Add(180 * time.Second))
			n, rErr := remoteConn.Read(buf)
			if rErr != nil {
				return
			}
			if n > 0 {
				if _, wErr := localConn.WriteToUDP(buf[:n], clientAddr); wErr != nil {
					return
				}
			}
		}
	}()

	return bridgePort, nil
}

func (m *tsManager) closeUdpBridge(port int) bool {
	m.udpMu.Lock()
	sess := m.udpBridges[port]
	if sess != nil {
		delete(m.udpBridges, port)
	}
	m.udpMu.Unlock()
	if sess != nil {
		sess.close()
		return true
	}
	return false
}

func (m *tsManager) watchBus(srv *tsnet.Server, lc *local.Client) {
	for {
		if m.appCtx.Err() != nil {
			return
		}
		m.mu.Lock()
		stillSame := (m.srv == srv)
		m.mu.Unlock()
		if !stillSame {
			return
		}

		watcher, err := lc.WatchIPNBus(m.appCtx, ipn.NotifyInitialState)
		if err != nil {
			select {
			case <-m.appCtx.Done():
				return
			case <-time.After(700 * time.Millisecond):
			}
			continue
		}

		for {
			n, err := watcher.Next()
			if err != nil {
				_ = watcher.Close()
				break
			}
			m.mu.Lock()
			if n.ErrMessage != nil && *n.ErrMessage != "" {
				m.lastErr = *n.ErrMessage
			}
			if n.BrowseToURL != nil && *n.BrowseToURL != "" {
				m.authURL = *n.BrowseToURL
			}
			if n.State != nil {
				stateStr := n.State.String()
				m.lastState = stateStr
				if *n.State == ipn.Running {
					m.authURL = ""
					m.lastErr = ""
				}
			}
			if n.LoginFinished != nil {
				m.authURL = ""
			}
			m.mu.Unlock()

			if n.State != nil && *n.State == ipn.Running {
				m.ensureHostListener(srv)
			}
			m.signalWaiter()
		}
	}
}

func (m *tsManager) buildStatus(ctx context.Context) StatusDTO {
	out := StatusDTO{
		BackendState: "Stopped",
		SelfHostName: m.hostname,
		ControlPort:  m.controlPort,
		SocksPort:    m.socksPort,
		Peers:        []PeerDTO{},
	}

	m.mu.Lock()
	srv := m.srv
	lc := m.lc
	cachedAuth := m.authURL
	cachedState := m.lastState
	cachedErr := m.lastErr
	listening := m.hostLn != nil
	listeningUdp := m.hostUdpLn != nil
	m.mu.Unlock()

	if srv == nil || lc == nil {
		out.BackendState = cachedState
		out.Error = cachedErr
		return out
	}

	stCtx, cancel := context.WithTimeout(ctx, 2500*time.Millisecond)
	defer cancel()
	st, err := lc.Status(stCtx)
	if err != nil {
		out.BackendState = cachedState
		out.AuthURL = cachedAuth
		if cachedErr != "" {
			out.Error = cachedErr
		} else {
			out.Error = err.Error()
		}
		out.Listening49810 = listening
		out.Listening49811 = listeningUdp
		return out
	}

	if st.BackendState != "" {
		out.BackendState = st.BackendState
	} else {
		out.BackendState = cachedState
	}
	if st.AuthURL != "" {
		out.AuthURL = st.AuthURL
	} else if out.BackendState != "Running" {
		out.AuthURL = cachedAuth
	}
	if cachedErr != "" {
		out.Error = cachedErr
	} else if len(st.Health) > 0 && out.BackendState != "Running" {
		out.Error = strings.Join(st.Health, "; ")
	}

	if out.BackendState == "Running" {
		m.ensureHostListener(srv)
		m.mu.Lock()
		listening = m.hostLn != nil
		listeningUdp = m.hostUdpLn != nil
		m.mu.Unlock()
		out.AuthURL = ""
	}
	out.Listening49810 = listening
	out.Listening49811 = listeningUdp

	for _, addr := range st.TailscaleIPs {
		a := addr.Unmap()
		if a.Is4() && out.SelfIPv4 == "" {
			out.SelfIPv4 = a.String()
		} else if a.Is6() && out.SelfIPv6 == "" {
			out.SelfIPv6 = a.String()
		}
	}
	if st.CurrentTailnet != nil {
		out.TailnetName = st.CurrentTailnet.Name
	}

	newKnown := make(map[string]bool)
	if st.Self != nil {
		if st.Self.HostName != "" {
			out.SelfHostName = st.Self.HostName
		}
		out.SelfDNSName = strings.TrimSuffix(st.Self.DNSName, ".")
		for _, addr := range st.Self.TailscaleIPs {
			a := addr.Unmap()
			if a.Is4() && out.SelfIPv4 == "" {
				out.SelfIPv4 = a.String()
			} else if a.Is6() && out.SelfIPv6 == "" {
				out.SelfIPv6 = a.String()
			}
		}
		if st.User != nil {
			if up, ok := st.User[st.Self.UserID]; ok {
				out.LoginName = up.LoginName
				out.DisplayName = up.DisplayName
			}
		}
	}

	for _, p := range st.Peer {
		if p == nil {
			continue
		}
		dto := PeerDTO{
			ID:        string(p.ID),
			HostName:  p.HostName,
			DNSName:   strings.TrimSuffix(p.DNSName, "."),
			OS:        p.OS,
			Online:    p.Online,
			Active:    p.Active,
			CurAddr:   p.CurAddr,
			Relay:     p.Relay,
			PeerRelay: p.PeerRelay,
		}
		if !p.LastSeen.IsZero() {
			dto.LastSeen = p.LastSeen.UTC().Format(time.RFC3339)
		}
		for _, ap := range p.Addrs {
			s := strings.TrimSpace(ap)
			if s == "" {
				continue
			}
			dto.Addrs = append(dto.Addrs, s)
			if len(dto.Addrs) >= 12 {
				break
			}
		}
		for _, addr := range p.TailscaleIPs {
			a := addr.Unmap()
			if a.Is4() && dto.IPv4 == "" {
				dto.IPv4 = a.String()
			} else if a.Is6() && dto.IPv6 == "" {
				dto.IPv6 = a.String()
			}
		}
		if dto.DNSName != "" {
			newKnown[strings.ToLower(dto.DNSName)] = true
		}
		if dto.HostName != "" {
			newKnown[strings.ToLower(dto.HostName)] = true
		}
		if dto.IPv4 == "" && dto.IPv6 == "" {
			continue
		}
		out.Peers = append(out.Peers, dto)
	}

	sort.Slice(out.Peers, func(i, j int) bool {
		if out.Peers[i].Online != out.Peers[j].Online {
			return out.Peers[i].Online
		}
		return strings.ToLower(out.Peers[i].HostName) < strings.ToLower(out.Peers[j].HostName)
	})

	m.mu.Lock()
	m.knownPeers = newKnown
	m.mu.Unlock()
	return out
}

func (m *tsManager) dialTailnet(ctx context.Context, addr string) (net.Conn, error) {
	_, _, err := m.ensureStarted()
	if err != nil {
		return nil, err
	}
	m.mu.Lock()
	srv := m.srv
	m.mu.Unlock()
	if srv == nil {
		return nil, errors.New("tailnet server not ready")
	}
	return srv.Dial(ctx, "tcp", addr)
}

func (m *tsManager) shutdown() {
	m.closeHostListener()
	m.mu.Lock()
	srv := m.srv
	m.srv = nil
	m.lc = nil
	m.lastState = "Stopped"
	m.mu.Unlock()
	if srv != nil {
		_ = srv.Close()
	}
	m.appCancel()
}

func forwardConn(src net.Conn, target string) {
	defer src.Close()
	dialer := net.Dialer{Timeout: 4 * time.Second}
	dst, err := dialer.Dial("tcp", target)
	if err != nil {
		return
	}
	defer dst.Close()

	if tc, ok := dst.(*net.TCPConn); ok {
		_ = tc.SetNoDelay(true)
	}
	if tc, ok := src.(*net.TCPConn); ok {
		_ = tc.SetNoDelay(true)
	}

	var wg sync.WaitGroup
	wg.Add(2)
	go func() {
		defer wg.Done()
		_, _ = io.Copy(dst, src)
		if tc, ok := dst.(interface{ CloseWrite() error }); ok {
			_ = tc.CloseWrite()
		}
	}()
	go func() {
		defer wg.Done()
		_, _ = io.Copy(src, dst)
		if tc, ok := src.(interface{ CloseWrite() error }); ok {
			_ = tc.CloseWrite()
		}
	}()
	wg.Wait()
}

func checkConstantToken(supplied, expected string) bool {
	if expected == "" {
		return true
	}
	if len(supplied) != len(expected) {
		return false
	}
	return subtle.ConstantTimeCompare([]byte(supplied), []byte(expected)) == 1
}

// Authenticated local SOCKS5 server (RFC 1928 + RFC 1929 username/password).
func startSocks5Server(
	ctx context.Context,
	token string,
	dialFn func(context.Context, string) (net.Conn, error),
	allowFn func(string) bool,
) (int, net.Listener, error) {
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return 0, nil, err
	}
	port := ln.Addr().(*net.TCPAddr).Port
	go func() {
		<-ctx.Done()
		_ = ln.Close()
	}()
	go func() {
		for {
			c, err := ln.Accept()
			if err != nil {
				return
			}
			go handleSocks5Conn(ctx, c, token, dialFn, allowFn)
		}
	}()
	return port, ln, nil
}

func handleSocks5Conn(
	ctx context.Context,
	client net.Conn,
	token string,
	dialFn func(context.Context, string) (net.Conn, error),
	allowFn func(string) bool,
) {
	defer client.Close()
	if tc, ok := client.(*net.TCPConn); ok {
		_ = tc.SetNoDelay(true)
	}
	_ = client.SetDeadline(time.Now().Add(15 * time.Second))

	hdr := make([]byte, 2)
	if _, err := io.ReadFull(client, hdr); err != nil {
		return
	}
	if hdr[0] != 0x05 || hdr[1] == 0 {
		return
	}
	methods := make([]byte, hdr[1])
	if _, err := io.ReadFull(client, methods); err != nil {
		return
	}
	hasUserPass := false
	for _, m := range methods {
		if m == 0x02 {
			hasUserPass = true
			break
		}
	}
	if !hasUserPass {
		_, _ = client.Write([]byte{0x05, 0xFF})
		return
	}
	if _, err := client.Write([]byte{0x05, 0x02}); err != nil {
		return
	}

	// RFC 1929 subnegotiation
	authVerLen := make([]byte, 2)
	if _, err := io.ReadFull(client, authVerLen); err != nil {
		return
	}
	if authVerLen[0] != 0x01 {
		_, _ = client.Write([]byte{0x01, 0x01})
		return
	}
	uBytes := make([]byte, authVerLen[1])
	if _, err := io.ReadFull(client, uBytes); err != nil {
		return
	}
	pLenBuf := make([]byte, 1)
	if _, err := io.ReadFull(client, pLenBuf); err != nil {
		return
	}
	pBytes := make([]byte, pLenBuf[0])
	if _, err := io.ReadFull(client, pBytes); err != nil {
		return
	}
	if string(uBytes) != socksUserName || !checkConstantToken(string(pBytes), token) {
		_, _ = client.Write([]byte{0x01, 0x01})
		return
	}
	if _, err := client.Write([]byte{0x01, 0x00}); err != nil {
		return
	}

	// SOCKS5 Request
	reqHdr := make([]byte, 4)
	if _, err := io.ReadFull(client, reqHdr); err != nil {
		return
	}
	if reqHdr[0] != 0x05 || reqHdr[1] != 0x01 {
		_, _ = client.Write([]byte{0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0})
		return
	}

	var targetHost string
	switch reqHdr[3] {
	case 0x01: // IPv4
		ip := make([]byte, 4)
		if _, err := io.ReadFull(client, ip); err != nil {
			return
		}
		targetHost = net.IP(ip).String()
	case 0x03: // Domain
		dLen := make([]byte, 1)
		if _, err := io.ReadFull(client, dLen); err != nil || dLen[0] == 0 {
			return
		}
		d := make([]byte, dLen[0])
		if _, err := io.ReadFull(client, d); err != nil {
			return
		}
		targetHost = string(d)
	case 0x04: // IPv6
		ip := make([]byte, 16)
		if _, err := io.ReadFull(client, ip); err != nil {
			return
		}
		targetHost = net.IP(ip).String()
	default:
		_, _ = client.Write([]byte{0x05, 0x08, 0x00, 0x01, 0, 0, 0, 0, 0, 0})
		return
	}

	portBuf := make([]byte, 2)
	if _, err := io.ReadFull(client, portBuf); err != nil {
		return
	}
	port := binary.BigEndian.Uint16(portBuf)
	if port == 0 {
		_, _ = client.Write([]byte{0x05, 0x02, 0x00, 0x01, 0, 0, 0, 0, 0, 0})
		return
	}

	if allowFn != nil && !allowFn(targetHost) {
		_, _ = client.Write([]byte{0x05, 0x02, 0x00, 0x01, 0, 0, 0, 0, 0, 0})
		return
	}

	dialCtx, dialCancel := context.WithTimeout(ctx, 12*time.Second)
	defer dialCancel()
	remote, err := dialFn(dialCtx, net.JoinHostPort(strings.TrimSuffix(targetHost, "."), strconv.Itoa(int(port))))
	if err != nil {
		_, _ = client.Write([]byte{0x05, 0x05, 0x00, 0x01, 0, 0, 0, 0, 0, 0})
		return
	}
	defer remote.Close()

	if _, err := client.Write([]byte{0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0}); err != nil {
		return
	}
	_ = client.SetDeadline(time.Time{})

	var wg sync.WaitGroup
	wg.Add(2)
	go func() {
		defer wg.Done()
		_, _ = io.Copy(remote, client)
		if tc, ok := remote.(interface{ CloseWrite() error }); ok {
			_ = tc.CloseWrite()
		}
	}()
	go func() {
		defer wg.Done()
		_, _ = io.Copy(client, remote)
		if tc, ok := client.(interface{ CloseWrite() error }); ok {
			_ = tc.CloseWrite()
		}
	}()
	wg.Wait()
}

func buildControlMux(token string, m *tsManager, statusOverride func(context.Context) StatusDTO) http.Handler {
	mux := http.NewServeMux()
	authWrap := func(next http.HandlerFunc) http.HandlerFunc {
		return func(w http.ResponseWriter, r *http.Request) {
			if !checkConstantToken(r.Header.Get("X-BoBo-Token"), token) {
				writeJSON(w, http.StatusUnauthorized, map[string]any{"ok": false, "error": "unauthorized"})
				return
			}
			next(w, r)
		}
	}

	mux.HandleFunc("/status", authWrap(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet {
			writeJSON(w, http.StatusMethodNotAllowed, map[string]any{"ok": false, "error": "method not allowed"})
			return
		}
		if statusOverride != nil {
			writeJSON(w, http.StatusOK, statusOverride(r.Context()))
			return
		}
		writeJSON(w, http.StatusOK, m.buildStatus(r.Context()))
	}))

	mux.HandleFunc("/login", authWrap(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			writeJSON(w, http.StatusMethodNotAllowed, map[string]any{"ok": false, "error": "method not allowed"})
			return
		}
		_, lc, err := m.ensureStarted()
		if err != nil {
			writeJSON(w, http.StatusOK, map[string]any{"ok": false, "backendState": "Stopped", "error": err.Error()})
			return
		}
		st := m.buildStatus(r.Context())
		if st.BackendState == "Running" {
			writeJSON(w, http.StatusOK, map[string]any{"ok": true, "backendState": "Running", "authUrl": "", "status": st})
			return
		}
		if err := lc.StartLoginInteractive(r.Context()); err != nil {
			writeJSON(w, http.StatusOK, map[string]any{"ok": false, "backendState": st.BackendState, "error": err.Error()})
			return
		}
		deadline := time.Now().Add(10 * time.Second)
		for time.Now().Before(deadline) {
			st = m.buildStatus(r.Context())
			if st.BackendState == "Running" || st.AuthURL != "" {
				writeJSON(w, http.StatusOK, map[string]any{
					"ok":           true,
					"backendState": st.BackendState,
					"authUrl":      st.AuthURL,
					"status":       st,
				})
				return
			}
			select {
			case <-r.Context().Done():
				writeJSON(w, http.StatusOK, map[string]any{"ok": false, "backendState": st.BackendState, "error": "cancelled"})
				return
			case <-m.loginWaitCh:
			case <-time.After(350 * time.Millisecond):
			}
		}
		st = m.buildStatus(r.Context())
		writeJSON(w, http.StatusOK, map[string]any{
			"ok":           st.AuthURL != "" || st.BackendState == "Running",
			"backendState": st.BackendState,
			"authUrl":      st.AuthURL,
			"error":        st.Error,
			"status":       st,
		})
	}))

	mux.HandleFunc("/logout", authWrap(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			writeJSON(w, http.StatusMethodNotAllowed, map[string]any{"ok": false, "error": "method not allowed"})
			return
		}
		m.mu.Lock()
		srv := m.srv
		lc := m.lc
		m.srv = nil
		m.lc = nil
		m.authURL = ""
		m.lastErr = ""
		m.lastState = "Stopped"
		m.knownPeers = make(map[string]bool)
		m.mu.Unlock()
		m.closeHostListener()
		if lc != nil {
			ctx, cancel := context.WithTimeout(r.Context(), 3*time.Second)
			_ = lc.Logout(ctx)
			cancel()
		}
		if srv != nil {
			_ = srv.Close()
		}
		if strings.TrimSpace(m.stateDir) != "" {
			if entries, err := os.ReadDir(m.stateDir); err == nil {
				for _, entry := range entries {
					_ = os.RemoveAll(filepath.Join(m.stateDir, entry.Name()))
				}
			}
		}
		writeJSON(w, http.StatusOK, map[string]any{"ok": true})
	}))

	mux.HandleFunc("/ping", authWrap(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			writeJSON(w, http.StatusMethodNotAllowed, map[string]any{"ok": false, "error": "method not allowed"})
			return
		}
		rawIP := strings.TrimSpace(r.URL.Query().Get("ip"))
		addr, err := netip.ParseAddr(rawIP)
		if err != nil {
			writeJSON(w, http.StatusOK, PingDTO{OK: false, Error: "invalid target IP"})
			return
		}
		_, lc, err := m.ensureStarted()
		if err != nil {
			writeJSON(w, http.StatusOK, PingDTO{OK: false, Error: err.Error()})
			return
		}
		var best PingDTO
		for attempt := 0; attempt < 2; attempt++ {
			pCtx, pCancel := context.WithTimeout(r.Context(), 2200*time.Millisecond)
			res, pErr := lc.Ping(pCtx, addr, tailcfg.PingDisco)
			pCancel()
			if pErr != nil {
				best = PingDTO{OK: false, Error: pErr.Error()}
				continue
			}
			if res.Err != "" {
				best = PingDTO{OK: false, Error: res.Err}
				continue
			}
			best = PingDTO{
				OK:             true,
				Direct:         res.Endpoint != "",
				Endpoint:       res.Endpoint,
				DERPRegionCode: res.DERPRegionCode,
				PeerRelay:      res.PeerRelay,
				LatencyMs:      res.LatencySeconds * 1000.0,
			}
			if best.Direct {
				break
			}
		}
		writeJSON(w, http.StatusOK, best)
	}))

	mux.HandleFunc("/udp-bridge", authWrap(func(w http.ResponseWriter, r *http.Request) {
		if m == nil {
			writeJSON(w, http.StatusServiceUnavailable, map[string]any{"ok": false, "error": "manager unavailable"})
			return
		}
		switch r.Method {
		case http.MethodPost:
			peerIp := strings.TrimSpace(r.URL.Query().Get("peerIp"))
			if !m.isHostAllowed(peerIp) {
				writeJSON(w, http.StatusForbidden, map[string]any{"ok": false, "error": "non-tailnet peerIp rejected"})
				return
			}
			remotePort, rErr := strconv.Atoi(strings.TrimSpace(r.URL.Query().Get("remotePort")))
			localPort, lErr := strconv.Atoi(strings.TrimSpace(r.URL.Query().Get("localPort")))
			if rErr != nil || lErr != nil || remotePort <= 0 || remotePort > 65535 || localPort <= 0 || localPort > 65535 {
				writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "error": "invalid remotePort or localPort"})
				return
			}
			bridgePort, bErr := m.openUdpBridge(r.Context(), peerIp, remotePort, localPort)
			if bErr != nil {
				writeJSON(w, http.StatusBadGateway, map[string]any{"ok": false, "error": bErr.Error()})
				return
			}
			writeJSON(w, http.StatusOK, map[string]any{"ok": true, "bridgePort": bridgePort})
		case http.MethodDelete:
			port, pErr := strconv.Atoi(strings.TrimSpace(r.URL.Query().Get("port")))
			if pErr != nil || port <= 0 || port > 65535 {
				writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "error": "invalid bridge port"})
				return
			}
			m.closeUdpBridge(port)
			writeJSON(w, http.StatusOK, map[string]any{"ok": true})
		default:
			writeJSON(w, http.StatusMethodNotAllowed, map[string]any{"ok": false, "error": "method not allowed"})
		}
	}))

	mux.HandleFunc("/shutdown", authWrap(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			writeJSON(w, http.StatusMethodNotAllowed, map[string]any{"ok": false, "error": "method not allowed"})
			return
		}
		writeJSON(w, http.StatusOK, map[string]any{"ok": true})
		if f, ok := w.(http.Flusher); ok {
			f.Flush()
		}
		go func() {
			time.Sleep(30 * time.Millisecond)
			if m != nil {
				m.shutdown()
			}
		}()
	}))

	return mux
}

func writeJSON(w http.ResponseWriter, statusCode int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(statusCode)
	_ = json.NewEncoder(w).Encode(v)
}

func runSelfTest() error {
	if deriveUdpForwardTarget("127.0.0.1:49810") != "127.0.0.1:49811" {
		return errors.New("udp target derivation mismatch")
	}
	if sanitizeHostname("My_PC 2026!") != "bobo-my-pc-2026" {
		return errors.New("hostname sanitize mismatch")
	}
	known := map[string]bool{"desk.tail123.ts.net": true}
	if !isAllowedTailnetHost("100.64.0.12", known) || !isAllowedTailnetHost("peer.demo.ts.net", known) {
		return errors.New("valid tailnet destination rejected")
	}
	if isAllowedTailnetHost("192.168.1.10", known) || isAllowedTailnetHost("example.com", known) || isAllowedTailnetHost("127.0.0.1", known) {
		return errors.New("non-tailnet destination allowed")
	}

	// Mock echo server
	echoLn, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return err
	}
	defer echoLn.Close()
	go func() {
		for {
			c, err := echoLn.Accept()
			if err != nil {
				return
			}
			go func(conn net.Conn) {
				defer conn.Close()
				_, _ = io.Copy(conn, conn)
			}(c)
		}
	}()

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	const tok = "test-secret-token-32bytes"
	socksPort, sLn, err := startSocks5Server(
		ctx,
		tok,
		func(c context.Context, _ string) (net.Conn, error) {
			return (&net.Dialer{Timeout: 2 * time.Second}).DialContext(c, "tcp", echoLn.Addr().String())
		},
		func(h string) bool { return isAllowedTailnetHost(h, known) },
	)
	if err != nil {
		return err
	}
	defer sLn.Close()

	// Test SOCKS5 auth + connect + echo round-trip to Tailnet IP
	conn, err := net.DialTimeout("tcp", fmt.Sprintf("127.0.0.1:%d", socksPort), 2*time.Second)
	if err != nil {
		return err
	}
	defer conn.Close()
	_, _ = conn.Write([]byte{0x05, 0x01, 0x02})
	reply := make([]byte, 2)
	if _, err := io.ReadFull(conn, reply); err != nil || reply[0] != 0x05 || reply[1] != 0x02 {
		return errors.New("socks5 method negotiation failed")
	}
	authReq := append([]byte{0x01, byte(len(socksUserName))}, []byte(socksUserName)...)
	authReq = append(authReq, byte(len(tok)))
	authReq = append(authReq, []byte(tok)...)
	_, _ = conn.Write(authReq)
	if _, err := io.ReadFull(conn, reply); err != nil || reply[1] != 0x00 {
		return errors.New("socks5 user/pass failed")
	}
	ip4 := net.ParseIP("100.64.0.12").To4()
	req := []byte{0x05, 0x01, 0x00, 0x01, ip4[0], ip4[1], ip4[2], ip4[3], 0xC2, 0x92} // :49810
	_, _ = conn.Write(req)
	rep := make([]byte, 10)
	if _, err := io.ReadFull(conn, rep); err != nil || rep[1] != 0x00 {
		return errors.New("socks5 connect failed")
	}
	_, _ = conn.Write([]byte("BOBO-OK"))
	echoBuf := make([]byte, 7)
	if _, err := io.ReadFull(conn, echoBuf); err != nil || string(echoBuf) != "BOBO-OK" {
		return errors.New("socks5 echo mismatch")
	}

	// Test HTTP token rejection + status JSON
	mgr := newTsManager(ctx, cancel, "", "test-host", defaultForwardTarget, tok)
	mgr.socksPort = socksPort
	hLn, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return err
	}
	defer hLn.Close()
	hSrv := &http.Server{Handler: buildControlMux(tok, mgr, nil)}
	go func() { _ = hSrv.Serve(hLn) }()
	defer hSrv.Close()

	baseURL := "http://" + hLn.Addr().String()
	unauthResp, err := http.Get(baseURL + "/status")
	if err != nil {
		return err
	}
	unauthResp.Body.Close()
	if unauthResp.StatusCode != http.StatusUnauthorized {
		return errors.New("expected 401 without X-BoBo-Token")
	}

	reqHTTP, _ := http.NewRequest(http.MethodGet, baseURL+"/status", nil)
	reqHTTP.Header.Set("X-BoBo-Token", tok)
	authResp, err := http.DefaultClient.Do(reqHTTP)
	if err != nil {
		return err
	}
	defer authResp.Body.Close()
	if authResp.StatusCode != http.StatusOK {
		return errors.New("expected 200 with valid X-BoBo-Token")
	}
	return nil
}

func main() {
	log.SetOutput(io.Discard)

	stateDir := flag.String("state-dir", "", "tsnet state directory")
	hostname := flag.String("hostname", "", "tailnet node hostname")
	forwardTarget := flag.String("forward-target", defaultForwardTarget, "local BoBoHostServer target host:port")
	parentPID := flag.Int("parent-pid", 0, "parent process PID to monitor")
	autoStart := flag.Bool("auto-start", false, "automatically start tsnet engine if state exists or requested")
	selfTest := flag.Bool("self-test", false, "run offline self-test and exit")
	flag.Parse()

	if *selfTest {
		if err := runSelfTest(); err != nil {
			fmt.Fprintln(os.Stderr, "FAIL:", err)
			os.Exit(1)
		}
		fmt.Println("BoBoTsNet self-test: PASS")
		return
	}

	if strings.TrimSpace(*stateDir) == "" {
		fmt.Fprintln(os.Stderr, "--state-dir is required")
		os.Exit(2)
	}
	if err := os.MkdirAll(*stateDir, 0o700); err != nil {
		fmt.Fprintln(os.Stderr, "mkdir state-dir failed:", err)
		os.Exit(2)
	}

	token := strings.TrimSpace(os.Getenv("BOBO_TSNET_TOKEN"))
	if token == "" {
		fmt.Fprintln(os.Stderr, "BOBO_TSNET_TOKEN is required")
		os.Exit(2)
	}

	appCtx, appCancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer appCancel()

	mgr := newTsManager(appCtx, appCancel, *stateDir, *hostname, *forwardTarget, token)

	socksPort, socksLn, err := startSocks5Server(appCtx, token, mgr.dialTailnet, mgr.isHostAllowed)
	if err != nil {
		fmt.Fprintln(os.Stderr, "socks5 listen failed:", err)
		os.Exit(3)
	}
	defer socksLn.Close()
	mgr.socksPort = socksPort

	httpLn, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		fmt.Fprintln(os.Stderr, "control http listen failed:", err)
		os.Exit(4)
	}
	defer httpLn.Close()
	mgr.controlPort = httpLn.Addr().(*net.TCPAddr).Port

	httpSrv := &http.Server{Handler: buildControlMux(token, mgr, nil)}
	go func() {
		_ = httpSrv.Serve(httpLn)
	}()

	watchParentProcess(*parentPID, func() {
		mgr.shutdown()
	})

	// Immediately notify parent process over stdout (single JSON line) so UI stays responsive.
	readyJSON, _ := json.Marshal(map[string]any{
		"ready":       true,
		"controlPort": mgr.controlPort,
		"socksPort":   mgr.socksPort,
	})
	fmt.Println(string(readyJSON))
	_ = os.Stdout.Sync()

	if *autoStart || hasExistingState(*stateDir) {
		go func() {
			_, _, _ = mgr.ensureStarted()
		}()
	}

	<-appCtx.Done()
	_ = httpSrv.Close()
	mgr.shutdown()
}
