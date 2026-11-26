//go:build !linux

package main

type cpuSampler struct{}

func (s *cpuSampler) Sample() float64 {
	return 0
}
