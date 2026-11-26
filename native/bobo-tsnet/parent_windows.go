//go:build windows

package main

import (
	"golang.org/x/sys/windows"
)

func watchParentProcess(pid int, onExit func()) {
	if pid <= 0 {
		return
	}
	go func() {
		h, err := windows.OpenProcess(windows.SYNCHRONIZE, false, uint32(pid))
		if err != nil {
			// Parent already gone or inaccessible; trigger exit cleanly if process isn't alive.
			onExit()
			return
		}
		defer windows.CloseHandle(h)
		_, _ = windows.WaitForSingleObject(h, windows.INFINITE)
		onExit()
	}()
}
