//go:build !windows

package main

func watchParentProcess(pid int, onExit func()) {
	_ = pid
	_ = onExit
}
