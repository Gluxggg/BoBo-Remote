package main

import (
	"context"
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
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
	"time"
)

const relayVersion = "7.2.0"

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
	UptimeSeconds    int64   `json:"uptimeSeconds"`
	Version          string  `json:"version"`
}

type RelayConfig struct {
	ListenAddr       string
	TargetAddr       string
	HealthListenAddr string
	MaxSessions      int
	MaxBandwidthMbps float64
	Region           string
	DialTimeout      time.Duration
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
	httpSrv   *http.Server
	httpLn    net.Listener
	stopCh    chan struct{}
	stopOnce  sync.Once
	wg        sync.WaitGroup
}

func NewRelayServer(cfg RelayConfig) *RelayServer {
	if cfg.DialTimeout <= 0 {
		cfg.DialTimeout = 5 * time.Second
	}
	s := &RelayServer{
		cfg:       cfg,
		startedAt: time.Now(),
		stopCh:    make(chan struct{}),
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

	if strings.TrimSpace(s.cfg.HealthListenAddr) != "" {
		hLn, err := net.Listen("tcp", s.cfg.HealthListenAddr)
		if err != nil {
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

func (s *RelayServer) HealthAddr() string {
	if s.httpLn == nil {
		return ""
	}
	return s.httpLn.Addr().String()
}

func (s *RelayServer) SetAccepting(v bool) {
	s.accepting.Store(v)
}

func (s *RelayServer) Close() {
	s.stopOnce.Do(func() {
		s.accepting.Store(false)
		close(s.stopCh)
		if s.ln != nil {
			_ = s.ln.Close()
		}
		if s.httpSrv != nil {
			ctx, cancel := context.WithTimeout(context.Background(), time.Second)
			_ = s.httpSrv.Shutdown(ctx)
			cancel()
		}
		s.wg.Wait()
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
	if tc, ok := client.(*net.TCPConn); ok {
		_ = tc.SetNoDelay(true)
	}

	dialer := net.Dialer{Timeout: s.cfg.DialTimeout}
	upstream, err := dialer.Dial("tcp", s.cfg.TargetAddr)
	if err != nil {
		return
	}
	defer upstream.Close()
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

func main() {
	listenAddr := flag.String("listen", ":49810", "TCP listen address forwarded to -target")
	targetAddr := flag.String("target", "", "upstream BoBo host address host:port (required)")
	healthAddr := flag.String("health-listen", ":49811", "HTTP load/health listen address (empty to disable)")
	maxSessions := flag.Int("max-sessions", 64, "maximum concurrent relayed sessions (<=0 unlimited)")
	maxBwMbps := flag.Float64("max-bandwidth-mbps", 200.0, "reference relay bandwidth capacity in Mbps")
	region := flag.String("region", "", "optional region label reported in /bobo-relay/health")
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
		HealthListenAddr: *healthAddr,
		MaxSessions:      *maxSessions,
		MaxBandwidthMbps: *maxBwMbps,
		Region:           strings.TrimSpace(*region),
	})
	if err := srv.Start(); err != nil {
		log.Fatalf("start bobo-relay failed: %v", err)
	}
	log.Printf("bobo-relay %s listening=%s -> target=%s health=%s max_sessions=%d max_bw=%.0fMbps",
		relayVersion, srv.RelayAddr(), strings.TrimSpace(*targetAddr), srv.HealthAddr(), *maxSessions, *maxBwMbps)

	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, os.Interrupt, syscall.SIGTERM)
	<-sigCh

	log.Println("bobo-relay entering drain mode (accepting=false)...")
	srv.SetAccepting(false)
	time.Sleep(1500 * time.Millisecond)
	srv.Close()
}
