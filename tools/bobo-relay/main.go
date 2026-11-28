package main

import (
	"context"
	"encoding/binary"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"log"
	"math"
	"net"
	"net/http"
	"os"
	"os/signal"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
	"time"
)

const (
	relayVersion    = "7.6.5"
	stunMagicCookie = uint32(0x2112A442)
)

type HealthSnapshot struct {
	OK               bool    `json:"ok"`
	Accepting        bool    `json:"accepting"`
	Load             float64 `json:"load"`
	CPUPct           float64 `json:"cpuPct"`
	ActiveSessions   int64   `json:"activeSessions"`
	MaxSessions      int     `json:"maxSessions"`
	BandwidthMbps    float64 `json:"bandwidthMbps"`
	MaxBandwidthMbps float64 `json:"maxBandwidthMbps"`
	Region           string  `json:"region"`
	UDPPort          int     `json:"udpPort,omitempty"`
	STUNPort         int     `json:"stunPort,omitempty"`
	UptimeSeconds    int64   `json:"uptimeSeconds"`
	Version          string  `json:"version"`
}

type RelayConfig struct {
	ListenAddr       string
	TargetAddr       string
	UDPListenAddr    string
	UDPTargetAddr    string
	STUNListenAddr   string
	HealthListenAddr string
	MaxSessions      int
	MaxBandwidthMbps float64
	Region           string
	DialTimeout      time.Duration
	DrainTimeout     time.Duration
}

type RelayServer struct {
	cfg       RelayConfig
	startedAt time.Time
	accepting atomic.Bool
	active    atomic.Int64

	bytesThisWindow atomic.Int64
	bwMu            sync.Mutex
	bwSamples       [5]uint64
	bwSampleIdx     int
	bwMbps          float64

	cpu       cpuSampler
	ln        net.Listener
	udpConn   *net.UDPConn
	stunConn  *net.UDPConn
	udpPort   int
	stunPort  int
	httpSrv   *http.Server
	httpLn    net.Listener
	stopCh      chan struct{}
	stopOnce    sync.Once
	wg          sync.WaitGroup
	connsMu     sync.Mutex
	connsClosed bool
	activeConns map[net.Conn]struct{}
}

func deriveUdpTargetAddr(tcpTarget, explicitUdp string) string {
	explicitUdp = strings.TrimSpace(explicitUdp)
	if explicitUdp != "" {
		return explicitUdp
	}
	host, portStr, err := net.SplitHostPort(strings.TrimSpace(tcpTarget))
	if err != nil {
		return ""
	}
	p, err := strconv.Atoi(portStr)
	if err != nil || p <= 0 || p >= 65535 {
		return ""
	}
	return net.JoinHostPort(host, strconv.Itoa(p+1))
}

func NewRelayServer(cfg RelayConfig) *RelayServer {
	if cfg.DialTimeout <= 0 {
		cfg.DialTimeout = 5 * time.Second
	}
	if cfg.DrainTimeout <= 0 {
		cfg.DrainTimeout = 10 * time.Second
	}
	s := &RelayServer{
		cfg:         cfg,
		startedAt:   time.Now(),
		stopCh:      make(chan struct{}),
		activeConns: make(map[net.Conn]struct{}),
	}
	s.accepting.Store(true)
	return s
}

func (s *RelayServer) Start() error {
	ln, err := net.Listen("tcp", s.cfg.ListenAddr)
	if err != nil {
		return err
	}
	s.ln = ln

	if strings.TrimSpace(s.cfg.UDPListenAddr) != "" {
		udpTgtStr := deriveUdpTargetAddr(s.cfg.TargetAddr, s.cfg.UDPTargetAddr)
		if udpTgtStr != "" {
			udpTgt, uErr := net.ResolveUDPAddr("udp", udpTgtStr)
			if uErr != nil {
				_ = ln.Close()
				return uErr
			}
			uAddr, uErr := net.ResolveUDPAddr("udp", s.cfg.UDPListenAddr)
			if uErr != nil {
				_ = ln.Close()
				return uErr
			}
			uc, uErr := net.ListenUDP("udp", uAddr)
			if uErr != nil {
				_ = ln.Close()
				return uErr
			}
			s.udpConn = uc
			s.udpPort = uc.LocalAddr().(*net.UDPAddr).Port
			s.wg.Add(1)
			go s.serveUdpRelay(uc, udpTgt)
		}
	}

	if strings.TrimSpace(s.cfg.STUNListenAddr) != "" {
		stAddr, sErr := net.ResolveUDPAddr("udp", s.cfg.STUNListenAddr)
		if sErr != nil {
			if s.udpConn != nil {
				_ = s.udpConn.Close()
			}
			_ = ln.Close()
			return sErr
		}
		sc, sErr := net.ListenUDP("udp", stAddr)
		if sErr != nil {
			if s.udpConn != nil {
				_ = s.udpConn.Close()
			}
			_ = ln.Close()
			return sErr
		}
		s.stunConn = sc
		s.stunPort = sc.LocalAddr().(*net.UDPAddr).Port
		s.wg.Add(1)
		go s.serveStun(sc)
	}

	if strings.TrimSpace(s.cfg.HealthListenAddr) != "" {
		hLn, err := net.Listen("tcp", s.cfg.HealthListenAddr)
		if err != nil {
			if s.stunConn != nil {
				_ = s.stunConn.Close()
			}
			if s.udpConn != nil {
				_ = s.udpConn.Close()
			}
			_ = ln.Close()
			return err
		}
		s.httpLn = hLn
		mux := http.NewServeMux()
		mux.HandleFunc("/bobo-relay/health", s.handleHealth)
		mux.HandleFunc("/health", s.handleHealth)
		s.httpSrv = &http.Server{Handler: mux}
		s.wg.Add(1)
		go func() {
			defer s.wg.Done()
			_ = s.httpSrv.Serve(hLn)
		}()
	}

	s.wg.Add(1)
	go s.bandwidthTicker()

	s.wg.Add(1)
	go s.acceptLoop()

	return nil
}

func (s *RelayServer) RelayAddr() string {
	if s.ln == nil {
		return ""
	}
	return s.ln.Addr().String()
}

func (s *RelayServer) UdpAddr() string {
	if s.udpConn == nil {
		return ""
	}
	return s.udpConn.LocalAddr().String()
}

func (s *RelayServer) StunAddr() string {
	if s.stunConn == nil {
		return ""
	}
	return s.stunConn.LocalAddr().String()
}

func (s *RelayServer) HealthAddr() string {
	if s.httpLn == nil {
		return ""
	}
	return s.httpLn.Addr().String()
}

func (s *RelayServer) SetAccepting(v bool) {
	s.accepting.Store(v)
}

func (s *RelayServer) trackConn(c net.Conn) bool {
	if c == nil {
		return false
	}
	s.connsMu.Lock()
	defer s.connsMu.Unlock()
	if s.connsClosed {
		return false
	}
	if s.activeConns == nil {
		s.activeConns = make(map[net.Conn]struct{})
	}
	s.activeConns[c] = struct{}{}
	return true
}

func (s *RelayServer) untrackConn(c net.Conn) {
	if c == nil {
		return
	}
	s.connsMu.Lock()
	if s.activeConns != nil {
		delete(s.activeConns, c)
	}
	s.connsMu.Unlock()
}

func (s *RelayServer) snapshotActiveConns(markClosed bool) []net.Conn {
	s.connsMu.Lock()
	if markClosed {
		s.connsClosed = true
	}
	conns := make([]net.Conn, 0, len(s.activeConns))
	for c := range s.activeConns {
		conns = append(conns, c)
	}
	s.connsMu.Unlock()
	return conns
}

func (s *RelayServer) Close() {
	s.stopOnce.Do(func() {
		s.accepting.Store(false)
		close(s.stopCh)
		if s.ln != nil {
			_ = s.ln.Close()
		}
		if s.udpConn != nil {
			_ = s.udpConn.Close()
		}
		if s.stunConn != nil {
			_ = s.stunConn.Close()
		}
		if s.httpSrv != nil {
			ctx, cancel := context.WithTimeout(context.Background(), time.Second)
			_ = s.httpSrv.Shutdown(ctx)
			cancel()
		}

		drain := s.cfg.DrainTimeout
		if drain <= 0 {
			drain = 10 * time.Second
		}
		deadline := time.Now().Add(drain)
		for _, c := range s.snapshotActiveConns(false) {
			_ = c.SetDeadline(deadline)
		}

		waitDone := make(chan struct{})
		go func() {
			s.wg.Wait()
			close(waitDone)
		}()

		timer := time.NewTimer(drain)
		defer timer.Stop()
		select {
		case <-waitDone:
		case <-timer.C:
			for _, c := range s.snapshotActiveConns(true) {
				_ = c.Close()
			}
			<-waitDone
		}
	})
}

func (s *RelayServer) bandwidthTicker() {
	defer s.wg.Done()
	ticker := time.NewTicker(time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-s.stopCh:
			return
		case <-ticker.C:
			bytes := s.bytesThisWindow.Swap(0)
			if bytes < 0 {
				bytes = 0
			}
			s.bwMu.Lock()
			s.bwSamples[s.bwSampleIdx] = uint64(bytes)
			s.bwSampleIdx = (s.bwSampleIdx + 1) % len(s.bwSamples)
			var total uint64
			for _, b := range s.bwSamples {
				total += b
			}
			avgBytesPerSec := float64(total) / float64(len(s.bwSamples))
			s.bwMbps = (avgBytesPerSec * 8.0) / 1_000_000.0
			s.bwMu.Unlock()
		}
	}
}

func (s *RelayServer) currentBandwidthMbps() float64 {
	s.bwMu.Lock()
	v := s.bwMbps
	s.bwMu.Unlock()
	if v < 0.001 {
		pending := s.bytesThisWindow.Load()
		if pending > 0 {
			v = (float64(pending) * 8.0) / 1_000_000.0
		}
	}
	return math.Round(v*100.0) / 100.0
}

func computeNormalizedLoad(active int64, maxSessions int, cpuPct float64, bwMbps float64, maxBwMbps float64) float64 {
	load := 0.0
	if cpuPct > 0 {
		if r := cpuPct / 100.0; r > load {
			load = r
		}
	}
	if maxSessions > 0 {
		if r := float64(active) / float64(maxSessions); r > load {
			load = r
		}
	}
	if maxBwMbps > 0 && bwMbps > 0 {
		if r := bwMbps / maxBwMbps; r > load {
			load = r
		}
	}
	if load < 0 {
		return 0
	}
	if load > 1 {
		return 1
	}
	return math.Round(load*1000.0) / 1000.0
}

func (s *RelayServer) Snapshot() HealthSnapshot {
	active := s.active.Load()
	cpuPct := math.Round(s.cpu.Sample()*10.0) / 10.0
	bwMbps := s.currentBandwidthMbps()
	load := computeNormalizedLoad(active, s.cfg.MaxSessions, cpuPct, bwMbps, s.cfg.MaxBandwidthMbps)
	acceptingNow := s.accepting.Load() && (s.cfg.MaxSessions <= 0 || active < int64(s.cfg.MaxSessions))
	return HealthSnapshot{
		OK:               true,
		Accepting:        acceptingNow,
		Load:             load,
		CPUPct:           cpuPct,
		ActiveSessions:   active,
		MaxSessions:      s.cfg.MaxSessions,
		BandwidthMbps:    bwMbps,
		MaxBandwidthMbps: s.cfg.MaxBandwidthMbps,
		Region:           s.cfg.Region,
		UDPPort:          s.udpPort,
		STUNPort:         s.stunPort,
		UptimeSeconds:    int64(time.Since(s.startedAt).Seconds()),
		Version:          relayVersion,
	}
}

func (s *RelayServer) handleHealth(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet && r.Method != http.MethodHead {
		w.WriteHeader(http.StatusMethodNotAllowed)
		return
	}
	snap := s.Snapshot()
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(http.StatusOK)
	if r.Method == http.MethodGet {
		_ = json.NewEncoder(w).Encode(snap)
	}
}

func (s *RelayServer) acceptLoop() {
	defer s.wg.Done()
	for {
		conn, err := s.ln.Accept()
		if err != nil {
			if errors.Is(err, net.ErrClosed) || s.ln == nil {
				return
			}
			select {
			case <-s.stopCh:
				return
			default:
				time.Sleep(25 * time.Millisecond)
				continue
			}
		}
		if !s.accepting.Load() || (s.cfg.MaxSessions > 0 && s.active.Load() >= int64(s.cfg.MaxSessions)) {
			_ = conn.Close()
			continue
		}
		s.active.Add(1)
		s.wg.Add(1)
		go func(c net.Conn) {
			defer s.wg.Done()
			defer s.active.Add(-1)
			s.relayConn(c)
		}(conn)
	}
}

func (s *RelayServer) relayConn(client net.Conn) {
	defer client.Close()
	if !s.trackConn(client) {
		return
	}
	defer s.untrackConn(client)
	if tc, ok := client.(*net.TCPConn); ok {
		_ = tc.SetNoDelay(true)
	}

	dialer := net.Dialer{Timeout: s.cfg.DialTimeout}
	upstream, err := dialer.Dial("tcp", s.cfg.TargetAddr)
	if err != nil {
		return
	}
	defer upstream.Close()
	if !s.trackConn(upstream) {
		return
	}
	defer s.untrackConn(upstream)
	if tc, ok := upstream.(*net.TCPConn); ok {
		_ = tc.SetNoDelay(true)
	}

	var pipeWg sync.WaitGroup
	pipeWg.Add(2)
	copySide := func(dst, src net.Conn) {
		defer pipeWg.Done()
		buf := make([]byte, 32*1024)
		for {
			n, rErr := src.Read(buf)
			if n > 0 {
				s.bytesThisWindow.Add(int64(n))
				written := 0
				for written < n {
					w, wErr := dst.Write(buf[written:n])
					if wErr != nil {
						return
					}
					written += w
				}
			}
			if rErr != nil {
				break
			}
		}
		if tc, ok := dst.(interface{ CloseWrite() error }); ok {
			_ = tc.CloseWrite()
		}
	}
	go copySide(upstream, client)
	go copySide(client, upstream)
	pipeWg.Wait()
}

type udpRelayClient struct {
	clientAddr *net.UDPAddr
	upConn     *net.UDPConn
	lastUnix   atomic.Int64
	closeOnce  sync.Once
}

func (c *udpRelayClient) close() {
	c.closeOnce.Do(func() {
		if c.upConn != nil {
			_ = c.upConn.Close()
		}
	})
}

func (s *RelayServer) serveUdpRelay(uc *net.UDPConn, target *net.UDPAddr) {
	defer s.wg.Done()
	var mu sync.Mutex
	clients := make(map[string]*udpRelayClient)
	maxUdpClients := 128
	if s.cfg.MaxSessions > 0 && s.cfg.MaxSessions*2 > maxUdpClients {
		maxUdpClients = s.cfg.MaxSessions * 2
	}

	reapStop := make(chan struct{})
	defer func() {
		close(reapStop)
		mu.Lock()
		for k, c := range clients {
			c.close()
			delete(clients, k)
		}
		mu.Unlock()
	}()

	go func() {
		ticker := time.NewTicker(15 * time.Second)
		defer ticker.Stop()
		for {
			select {
			case <-reapStop:
				return
			case <-s.stopCh:
				return
			case <-ticker.C:
				cutoff := time.Now().Unix() - 90
				mu.Lock()
				for k, c := range clients {
					if c.lastUnix.Load() < cutoff {
						c.close()
						delete(clients, k)
					}
				}
				mu.Unlock()
			}
		}
	}()

	buf := make([]byte, 2048)
	for {
		n, clientAddr, err := uc.ReadFromUDP(buf)
		if err != nil {
			select {
			case <-s.stopCh:
				return
			default:
				if errors.Is(err, net.ErrClosed) {
					return
				}
				continue
			}
		}
		if n <= 0 || clientAddr == nil {
			continue
		}
		s.bytesThisWindow.Add(int64(n))
		key := clientAddr.String()
		nowUnix := time.Now().Unix()

		mu.Lock()
		cl := clients[key]
		if cl == nil {
			if len(clients) >= maxUdpClients {
				mu.Unlock()
				continue
			}
			upConn, dErr := net.DialUDP("udp", nil, target)
			if dErr != nil {
				mu.Unlock()
				continue
			}
			cl = &udpRelayClient{
				clientAddr: clientAddr,
				upConn:     upConn,
			}
			cl.lastUnix.Store(nowUnix)
			clients[key] = cl
			mu.Unlock()

			s.wg.Add(1)
			go func(k string, ent *udpRelayClient) {
				defer s.wg.Done()
				defer func() {
					ent.close()
					mu.Lock()
					if clients[k] == ent {
						delete(clients, k)
					}
					mu.Unlock()
				}()
				rbuf := make([]byte, 2048)
				for {
					rn, rErr := ent.upConn.Read(rbuf)
					if rErr != nil {
						return
					}
					if rn > 0 {
						ent.lastUnix.Store(time.Now().Unix())
						s.bytesThisWindow.Add(int64(rn))
						if _, wErr := uc.WriteToUDP(rbuf[:rn], ent.clientAddr); wErr != nil {
							return
						}
					}
				}
			}(key, cl)
		} else {
			cl.lastUnix.Store(nowUnix)
			mu.Unlock()
		}
		_, _ = cl.upConn.Write(buf[:n])
	}
}

func (s *RelayServer) serveStun(sc *net.UDPConn) {
	defer s.wg.Done()
	buf := make([]byte, 1500)
	for {
		n, peer, err := sc.ReadFromUDP(buf)
		if err != nil {
			select {
			case <-s.stopCh:
				return
			default:
				if errors.Is(err, net.ErrClosed) {
					return
				}
				continue
			}
		}
		// RFC 5389 Binding Request: >=20 bytes, msgType 0x0001, magic cookie 0x2112A442
		if n < 20 || peer == nil || peer.IP == nil {
			continue
		}
		if binary.BigEndian.Uint16(buf[0:2]) != 0x0001 || binary.BigEndian.Uint32(buf[4:8]) != stunMagicCookie {
			continue
		}
		ip4 := peer.IP.To4()
		var resp [44]byte
		binary.BigEndian.PutUint16(resp[0:2], 0x0101) // Binding Success Response
		binary.BigEndian.PutUint32(resp[4:8], stunMagicCookie)
		copy(resp[8:20], buf[8:20]) // Transaction ID
		binary.BigEndian.PutUint16(resp[20:22], 0x0020) // XOR-MAPPED-ADDRESS
		xorPort := uint16(peer.Port) ^ uint16(stunMagicCookie>>16)

		var totalLen int
		if ip4 != nil {
			binary.BigEndian.PutUint16(resp[2:4], 12)   // attr header (4) + body (8)
			binary.BigEndian.PutUint16(resp[22:24], 8)
			resp[24] = 0
			resp[25] = 0x01 // IPv4
			binary.BigEndian.PutUint16(resp[26:28], xorPort)
			for i := 0; i < 4; i++ {
				resp[28+i] = ip4[i] ^ resp[4+i]
			}
			totalLen = 32
		} else {
			ip6 := peer.IP.To16()
			if ip6 == nil {
				continue
			}
			binary.BigEndian.PutUint16(resp[2:4], 24)   // attr header (4) + body (20)
			binary.BigEndian.PutUint16(resp[22:24], 20)
			resp[24] = 0
			resp[25] = 0x02 // IPv6
			binary.BigEndian.PutUint16(resp[26:28], xorPort)
			for i := 0; i < 16; i++ {
				resp[28+i] = ip6[i] ^ resp[4+i]
			}
			totalLen = 44
		}
		_, _ = sc.WriteToUDP(resp[:totalLen], peer)
	}
}

func main() {
	listenAddr := flag.String("listen", ":49810", "TCP listen address forwarded to -target")
	targetAddr := flag.String("target", "", "upstream BoBo host address host:port (required)")
	udpListenAddr := flag.String("udp-listen", ":49811", "UDP blind relay listen address (empty to disable)")
	udpTargetAddr := flag.String("udp-target", "", "upstream BoBo UDP host:port (default derived as target port+1)")
	stunListenAddr := flag.String("stun-listen", ":49812", "built-in RFC 5389 STUN UDP listen address (empty to disable)")
	healthAddr := flag.String("health-listen", ":49811", "HTTP load/health listen address (empty to disable)")
	maxSessions := flag.Int("max-sessions", 64, "maximum concurrent relayed sessions (<=0 unlimited)")
	maxBwMbps := flag.Float64("max-bandwidth-mbps", 200.0, "reference relay bandwidth capacity in Mbps")
	region := flag.String("region", "", "optional region label reported in /bobo-relay/health")
	drainTimeout := flag.Duration("drain-timeout", 10*time.Second, "max wait for active TCP relay sessions during graceful shutdown")
	showVer := flag.Bool("version", false, "print version and exit")
	flag.Parse()

	if *showVer {
		fmt.Println("bobo-relay " + relayVersion)
		return
	}
	if strings.TrimSpace(*targetAddr) == "" {
		_, _ = fmt.Fprintln(os.Stderr, "error: -target <host:port> is required")
		os.Exit(2)
	}

	srv := NewRelayServer(RelayConfig{
		ListenAddr:       *listenAddr,
		TargetAddr:       strings.TrimSpace(*targetAddr),
		UDPListenAddr:    strings.TrimSpace(*udpListenAddr),
		UDPTargetAddr:    strings.TrimSpace(*udpTargetAddr),
		STUNListenAddr:   strings.TrimSpace(*stunListenAddr),
		HealthListenAddr: *healthAddr,
		MaxSessions:      *maxSessions,
		MaxBandwidthMbps: *maxBwMbps,
		Region:           strings.TrimSpace(*region),
		DrainTimeout:     *drainTimeout,
	})
	if err := srv.Start(); err != nil {
		log.Fatalf("start bobo-relay failed: %v", err)
	}
	log.Printf("bobo-relay %s tcp=%s udp=%s stun=%s -> target=%s health=%s max_sessions=%d max_bw=%.0fMbps",
		relayVersion, srv.RelayAddr(), srv.UdpAddr(), srv.StunAddr(), strings.TrimSpace(*targetAddr), srv.HealthAddr(), *maxSessions, *maxBwMbps)

	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, os.Interrupt, syscall.SIGTERM)
	<-sigCh

	log.Println("bobo-relay entering drain mode (accepting=false)...")
	srv.SetAccepting(false)
	time.Sleep(1500 * time.Millisecond)
	srv.Close()
}
