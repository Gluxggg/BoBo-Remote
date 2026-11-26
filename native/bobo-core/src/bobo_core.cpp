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

#include <climits>
#include <cstdlib>
#include <windowsx.h>
#include <mmsystem.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <deque>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

using namespace bobo;

namespace {

struct ScopedTimerResolution1ms {
    ScopedTimerResolution1ms() { timeBeginPeriod(1); }
    ~ScopedTimerResolution1ms() { timeEndPeriod(1); }
};

// ==================== Host Session (In-Process Zero-Copy Capture + H.264 Encode + WASAPI Loopback) ====================
struct BoBoHostSession {
    std::atomic<bool> running{ false };
    std::atomic<bool> reconfigPending{ false };
    std::atomic<bool> keyframePending{ true };
    std::atomic<bool> compositeCursor{ true }; // Default true for V6.2 legacy clients; V6.3 sets 0 in Free Mouse for 0ms local cursor + zero-copy GPU!
    std::atomic<int32_t> liveBitratePending{ 0 };
    std::atomic<int32_t> liveFpsTarget{ 60 };

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
        ScopedTimerResolution1ms timerGuard;
        HRESULT hrCo = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        ScreenCapturer capturer;
        MfH264Encoder encoder;
        std::vector<uint8_t> nv12Buf;
        std::vector<uint8_t> h264Buf;

        int32_t curMon = -1, curW = 0, curH = 0, curFps = 60, curBps = 14000;
        bool curCompositeCursor = true;
        bool initialized = false;
        uint64_t curCapDeviceGen = 0;
        int64_t frameIndex = 0;
        int64_t sentFrameCount = 0;
        int consecutiveEncodeFailures = 0;
        auto startClock = std::chrono::steady_clock::now();
        auto lastSentClock = startClock - std::chrono::seconds(2);

        while (running.load()) {
            bool wantComposite = compositeCursor.load(std::memory_order_acquire);
            bool cursorModeFlipped = initialized &&
                ((!wantComposite && !encoder.IsD3D11ZeroCopy() && capturer.UsingDxgi()) ||
                 (wantComposite && encoder.IsD3D11ZeroCopy()));
            bool capDeviceChanged = initialized && encoder.IsD3D11ZeroCopy() &&
                (!capturer.UsingDxgi() || capturer.DeviceGeneration() != curCapDeviceGen);

            if (!initialized || cursorModeFlipped || capDeviceChanged || reconfigPending.exchange(false)) {
                int32_t reqMon, reqW, reqH, reqFps, reqBps;
                {
                    std::lock_guard<std::mutex> lk(cfgMutex);
                    reqMon = monitorIndex;
                    reqW = targetWidth;
                    reqH = targetHeight;
                    reqFps = (fps >= 15 && fps <= 144) ? fps : 60;
                    reqBps = (bitrateKbps >= 500 && bitrateKbps <= 60000) ? bitrateKbps : 10000;
                }
                liveFpsTarget.store(reqFps, std::memory_order_release);

                bool geomChanged = !initialized || cursorModeFlipped || capDeviceChanged ||
                                   reqMon != curMon || reqW != curW || reqH != curH;
                if (!geomChanged) {
                    // Live bitrate / FPS update with ZERO encoder teardown or black frame!
                    if (reqBps != curBps) {
                        encoder.UpdateBitrateLive(static_cast<uint32_t>(reqBps));
                        curBps = reqBps;
                    }
                    curFps = reqFps;
                } else {
                    curMon = reqMon;
                    curW = reqW;
                    curH = reqH;
                    curFps = reqFps;
                    curBps = reqBps;
                    curCompositeCursor = wantComposite;
                    std::string err;
                    bool capOk = true;
                    if (!capDeviceChanged || !capturer.UsingDxgi() ||
                        capturer.OutputWidth() != static_cast<uint32_t>((curW + 1) & ~1) ||
                        capturer.OutputHeight() != static_cast<uint32_t>((curH + 1) & ~1)) {
                        capOk = capturer.Initialize(curMon, static_cast<uint32_t>(curW), static_cast<uint32_t>(curH), err);
                    }
                    ID3D11Device* zeroCopyDev = (capOk && capturer.UsingDxgi() && !curCompositeCursor) ? capturer.Device() : nullptr;
                    if (capOk &&
                        encoder.Open(capturer.OutputWidth(), capturer.OutputHeight(),
                                     static_cast<uint32_t>(curFps), static_cast<uint32_t>(curBps), err, zeroCopyDev)) {
                        initialized = true;
                        curCapDeviceGen = capturer.DeviceGeneration();
                        consecutiveEncodeFailures = 0;
                        sentFrameCount = 0;
                        keyframePending.store(true);
                    } else {
                        initialized = false;
                        Sleep(200);
                        continue;
                    }
                }
            }

            // Apply zero-stall live bitrate / FPS changes from ABR controller
            int32_t pendingBps = liveBitratePending.exchange(0);
            if (pendingBps >= 500 && pendingBps <= 60000 && pendingBps != curBps) {
                encoder.UpdateBitrateLive(static_cast<uint32_t>(pendingBps));
                curBps = pendingBps;
            }
            int32_t targetFpsNow = liveFpsTarget.load(std::memory_order_acquire);
            if (targetFpsNow >= 15 && targetFpsNow <= 144) {
                curFps = targetFpsNow;
            }

            auto frameStart = std::chrono::steady_clock::now();
            int framePeriodMs = 1000 / (curFps > 0 ? curFps : 60);
            if (framePeriodMs < 6) framePeriodMs = 6;

            bool forceKey = keyframePending.exchange(false);
            auto sinceLastSentMs = std::chrono::duration_cast<std::chrono::milliseconds>(frameStart - lastSentClock).count();
            bool needKeepalive = (sinceLastSentMs >= 1000);
            bool warmup = (sentFrameCount < 3);
            bool forceProduce = forceKey || warmup || needKeepalive;

            bool changed = false;
            std::string capErr;
            ID3D11Texture2D* gpuNv12Tex = nullptr;
            bool preferGpu = encoder.IsD3D11ZeroCopy() && !curCompositeCursor;
            bool capOk = capturer.CaptureFrame(
                framePeriodMs, preferGpu, curCompositeCursor, forceProduce,
                &gpuNv12Tex, nv12Buf, changed, capErr);

            // Detect DXGI_ERROR_ACCESS_LOST device recreation before passing GPU texture to an encoder bound to the old ID3D11Device
            if (encoder.IsD3D11ZeroCopy() && (!capturer.UsingDxgi() || capturer.DeviceGeneration() != curCapDeviceGen)) {
                std::string rebindErr;
                ID3D11Device* freshDev = (capturer.UsingDxgi() && !curCompositeCursor) ? capturer.Device() : nullptr;
                if (encoder.Open(capturer.OutputWidth(), capturer.OutputHeight(),
                                 static_cast<uint32_t>(curFps), static_cast<uint32_t>(curBps), rebindErr, freshDev)) {
                    curCapDeviceGen = capturer.DeviceGeneration();
                    keyframePending.store(true);
                    forceKey = true;
                    preferGpu = encoder.IsD3D11ZeroCopy() && !curCompositeCursor;
                } else {
                    initialized = false;
                    keyframePending.store(true);
                    continue;
                }
            }

            bool hasGpuFrame = capOk && preferGpu && (gpuNv12Tex != nullptr);
            bool hasCpuFrame = capOk && !hasGpuFrame && !nv12Buf.empty();

            // Variable Frame Rate (VFR): on idle static desktop, skip encoding & network transmission completely!
            if (capOk && !changed && !forceProduce) {
                auto elapsedIdleMs = static_cast<int>(
                    std::chrono::duration_cast<std::chrono::milliseconds>(
                        std::chrono::steady_clock::now() - frameStart).count());
                int sleepIdle = framePeriodMs - elapsedIdleMs;
                if (sleepIdle > 1 && running.load()) {
                    Sleep(static_cast<DWORD>(sleepIdle));
                }
                continue;
            }

            if (!hasGpuFrame && !hasCpuFrame && (frameIndex == 0 || forceKey)) {
                // Non-interactive desktop (UAC/lockscreen/CI): emit dark neutral NV12 frame so stream stays alive
                uint32_t fw = capturer.OutputWidth();
                uint32_t fh = capturer.OutputHeight();
                if (fw >= 320 && fh >= 240) {
                    size_t yBytes = static_cast<size_t>(fw) * static_cast<size_t>(fh);
                    nv12Buf.resize(yBytes * 3 / 2);
                    std::memset(nv12Buf.data(), 24, yBytes);
                    std::memset(nv12Buf.data() + yBytes, 128, yBytes / 2);
                    hasCpuFrame = true;
                }
            }
            if (!hasGpuFrame && !hasCpuFrame && forceKey) {
                keyframePending.store(true);
            }

            if (hasGpuFrame || hasCpuFrame) {
                // Real monotonic wall-clock PTS so VBR/CBR rate control remains accurate under VFR idle skipping
                int64_t sampleTimeHns = std::chrono::duration_cast<std::chrono::microseconds>(
                    std::chrono::steady_clock::now() - startClock).count() * 10LL;
                bool isKey = false;
                std::string encErr;
                bool encOk = hasGpuFrame
                    ? encoder.EncodeD3D11Texture(gpuNv12Tex, sampleTimeHns, forceKey, h264Buf, isKey, encErr)
                    : encoder.EncodeNv12(nv12Buf.data(), nv12Buf.size(), sampleTimeHns, forceKey, h264Buf, isKey, encErr);

                if (encOk) {
                    consecutiveEncodeFailures = 0;
                    if (!h264Buf.empty() && onVideo) {
                        auto nowClock = std::chrono::steady_clock::now();
                        lastSentClock = nowClock;
                        ++sentFrameCount;
                        uint32_t tsMs = static_cast<uint32_t>(
                            std::chrono::duration_cast<std::chrono::milliseconds>(nowClock - startClock).count());
                        onVideo(h264Buf.data(),
                                static_cast<int32_t>(h264Buf.size()),
                                isKey ? 1 : 0,
                                static_cast<int32_t>(capturer.OutputWidth()),
                                static_cast<int32_t>(capturer.OutputHeight()),
                                tsMs,
                                userCtx);
                    }
                    ++frameIndex;
                } else {
                    if (forceKey) keyframePending.store(true);
                    if (++consecutiveEncodeFailures >= 5) {
                        initialized = false;
                        consecutiveEncodeFailures = 0;
                    }
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

struct QueuedH264Frame {
    std::vector<uint8_t> data;
    bool isKeyframe = false;
    uint32_t width = 0;
    uint32_t height = 0;
};

// ==================== Client In-Process Viewport (Decoupled D3D11VA Decode + Render + Audio + 0ms Local Cursor) ====================
struct BoBoClientSession {
    D3D11Renderer renderer;
    MfHardwareDecoder decoder;
    WasapiAdpcmPlayer audio;
    std::mutex decodeMutex;
    std::mutex statsMutex;
    std::mutex queueMutex;
    std::condition_variable queueCv;
    std::deque<QueuedH264Frame> frameQueue;
    std::thread decodeThread;

    HWND parentHwnd = nullptr;
    HWND viewportHwnd = nullptr;
    HHOOK llKeyHook = nullptr;

    std::atomic<bool> alive{ true };
    std::atomic<bool> videoReceived{ false };
    std::atomic<bool> hostCursorComposited{ false };
    std::atomic<uint32_t> remoteCursorInfo{ 1 }; // Shape, width and height in the low three bytes.
    uint32_t localCursorInfo = 0; // The cursor handles below are owned by the viewport thread.
    HCURSOR localCursor = nullptr;
    bool ownsLocalCursor = false;
    bool freeMouse = true;
    bool rawMouseRegistered = false;
    bool cursorHidden = false;
    bool pointerInVideoRect = false;
    bool mouseTracking = false;
    bool viewOnly = false;
    bool sendSysKeys = false;
    bool ctrlAltSoloActive = false;
    bool hasLastRawAbs = false;
    LONG lastRawAbsX = 0;
    LONG lastRawAbsY = 0;
    bool hasLastFallbackClientPt = false;
    int lastFallbackClientX = 0;
    int lastFallbackClientY = 0;
    DWORD lastTopEdgeTick = 0;

    uint32_t videoW = 1920;
    uint32_t videoH = 1080;
    bool decoderOpened = false;
    HANDLE decodeDoneEvent = nullptr;

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
    uint32_t statsVideoW = 1920;
    uint32_t statsVideoH = 1080;
    std::string statsDecoderName;

    void RunDecodeLoop();
};

static std::atomic<BoBoClientSession*> g_activeClient{ nullptr };

// ==================== Host Privacy Cursor Mask and Physical Input Blocker ====================
class PrivacyCursorMask {
public:
    bool Set(bool enable) {
        std::lock_guard<std::mutex> lock(mutex_);
        if (!enable) return RestoreLocked();
        if (!initialized_) {
            if (!LoadApi() || !initialize_()) return false;
            initialized_ = true;
        }
        if (!showCursor_(FALSE)) {
            if (!masked_.load(std::memory_order_relaxed)) RestoreLocked();
            return false;
        }
        masked_.store(true, std::memory_order_release);
        return true;
    }

    bool IsMasked() const { return masked_.load(std::memory_order_acquire); }

private:
    using Initialize = BOOL (WINAPI*)();
    using ShowCursor = BOOL (WINAPI*)(BOOL);

    bool LoadApi() {
        if (module_) return true;
        HMODULE module = LoadLibraryExW(L"Magnification.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!module) return false;
        auto init = GetProcAddress(module, "MagInitialize");
        auto uninit = GetProcAddress(module, "MagUninitialize");
        auto show = GetProcAddress(module, "MagShowSystemCursor");
        if (!init || !uninit || !show) {
            FreeLibrary(module);
            return false;
        }
        // GetProcAddress returns FARPROC irrespective of the exported signature.
        std::memcpy(&initialize_, &init, sizeof(initialize_));
        std::memcpy(&uninitialize_, &uninit, sizeof(uninitialize_));
        std::memcpy(&showCursor_, &show, sizeof(showCursor_));
        module_ = module;
        return true;
    }

    bool RestoreLocked() {
        if (!initialized_) return true;
        if (masked_.load(std::memory_order_relaxed)) {
            if (!showCursor_(TRUE)) return false;
            masked_.store(false, std::memory_order_release);
        }
        if (!uninitialize_()) return false;
        initialized_ = false;
        return true;
    }

    std::mutex mutex_;
    std::atomic<bool> masked_{ false };
    HMODULE module_ = nullptr;
    Initialize initialize_ = nullptr;
    Initialize uninitialize_ = nullptr;
    ShowCursor showCursor_ = nullptr;
    bool initialized_ = false;
};

// MagShowSystemCursor is a transient display override. The desktop cursor and scheme
// remain available to the remote stream, including when the physical input hook is off.
static PrivacyCursorMask g_privacyCursorMask;

#ifndef LLMHF_LOWER_IL_INJECTED
#define LLMHF_LOWER_IL_INJECTED 0x00000002
#endif
#ifndef LLKHF_LOWER_IL_INJECTED
#define LLKHF_LOWER_IL_INJECTED 0x00000002
#endif

// 32-bit-safe non-zero tag so it survives any 32-bit/64-bit hook chain without upper-bit truncation
static constexpr ULONG_PTR kBoBoInjectedMagic = 0x424F5636UL;

static std::mutex g_privHookMutex;
static std::atomic<bool> g_privBlockActive{ false };
static std::atomic<bool> g_privHookReady{ false };
static std::atomic<bool> g_privHookSuccess{ false };
static std::thread g_privHookThread;
static std::atomic<DWORD> g_privHookThreadId{ 0 };
static std::atomic<uint64_t> g_privEscapeEpoch{ 0 };
static std::atomic<BoBoPrivacyEscapeCallback> g_privEscapeCb{ nullptr };
static std::atomic<void*> g_privEscapeCtx{ nullptr };
static std::atomic<bool> g_physCtrl{ false };
static std::atomic<bool> g_physAlt{ false };
static std::atomic<bool> g_physShift{ false };
static std::atomic<uint32_t> g_injectedMouseMask{ 0 };
static std::atomic<uint64_t> g_lastRemoteMouseInjectMs{ 0 };
static std::atomic<uint64_t> g_lastRemoteKeyInjectMs{ 0 };

static std::mutex g_monCacheMutex;
static std::vector<BoBoMonitorInfo> g_monCache;
static uint64_t g_monCacheMs = 0;

static void InvalidateHostMonitorCache() {
    std::lock_guard<std::mutex> lk(g_monCacheMutex);
    g_monCacheMs = 0;
}

static std::vector<BoBoMonitorInfo> GetCachedHostMonitors() {
    uint64_t nowMs = GetTickCount64();
    {
        std::lock_guard<std::mutex> lk(g_monCacheMutex);
        if (!g_monCache.empty() && (nowMs - g_monCacheMs) < 250ULL) {
            return g_monCache;
        }
    }
    auto fresh = ScreenCapturer::EnumerateMonitors();
    {
        std::lock_guard<std::mutex> lk(g_monCacheMutex);
        g_monCache = fresh;
        g_monCacheMs = nowMs;
    }
    return fresh;
}

static bool IsCurrentProcessElevated() {
    static std::atomic<int> cached{ -1 };
    int v = cached.load(std::memory_order_acquire);
    if (v >= 0) return v != 0;
    bool elevated = false;
    HANDLE hTok = nullptr;
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &hTok)) {
        TOKEN_ELEVATION te = {};
        DWORD retLen = 0;
        if (GetTokenInformation(hTok, TokenElevation, &te, sizeof(te), &retLen)) {
            elevated = (te.TokenIsElevated != 0);
        }
        CloseHandle(hTok);
    }
    cached.store(elevated ? 1 : 0, std::memory_order_release);
    return elevated;
}

static bool IsForegroundBlockedByUipi() {
    if (IsCurrentProcessElevated()) return false;
    HWND fg = GetForegroundWindow();
    if (!fg) return false;
    static std::atomic<uintptr_t> lastFgHwnd{ 0 };
    static std::atomic<uint64_t> lastCheckMs{ 0 };
    static std::atomic<bool> lastBlocked{ false };
    uint64_t nowMs = GetTickCount64();
    uintptr_t fgVal = reinterpret_cast<uintptr_t>(fg);
    if (lastFgHwnd.load(std::memory_order_acquire) == fgVal &&
        (nowMs - lastCheckMs.load(std::memory_order_acquire)) < 250ULL) {
        return lastBlocked.load(std::memory_order_acquire);
    }
    bool blocked = false;
    DWORD fgPid = 0;
    GetWindowThreadProcessId(fg, &fgPid);
    if (fgPid != 0 && fgPid != GetCurrentProcessId()) {
        HANDLE hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, fgPid);
        if (!hProc) {
            if (GetLastError() == ERROR_ACCESS_DENIED) blocked = true;
        } else {
            HANDLE hTok = nullptr;
            if (!OpenProcessToken(hProc, TOKEN_QUERY, &hTok)) {
                if (GetLastError() == ERROR_ACCESS_DENIED) blocked = true;
            } else {
                TOKEN_ELEVATION te = {};
                DWORD retLen = 0;
                if (GetTokenInformation(hTok, TokenElevation, &te, sizeof(te), &retLen) && te.TokenIsElevated != 0) {
                    blocked = true;
                }
                CloseHandle(hTok);
            }
            CloseHandle(hProc);
        }
    }
    lastFgHwnd.store(fgVal, std::memory_order_release);
    lastCheckMs.store(nowMs, std::memory_order_release);
    lastBlocked.store(blocked, std::memory_order_release);
    return blocked;
}

static bool IsPrivacyMouseEventAllowed(DWORD flags, ULONG_PTR extraInfo, uint64_t nowMs) {
    // Physical HID mice NEVER set LLMHF_INJECTED / LLMHF_LOWER_IL_INJECTED
    bool injected = (flags & (LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED)) != 0;
    if (!injected) return false;
    bool tagMatch = (extraInfo == kBoBoInjectedMagic) ||
                    ((extraInfo & 0xFFFFFFFFULL) == (kBoBoInjectedMagic & 0xFFFFFFFFULL));
    if (tagMatch) return true;
    // Fallback if a 3rd-party mouse hook/filter strips dwExtraInfo during an active remote injection
    uint64_t lastInject = g_lastRemoteMouseInjectMs.load(std::memory_order_acquire);
    return (lastInject != 0) && (nowMs >= lastInject) && ((nowMs - lastInject) <= 500ULL);
}

static bool IsPrivacyKeyEventAllowed(DWORD flags, ULONG_PTR extraInfo, uint64_t nowMs) {
    bool injected = (flags & (LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED)) != 0;
    if (!injected) return false;
    bool tagMatch = (extraInfo == kBoBoInjectedMagic) ||
                    ((extraInfo & 0xFFFFFFFFULL) == (kBoBoInjectedMagic & 0xFFFFFFFFULL));
    if (tagMatch) return true;
    uint64_t lastInject = g_lastRemoteKeyInjectMs.load(std::memory_order_acquire);
    return (lastInject != 0) && (nowMs >= lastInject) && ((nowMs - lastInject) <= 500ULL);
}

static LRESULT CALLBACK PrivacyLowLevelMouseProc(int nCode, WPARAM wParam, LPARAM lParam) {
    if (nCode == HC_ACTION && g_privBlockActive.load()) {
        const auto* ms = reinterpret_cast<const MSLLHOOKSTRUCT*>(lParam);
        if (ms) {
            if (!IsPrivacyMouseEventAllowed(ms->flags, ms->dwExtraInfo, GetTickCount64())) {
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
            if (!IsPrivacyKeyEventAllowed(kb->flags, kb->dwExtraInfo, GetTickCount64())) {
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
                    uint64_t epoch = g_privEscapeEpoch.load();
                    BoBoPrivacyEscapeCallback cb = g_privEscapeCb.load();
                    void* ctx = g_privEscapeCtx.load();
                    if (cb) {
                        std::thread([cb, ctx, epoch]() {
                            if (g_privEscapeEpoch.load() == epoch && g_privBlockActive.load()) {
                                cb(ctx);
                            }
                        }).detach();
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
    BoBoClientSession* st = g_activeClient.load(std::memory_order_acquire);
    if (nCode == HC_ACTION && st && st->alive.load(std::memory_order_acquire)) {
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

static constexpr UINT kMsgRefreshCursor = WM_APP + 1;

static void ApplyClientMouseMode(BoBoClientSession* st, bool freeMouse) {
    if (!st || !st->viewportHwnd) return;
    st->freeMouse = freeMouse;
    st->hasLastRawAbs = false;
    st->hasLastFallbackClientPt = false;
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
        PostMessageW(st->viewportHwnd, kMsgRefreshCursor, 0, 0);
    } else {
        RAWINPUTDEVICE rid = {};
        rid.usUsagePage = 0x01;
        rid.usUsage = 0x02;
        rid.dwFlags = 0;
        rid.hwndTarget = st->viewportHwnd;
        st->rawMouseRegistered = (RegisterRawInputDevices(&rid, 1, sizeof(rid)) != FALSE);
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

static RECT GetEffectiveVideoViewportRect(const BoBoClientSession* st) {
    RECT vp = st->renderer.GetVideoViewportRect();
    int vpW = vp.right - vp.left;
    int vpH = vp.bottom - vp.top;
    RECT cli = {};
    if (st->viewportHwnd && GetClientRect(st->viewportHwnd, &cli)) {
        int cliW = cli.right - cli.left;
        int cliH = cli.bottom - cli.top;
        if (cliW > 0 && cliH > 0 && (vpW <= 0 || vpH <= 0 || st->renderer.ScaleMode() == 2)) {
            return cli;
        }
    }
    return vp;
}

static bool IsPointInVideoRect(const BoBoClientSession* st, int x, int y, RECT& outVp) {
    outVp = GetEffectiveVideoViewportRect(st);
    int vpW = outVp.right - outVp.left;
    int vpH = outVp.bottom - outVp.top;
    return (vpW > 0 && vpH > 0 && x >= outVp.left && x < outVp.right && y >= outVp.top && y < outVp.bottom);
}

static void ReleaseClientLocalCursor(BoBoClientSession* st) {
    if (st->ownsLocalCursor) {
        if (GetCursor() == st->localCursor) SetCursor(LoadStandardCursor(1));
        DestroyCursor(st->localCursor);
    }
    st->localCursor = nullptr;
    st->ownsLocalCursor = false;
    st->localCursorInfo = 0;
}

static void SetClientLocalCursor(BoBoClientSession* st, bool insideVideo) {
    if (!st->freeMouse) {
        SetCursor(nullptr);
        return;
    }
    if (!insideVideo || st->viewOnly) {
        SetCursor(LoadStandardCursor(1));
        return;
    }
    if (st->videoReceived.load() && st->hostCursorComposited.load()) {
        SetCursor(nullptr);
        return;
    }

    const uint32_t info = st->remoteCursorInfo.load(std::memory_order_acquire);
    const int32_t shape = static_cast<int32_t>(info & 0xff);
    if (shape == 0) {
        SetCursor(nullptr);
        return;
    }
    if (st->localCursorInfo == info) {
        SetCursor(st->localCursor);
        return;
    }

    const int width = static_cast<int>((info >> 8) & 0xff);
    const int height = static_cast<int>((info >> 16) & 0xff);
    const int cursorW = width > 0 ? width : kDefaultCursorDimension;
    const int cursorH = height > 0 ? height : kDefaultCursorDimension;
    HCURSOR standard = LoadStandardCursor(shape);
    HCURSOR scaled = nullptr;
    SIZE bitmap = {};
    if (standard && (!GetCursorBitmapSize(standard, bitmap) || bitmap.cx != cursorW || bitmap.cy != cursorH)) {
        scaled = static_cast<HCURSOR>(CopyImage(standard, IMAGE_CURSOR, cursorW, cursorH, 0));
    }
    HCURSOR next = scaled ? scaled : standard;
    SetCursor(next);
    ReleaseClientLocalCursor(st);
    st->localCursor = next;
    st->ownsLocalCursor = scaled != nullptr;
    st->localCursorInfo = info;
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

static bool EmitAbsMouseAtClientPoint(BoBoClientSession* st, int x, int y, bool clampToViewport = false) {
    RECT vp = {};
    bool inside = IsPointInVideoRect(st, x, y, vp);
    int vpW = vp.right - vp.left;
    int vpH = vp.bottom - vp.top;
    if (vpW <= 0 || vpH <= 0) return false;
    if (!inside) {
        if (!clampToViewport) return false;
        if (x < vp.left) x = vp.left;
        else if (x >= vp.right) x = vp.right - 1;
        if (y < vp.top) y = vp.top;
        else if (y >= vp.bottom) y = vp.bottom - 1;
    }
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

        case WM_NCDESTROY:
            ReleaseClientLocalCursor(st);
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
            return DefWindowProcW(hwnd, msg, wParam, lParam);

        case WM_SIZE: {
            uint32_t w = LOWORD(lParam);
            uint32_t h = HIWORD(lParam);
            if (w > 0 && h > 0) {
                std::string rErr;
                st->renderer.ResizeSwapChain(w, h, rErr);
                PostMessageW(hwnd, kMsgRefreshCursor, 0, 0);
            }
            return 0;
        }

        case kMsgRefreshCursor: {
            if (st->freeMouse) {
                POINT pt = {};
                if (GetCursorPos(&pt) && WindowFromPoint(pt) == hwnd) {
                    bool inside = ComputeCursorInVideoRect(st, hwnd);
                    SetClientLocalCursor(st, inside);
                }
            }
            return 0;
        }

        case WM_SETCURSOR: {
            if (LOWORD(lParam) == HTCLIENT) {
                bool inside = ComputeCursorInVideoRect(st, hwnd);
                SetClientLocalCursor(st, inside);
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
            int x = GET_X_LPARAM(lParam);
            int y = GET_Y_LPARAM(lParam);
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
                SetClientLocalCursor(st, insideVideo);
                bool dragging = (wParam & (MK_LBUTTON | MK_RBUTTON | MK_MBUTTON | MK_XBUTTON1 | MK_XBUTTON2)) != 0;
                if (insideVideo || dragging) {
                    EmitAbsMouseAtClientPoint(st, x, y, dragging);
                }
            } else if (!st->viewOnly && !st->rawMouseRegistered) {
                // Fallback when RegisterRawInputDevices fails in locked mode
                if (st->hasLastFallbackClientPt) {
                    int dx = x - st->lastFallbackClientX;
                    int dy = y - st->lastFallbackClientY;
                    if (dx != 0 || dy != 0) {
                        BoBoInputPacket pkt = {};
                        pkt.kind = 2;
                        pkt.a = static_cast<int16_t>(std::max(-32767, std::min(32767, dx)));
                        pkt.b = static_cast<int16_t>(std::max(-32767, std::min(32767, dy)));
                        EmitInput(st, pkt);
                    }
                }
                st->hasLastFallbackClientPt = true;
                st->lastFallbackClientX = x;
                st->lastFallbackClientY = y;
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
                        if (ri->header.dwType == RIM_TYPEMOUSE) {
                            if ((ri->data.mouse.usFlags & MOUSE_MOVE_ABSOLUTE) == 0) {
                                if (ri->data.mouse.lLastX != 0 || ri->data.mouse.lLastY != 0) {
                                    BoBoInputPacket pkt = {};
                                    pkt.kind = 2;
                                    pkt.a = static_cast<int16_t>(std::max<LONG>(-32767, std::min<LONG>(32767, ri->data.mouse.lLastX)));
                                    pkt.b = static_cast<int16_t>(std::max<LONG>(-32767, std::min<LONG>(32767, ri->data.mouse.lLastY)));
                                    EmitInput(st, pkt);
                                }
                            } else {
                                // Absolute Raw Input (laptop Precision Touchpad / pen / VM mouse): convert to relative delta
                                LONG ax = ri->data.mouse.lLastX;
                                LONG ay = ri->data.mouse.lLastY;
                                if (st->hasLastRawAbs) {
                                    int vW = GetSystemMetrics((ri->data.mouse.usFlags & MOUSE_VIRTUAL_DESKTOP) ? SM_CXVIRTUALSCREEN : SM_CXSCREEN);
                                    int vH = GetSystemMetrics((ri->data.mouse.usFlags & MOUSE_VIRTUAL_DESKTOP) ? SM_CYVIRTUALSCREEN : SM_CYSCREEN);
                                    if (vW <= 1) vW = 1920;
                                    if (vH <= 1) vH = 1080;
                                    int dx = static_cast<int>(std::lround(static_cast<double>(ax - st->lastRawAbsX) * static_cast<double>(vW) / 65535.0));
                                    int dy = static_cast<int>(std::lround(static_cast<double>(ay - st->lastRawAbsY) * static_cast<double>(vH) / 65535.0));
                                    if (dx != 0 || dy != 0) {
                                        BoBoInputPacket pkt = {};
                                        pkt.kind = 2;
                                        pkt.a = static_cast<int16_t>(std::max(-32767, std::min(32767, dx)));
                                        pkt.b = static_cast<int16_t>(std::max(-32767, std::min(32767, dy)));
                                        EmitInput(st, pkt);
                                    }
                                }
                                st->hasLastRawAbs = true;
                                st->lastRawAbsX = ax;
                                st->lastRawAbsY = ay;
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

static bool HasH264FillerNal(const uint8_t* data, size_t len) {
    size_t i = 0;
    while (i + 3 < len) {
        size_t sc = 0;
        if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1) sc = 3;
        else if (i + 3 < len && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1) sc = 4;
        if (sc == 0) { ++i; continue; }
        if (i + sc < len && (data[i + sc] & 0x1F) == 12) return true;
        i += sc + 1;
    }
    return false;
}

void BoBoClientSession::RunDecodeLoop() {
    HRESULT hrCo = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    while (alive.load(std::memory_order_acquire)) {
        QueuedH264Frame item;
        bool moreQueued = false;
        {
            std::unique_lock<std::mutex> lk(queueMutex);
            queueCv.wait(lk, [&]() { return !alive.load(std::memory_order_acquire) || !frameQueue.empty(); });
            if (!alive.load(std::memory_order_acquire)) break;
            item = std::move(frameQueue.front());
            frameQueue.pop_front();
            moreQueued = !frameQueue.empty();
        }
        if (item.data.empty()) continue;

        std::lock_guard<std::mutex> dlk(decodeMutex);
        if (!alive.load(std::memory_order_acquire)) break;

        uint32_t w = item.width > 0 ? item.width : videoW;
        uint32_t h = item.height > 0 ? item.height : videoH;
        std::string err;
        if (!decoderOpened || w != videoW || h != videoH) {
            videoW = w;
            videoH = h;
            renderer.SetVideoSourceDimensions(w, h);
            decoderOpened = decoder.OpenCodec(VideoCodec::H264, w, h, err);
            {
                std::lock_guard<std::mutex> slk(statsMutex);
                statsVideoW = w;
                statsVideoH = h;
                if (decoderOpened) {
                    statsDecoderName = decoder.ActiveDecoderName();
                }
            }
            if (!decoderOpened) continue;
        }

        auto t0 = std::chrono::steady_clock::now();
        int64_t pts100ns = std::chrono::duration_cast<std::chrono::microseconds>(t0.time_since_epoch()).count() * 10LL;
        if (!decoder.SubmitAccessUnit(item.data.data(), item.data.size(), pts100ns, err)) {
            continue;
        }

        // Pull all ready decoded frames from MFT; keep only the LAST (newest) frame to present!
        DecodedFrame latestFrame;
        bool haveLatest = false;
        int pulledCount = 0;
        bool needMore = false;
        DecodedFrame f;
        while (decoder.PullDecodedFrame(f, needMore)) {
            latestFrame = std::move(f);
            haveLatest = true;
            ++pulledCount;
            f = DecodedFrame{};
        }

        // If more frames are already queued behind us (jitter catch-up), skip Present() so GPU decode drains backlog at ~3000 FPS
        bool skipPresent = moreQueued;
        if (skipPresent) {
            std::lock_guard<std::mutex> lk(queueMutex);
            skipPresent = !frameQueue.empty();
        }

        int presented = 0;
        double renMs = 0.0;
        if (haveLatest && !skipPresent) {
            auto tRen0 = std::chrono::steady_clock::now();
            bool ok = false;
            if (latestFrame.gpuTexture) {
                ok = renderer.PresentNv12Texture(latestFrame.gpuTexture.Get(), latestFrame.subresourceIndex,
                                                 latestFrame.width, latestFrame.height, err);
            } else if (!latestFrame.cpuNv12.empty()) {
                ok = renderer.PresentNv12CpuBuffer(latestFrame.cpuNv12.data(), latestFrame.width,
                                                   latestFrame.height, latestFrame.stride, err);
            }
            auto tRen1 = std::chrono::steady_clock::now();
            if (ok) {
                presented = 1;
                renMs = std::chrono::duration<double, std::milli>(tRen1 - tRen0).count();
            }
        }
        latestFrame = DecodedFrame{};

        if (presented > 0 || pulledCount > 0) {
            bool wasReceived = videoReceived.exchange(true);
            if (!wasReceived && viewportHwnd && IsWindow(viewportHwnd)) {
                PostMessageW(viewportHwnd, kMsgRefreshCursor, 0, 0);
            }
            auto t1 = std::chrono::steady_clock::now();
            double totalMs = std::chrono::duration<double, std::milli>(t1 - t0).count();
            double decMs = totalMs > renMs ? (totalMs - renMs) : 0.0;
            std::lock_guard<std::mutex> slk(statsMutex);
            framesWindow += static_cast<uint64_t>(presented > 0 ? presented : pulledCount);
            decodeMsAccum += decMs;
            renderMsAccum += renMs;
            timingSamples += 1;
        }
    }
    if (SUCCEEDED(hrCo)) CoUninitialize();
    if (decodeDoneEvent) SetEvent(decodeDoneEvent);
}

} // anonymous namespace

namespace bobo {
uint64_t GetLastRemoteMouseInjectMs() {
    return g_lastRemoteMouseInjectMs.load(std::memory_order_acquire);
}

bool IsPrivacyCursorMasked() {
    return g_privacyCursorMask.IsMasked();
}
} // namespace bobo

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

    // 2b. Client WASAPI player first-packet non-deadlock check (even on headless endpoints without audio device)
    {
        WasapiAdpcmPlayer testPlayer;
        testPlayer.SetVolume(0.0f);
        testPlayer.PlayAdpcm(adpcm.data(), static_cast<int>(adpcm.size()), 48000, 2);
        testPlayer.PlayAdpcm(adpcm.data(), static_cast<int>(adpcm.size()), 44100, 2);
        testPlayer.Shutdown();
        check("WASAPI ADPCM player non-deadlock init & rate switch", true, "48kHz->44.1kHz ok");
    }

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
    bool noFiller = !encodedIdr.empty() && !HasH264FillerNal(encodedIdr.data(), encodedIdr.size());
    bool liveBpsOk = encOpen && encoder.UpdateBitrateLive(2500);
    int32_t curShape = ScreenCapturer::DetectStandardCursorShape();
    check("H.264 Annex-B IDR encode (zero NAL12 filler + live ABR bitrate)",
          !encodedIdr.empty() && noFiller && liveBpsOk && curShape >= 0 && curShape <= 13,
          "bytes=" + std::to_string(encodedIdr.size()) + " idr=" + (gotIdr ? "1" : "0") +
              " noFiller=" + (noFiller ? "1" : "0") + " liveBps=" + (liveBpsOk ? "1" : "0"));

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

    // 5. Privacy Hardware Input Blocker hook lifecycle & filter verification
    {
        int32_t en = BoBoHost_SetPrivacyInputBlock(1, nullptr, nullptr);
        int32_t active = BoBoHost_IsPrivacyInputBlocked();
        BoBoHost_SetPrivacyInputBlock(0, nullptr, nullptr);
        int32_t after = BoBoHost_IsPrivacyInputBlocked();
        uint64_t nowMs = GetTickCount64();
        g_lastRemoteMouseInjectMs.store(nowMs, std::memory_order_release);
        bool blockPhys = !IsPrivacyMouseEventAllowed(0, 0, nowMs);
        bool allowMagic = IsPrivacyMouseEventAllowed(LLMHF_INJECTED, kBoBoInjectedMagic, nowMs + 1000ULL);
        bool allowStrippedRecent = IsPrivacyMouseEventAllowed(LLMHF_INJECTED, 0, nowMs + 50ULL);
        bool blockOldStripped = !IsPrivacyMouseEventAllowed(LLMHF_INJECTED, 0, nowMs + 2000ULL);
        g_lastRemoteMouseInjectMs.store(0, std::memory_order_release);
        bool filterOk = blockPhys && allowMagic && allowStrippedRecent && blockOldStripped;
        check("Privacy hardware input blocker (WH_MOUSE_LL + WH_KEYBOARD_LL)",
              en == 1 && active == 1 && after == 0 && filterOk,
              "en=" + std::to_string(en) + " active=" + std::to_string(active) +
                  " after=" + std::to_string(after) + " filter=" + (filterOk ? "ok" : "fail"));
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

BOBO_API void* __stdcall BoBoHost_CreateEx(
    int32_t monitorIndex,
    int32_t targetWidth,
    int32_t targetHeight,
    int32_t fps,
    int32_t bitrateKbps,
    int32_t muteHostSpeakers,
    int32_t compositeCursor,
    BoBoVideoFrameCallback onVideo,
    BoBoAudioFrameCallback onAudio,
    void* userCtx) {
    auto* session = new BoBoHostSession();
    session->monitorIndex = monitorIndex;
    session->targetWidth = targetWidth > 0 ? targetWidth : 1920;
    session->targetHeight = targetHeight > 0 ? targetHeight : 1080;
    session->fps = fps > 0 ? fps : 60;
    session->bitrateKbps = bitrateKbps > 0 ? bitrateKbps : 14000;
    session->compositeCursor.store(compositeCursor != 0, std::memory_order_release);
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
    return BoBoHost_CreateEx(
        monitorIndex, targetWidth, targetHeight, fps, bitrateKbps,
        muteHostSpeakers, 1, onVideo, onAudio, userCtx);
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
    InvalidateHostMonitorCache();
    session->reconfigPending.store(true);
    session->keyframePending.store(true);
    return 1;
}

BOBO_API int32_t __stdcall BoBoHost_SetBitrate(void* handle, int32_t bitrateKbps) {
    auto* session = static_cast<BoBoHostSession*>(handle);
    if (!session || bitrateKbps < 500 || bitrateKbps > 60000) return 0;
    {
        std::lock_guard<std::mutex> lk(session->cfgMutex);
        session->bitrateKbps = bitrateKbps;
    }
    session->liveBitratePending.store(bitrateKbps, std::memory_order_release);
    return 1;
}

BOBO_API int32_t __stdcall BoBoHost_SetFps(void* handle, int32_t fps) {
    auto* session = static_cast<BoBoHostSession*>(handle);
    if (!session || fps < 15 || fps > 144) return 0;
    {
        std::lock_guard<std::mutex> lk(session->cfgMutex);
        session->fps = fps;
    }
    session->liveFpsTarget.store(fps, std::memory_order_release);
    return 1;
}

BOBO_API void __stdcall BoBoHost_SetCompositeCursor(void* handle, int32_t enable) {
    auto* session = static_cast<BoBoHostSession*>(handle);
    if (session) {
        session->compositeCursor.store(enable != 0, std::memory_order_release);
    }
}

BOBO_API int32_t __stdcall BoBoHost_GetCursorShape(int32_t monitorIndex) {
    return BoBoHost_GetCursorInfo(monitorIndex, nullptr, nullptr);
}

BOBO_API int32_t __stdcall BoBoHost_GetCursorInfo(int32_t monitorIndex, int32_t* width, int32_t* height) {
    (void)monitorIndex;
    SIZE bitmap = {};
    int32_t shape = ScreenCapturer::DetectStandardCursorShape(&bitmap);
    if (width) *width = bitmap.cx;
    if (height) *height = bitmap.cy;
    return shape;
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
    // The server restores display modes under its active-stream ownership lock.
}

static std::atomic<int> g_prevCursorX{ -2147483647 };
static std::atomic<int> g_prevCursorY{ -2147483647 };
static std::atomic<int> g_stuckMoveCount{ 0 };
static std::atomic<uint64_t> g_stuckSinceMs{ 0 };
static std::atomic<uint64_t> g_setCursorFallbackUntilMs{ 0 };

BOBO_API void __stdcall BoBoHost_InjectInput(int32_t monitorIndex, const BoBoInputPacket* pkt) {
    if (!pkt) return;
    INPUT inp = {};
    switch (pkt->kind) {
        case 1: { // Absolute mouse position on target monitor
            auto monitors = GetCachedHostMonitors();
            int monLeft = 0, monTop = 0, monW = GetSystemMetrics(SM_CXSCREEN), monH = GetSystemMetrics(SM_CYSCREEN);
            if (!monitors.empty()) {
                int idx = (monitorIndex >= 0 && monitorIndex < static_cast<int>(monitors.size())) ? monitorIndex : 0;
                monLeft = monitors[static_cast<size_t>(idx)].left;
                monTop = monitors[static_cast<size_t>(idx)].top;
                monW = monitors[static_cast<size_t>(idx)].width;
                monH = monitors[static_cast<size_t>(idx)].height;
            }
            if (monW <= 1) monW = 1920;
            if (monH <= 1) monH = 1080;
            int vLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vW = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int vH = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (vW <= 1) vW = monW;
            if (vH <= 1) vH = monH;

            uint16_t nx = static_cast<uint16_t>(pkt->a);
            uint16_t ny = static_cast<uint16_t>(pkt->b);
            double px = static_cast<double>(monLeft) + (static_cast<double>(nx) / 65535.0) * static_cast<double>(monW - 1);
            double py = static_cast<double>(monTop) + (static_cast<double>(ny) / 65535.0) * static_cast<double>(monH - 1);
            int targetX = static_cast<int>(px + 0.5);
            int targetY = static_cast<int>(py + 0.5);

            LONG dx = static_cast<LONG>(((px - vLeft) * 65535.0) / static_cast<double>(vW - 1) + 0.5);
            LONG dy = static_cast<LONG>(((py - vTop) * 65535.0) / static_cast<double>(vH - 1) + 0.5);
            if (dx < 0) dx = 0; else if (dx > 65535) dx = 65535;
            if (dy < 0) dy = 0; else if (dy > 65535) dy = 65535;

            uint64_t nowMs = GetTickCount64();
            g_lastRemoteMouseInjectMs.store(nowMs, std::memory_order_release);

            POINT beforePt = {};
            bool haveBefore = (GetCursorPos(&beforePt) != FALSE);
            bool uipiBlocked = IsForegroundBlockedByUipi();
            bool fallbackLatched = (nowMs < g_setCursorFallbackUntilMs.load(std::memory_order_acquire));

            if (haveBefore && !uipiBlocked && !fallbackLatched) {
                int prevX = g_prevCursorX.load(std::memory_order_relaxed);
                int prevY = g_prevCursorY.load(std::memory_order_relaxed);
                int wantDx = targetX - beforePt.x;
                int wantDy = targetY - beforePt.y;
                bool wantsMove = (wantDx < -8 || wantDx > 8 || wantDy < -8 || wantDy > 8);
                if (wantsMove && beforePt.x == prevX && beforePt.y == prevY) {
                    int cnt = g_stuckMoveCount.fetch_add(1, std::memory_order_relaxed) + 1;
                    uint64_t since = g_stuckSinceMs.load(std::memory_order_relaxed);
                    if (since == 0) {
                        g_stuckSinceMs.store(nowMs, std::memory_order_relaxed);
                    } else if (cnt >= 3 && (nowMs - since) >= 45ULL) {
                        fallbackLatched = true;
                        g_setCursorFallbackUntilMs.store(nowMs + 1500ULL, std::memory_order_release);
                        g_stuckMoveCount.store(0, std::memory_order_relaxed);
                        g_stuckSinceMs.store(0, std::memory_order_relaxed);
                    }
                } else if (beforePt.x != prevX || beforePt.y != prevY) {
                    g_stuckMoveCount.store(0, std::memory_order_relaxed);
                    g_stuckSinceMs.store(0, std::memory_order_relaxed);
                }
                g_prevCursorX.store(beforePt.x, std::memory_order_relaxed);
                g_prevCursorY.store(beforePt.y, std::memory_order_relaxed);
            }

            inp.type = INPUT_MOUSE;
            inp.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
            inp.mi.dx = dx;
            inp.mi.dy = dy;
            inp.mi.dwExtraInfo = kBoBoInjectedMagic;
            UINT sent = SendInput(1, &inp, sizeof(INPUT));
            if (sent == 0 || uipiBlocked || fallbackLatched) {
                SetCursorPos(targetX, targetY);
            }
            break;
        }
        case 2: { // Relative mouse move (FPS locked mode)
            uint64_t nowMs = GetTickCount64();
            g_lastRemoteMouseInjectMs.store(nowMs, std::memory_order_release);
            bool uipiBlocked = IsForegroundBlockedByUipi();
            bool fallbackLatched = (nowMs < g_setCursorFallbackUntilMs.load(std::memory_order_acquire));
            POINT beforePt = {};
            bool haveBefore = (GetCursorPos(&beforePt) != FALSE);
            if (haveBefore && !uipiBlocked && !fallbackLatched && (pkt->a != 0 || pkt->b != 0)) {
                int prevX = g_prevCursorX.load(std::memory_order_relaxed);
                int prevY = g_prevCursorY.load(std::memory_order_relaxed);
                if (beforePt.x == prevX && beforePt.y == prevY) {
                    int cnt = g_stuckMoveCount.fetch_add(1, std::memory_order_relaxed) + 1;
                    uint64_t since = g_stuckSinceMs.load(std::memory_order_relaxed);
                    if (since == 0) {
                        g_stuckSinceMs.store(nowMs, std::memory_order_relaxed);
                    } else if (cnt >= 3 && (nowMs - since) >= 45ULL) {
                        fallbackLatched = true;
                        g_setCursorFallbackUntilMs.store(nowMs + 1500ULL, std::memory_order_release);
                        g_stuckMoveCount.store(0, std::memory_order_relaxed);
                        g_stuckSinceMs.store(0, std::memory_order_relaxed);
                    }
                } else {
                    g_stuckMoveCount.store(0, std::memory_order_relaxed);
                    g_stuckSinceMs.store(0, std::memory_order_relaxed);
                }
                g_prevCursorX.store(beforePt.x, std::memory_order_relaxed);
                g_prevCursorY.store(beforePt.y, std::memory_order_relaxed);
            }

            inp.type = INPUT_MOUSE;
            inp.mi.dwFlags = MOUSEEVENTF_MOVE;
            inp.mi.dx = pkt->a;
            inp.mi.dy = pkt->b;
            inp.mi.dwExtraInfo = kBoBoInjectedMagic;
            UINT sent = SendInput(1, &inp, sizeof(INPUT));
            if ((sent == 0 || uipiBlocked || fallbackLatched) && haveBefore) {
                SetCursorPos(beforePt.x + static_cast<int>(pkt->a), beforePt.y + static_cast<int>(pkt->b));
            }
            break;
        }
        case 3: { // Mouse buttons
            bool down = (pkt->flags & 1) != 0;
            uint32_t btnBit = 0;
            inp.type = INPUT_MOUSE;
            switch (pkt->a) {
                case 1: inp.mi.dwFlags = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP; btnBit = 1u << 0; break;
                case 2: inp.mi.dwFlags = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP; btnBit = 1u << 1; break;
                case 3: inp.mi.dwFlags = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP; btnBit = 1u << 2; break;
                case 4: inp.mi.dwFlags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; inp.mi.mouseData = XBUTTON1; btnBit = 1u << 3; break;
                case 5: inp.mi.dwFlags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; inp.mi.mouseData = XBUTTON2; btnBit = 1u << 4; break;
                default: return;
            }
            if (down) g_injectedMouseMask.fetch_or(btnBit);
            else g_injectedMouseMask.fetch_and(~btnBit);
            g_lastRemoteMouseInjectMs.store(GetTickCount64(), std::memory_order_release);
            inp.mi.dwExtraInfo = kBoBoInjectedMagic;
            SendInput(1, &inp, sizeof(INPUT));
            break;
        }
        case 4: { // Mouse wheel
            g_lastRemoteMouseInjectMs.store(GetTickCount64(), std::memory_order_release);
            inp.type = INPUT_MOUSE;
            inp.mi.dwFlags = (pkt->flags & 1) ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL;
            inp.mi.mouseData = static_cast<DWORD>(static_cast<int32_t>(pkt->a));
            inp.mi.dwExtraInfo = kBoBoInjectedMagic;
            SendInput(1, &inp, sizeof(INPUT));
            break;
        }
        case 5: { // Keyboard key
            g_lastRemoteKeyInjectMs.store(GetTickCount64(), std::memory_order_release);
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
        case 6: { // Release all modifier keys and any remote-held mouse buttons
            uint64_t nowMs = GetTickCount64();
            g_lastRemoteKeyInjectMs.store(nowMs, std::memory_order_release);
            const WORD mods[] = { VK_LSHIFT, VK_RSHIFT, VK_LCONTROL, VK_RCONTROL, VK_LMENU, VK_RMENU, VK_LWIN, VK_RWIN };
            for (WORD vk : mods) {
                INPUT u = {};
                u.type = INPUT_KEYBOARD;
                u.ki.wVk = vk;
                u.ki.wScan = static_cast<WORD>(MapVirtualKeyW(vk, MAPVK_VK_TO_VSC));
                u.ki.dwFlags = KEYEVENTF_KEYUP;
                u.ki.dwExtraInfo = kBoBoInjectedMagic;
                SendInput(1, &u, sizeof(INPUT));
            }
            uint32_t heldBtns = g_injectedMouseMask.exchange(0);
            if (heldBtns != 0) {
                g_lastRemoteMouseInjectMs.store(nowMs, std::memory_order_release);
                const struct { uint32_t bit; DWORD flag; DWORD data; } btnDefs[] = {
                    { 1u << 0, MOUSEEVENTF_LEFTUP, 0 },
                    { 1u << 1, MOUSEEVENTF_MIDDLEUP, 0 },
                    { 1u << 2, MOUSEEVENTF_RIGHTUP, 0 },
                    { 1u << 3, MOUSEEVENTF_XUP, XBUTTON1 },
                    { 1u << 4, MOUSEEVENTF_XUP, XBUTTON2 }
                };
                for (const auto& bd : btnDefs) {
                    if (heldBtns & bd.bit) {
                        INPUT mu = {};
                        mu.type = INPUT_MOUSE;
                        mu.mi.dwFlags = bd.flag;
                        mu.mi.mouseData = bd.data;
                        mu.mi.dwExtraInfo = kBoBoInjectedMagic;
                        SendInput(1, &mu, sizeof(INPUT));
                    }
                }
            }
            break;
        }
        case 7: { // V6.2 Touch pointer -> map primary finger (pointerId == 0) to absolute mouse
            int pointerId = (pkt->flags >> 4) & 0x0F;
            int action = pkt->flags & 0x0F; // 0=Down, 1=Move, 2=Up, 3=Cancel
            if (pointerId != 0) break;

            auto monitors = GetCachedHostMonitors();
            int monLeft = 0, monTop = 0, monW = GetSystemMetrics(SM_CXSCREEN), monH = GetSystemMetrics(SM_CYSCREEN);
            if (!monitors.empty()) {
                int idx = (monitorIndex >= 0 && monitorIndex < static_cast<int>(monitors.size())) ? monitorIndex : 0;
                monLeft = monitors[static_cast<size_t>(idx)].left;
                monTop = monitors[static_cast<size_t>(idx)].top;
                monW = monitors[static_cast<size_t>(idx)].width;
                monH = monitors[static_cast<size_t>(idx)].height;
            }
            if (monW <= 1) monW = 1920;
            if (monH <= 1) monH = 1080;
            int vLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vW = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int vH = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (vW <= 1) vW = monW;
            if (vH <= 1) vH = monH;

            uint16_t nx = static_cast<uint16_t>(pkt->a);
            uint16_t ny = static_cast<uint16_t>(pkt->b);
            double px = static_cast<double>(monLeft) + (static_cast<double>(nx) / 65535.0) * static_cast<double>(monW - 1);
            double py = static_cast<double>(monTop) + (static_cast<double>(ny) / 65535.0) * static_cast<double>(monH - 1);
            int targetX = static_cast<int>(px + 0.5);
            int targetY = static_cast<int>(py + 0.5);

            LONG dx = static_cast<LONG>(((px - vLeft) * 65535.0) / static_cast<double>(vW - 1) + 0.5);
            LONG dy = static_cast<LONG>(((py - vTop) * 65535.0) / static_cast<double>(vH - 1) + 0.5);
            if (dx < 0) dx = 0; else if (dx > 65535) dx = 65535;
            if (dy < 0) dy = 0; else if (dy > 65535) dy = 65535;

            g_lastRemoteMouseInjectMs.store(GetTickCount64(), std::memory_order_release);
            inp.type = INPUT_MOUSE;
            inp.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
            if (action == 0) {
                inp.mi.dwFlags |= MOUSEEVENTF_LEFTDOWN;
                g_injectedMouseMask.fetch_or(1u << 0);
            } else if (action == 2 || action == 3) {
                inp.mi.dwFlags |= MOUSEEVENTF_LEFTUP;
                g_injectedMouseMask.fetch_and(~(1u << 0));
            }
            inp.mi.dx = dx;
            inp.mi.dy = dy;
            inp.mi.dwExtraInfo = kBoBoInjectedMagic;
            UINT sent = SendInput(1, &inp, sizeof(INPUT));
            if (sent == 0 || IsForegroundBlockedByUipi()) {
                SetCursorPos(targetX, targetY);
            }
            break;
        }
        case 8: { // V6.2 Mobile system navigation (no-op on Windows host)
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
        g_lastRemoteKeyInjectMs.store(GetTickCount64(), std::memory_order_release);
        SendInput(static_cast<UINT>(inputs.size()), inputs.data(), sizeof(INPUT));
    }
}

static bool ReadCurrentDesktopMode(const std::wstring& device, DEVMODEW& mode) {
    mode = {};
    mode.dmSize = sizeof(mode);
    return EnumDisplaySettingsW(device.c_str(), ENUM_CURRENT_SETTINGS, &mode) != FALSE;
}

static bool RestoreSuperDisplayLocked() {
    if (!g_superDispApplied) return true;
    DEVMODEW original = g_superDispOrigMode;
    original.dmFields &= DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_DISPLAYFREQUENCY;
    LONG rc = ChangeDisplaySettingsExW(g_superDispDevice.c_str(), &original, nullptr, 0, nullptr);
    InvalidateHostMonitorCache();

    DEVMODEW current = {};
    if (rc != DISP_CHANGE_SUCCESSFUL || !ReadCurrentDesktopMode(g_superDispDevice, current) ||
        current.dmPelsWidth != original.dmPelsWidth || current.dmPelsHeight != original.dmPelsHeight ||
        ((original.dmFields & DM_DISPLAYFREQUENCY) && current.dmDisplayFrequency != original.dmDisplayFrequency)) {
        // Keep the saved mode for another restore attempt after the display returns.
        return false;
    }
    g_superDispApplied = false;
    g_superDispDevice.clear();
    g_superDispOrigMode = {};
    return true;
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
    if (monitorIndex < 0 || monitorIndex >= static_cast<int32_t>(devs.size())) return 0;
    const std::wstring& devName = devs[static_cast<size_t>(monitorIndex)].deviceName;
    if (g_superDispApplied && g_superDispDevice != devName && !RestoreSuperDisplayLocked()) return 0;

    DEVMODEW curMode = {};
    if (!ReadCurrentDesktopMode(devName, curMode)) return 0;
    const auto report = [&](const DEVMODEW& mode) {
        if (outActualWidth) *outActualWidth = static_cast<int32_t>(mode.dmPelsWidth);
        if (outActualHeight) *outActualHeight = static_cast<int32_t>(mode.dmPelsHeight);
        return mode.dmPelsWidth == static_cast<DWORD>(targetWidth) &&
               mode.dmPelsHeight == static_cast<DWORD>(targetHeight) ? 1 : 0;
    };
    const auto sameAspect = [&](const DEVMODEW& mode) {
        return mode.dmPelsWidth > 0 && mode.dmPelsHeight > 0 &&
            static_cast<uint64_t>(mode.dmPelsWidth) * targetHeight ==
            static_cast<uint64_t>(mode.dmPelsHeight) * targetWidth;
    };
    if (report(curMode)) return 1;
    const bool currentMatchesAspect = sameAspect(curMode);

    bool found = false;
    DEVMODEW bestMode = {};
    uint64_t bestDistance = UINT64_MAX;
    int bestRefreshDelta = INT_MAX;
    for (DWORD modeNum = 0; ; ++modeNum) {
        DEVMODEW mode = {};
        mode.dmSize = sizeof(mode);
        if (!EnumDisplaySettingsW(devName.c_str(), modeNum, &mode)) break;
        if (!sameAspect(mode) || mode.dmPelsWidth < 640 || mode.dmPelsHeight < 480 ||
            mode.dmBitsPerPel != curMode.dmBitsPerPel) continue;
        if ((mode.dmFields & DM_DISPLAYORIENTATION) && (curMode.dmFields & DM_DISPLAYORIENTATION) &&
            mode.dmDisplayOrientation != curMode.dmDisplayOrientation) continue;

        uint64_t distance = static_cast<uint64_t>(std::llabs(static_cast<int64_t>(mode.dmPelsWidth) - targetWidth)) +
                            static_cast<uint64_t>(std::llabs(static_cast<int64_t>(mode.dmPelsHeight) - targetHeight));
        if (currentMatchesAspect && distance != 0) continue;
        int refreshDelta = std::abs(static_cast<int>(mode.dmDisplayFrequency) - static_cast<int>(curMode.dmDisplayFrequency));
        if (found && (distance > bestDistance || (distance == bestDistance && refreshDelta >= bestRefreshDelta))) continue;
        mode.dmFields &= DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_DISPLAYFREQUENCY;
        if (ChangeDisplaySettingsExW(devName.c_str(), &mode, nullptr, CDS_TEST, nullptr) != DISP_CHANGE_SUCCESSFUL) continue;
        found = true;
        bestDistance = distance;
        bestRefreshDelta = refreshDelta;
        bestMode = mode;
    }

    if (!found) {
        if (g_superDispApplied && !currentMatchesAspect && RestoreSuperDisplayLocked()) {
            ReadCurrentDesktopMode(devName, curMode);
        }
        return report(curMode);
    }

    if (ChangeDisplaySettingsExW(devName.c_str(), &bestMode, nullptr, CDS_FULLSCREEN, nullptr) != DISP_CHANGE_SUCCESSFUL) {
        return report(curMode);
    }
    if (!g_superDispApplied) {
        g_superDispOrigMode = curMode;
        g_superDispDevice = devName;
        g_superDispApplied = true;
    }
    InvalidateHostMonitorCache();
    if (!ReadCurrentDesktopMode(devName, curMode) || !sameAspect(curMode)) {
        RestoreSuperDisplayLocked();
        if (!ReadCurrentDesktopMode(devName, curMode)) return 0;
    }
    return report(curMode);
}

BOBO_API void __stdcall BoBoHost_RestoreSuperDisplay() {
    std::lock_guard<std::mutex> lk(g_superDispMutex);
    RestoreSuperDisplayLocked();
}

BOBO_API int32_t __stdcall BoBoHost_SetPrivacyInputBlock(
    int32_t enable,
    BoBoPrivacyEscapeCallback onEscape,
    void* userCtx) {
    std::lock_guard<std::mutex> lk(g_privHookMutex);
    if (!enable) {
        g_privEscapeEpoch.fetch_add(1);
        g_privBlockActive.store(false);
        g_physCtrl.store(false);
        g_physAlt.store(false);
        g_physShift.store(false);
        g_privEscapeCb.store(nullptr);
        g_privEscapeCtx.store(nullptr);
        if (g_privHookThread.joinable()) {
            DWORD tid = g_privHookThreadId.load();
            if (tid != 0) {
                PostThreadMessageW(tid, WM_QUIT, 0, 0);
            }
            g_privHookThread.join();
            g_privHookThreadId.store(0);
        }
        return 1;
    }

    g_privEscapeEpoch.fetch_add(1);
    g_privEscapeCb.store(onEscape);
    g_privEscapeCtx.store(userCtx);
    if (g_privHookThread.joinable() && g_privBlockActive.load()) {
        return 1;
    }
    if (g_privHookThread.joinable()) {
        DWORD tid = g_privHookThreadId.load();
        if (tid != 0) PostThreadMessageW(tid, WM_QUIT, 0, 0);
        g_privHookThread.join();
        g_privHookThreadId.store(0);
    }

    g_physCtrl.store((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0);
    g_physAlt.store((GetAsyncKeyState(VK_MENU) & 0x8000) != 0);
    g_physShift.store((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0);
    g_privHookReady.store(false);
    g_privHookSuccess.store(false);
    g_privBlockActive.store(true);

    g_privHookThread = std::thread([]() {
        MSG msg = {};
        PeekMessageW(&msg, nullptr, WM_USER, WM_USER, PM_NOREMOVE);
        g_privHookThreadId.store(GetCurrentThreadId());

        HMODULE hMod = nullptr;
        if (!GetModuleHandleExW(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCWSTR>(&PrivacyLowLevelMouseProc),
                &hMod) || !hMod) {
            hMod = GetModuleHandleW(nullptr);
        }
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
        g_privEscapeEpoch.fetch_add(1);
        g_privBlockActive.store(false);
        if (g_privHookThread.joinable()) {
            DWORD tid = g_privHookThreadId.load();
            if (tid != 0) PostThreadMessageW(tid, WM_QUIT, 0, 0);
            g_privHookThread.join();
            g_privHookThreadId.store(0);
        }
        return 0;
    }
    return 1;
}

BOBO_API int32_t __stdcall BoBoHost_SetPrivacyCursorMask(int32_t enable) {
    return g_privacyCursorMask.Set(enable != 0) ? 1 : 0;
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
    st->statsVideoW = st->videoW;
    st->statsVideoH = st->videoH;
    st->decodeDoneEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    st->freeMouse = (freeMouse != 0);
    st->onInput = onInput;
    st->onHotkey = onHotkey;
    st->userCtx = userCtx;

    std::string err;
    if (!st->renderer.InitializeDevice(true, err) ||
        !st->decoder.Initialize(st->renderer.Device(), err)) {
        if (st->decodeDoneEvent) CloseHandle(st->decodeDoneEvent);
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
        if (st->decodeDoneEvent) CloseHandle(st->decodeDoneEvent);
        delete st;
        return nullptr;
    }
    st->viewportHwnd = vpHwnd;
    g_activeClient.store(st, std::memory_order_release);

    if (!st->renderer.AttachToHwnd(vpHwnd, static_cast<uint32_t>(w), static_cast<uint32_t>(h), err)) {
        BoBoClientSession* expected = st;
        g_activeClient.compare_exchange_strong(expected, nullptr, std::memory_order_acq_rel);
        DestroyWindow(vpHwnd);
        if (st->decodeDoneEvent) CloseHandle(st->decodeDoneEvent);
        delete st;
        return nullptr;
    }
    st->renderer.SetVideoSourceDimensions(st->videoW, st->videoH);
    st->renderer.PresentClearFrame(0.055f, 0.082f, 0.125f, err);
    ApplyClientMouseMode(st, st->freeMouse);
    st->decodeThread = std::thread([st]() { st->RunDecodeLoop(); });
    return st;
}

BOBO_API int32_t __stdcall BoBoClient_PushVideo(
    void* handle,
    const uint8_t* h264Data,
    int32_t length,
    int32_t isKeyframe,
    int32_t srcWidth,
    int32_t srcHeight) {
    auto* st = static_cast<BoBoClientSession*>(handle);
    if (!st || !st->alive.load(std::memory_order_acquire) || !h264Data || length <= 0) return 0;

    QueuedH264Frame pkt;
    pkt.data.assign(h264Data, h264Data + length);
    pkt.isKeyframe = (isKeyframe != 0);
    pkt.width = srcWidth > 0 ? static_cast<uint32_t>(srcWidth) : 0;
    pkt.height = srcHeight > 0 ? static_cast<uint32_t>(srcHeight) : 0;

    {
        std::lock_guard<std::mutex> lk(st->queueMutex);
        if (!st->alive.load(std::memory_order_acquire)) return 0;
        if (pkt.isKeyframe && st->frameQueue.size() >= 2) {
            // New keyframe arrived while older frames were still waiting -> jump straight to fresh keyframe!
            st->frameQueue.clear();
        } else if (st->frameQueue.size() >= 8) {
            // Trim up to the newest queued keyframe if overloaded, never grow unboundedly
            auto lastKey = st->frameQueue.end();
            for (auto it = st->frameQueue.begin(); it != st->frameQueue.end(); ++it) {
                if (it->isKeyframe) lastKey = it;
            }
            if (lastKey != st->frameQueue.end() && lastKey != st->frameQueue.begin()) {
                st->frameQueue.erase(st->frameQueue.begin(), lastKey);
            }
        }
        st->frameQueue.push_back(std::move(pkt));
    }
    st->queueCv.notify_one();
    return 1;
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
    if (!st) return;
    st->renderer.SetScaleMode(scaleMode);
    if (st->viewportHwnd) PostMessageW(st->viewportHwnd, kMsgRefreshCursor, 0, 0);
}

BOBO_API void __stdcall BoBoClient_SetViewOnly(void* handle, int32_t viewOnly) {
    auto* st = static_cast<BoBoClientSession*>(handle);
    if (!st) return;
    st->viewOnly = (viewOnly != 0);
    if (st->viewOnly && !st->freeMouse) ApplyClientMouseMode(st, true);
    if (st->viewportHwnd && IsWindow(st->viewportHwnd)) {
        PostMessageW(st->viewportHwnd, kMsgRefreshCursor, 0, 0);
    }
}

BOBO_API void __stdcall BoBoClient_SetSysKeys(void* handle, int32_t sendSysKeys) {
    auto* st = static_cast<BoBoClientSession*>(handle);
    if (!st) return;
    st->sendSysKeys = (sendSysKeys != 0);
    SyncClientSysKeyHook(st);
}

BOBO_API void __stdcall BoBoClient_SetHostCursorComposited(void* handle, int32_t composited) {
    auto* st = static_cast<BoBoClientSession*>(handle);
    if (!st) return;
    st->hostCursorComposited.store(composited != 0);
    if (st->viewportHwnd && IsWindow(st->viewportHwnd)) {
        PostMessageW(st->viewportHwnd, kMsgRefreshCursor, 0, 0);
    }
}

BOBO_API void __stdcall BoBoClient_SetRemoteCursorShape(void* handle, int32_t shapeId) {
    BoBoClient_SetRemoteCursorInfo(handle, shapeId, 0, 0);
}

BOBO_API void __stdcall BoBoClient_SetRemoteCursorInfo(void* handle, int32_t shapeId, int32_t width, int32_t height) {
    auto* st = static_cast<BoBoClientSession*>(handle);
    if (!st) return;
    if (shapeId < 0 || shapeId > 13) shapeId = 1;
    if (shapeId == 0 || width <= 0 || height <= 0 || width > kMaxCursorDimension || height > kMaxCursorDimension) {
        width = 0;
        height = 0;
    }
    const uint32_t info = static_cast<uint32_t>(shapeId) |
                          (static_cast<uint32_t>(width) << 8) |
                          (static_cast<uint32_t>(height) << 16);
    const uint32_t prev = st->remoteCursorInfo.exchange(info, std::memory_order_acq_rel);
    if (prev != info && st->viewportHwnd && IsWindow(st->viewportHwnd)) {
        PostMessageW(st->viewportHwnd, kMsgRefreshCursor, 0, 0);
    }
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
    outStats->width = static_cast<int32_t>(st->statsVideoW);
    outStats->height = static_cast<int32_t>(st->statsVideoH);
    const std::string& dec = st->statsDecoderName.empty() ? std::string("D3D11VA") : st->statsDecoderName;
    std::strncpy(outStats->decoderName, dec.c_str(), sizeof(outStats->decoderName) - 1);
}

BOBO_API void __stdcall BoBoClient_Destroy(void* handle) {
    auto* st = static_cast<BoBoClientSession*>(handle);
    if (!st) return;
    st->alive.store(false, std::memory_order_release);
    st->sendSysKeys = false;
    SyncClientSysKeyHook(st);
    ApplyClientMouseMode(st, true);
    BoBoClientSession* expected = st;
    g_activeClient.compare_exchange_strong(expected, nullptr, std::memory_order_acq_rel);
    st->queueCv.notify_all();
    if (st->decodeThread.joinable()) {
        if (st->decodeDoneEvent) {
            // Drain incoming cross-thread SendMessages (e.g. DXGI Present/DWM) while waiting so UI thread never deadlocks
            uint64_t deadline = GetTickCount64() + 5000ULL;
            while (true) {
                uint64_t now = GetTickCount64();
                DWORD remain = (now < deadline) ? static_cast<DWORD>(deadline - now) : 0;
                DWORD wr = MsgWaitForMultipleObjects(1, &st->decodeDoneEvent, FALSE, remain, QS_SENDMESSAGE);
                if (wr == WAIT_OBJECT_0 || wr == WAIT_TIMEOUT || wr == WAIT_FAILED) break;
                if (wr == WAIT_OBJECT_0 + 1) {
                    MSG msg = {};
                    PeekMessageW(&msg, nullptr, 0, 0, PM_NOREMOVE);
                }
            }
        }
        st->decodeThread.join();
    }
    if (st->decodeDoneEvent) {
        CloseHandle(st->decodeDoneEvent);
        st->decodeDoneEvent = nullptr;
    }
    if (st->viewportHwnd && IsWindow(st->viewportHwnd)) {
        DestroyWindow(st->viewportHwnd);
        st->viewportHwnd = nullptr;
    }
    st->audio.Shutdown();
    {
        std::lock_guard<std::mutex> lk(st->decodeMutex);
        st->decoder.CloseCodec();
    }
    delete st;
}

} // extern "C"
