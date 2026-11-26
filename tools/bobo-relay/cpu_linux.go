//go:build linux

package main

import (
	"os"
	"strconv"
	"strings"
	"sync"
)

type cpuSampler struct {
	mu       sync.Mutex
	hasPrev  bool
	prevIdle uint64
	prevTot  uint64
	lastPct  float64
}

func (s *cpuSampler) Sample() float64 {
	raw, err := os.ReadFile("/proc/stat")
	if err != nil {
		return 0
	}
	lineEnd := strings.IndexByte(string(raw), '\n')
	line := string(raw)
	if lineEnd >= 0 {
		line = line[:lineEnd]
	}
	fields := strings.Fields(line)
	if len(fields) < 5 || fields[0] != "cpu" {
		return 0
	}
	var total uint64
	var idle uint64
	for i := 1; i < len(fields); i++ {
		val, err := strconv.ParseUint(fields[i], 10, 64)
		if err != nil {
			return 0
		}
		total += val
		if i == 4 || i == 5 { // idle + iowait
			idle += val
		}
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	if !s.hasPrev {
		s.hasPrev = true
		s.prevIdle = idle
		s.prevTot = total
		return s.lastPct
	}
	dtTotal := total - s.prevTot
	dtIdle := idle - s.prevIdle
	s.prevIdle = idle
	s.prevTot = total
	if dtTotal == 0 {
		return s.lastPct
	}
	pct := (float64(dtTotal-dtIdle) / float64(dtTotal)) * 100.0
	if pct < 0 {
		pct = 0
	} else if pct > 100 {
		pct = 100
	}
	s.lastPct = pct
	return pct
}
