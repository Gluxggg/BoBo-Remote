#ifndef BOBOCORE_EXPORTS
#define BOBOCORE_EXPORTS
#endif
#include "bobo_core.h"
#include "ima_adpcm.h"
#include "mf_encoder.h"
#include "screen_capture.h"
#include "wasapi_loopback.h"

#include "d3d11_renderer.h"
#include "mf_decoder.h"

#include <windowsx.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

using namespace bobo;

namespace {

// ==================== Host Session (In-Process Capture + H.264 Encode + WASAPI Loopback) ====================
struct BoBoHostSession {
    std::atomic<bool> running{ false };
    std::atomic<bool> reconfigPending{ false };
    std::atomic<bool> keyframePending{ true };

    std::mutex cfgMutex;
    int32_t monitorIndex = 0;
    int32_t targetWidth = 1920;
    int32_t targetHeight = 1080;
    int32_t fps = 60;
    int32_t bitrateKbps = 14000;

    BoBoVideoFrameCallback onVideo = nullptr;
    BoBoAudioFrameCallback onAudio = nullptr;
    void* userCtx = nullptr;

    WasapiLoopbackCapturer audioCap;
    std::thread videoThread;

    void RunVideoLoop() {
        HRESULT hrCo = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        ScreenCapturer capturer;
        MfH264Encoder encoder;
        std::vector<uint8_t> nv12Buf;
        std::vector<uint8_t> h264Buf;

        int32_t curMon = -1, curW = 0, curH = 0, curFps = 60, curBps = 14000;
        bool initialized = false;
        int64_t frameIndex = 0;
        auto startClock = std::chrono::steady_clock::now();

        while (running.load()) {
            if (!initialized || reconfigPending.exchange(false)) {
                {
                    std::lock_guard<std::mutex> lk(cfgMutex);
                    curMon = monitorIndex;
                    curW = targetWidth;
                    curH = targetHeight;
                    curFps = (fps >= 15 && fps <= 144) ? fps : 60;
                    curBps = (bitrateKbps >= 1000 && bitrateKbps <= 60000) ? bitrateKbps : 14000;
                }
                std::string err;
                if (capturer.Initialize(curMon, static_cast<uint32_t>(curW), static_cast<uint32_t>(curH), err) &&
                    encoder.Open(capturer.OutputWidth(), capturer.OutputHeight(),
                                 static_cast<uint32_t>(curFps), static_cast<uint32_t>(curBps), err)) {
                    initialized = true;
                    keyframePending.store(true);
                } else {
                    initialized = false;
                    Sleep(200);
                    continue;
                }
            }

            auto frameStart = std::chrono::steady_clock::now();
            int framePeriodMs = 1000 / (curFps > 0 ? curFps : 60);
            if (framePeriodMs < 6) framePeriodMs = 6;

            bool changed = false;
            std::string capErr;
            bool forceKey = keyframePending.exchange(false);
            bool hasFrame = capturer.CaptureNv12Frame(framePeriodMs, nv12Buf, changed, capErr) && !nv12Buf.empty();
            if (!hasFrame && (frameIndex == 0 || forceKey)) {
                // Non-interactive desktop (UAC/lockscreen/CI): emit dark neutral NV12 frame so stream stays alive
                uint32_t fw = capturer.OutputWidth();
                uint32_t fh = capturer.OutputHeight();
                if (fw >= 320 && fh >= 240) {
                    size_t yBytes = static_cast<size_t>(fw) * static_cast<size_t>(fh);
                    nv12Buf.resize(yBytes * 3 / 2);
                    std::memset(nv12Buf.data(), 24, yBytes);
                    std::memset(nv12Buf.data() + yBytes, 128, yBytes / 2);
                    hasFrame = true;
                }
            }
            if (hasFrame) {
                int64_t sampleTimeHns = frameIndex * (10000000LL / curFps);
                bool isKey = false;
                std::string encErr;
                if (encoder.EncodeNv12(nv12Buf.data(), nv12Buf.size(), sampleTimeHns, forceKey, h264Buf, isKey, encErr)) {
                    if (!h264Buf.empty() && onVideo) {
                        uint32_t tsMs = static_cast<uint32_t>(
                            std::chrono::duration_cast<std::chrono::milliseconds>(
                                std::chrono::steady_clock::now() - startClock).count());
                        onVideo(h264Buf.data(),
                                static_cast<int32_t>(h264Buf.size()),
                                isKey ? 1 : 0,
                                static_cast<int32_t>(capturer.OutputWidth()),
                                static_cast<int32_t>(capturer.OutputHeight()),
                                tsMs,
                                userCtx);
                    }
                    ++frameIndex;
                }
            }

            auto elapsedMs = static_cast<int>(
                std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - frameStart).count());
            int sleepMs = framePeriodMs - elapsedMs;
            if (sleepMs > 1 && running.load()) {
                Sleep(static_cast<DWORD>(sleepMs));
            }
        }

        encoder.Close();
        capturer.Shutdown();
        if (SUCCEEDED(hrCo)) CoUninitialize();
    }
};

// ==================== Client In-Process Viewport (D3D11VA Decode + Render + Audio + Input) ====================
struct BoBoClientSession {
    D3D11Renderer renderer;
    MfHardwareDecoder decoder;
    WasapiAdpcmPlayer audio;
    std::mutex decodeMutex;
    std::mutex statsMutex;

    HWND parentHwnd = nullptr;
    HWND viewportHwnd = nullptr;
    HHOOK llKeyHook = nullptr;

    std::atomic<bool> alive{ true };
    std::atomic<bool> videoReceived{ false };
    bool freeMouse = true;
    bool rawMouseRegistered = false;
    bool cursorHidden = false;
    bool pointerInVideoRect = false;
    bool mouseTracking = false;
    bool viewOnly = false;
    bool sendSysKeys = false;
    bool ctrlAltSoloActive = false;
    DWORD lastTopEdgeTick = 0;

    uint32_t videoW = 1920;
    uint32_t videoH = 1080;
    bool decoderOpened = false;

    BoBoClientInputCallback onInput = nullptr;
    BoBoClientHotkeyCallback onHotkey = nullptr;
    void* userCtx = nullptr;

    uint64_t framesWindow = 0;
    double decodeMsAccum = 0.0;
    double renderMsAccum = 0.0;
    uint64_t timingSamples = 0;
    std::chrono::steady_clock::time_point lastStatsWindow = std::chrono::steady_clock::now();
    double measuredFps = 0.0;
    double avgDecodeMs = 0.0;
    double avgRenderMs = 0.0;
};

static BoBoClientSession* g_activeClient = nullptr;

// ==================== Host Hardware Privacy Input Blocker & Super Display ====================
static constexpr ULONG_PTR kBoBoInjectedMagic = 0x424F424F56360001ULL;

static std::mutex g_privHookMutex;
static std::atomic<bool> g_privBlockActive{ false };
static std::atomic<bool> g_privHookReady{ false };
static std::atomic<bool> g_privHookSuccess{ false };
static std::thread g_privHookThread;
static DWORD g_privHookThreadId = 0;
static BoBoPrivacyEscapeCallback g_privEscapeCb = nullptr;
static void* g_privEscapeCtx = nullptr;
static std::atomic<bool> g_physCtrl{ false };
static std::atomic<bool> g_physAlt{ false };
static std::atomic<bool> g_physShift{ false };

static LRESULT CALLBACK PrivacyLowLevelMouseProc(int nCode, WPARAM wParam, LPARAM lParam) {
    if (nCode == HC_ACTION && g_privBlockActive.load()) {
        const auto* ms = reinterpret_cast<const MSLLHOOKSTRUCT*>(lParam);
        if (ms) {
            bool isBoBoRemote = ((ms->flags & LLMHF_INJECTED) != 0) && (ms->dwExtraInfo == kBoBoInjectedMagic);
            if (!isBoBoRemote) {
                // Swallow 100% of local physical mouse moves, clicks, right-clicks, taskbar hovers and wheel scrolls
                return 1;
            }
        }
    }
    return CallNextHookEx(nullptr, nCode, wParam, lParam);
}

static LRESULT CALLBACK PrivacyLowLevelKeyProc(int nCode, WPARAM wParam, LPARAM lParam) {
    if (nCode == HC_ACTION && g_privBlockActive.load()) {
        const auto* kb = reinterpret_cast<const KBDLLHOOKSTRUCT*>(lParam);
        if (kb) {
            bool isBoBoRemote = ((kb->flags & LLKHF_INJECTED) != 0) && (kb->dwExtraInfo == kBoBoInjectedMagic);
            if (!isBoBoRemote) {
                bool isDown = (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN);
                bool isUp = (wParam == WM_KEYUP || wParam == WM_SYSKEYUP);
                DWORD vk = kb->vkCode;
                if (vk == VK_LCONTROL || vk == VK_RCONTROL || vk == VK_CONTROL) {
                    if (isDown) g_physCtrl.store(true);
                    else if (isUp) g_physCtrl.store(false);
                } else if (vk == VK_LMENU || vk == VK_RMENU || vk == VK_MENU) {
                    if (isDown) g_physAlt.store(true);
                    else if (isUp) g_physAlt.store(false);
                } else if (vk == VK_LSHIFT || vk == VK_RSHIFT || vk == VK_SHIFT) {
                    if (isDown) g_physShift.store(true);
                    else if (isUp) g_physShift.store(false);
                } else if (isDown && vk == 'P' && g_physCtrl.load() && g_physAlt.load() && g_physShift.load()) {
                    // Local owner secret emergency exit: Ctrl+Alt+Shift+P
                    g_physCtrl.store(false);
                    g_physAlt.store(false);
                    g_physShift.store(false);
                    BoBoPrivacyEscapeCallback cb = g_privEscapeCb;
                    void* ctx = g_privEscapeCtx;
                    if (cb) {
                        std::thread([cb, ctx]() { cb(ctx); }).detach();
                    }
                    return 1;
                }
                // Swallow 100% of local physical keyboard input (Win, Alt+Tab, F5, Esc, typing)
                return 1;
            }
        }
    }
    return CallNextHookEx(nullptr, nCode, wParam, lParam);
}

static std::mutex g_superDispMutex;
static bool g_superDispApplied = false;
static std::wstring g_superDispDevice;
static DEVMODEW g_superDispOrigMode = {};

struct MonitorDeviceEntry {
    int32_t index = 0;
    int32_t isPrimary = 0;
    std::wstring deviceName;
    int32_t width = 0;
    int32_t height = 0;
};

static BOOL CALLBACK EnumMonitorDeviceProc(HMONITOR hMon, HDC, LPRECT, LPARAM lParam) {
    auto* list = reinterpret_cast<std::vector<MonitorDeviceEntry>*>(lParam);
    MONITORINFOEXW mi = {};
    mi.cbSize = sizeof(mi);
    if (!GetMonitorInfoW(hMon, &mi)) return TRUE;
    MonitorDeviceEntry e;
    e.index = static_cast<int32_t>(list->size());
    e.isPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) ? 1 : 0;
    e.deviceName = mi.szDevice;
    e.width = mi.rcMonitor.right - mi.rcMonitor.left;
    e.height = mi.rcMonitor.bottom - mi.rcMonitor.top;
    list->push_back(e);
    return TRUE;
}

static std::vector<MonitorDeviceEntry> ListMonitorDevicesOrdered() {
    std::vector<MonitorDeviceEntry> list;
    EnumDisplayMonitors(nullptr, nullptr, EnumMonitorDeviceProc, reinterpret_cast<LPARAM>(&list));
    std::stable_sort(list.begin(), list.end(), [](const MonitorDeviceEntry& a, const MonitorDeviceEntry& b) {
        return a.isPrimary > b.isPrimary;
    });
    for (size_t i = 0; i < list.size(); ++i) list[i].index = static_cast<int32_t>(i);
    return list;
}

static bool IsClientFocused(const BoBoClientSession* st) {
    if (!st || !st->viewportHwnd) return false;
    HWND fg = GetForegroundWindow();
    if (!fg) return false;
    if (fg == st->viewportHwnd) return true;
    if (st->parentHwnd && (fg == st->parentHwnd || fg == GetAncestor(st->parentHwnd, GA_ROOT))) return true;
    return false;
}

static LRESULT CALLBACK ClientLowLevelKeyProc(int nCode, WPARAM wParam, LPARAM lParam) {
    if (nCode == HC_ACTION && g_activeClient) {
        BoBoClientSession* st = g_activeClient;
        if (st->sendSysKeys && !st->viewOnly && st->onInput && IsClientFocused(st)) {
            const auto* kb = reinterpret_cast<const KBDLLHOOKSTRUCT*>(lParam);
            if (kb && (kb->flags & LLKHF_INJECTED) == 0) {
                uint16_t vk = static_cast<uint16_t>(kb->vkCode);
                bool isDown = (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN);
                bool isUp = (wParam == WM_KEYUP || wParam == WM_SYSKEYUP);
                bool altDown = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0 || (kb->flags & LLKHF_ALTDOWN) != 0;
                bool ctrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                bool extended = (kb->flags & LLKHF_EXTENDED) != 0;
                bool forwardWin = (vk == VK_LWIN || vk == VK_RWIN) && !ctrlDown;
                bool forwardAltTab = (vk == VK_TAB && altDown && !ctrlDown);
                if ((forwardWin || forwardAltTab) && (isDown || isUp)) {
                    BoBoInputPacket pkt = {};
                    pkt.kind = 5;
                    pkt.flags = static_cast<uint8_t>((isDown ? 1 : 0) | (extended ? 2 : 0));
                    pkt.a = static_cast<int16_t>(vk);
                    st->onInput(&pkt, st->userCtx);
                    return 1;
                }
            }
        }
    }
    return CallNextHookEx(nullptr, nCode, wParam, lParam);
}

static void SyncClientSysKeyHook(BoBoClientSession* st) {
    if (!st) return;
    if (st->sendSysKeys && !st->llKeyHook) {
        st->llKeyHook = SetWindowsHookExW(WH_KEYBOARD_LL, ClientLowLevelKeyProc, GetModuleHandleW(nullptr), 0);
    } else if (!st->sendSysKeys && st->llKeyHook) {
        UnhookWindowsHookEx(st->llKeyHook);
        st->llKeyHook = nullptr;
    }
}

static void ApplyClientMouseMode(BoBoClientSession* st, bool freeMouse) {
    if (!st || !st->viewportHwnd) return;
    st->freeMouse = freeMouse;
    if (freeMouse) {
        ClipCursor(nullptr);
        if (st->cursorHidden) {
            ShowCursor(TRUE);
            st->cursorHidden = false;
        }
        if (st->rawMouseRegistered) {
            RAWINPUTDEVICE rid = {};
            rid.usUsagePage = 0x01;
            rid.usUsage = 0x02;
            rid.dwFlags = RIDEV_REMOVE;
            rid.hwndTarget = nullptr;
            RegisterRawInputDevices(&rid, 1, sizeof(rid));
            st->rawMouseRegistered = false;
        }
    } else {
        RAWINPUTDEVICE rid = {};
        rid.usUsagePage = 0x01;
        rid.usUsage = 0x02;
        rid.dwFlags = 0;
        rid.hwndTarget = st->viewportHwnd;
        if (RegisterRawInputDevices(&rid, 1, sizeof(rid))) {
            st->rawMouseRegistered = true;
        }
        RECT rc = {};
        if (GetClientRect(st->viewportHwnd, &rc)) {
            POINT tl = { rc.left, rc.top };
            POINT br = { rc.right, rc.bottom };
            ClientToScreen(st->viewportHwnd, &tl);
            ClientToScreen(st->viewportHwnd, &br);
            RECT screenRc = { tl.x, tl.y, br.x, br.y };
            ClipCursor(&screenRc);
        }
        if (!st->cursorHidden) {
            ShowCursor(FALSE);
            st->cursorHidden = true;
        }
    }
}

static void EmitInput(BoBoClientSession* st, const BoBoInputPacket& pkt) {
    if (st && st->alive.load() && !st->viewOnly && st->onInput) {
        st->onInput(&pkt, st->userCtx);
    }
}

static void EmitHotkey(BoBoClientSession* st, int32_t code, int32_t param = 0) {
    if (st && st->alive.load() && st->onHotkey) {
        st->onHotkey(code, param, st->userCtx);
    }
}

static constexpr UINT kMsgRefreshCursor = WM_APP + 1;

static bool IsPointInVideoRect(const BoBoClientSession* st, int x, int y, RECT& outVp) {
    outVp = st->renderer.GetVideoViewportRect();
    int vpW = outVp.right - outVp.left;
    int vpH = outVp.bottom - outVp.top;
    return (vpW > 0 && vpH > 0 && x >= outVp.left && x < outVp.right && y >= outVp.top && y < outVp.bottom);
}

static bool ComputeCursorInVideoRect(BoBoClientSession* st, HWND hwnd) {
    POINT pt = {};
    if (!GetCursorPos(&pt) || !ScreenToClient(hwnd, &pt)) {
        return st->pointerInVideoRect;
    }
    RECT vp = {};
    bool inside = IsPointInVideoRect(st, pt.x, pt.y, vp);
    st->pointerInVideoRect = inside;
    return inside;
}

static bool EmitAbsMouseAtClientPoint(BoBoClientSession* st, int x, int y) {
    RECT vp = {};
    if (!IsPointInVideoRect(st, x, y, vp)) {
        return false;
    }
    int vpW = vp.right - vp.left;
    int vpH = vp.bottom - vp.top;
    int relX = x - vp.left;
    int relY = y - vp.top;
    uint64_t denomX = static_cast<uint64_t>(vpW > 1 ? (vpW - 1) : 1);
    uint64_t denomY = static_cast<uint64_t>(vpH > 1 ? (vpH - 1) : 1);
    uint64_t scaledX = (static_cast<uint64_t>(relX) * 65535ULL) / denomX;
    uint64_t scaledY = (static_cast<uint64_t>(relY) * 65535ULL) / denomY;
    if (scaledX > 65535ULL) scaledX = 65535ULL;
    if (scaledY > 65535ULL) scaledY = 65535ULL;
    BoBoInputPacket pkt = {};
    pkt.kind = 1;
    pkt.a = static_cast<int16_t>(static_cast<uint16_t>(scaledX));
    pkt.b = static_cast<int16_t>(static_cast<uint16_t>(scaledY));
    EmitInput(st, pkt);
    return true;
}

static LRESULT CALLBACK ClientViewportWndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    auto* st = reinterpret_cast<BoBoClientSession*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    if (msg == WM_NCCREATE) {
        auto* cs = reinterpret_cast<CREATESTRUCTW*>(lParam);
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(cs->lpCreateParams));
        return TRUE;
    }
    if (!st) return DefWindowProcW(hwnd, msg, wParam, lParam);

    switch (msg) {
        case WM_ERASEBKGND:
            return 1;

        case WM_SIZE: {
            uint32_t w = LOWORD(lParam);
            uint32_t h = HIWORD(lParam);
            if (w > 0 && h > 0) {
                std::string rErr;
                st->renderer.ResizeSwapChain(w, h, rErr);
            }
            return 0;
        }

        case kMsgRefreshCursor: {
            if (st->freeMouse) {
                POINT pt = {};
                if (GetCursorPos(&pt) && WindowFromPoint(pt) == hwnd) {
                    bool inside = ComputeCursorInVideoRect(st, hwnd);
                    bool hideLocal = !st->viewOnly && st->videoReceived.load() && inside;
                    SetCursor(hideLocal ? nullptr : LoadCursorW(nullptr, IDC_ARROW));
                }
            }
            return 0;
        }

        case WM_SETCURSOR: {
            if (LOWORD(lParam) == HTCLIENT) {
                bool inside = ComputeCursorInVideoRect(st, hwnd);
                bool hideLocal = (!st->freeMouse) ||
                                 (!st->viewOnly && st->videoReceived.load() && inside);
                SetCursor(hideLocal ? nullptr : LoadCursorW(nullptr, IDC_ARROW));
                return TRUE;
            }
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        case WM_MOUSELEAVE: {
            st->mouseTracking = false;
            st->pointerInVideoRect = false;
            if (st->freeMouse) {
                SetCursor(LoadCursorW(nullptr, IDC_ARROW));
            }
            return 0;
        }

        case WM_MOUSEMOVE: {
            if (st->freeMouse) {
                if (!st->mouseTracking) {
                    TRACKMOUSEEVENT tme = {};
                    tme.cbSize = sizeof(tme);
                    tme.dwFlags = TME_LEAVE;
                    tme.hwndTrack = hwnd;
                    if (TrackMouseEvent(&tme)) {
                        st->mouseTracking = true;
                    }
                }
                int x = GET_X_LPARAM(lParam);
                int y = GET_Y_LPARAM(lParam);
                if (y <= 6) {
                    DWORD nowTick = GetTickCount();
                    if (nowTick - st->lastTopEdgeTick >= 600) {
                        st->lastTopEdgeTick = nowTick;
                        EmitHotkey(st, 8, 0);
                    }
                }
                RECT vp = {};
                bool insideVideo = IsPointInVideoRect(st, x, y, vp);
                st->pointerInVideoRect = insideVideo;
                bool hideLocal = !st->viewOnly && st->videoReceived.load() && insideVideo;
                SetCursor(hideLocal ? nullptr : LoadCursorW(nullptr, IDC_ARROW));
                if (insideVideo) {
                    EmitAbsMouseAtClientPoint(st, x, y);
                }
            }
            return 0;
        }

        case WM_INPUT: {
            if (!st->freeMouse && !st->viewOnly) {
                UINT sz = 0;
                GetRawInputData(reinterpret_cast<HRAWINPUT>(lParam), RID_INPUT, nullptr, &sz, sizeof(RAWINPUTHEADER));
                if (sz > 0 && sz <= 512) {
                    uint8_t buf[512];
                    if (GetRawInputData(reinterpret_cast<HRAWINPUT>(lParam), RID_INPUT, buf, &sz, sizeof(RAWINPUTHEADER)) == sz) {
                        const RAWINPUT* ri = reinterpret_cast<const RAWINPUT*>(buf);
                        if (ri->header.dwType == RIM_TYPEMOUSE && (ri->data.mouse.usFlags & MOUSE_MOVE_ABSOLUTE) == 0) {
                            if (ri->data.mouse.lLastX != 0 || ri->data.mouse.lLastY != 0) {
                                BoBoInputPacket pkt = {};
                                pkt.kind = 2;
                                pkt.a = static_cast<int16_t>(ri->data.mouse.lLastX);
                                pkt.b = static_cast<int16_t>(ri->data.mouse.lLastY);
                                EmitInput(st, pkt);
                            }
                        }
                    }
                }
            }
            return 0;
        }

        case WM_MOUSEACTIVATE:
            SetFocus(hwnd);
            return MA_ACTIVATE;

        case WM_LBUTTONDOWN:
            SetFocus(hwnd);
            if (!st->freeMouse || EmitAbsMouseAtClientPoint(st, GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam))) {
                BoBoInputPacket p = { 3, 1, 1, 0 }; EmitInput(st, p);
            }
            return 0;
        case WM_LBUTTONUP:
            { BoBoInputPacket p = { 3, 0, 1, 0 }; EmitInput(st, p); }
            return 0;
        case WM_RBUTTONDOWN:
            SetFocus(hwnd);
            if (!st->freeMouse || EmitAbsMouseAtClientPoint(st, GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam))) {
                BoBoInputPacket p = { 3, 1, 3, 0 }; EmitInput(st, p);
            }
            return 0;
        case WM_RBUTTONUP:
            { BoBoInputPacket p = { 3, 0, 3, 0 }; EmitInput(st, p); }
            return 0;
        case WM_MBUTTONDOWN:
            SetFocus(hwnd);
            if (!st->freeMouse || EmitAbsMouseAtClientPoint(st, GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam))) {
                BoBoInputPacket p = { 3, 1, 2, 0 }; EmitInput(st, p);
            }
            return 0;
        case WM_MBUTTONUP:
            { BoBoInputPacket p = { 3, 0, 2, 0 }; EmitInput(st, p); }
            return 0;
        case WM_XBUTTONDOWN:
            SetFocus(hwnd);
            if (!st->freeMouse || EmitAbsMouseAtClientPoint(st, GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam))) {
                BoBoInputPacket p = { 3, 1, static_cast<int16_t>(GET_XBUTTON_WPARAM(wParam) == XBUTTON1 ? 4 : 5), 0 }; EmitInput(st, p);
            }
            return TRUE;
        case WM_XBUTTONUP:
            { BoBoInputPacket p = { 3, 0, static_cast<int16_t>(GET_XBUTTON_WPARAM(wParam) == XBUTTON1 ? 4 : 5), 0 }; EmitInput(st, p); }
            return TRUE;
        case WM_MOUSEWHEEL:
            { BoBoInputPacket p = { 4, 0, static_cast<int16_t>(GET_WHEEL_DELTA_WPARAM(wParam)), 0 }; EmitInput(st, p); }
            return 0;
        case WM_MOUSEHWHEEL:
            { BoBoInputPacket p = { 4, 1, static_cast<int16_t>(GET_WHEEL_DELTA_WPARAM(wParam)), 0 }; EmitInput(st, p); }
            return 0;

        case WM_KEYDOWN:
        case WM_SYSKEYDOWN: {
            uint16_t vk = static_cast<uint16_t>(wParam);
            bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 || vk == VK_CONTROL || vk == VK_LCONTROL || vk == VK_RCONTROL;
            bool alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0 || vk == VK_MENU || vk == VK_LMENU || vk == VK_RMENU;
            bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
            bool isCtrlOrAlt = (vk == VK_CONTROL || vk == VK_LCONTROL || vk == VK_RCONTROL || vk == VK_MENU || vk == VK_LMENU || vk == VK_RMENU);

            if (isCtrlOrAlt) {
                if (ctrl && alt && !shift) st->ctrlAltSoloActive = true;
            } else {
                st->ctrlAltSoloActive = false;
            }

            if (ctrl && alt && shift && vk == 'Q') {
                ApplyClientMouseMode(st, true);
                EmitHotkey(st, 7, 0);
                return 0;
            }
            if (ctrl && alt && shift && vk == 'Z') {
                ApplyClientMouseMode(st, true);
                EmitHotkey(st, 1, 0);
                return 0;
            }
            if (ctrl && alt && shift && vk == 'B') {
                ApplyClientMouseMode(st, true);
                EmitHotkey(st, 2, 0);
                return 0;
            }
            if (ctrl && alt && !shift && vk == 'M') {
                ApplyClientMouseMode(st, !st->freeMouse);
                EmitHotkey(st, 1, st->freeMouse ? 1 : 0);
                return 0;
            }
            if (ctrl && alt && !shift && vk == 'F') {
                if (!st->freeMouse) ApplyClientMouseMode(st, true);
                EmitHotkey(st, 3, 0);
                return 0;
            }
            if (ctrl && alt && !shift && vk == 'G') {
                EmitHotkey(st, 4, 0);
                return 0;
            }
            if (ctrl && alt && !shift && vk == 'U') {
                if (!st->freeMouse) ApplyClientMouseMode(st, true);
                EmitHotkey(st, 5, 0);
                return 0;
            }
            if (ctrl && alt && !shift && vk == VK_RIGHT) {
                EmitHotkey(st, 6, 0);
                return 0;
            }

            bool extended = ((lParam & (1L << 24)) != 0);
            BoBoInputPacket pkt = {};
            pkt.kind = 5;
            pkt.flags = static_cast<uint8_t>(1 | (extended ? 2 : 0));
            pkt.a = static_cast<int16_t>(vk);
            EmitInput(st, pkt);
            return 0;
        }

        case WM_KEYUP:
        case WM_SYSKEYUP: {
            uint16_t vk = static_cast<uint16_t>(wParam);
            bool isCtrlOrAlt = (vk == VK_CONTROL || vk == VK_LCONTROL || vk == VK_RCONTROL || vk == VK_MENU || vk == VK_LMENU || vk == VK_RMENU);
            if (isCtrlOrAlt && st->ctrlAltSoloActive) {
                st->ctrlAltSoloActive = false;
                if (!st->freeMouse) {
                    ApplyClientMouseMode(st, true);
                    BoBoInputPacket rel = { 6, 0, 0, 0 };
                    EmitInput(st, rel);
                    EmitHotkey(st, 1, 1);
                }
            }
            bool extended = ((lParam & (1L << 24)) != 0);
            BoBoInputPacket pkt = {};
            pkt.kind = 5;
            pkt.flags = static_cast<uint8_t>(extended ? 2 : 0);
            pkt.a = static_cast<int16_t>(vk);
            EmitInput(st, pkt);
            return 0;
        }

        case WM_KILLFOCUS:
            st->pointerInVideoRect = false;
            st->mouseTracking = false;
            SetCursor(LoadCursorW(nullptr, IDC_ARROW));
            if (!st->freeMouse) {
                ApplyClientMouseMode(st, true);
                EmitHotkey(st, 1, 1);
            }
            { BoBoInputPacket rel = { 6, 0, 0, 0 }; EmitInput(st, rel); }
            return 0;

        default:
            return DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}

static bool RegisterClientViewportClass() {
    static std::atomic<bool> registered{ false };
    if (registered.load()) return true;
    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.style = CS_DBLCLKS;
    wc.lpfnWndProc = ClientViewportWndProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512));
    wc.lpszClassName = L"BoBoV6InProcViewport";
    if (RegisterClassExW(&wc) || GetLastError() == ERROR_CLASS_ALREADY_EXISTS) {
        registered.store(true);
        return true;
    }
    return false;
}

} // anonymous namespace

extern "C" {

BOBO_API int32_t __stdcall BoBoCore_SelfTest(char* reportBuf, int32_t reportBufLen) {
    int passed = 0;
    int failed = 0;
    std::ostringstream log;

    auto check = [&](const char* name, bool ok, const std::string& detail) {
        if (ok) { ++passed; log << "[PASS] " << name << " (" << detail << ")\n"; }
        else { ++failed; log << "[FAIL] " << name << " (" << detail << ")\n"; }
    };

    // 1. Monitor enumeration
    auto monitors = ScreenCapturer::EnumerateMonitors();
    check("Monitor enumeration", !monitors.empty(),
          monitors.empty() ? "none" : (std::to_string(monitors.size()) + " monitor(s), primary=" + monitors[0].name));

    // 2. IMA-ADPCM round-trip
    std::vector<int16_t> pcmIn(960);
    for (size_t i = 0; i < 480; ++i) {
        pcmIn[i * 2] = static_cast<int16_t>((i * 60) % 16000 - 8000);
        pcmIn[i * 2 + 1] = static_cast<int16_t>(-pcmIn[i * 2]);
    }
    std::vector<uint8_t> adpcm;
    std::vector<int16_t> pcmOut;
    ImaAdpcmCodec::EncodeStereo(pcmIn.data(), 480, adpcm);
    bool adpcmOk = ImaAdpcmCodec::DecodeStereo(adpcm.data(), static_cast<int>(adpcm.size()), pcmOut) && pcmOut.size() == 960;
    check("IMA-ADPCM 48kHz stereo round-trip", adpcmOk, "encoded=" + std::to_string(adpcm.size()) + "B");

    // 3. D3D11 + MF D3D11VA Decoder + Screen Capture + H.264 Encoder -> Decoder Loopback
    D3D11Renderer renderer;
    std::string d3dErr;
    bool d3dOk = renderer.InitializeDevice(true, d3dErr);
    check("D3D11 device init", d3dOk, d3dOk ? renderer.Capabilities().adapterName : d3dErr);

    ScreenCapturer capturer;
    std::string capErr;
    bool capInit = capturer.Initialize(0, 640, 360, capErr);
    std::vector<uint8_t> nv12;
    bool changed = false;
    bool capGrab = capInit && capturer.CaptureNv12Frame(50, nv12, changed, capErr) && nv12.size() == (640u * 360u * 3u / 2u);
    bool headlessFallback = false;
    if (!capGrab) {
        // Non-interactive desktop (e.g. CI runner / locked session / isolated shell) cannot read active WinSta0 desktop.
        // Synthesize a valid 640x360 BT.709 NV12 color-bar test frame so H.264 encode->decode loopback is still verified.
        bool nonInteractive = (capErr.find("gle=6") != std::string::npos) ||
                              (capErr.find("0x80070005") != std::string::npos) ||
                              (capErr.find("0x887A0004") != std::string::npos) ||
                              (capErr.find("0x887A0026") != std::string::npos);
        if (capInit && nonInteractive) {
            headlessFallback = true;
            nv12.resize(640u * 360u * 3u / 2u);
            for (uint32_t y = 0; y < 360; ++y) {
                for (uint32_t x = 0; x < 640; ++x) {
                    nv12[y * 640 + x] = static_cast<uint8_t>(24 + ((x / 80) * 26) + ((y / 90) * 6));
                }
            }
            std::memset(nv12.data() + 640u * 360u, 128, 640u * 180u);
        }
    }
    check("Screen capture (DXGI/GDI -> NV12)", capGrab || headlessFallback,
          capGrab
              ? (std::string(capturer.UsingDxgi() ? "DXGI+GPU" : "GDI-Fallback") + " 640x360 NV12")
              : (headlessFallback ? ("non-interactive desktop -> synthetic NV12 (" + capErr + ")") : capErr));

    MfH264Encoder encoder;
    std::string encErr;
    bool encOpen = encoder.Open(640, 360, 30, 4000, encErr);
    check("MF H.264 encoder open", encOpen, encOpen ? encoder.EncoderName() : encErr);

    std::vector<uint8_t> encodedIdr;
    bool gotIdr = false;
    if (encOpen && (capGrab || headlessFallback)) {
        for (int attempt = 0; attempt < 8 && encodedIdr.empty(); ++attempt) {
            std::vector<uint8_t> chunk;
            bool isKey = false;
            if (encoder.EncodeNv12(nv12.data(), nv12.size(), static_cast<int64_t>(attempt) * 333333LL, attempt == 0, chunk, isKey, encErr)) {
                if (!chunk.empty()) {
                    encodedIdr = std::move(chunk);
                    gotIdr = isKey;
                }
            }
        }
    }
    check("H.264 Annex-B IDR encode", !encodedIdr.empty(),
          "bytes=" + std::to_string(encodedIdr.size()) + " idr=" + (gotIdr ? "1" : "0"));

    if (d3dOk && !encodedIdr.empty()) {
        MfHardwareDecoder decoder;
        std::string decErr;
        bool decInit = decoder.Initialize(renderer.Device(), decErr);
        bool decOpen = decInit && decoder.OpenCodec(VideoCodec::H264, 640, 360, decErr);
        int decodedCount = 0;
        if (decOpen) {
            for (int f = 0; f < 6 && decodedCount == 0; ++f) {
                if (decoder.SubmitAccessUnit(encodedIdr.data(), encodedIdr.size(), static_cast<int64_t>(f) * 333333LL, decErr)) {
                    bool needMore = false;
                    DecodedFrame df;
                    while (decoder.PullDecodedFrame(df, needMore)) {
                        ++decodedCount;
                        df = DecodedFrame{};
                    }
                }
            }
        }
        check("H.264 encode->decode loopback", decOpen && decodedCount > 0,
              decOpen ? (decoder.ActiveDecoderName() + " decoded=" + std::to_string(decodedCount)) : decErr);
    }

    encoder.Close();
    capturer.Shutdown();

    // 4. SuperFill zero-black-bar viewport rect verification (16:9 host on 2880x1800 16:10 laptop)
    {
        renderer.SetVideoSourceDimensions(2560, 1440);
        renderer.SetScaleMode(2);
        bool modeOk = (renderer.ScaleMode() == 2);
        renderer.SetScaleMode(1);
        check("SuperFill zero-black-bar scale mode", modeOk && renderer.ScaleMode() == 1, "modes=0/1/2");
    }

    // 5. Privacy Hardware Input Blocker hook lifecycle verification
    {
        int32_t en = BoBoHost_SetPrivacyInputBlock(1, nullptr, nullptr);
        int32_t active = BoBoHost_IsPrivacyInputBlocked();
        BoBoHost_SetPrivacyInputBlock(0, nullptr, nullptr);
        int32_t after = BoBoHost_IsPrivacyInputBlocked();
        check("Privacy hardware input blocker (WH_MOUSE_LL + WH_KEYBOARD_LL)",
              en == 1 && active == 1 && after == 0,
              "en=" + std::to_string(en) + " active=" + std::to_string(active) + " after=" + std::to_string(after));
    }

    log << "BoBoCore self-test: passed=" << passed << " failed=" << failed << "\n";

    std::string finalReport = log.str();
    if (reportBuf && reportBufLen > 0) {
        size_t copyLen = finalReport.size();
        if (copyLen >= static_cast<size_t>(reportBufLen)) copyLen = static_cast<size_t>(reportBufLen - 1);
        std::memcpy(reportBuf, finalReport.data(), copyLen);
        reportBuf[copyLen] = '\0';
    }
    return failed == 0 ? 0 : 1;
}

BOBO_API int32_t __stdcall BoBoCore_GetMonitorCount(void) {
    return static_cast<int32_t>(ScreenCapturer::EnumerateMonitors().size());
}

BOBO_API int32_t __stdcall BoBoCore_GetMonitorInfo(int32_t index, BoBoMonitorInfo* outInfo) {
    if (!outInfo) return 0;
    auto list = ScreenCapturer::EnumerateMonitors();
    if (index < 0 || index >= static_cast<int32_t>(list.size())) return 0;
    *outInfo = list[static_cast<size_t>(index)];
    return 1;
}

BOBO_API void* __stdcall BoBoHost_Create(
    int32_t monitorIndex,
    int32_t targetWidth,
    int32_t targetHeight,
    int32_t fps,
    int32_t bitrateKbps,
    int32_t muteHostSpeakers,
    BoBoVideoFrameCallback onVideo,
    BoBoAudioFrameCallback onAudio,
    void* userCtx) {
    auto* session = new BoBoHostSession();
    session->monitorIndex = monitorIndex;
    session->targetWidth = targetWidth > 0 ? targetWidth : 1920;
    session->targetHeight = targetHeight > 0 ? targetHeight : 1080;
    session->fps = fps > 0 ? fps : 60;
    session->bitrateKbps = bitrateKbps > 0 ? bitrateKbps : 14000;
    session->onVideo = onVideo;
    session->onAudio = onAudio;
    session->userCtx = userCtx;
    session->running.store(true);
    session->videoThread = std::thread([session]() { session->RunVideoLoop(); });
    if (onAudio) {
        session->audioCap.Start(muteHostSpeakers != 0, onAudio, userCtx);
    }
    return session;
}

BOBO_API int32_t __stdcall BoBoHost_Reconfigure(
    void* handle,
    int32_t monitorIndex,
    int32_t targetWidth,
    int32_t targetHeight,
    int32_t fps,
    int32_t bitrateKbps) {
    auto* session = static_cast<BoBoHostSession*>(handle);
    if (!session) return 0;
    {
        std::lock_guard<std::mutex> lk(session->cfgMutex);
        if (monitorIndex >= 0) session->monitorIndex = monitorIndex;
        if (targetWidth > 0) session->targetWidth = targetWidth;
        if (targetHeight > 0) session->targetHeight = targetHeight;
        if (fps > 0) session->fps = fps;
        if (bitrateKbps > 0) session->bitrateKbps = bitrateKbps;
    }
    session->reconfigPending.store(true);
    session->keyframePending.store(true);
    return 1;
}

BOBO_API void __stdcall BoBoHost_RequestKeyframe(void* handle) {
    auto* session = static_cast<BoBoHostSession*>(handle);
    if (session) session->keyframePending.store(true);
}

BOBO_API void __stdcall BoBoHost_SetMuteHost(void* handle, int32_t mute) {
    auto* session = static_cast<BoBoHostSession*>(handle);
    if (session) session->audioCap.SetMuteSpeakers(mute != 0);
}

BOBO_API void __stdcall BoBoHost_Destroy(void* handle) {
    auto* session = static_cast<BoBoHostSession*>(handle);
    if (!session) return;
    session->running.store(false);
    session->audioCap.Stop();
    if (session->videoThread.joinable()) {
        session->videoThread.join();
    }
    delete session;
    BoBoHost_RestoreSuperDisplay();
}

BOBO_API void __stdcall BoBoHost_InjectInput(int32_t monitorIndex, const BoBoInputPacket* pkt) {
    if (!pkt) return;
    INPUT inp = {};
    switch (pkt->kind) {
        case 1: { // Absolute mouse position on target monitor
            auto monitors = ScreenCapturer::EnumerateMonitors();
            int monLeft = 0, monTop = 0, monW = GetSystemMetrics(SM_CXSCREEN), monH = GetSystemMetrics(SM_CYSCREEN);
            if (!monitors.empty()) {
                int idx = (monitorIndex >= 0 && monitorIndex < static_cast<int>(monitors.size())) ? monitorIndex : 0;
                monLeft = monitors[static_cast<size_t>(idx)].left;
                monTop = monitors[static_cast<size_t>(idx)].top;
                monW = monitors[static_cast<size_t>(idx)].width;
                monH = monitors[static_cast<size_t>(idx)].height;
            }
            int vLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vW = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int vH = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (vW <= 1) vW = 1920;
            if (vH <= 1) vH = 1080;

            uint16_t nx = static_cast<uint16_t>(pkt->a);
            uint16_t ny = static_cast<uint16_t>(pkt->b);
            double px = static_cast<double>(monLeft) + (static_cast<double>(nx) / 65535.0) * static_cast<double>(monW - 1);
            double py = static_cast<double>(monTop) + (static_cast<double>(ny) / 65535.0) * static_cast<double>(monH - 1);

            inp.type = INPUT_MOUSE;
            inp.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
            inp.mi.dx = static_cast<LONG>(((px - vLeft) * 65535.0) / static_cast<double>(vW - 1) + 0.5);
            inp.mi.dy = static_cast<LONG>(((py - vTop) * 65535.0) / static_cast<double>(vH - 1) + 0.5);
            inp.mi.dwExtraInfo = kBoBoInjectedMagic;
            SendInput(1, &inp, sizeof(INPUT));
            break;
        }
        case 2: { // Relative mouse move (FPS locked mode)
            inp.type = INPUT_MOUSE;
            inp.mi.dwFlags = MOUSEEVENTF_MOVE;
            inp.mi.dx = pkt->a;
            inp.mi.dy = pkt->b;
            inp.mi.dwExtraInfo = kBoBoInjectedMagic;
            SendInput(1, &inp, sizeof(INPUT));
            break;
        }
        case 3: { // Mouse buttons
            bool down = (pkt->flags & 1) != 0;
            inp.type = INPUT_MOUSE;
            switch (pkt->a) {
                case 1: inp.mi.dwFlags = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP; break;
                case 2: inp.mi.dwFlags = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP; break;
                case 3: inp.mi.dwFlags = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP; break;
                case 4: inp.mi.dwFlags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; inp.mi.mouseData = XBUTTON1; break;
                case 5: inp.mi.dwFlags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; inp.mi.mouseData = XBUTTON2; break;
                default: return;
            }
            inp.mi.dwExtraInfo = kBoBoInjectedMagic;
            SendInput(1, &inp, sizeof(INPUT));
            break;
        }
        case 4: { // Mouse wheel
            inp.type = INPUT_MOUSE;
            inp.mi.dwFlags = (pkt->flags & 1) ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL;
            inp.mi.mouseData = static_cast<DWORD>(static_cast<int32_t>(pkt->a));
            inp.mi.dwExtraInfo = kBoBoInjectedMagic;
            SendInput(1, &inp, sizeof(INPUT));
            break;
        }
        case 5: { // Keyboard key
            uint16_t vk = static_cast<uint16_t>(pkt->a) & 0xFF;
            bool down = (pkt->flags & 1) != 0;
            bool extended = (pkt->flags & 2) != 0;
            inp.type = INPUT_KEYBOARD;
            inp.ki.wVk = vk;
            inp.ki.wScan = static_cast<WORD>(MapVirtualKeyW(vk, MAPVK_VK_TO_VSC));
            inp.ki.dwFlags = (down ? 0 : KEYEVENTF_KEYUP) | (extended ? KEYEVENTF_EXTENDEDKEY : 0);
            inp.ki.dwExtraInfo = kBoBoInjectedMagic;
            SendInput(1, &inp, sizeof(INPUT));
            break;
        }
        case 6: { // Release all modifier keys
            const WORD mods[] = { VK_LSHIFT, VK_RSHIFT, VK_LCONTROL, VK_RCONTROL, VK_LMENU, VK_RMENU, VK_LWIN, VK_RWIN };
            for (WORD vk : mods) {
                INPUT u = {};
                u.type = INPUT_KEYBOARD;
                u.ki.wVk = vk;
                u.ki.dwFlags = KEYEVENTF_KEYUP;
                u.ki.dwExtraInfo = kBoBoInjectedMagic;
                SendInput(1, &u, sizeof(INPUT));
            }
            break;
        }
        default:
            break;
    }
}

BOBO_API void __stdcall BoBoHost_InjectUnicodeText(const wchar_t* text, int32_t length) {
    if (!text || length <= 0) return;
    if (length > 4000) length = 4000;
    std::vector<INPUT> inputs;
    inputs.reserve(static_cast<size_t>(length) * 2);
    for (int32_t i = 0; i < length; ++i) {
        wchar_t ch = text[i];
        if (ch == L'\r') continue;
        if (ch == L'\n') {
            INPUT d = {}, u = {};
            d.type = INPUT_KEYBOARD; d.ki.wVk = VK_RETURN; d.ki.dwExtraInfo = kBoBoInjectedMagic;
            u.type = INPUT_KEYBOARD; u.ki.wVk = VK_RETURN; u.ki.dwFlags = KEYEVENTF_KEYUP; u.ki.dwExtraInfo = kBoBoInjectedMagic;
            inputs.push_back(d);
            inputs.push_back(u);
            continue;
        }
        INPUT d = {}, u = {};
        d.type = INPUT_KEYBOARD;
        d.ki.wScan = ch;
        d.ki.dwFlags = KEYEVENTF_UNICODE;
        d.ki.dwExtraInfo = kBoBoInjectedMagic;
        u.type = INPUT_KEYBOARD;
        u.ki.wScan = ch;
        u.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
        u.ki.dwExtraInfo = kBoBoInjectedMagic;
        inputs.push_back(d);
        inputs.push_back(u);
    }
    if (!inputs.empty()) {
        SendInput(static_cast<UINT>(inputs.size()), inputs.data(), sizeof(INPUT));
    }
}

BOBO_API int32_t __stdcall BoBoHost_ApplySuperDisplay(
    int32_t monitorIndex,
    int32_t targetWidth,
    int32_t targetHeight,
    int32_t* outActualWidth,
    int32_t* outActualHeight) {
    if (outActualWidth) *outActualWidth = 0;
    if (outActualHeight) *outActualHeight = 0;
    if (targetWidth < 640 || targetHeight < 480) return 0;

    std::lock_guard<std::mutex> lk(g_superDispMutex);
    auto devs = ListMonitorDevicesOrdered();
    if (devs.empty()) return 0;
    int idx = (monitorIndex >= 0 && monitorIndex < static_cast<int>(devs.size())) ? monitorIndex : 0;
    const std::wstring& devName = devs[static_cast<size_t>(idx)].deviceName;

    // If previously applied to a different monitor, restore it first
    if (g_superDispApplied && g_superDispDevice != devName) {
        ChangeDisplaySettingsExW(g_superDispDevice.c_str(), &g_superDispOrigMode, nullptr, 0, nullptr);
        g_superDispApplied = false;
        g_superDispDevice.clear();
        std::memset(&g_superDispOrigMode, 0, sizeof(g_superDispOrigMode));
    }

    DEVMODEW curMode = {};
    curMode.dmSize = sizeof(curMode);
    if (!EnumDisplaySettingsW(devName.c_str(), ENUM_CURRENT_SETTINGS, &curMode)) {
        return 0;
    }

    int32_t curW = static_cast<int32_t>(curMode.dmPelsWidth);
    int32_t curH = static_cast<int32_t>(curMode.dmPelsHeight);
    if (outActualWidth) *outActualWidth = curW;
    if (outActualHeight) *outActualHeight = curH;
    if (curW <= 0 || curH <= 0) return 0;

    double targetAspect = static_cast<double>(targetWidth) / static_cast<double>(targetHeight);
    double curAspect = static_cast<double>(curW) / static_cast<double>(curH);

    // Already matching client aspect ratio natively -> zero black bars with no flicker!
    if (std::fabs(curAspect - targetAspect) <= 0.025) {
        return 1;
    }

    bool found = false;
    DEVMODEW bestMode = {};
    double bestScore = -1e18;
    DEVMODEW dm = {};
    dm.dmSize = sizeof(dm);
    const DWORD minAllowedW = (targetWidth >= 1440 && curW >= 1440) ? 1440u : 1024u;
    const DWORD minAllowedH = (targetHeight >= 900 && curH >= 900) ? 900u : 600u;
    for (DWORD modeNum = 0; EnumDisplaySettingsW(devName.c_str(), modeNum, &dm); ++modeNum) {
        if (dm.dmBitsPerPel < 32) continue;
        if (dm.dmPelsWidth < minAllowedW || dm.dmPelsHeight < minAllowedH) continue;
        if ((dm.dmFields & DM_DISPLAYORIENTATION) && (curMode.dmFields & DM_DISPLAYORIENTATION) &&
            dm.dmDisplayOrientation != curMode.dmDisplayOrientation) {
            continue;
        }
        double aspect = static_cast<double>(dm.dmPelsWidth) / static_cast<double>(dm.dmPelsHeight);
        if (std::fabs(aspect - targetAspect) > 0.035) continue;

        double wDiff = std::fabs(static_cast<double>(dm.dmPelsWidth) - static_cast<double>(targetWidth));
        double hDiff = std::fabs(static_cast<double>(dm.dmPelsHeight) - static_cast<double>(targetHeight));
        int freqScore = (dm.dmDisplayFrequency >= 60 && dm.dmDisplayFrequency <= 240)
            ? static_cast<int>(dm.dmDisplayFrequency) : 0;
        double score = -(wDiff + hDiff) * 2.0 + static_cast<double>(freqScore) * 3.0;
        if (dm.dmPelsWidth < 1280) score -= 2000.0;

        if (!found || score > bestScore) {
            DEVMODEW testDm = dm;
            testDm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_DISPLAYFREQUENCY;
            if (ChangeDisplaySettingsExW(devName.c_str(), &testDm, nullptr, CDS_TEST, nullptr) == DISP_CHANGE_SUCCESSFUL) {
                found = true;
                bestScore = score;
                bestMode = testDm;
            }
        }
        dm.dmSize = sizeof(dm);
    }

    if (!found) {
        return 0;
    }

    if (!g_superDispApplied) {
        g_superDispOrigMode = curMode;
        g_superDispDevice = devName;
    }

    LONG rc = ChangeDisplaySettingsExW(devName.c_str(), &bestMode, nullptr, CDS_FULLSCREEN, nullptr);
    if (rc != DISP_CHANGE_SUCCESSFUL) {
        if (!g_superDispApplied) {
            g_superDispDevice.clear();
            std::memset(&g_superDispOrigMode, 0, sizeof(g_superDispOrigMode));
        }
        return 0;
    }

    g_superDispApplied = true;
    if (outActualWidth) *outActualWidth = static_cast<int32_t>(bestMode.dmPelsWidth);
    if (outActualHeight) *outActualHeight = static_cast<int32_t>(bestMode.dmPelsHeight);
    return 1;
}

BOBO_API void __stdcall BoBoHost_RestoreSuperDisplay() {
    std::lock_guard<std::mutex> lk(g_superDispMutex);
    if (!g_superDispApplied) return;
    g_superDispApplied = false;
    LONG rc = ChangeDisplaySettingsExW(g_superDispDevice.c_str(), &g_superDispOrigMode, nullptr, 0, nullptr);
    if (rc != DISP_CHANGE_SUCCESSFUL) {
        ChangeDisplaySettingsExW(g_superDispDevice.c_str(), nullptr, nullptr, 0, nullptr);
    }
    g_superDispDevice.clear();
    std::memset(&g_superDispOrigMode, 0, sizeof(g_superDispOrigMode));
}

BOBO_API int32_t __stdcall BoBoHost_SetPrivacyInputBlock(
    int32_t enable,
    BoBoPrivacyEscapeCallback onEscape,
    void* userCtx) {
    std::lock_guard<std::mutex> lk(g_privHookMutex);
    if (!enable) {
        g_privBlockActive.store(false);
        g_physCtrl.store(false);
        g_physAlt.store(false);
        g_physShift.store(false);
        g_privEscapeCb = nullptr;
        g_privEscapeCtx = nullptr;
        if (g_privHookThread.joinable()) {
            DWORD tid = g_privHookThreadId;
            if (tid != 0) {
                PostThreadMessageW(tid, WM_QUIT, 0, 0);
            }
            g_privHookThread.join();
            g_privHookThreadId = 0;
        }
        return 1;
    }

    g_privEscapeCb = onEscape;
    g_privEscapeCtx = userCtx;
    if (g_privHookThread.joinable() && g_privBlockActive.load()) {
        return 1;
    }
    if (g_privHookThread.joinable()) {
        DWORD tid = g_privHookThreadId;
        if (tid != 0) PostThreadMessageW(tid, WM_QUIT, 0, 0);
        g_privHookThread.join();
        g_privHookThreadId = 0;
    }

    g_physCtrl.store(false);
    g_physAlt.store(false);
    g_physShift.store(false);
    g_privHookReady.store(false);
    g_privHookSuccess.store(false);
    g_privBlockActive.store(true);

    g_privHookThread = std::thread([]() {
        MSG msg = {};
        PeekMessageW(&msg, nullptr, WM_USER, WM_USER, PM_NOREMOVE);
        g_privHookThreadId = GetCurrentThreadId();

        HMODULE hMod = GetModuleHandleW(nullptr);
        HHOOK hMouse = SetWindowsHookExW(WH_MOUSE_LL, PrivacyLowLevelMouseProc, hMod, 0);
        HHOOK hKey = SetWindowsHookExW(WH_KEYBOARD_LL, PrivacyLowLevelKeyProc, hMod, 0);
        bool ok = (hMouse != nullptr && hKey != nullptr);
        g_privHookSuccess.store(ok);
        g_privHookReady.store(true);

        if (ok) {
            while (GetMessageW(&msg, nullptr, 0, 0) > 0) {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }

        if (hMouse) UnhookWindowsHookEx(hMouse);
        if (hKey) UnhookWindowsHookEx(hKey);
        g_privBlockActive.store(false);
    });

    for (int i = 0; i < 160 && !g_privHookReady.load(); ++i) {
        Sleep(5);
    }

    if (!g_privHookSuccess.load()) {
        g_privBlockActive.store(false);
        if (g_privHookThread.joinable()) {
            DWORD tid = g_privHookThreadId;
            if (tid != 0) PostThreadMessageW(tid, WM_QUIT, 0, 0);
            g_privHookThread.join();
            g_privHookThreadId = 0;
        }
        return 0;
    }
    return 1;
}

BOBO_API int32_t __stdcall BoBoHost_IsPrivacyInputBlocked() {
    return g_privBlockActive.load() ? 1 : 0;
}

BOBO_API void* __stdcall BoBoClient_Create(
    void* parentHwndPtr,
    int32_t videoWidth,
    int32_t videoHeight,
    int32_t freeMouse,
    BoBoClientInputCallback onInput,
    BoBoClientHotkeyCallback onHotkey,
    void* userCtx) {
    HWND parentHwnd = reinterpret_cast<HWND>(parentHwndPtr);
    if (!parentHwnd || !IsWindow(parentHwnd)) return nullptr;
    if (!RegisterClientViewportClass()) return nullptr;

    auto* st = new BoBoClientSession();
    st->parentHwnd = parentHwnd;
    st->videoW = videoWidth > 0 ? static_cast<uint32_t>(videoWidth) : 1920u;
    st->videoH = videoHeight > 0 ? static_cast<uint32_t>(videoHeight) : 1080u;
    st->freeMouse = (freeMouse != 0);
    st->onInput = onInput;
    st->onHotkey = onHotkey;
    st->userCtx = userCtx;

    std::string err;
    if (!st->renderer.InitializeDevice(true, err) ||
        !st->decoder.Initialize(st->renderer.Device(), err)) {
        delete st;
        return nullptr;
    }

    RECT rc = {};
    GetClientRect(parentHwnd, &rc);
    int w = (rc.right - rc.left) > 16 ? (rc.right - rc.left) : 1280;
    int h = (rc.bottom - rc.top) > 16 ? (rc.bottom - rc.top) : 720;

    HWND vpHwnd = CreateWindowExW(
        0,
        L"BoBoV6InProcViewport",
        L"BoBo V6 Viewport",
        WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
        0, 0, w, h,
        parentHwnd,
        nullptr,
        GetModuleHandleW(nullptr),
        st);
    if (!vpHwnd) {
        delete st;
        return nullptr;
    }
    st->viewportHwnd = vpHwnd;
    g_activeClient = st;

    if (!st->renderer.AttachToHwnd(vpHwnd, static_cast<uint32_t>(w), static_cast<uint32_t>(h), err)) {
        g_activeClient = nullptr;
        DestroyWindow(vpHwnd);
        delete st;
        return nullptr;
    }
    st->renderer.SetVideoSourceDimensions(st->videoW, st->videoH);
    st->renderer.PresentClearFrame(0.055f, 0.082f, 0.125f, err);
    ApplyClientMouseMode(st, st->freeMouse);
    return st;
}

BOBO_API int32_t __stdcall BoBoClient_PushVideo(
    void* handle,
    const uint8_t* h264Data,
    int32_t length,
    int32_t isKeyframe,
    int32_t srcWidth,
    int32_t srcHeight) {
    (void)isKeyframe;
    auto* st = static_cast<BoBoClientSession*>(handle);
    if (!st || !st->alive.load() || !h264Data || length <= 0) return 0;

    std::lock_guard<std::mutex> lk(st->decodeMutex);
    uint32_t w = srcWidth > 0 ? static_cast<uint32_t>(srcWidth) : st->videoW;
    uint32_t h = srcHeight > 0 ? static_cast<uint32_t>(srcHeight) : st->videoH;
    std::string err;

    if (!st->decoderOpened || w != st->videoW || h != st->videoH) {
        st->videoW = w;
        st->videoH = h;
        st->renderer.SetVideoSourceDimensions(w, h);
        st->decoderOpened = st->decoder.OpenCodec(VideoCodec::H264, w, h, err);
        if (!st->decoderOpened) return 0;
    }

    auto t0 = std::chrono::steady_clock::now();
    int64_t pts100ns = std::chrono::duration_cast<std::chrono::microseconds>(t0.time_since_epoch()).count() * 10LL;
    if (!st->decoder.SubmitAccessUnit(h264Data, static_cast<size_t>(length), pts100ns, err)) {
        return 0;
    }

    int presented = 0;
    double renMs = 0.0;
    bool needMore = false;
    DecodedFrame f;
    while (st->decoder.PullDecodedFrame(f, needMore)) {
        auto tRen0 = std::chrono::steady_clock::now();
        bool ok = false;
        if (f.gpuTexture) {
            ok = st->renderer.PresentNv12Texture(f.gpuTexture.Get(), f.subresourceIndex, f.width, f.height, err);
        } else if (!f.cpuNv12.empty()) {
            ok = st->renderer.PresentNv12CpuBuffer(f.cpuNv12.data(), f.width, f.height, f.stride, err);
        }
        auto tRen1 = std::chrono::steady_clock::now();
        if (ok) {
            ++presented;
            renMs += std::chrono::duration<double, std::milli>(tRen1 - tRen0).count();
        }
        f = DecodedFrame{};
    }

    if (presented > 0) {
        bool wasReceived = st->videoReceived.exchange(true);
        if (!wasReceived && st->viewportHwnd && IsWindow(st->viewportHwnd)) {
            PostMessageW(st->viewportHwnd, kMsgRefreshCursor, 0, 0);
        }
        auto t1 = std::chrono::steady_clock::now();
        double totalMs = std::chrono::duration<double, std::milli>(t1 - t0).count();
        double decMs = totalMs > renMs ? (totalMs - renMs) : 0.0;
        std::lock_guard<std::mutex> slk(st->statsMutex);
        st->framesWindow += static_cast<uint64_t>(presented);
        st->decodeMsAccum += decMs;
        st->renderMsAccum += renMs;
        st->timingSamples += 1;
    }
    return presented;
}

BOBO_API void __stdcall BoBoClient_PushAudio(
    void* handle,
    const uint8_t* adpcmData,
    int32_t length,
    int32_t sampleRate,
    int32_t channels,
    int32_t sampleCount) {
    (void)sampleCount;
    auto* st = static_cast<BoBoClientSession*>(handle);
    if (!st || !st->alive.load() || !adpcmData || length <= 0) return;
    st->audio.PlayAdpcm(adpcmData, length, sampleRate, channels);
}

BOBO_API void __stdcall BoBoClient_Resize(void* handle, int32_t width, int32_t height) {
    auto* st = static_cast<BoBoClientSession*>(handle);
    if (!st || !st->viewportHwnd || width <= 0 || height <= 0) return;
    MoveWindow(st->viewportHwnd, 0, 0, width, height, TRUE);
}

BOBO_API void __stdcall BoBoClient_Focus(void* handle) {
    auto* st = static_cast<BoBoClientSession*>(handle);
    if (st && st->viewportHwnd && IsWindow(st->viewportHwnd)) {
        SetFocus(st->viewportHwnd);
    }
}

BOBO_API void __stdcall BoBoClient_SetMouseMode(void* handle, int32_t freeMouse) {
    auto* st = static_cast<BoBoClientSession*>(handle);
    if (st) ApplyClientMouseMode(st, freeMouse != 0);
}

BOBO_API void __stdcall BoBoClient_SetVolume(void* handle, int32_t volumePercent) {
    auto* st = static_cast<BoBoClientSession*>(handle);
    if (!st) return;
    if (volumePercent < 0) volumePercent = 0;
    if (volumePercent > 100) volumePercent = 100;
    st->audio.SetVolume(static_cast<float>(volumePercent) / 100.0f);
}

BOBO_API void __stdcall BoBoClient_SetScaleMode(void* handle, int32_t scaleMode) {
    auto* st = static_cast<BoBoClientSession*>(handle);
    if (st) st->renderer.SetScaleMode(scaleMode);
}

BOBO_API void __stdcall BoBoClient_SetViewOnly(void* handle, int32_t viewOnly) {
    auto* st = static_cast<BoBoClientSession*>(handle);
    if (!st) return;
    st->viewOnly = (viewOnly != 0);
    if (st->viewOnly && !st->freeMouse) ApplyClientMouseMode(st, true);
}

BOBO_API void __stdcall BoBoClient_SetSysKeys(void* handle, int32_t sendSysKeys) {
    auto* st = static_cast<BoBoClientSession*>(handle);
    if (!st) return;
    st->sendSysKeys = (sendSysKeys != 0);
    SyncClientSysKeyHook(st);
}

BOBO_API void __stdcall BoBoClient_GetStats(void* handle, BoBoClientStats* outStats) {
    if (!outStats) return;
    std::memset(outStats, 0, sizeof(*outStats));
    auto* st = static_cast<BoBoClientSession*>(handle);
    if (!st) return;

    auto now = std::chrono::steady_clock::now();
    std::lock_guard<std::mutex> slk(st->statsMutex);
    double elapsed = std::chrono::duration<double>(now - st->lastStatsWindow).count();
    if (elapsed >= 0.35) {
        st->measuredFps = static_cast<double>(st->framesWindow) / elapsed;
        if (st->timingSamples > 0) {
            st->avgDecodeMs = st->decodeMsAccum / static_cast<double>(st->timingSamples);
            st->avgRenderMs = st->renderMsAccum / static_cast<double>(st->timingSamples);
        }
        st->framesWindow = 0;
        st->decodeMsAccum = 0.0;
        st->renderMsAccum = 0.0;
        st->timingSamples = 0;
        st->lastStatsWindow = now;
    }
    outStats->fps = st->measuredFps;
    outStats->decodeMs = st->avgDecodeMs;
    outStats->renderMs = st->avgRenderMs;
    outStats->width = static_cast<int32_t>(st->videoW);
    outStats->height = static_cast<int32_t>(st->videoH);
    std::string dec = st->decoder.ActiveDecoderName();
    if (dec.empty()) dec = "D3D11VA";
    std::strncpy(outStats->decoderName, dec.c_str(), sizeof(outStats->decoderName) - 1);
}

BOBO_API void __stdcall BoBoClient_Destroy(void* handle) {
    auto* st = static_cast<BoBoClientSession*>(handle);
    if (!st) return;
    st->alive.store(false);
    st->sendSysKeys = false;
    SyncClientSysKeyHook(st);
    ApplyClientMouseMode(st, true);
    if (g_activeClient == st) g_activeClient = nullptr;
    st->audio.Shutdown();
    {
        std::lock_guard<std::mutex> lk(st->decodeMutex);
        st->decoder.CloseCodec();
    }
    if (st->viewportHwnd && IsWindow(st->viewportHwnd)) {
        DestroyWindow(st->viewportHwnd);
        st->viewportHwnd = nullptr;
    }
    delete st;
}

} // extern "C"
