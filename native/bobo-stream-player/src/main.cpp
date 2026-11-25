#include "d3d11_renderer.h"
#include "ipc_protocol.h"
#include "mf_decoder.h"
#include "moonlight_bridge.h"

#include <windowsx.h>
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <iostream>
#include <memory>
#include <string>
#include <thread>
#include <vector>

#ifdef BOBO_WITH_MOONLIGHT_COMMON_C
#pragma warning(push)
#pragma warning(disable : 4200)
extern "C" {
#include <Limelight.h>
#include <PlatformCrypto.h>
}
#pragma warning(pop)
#endif

using namespace bobo;

static const UINT WM_BOBO_IPC = WM_APP + 101;
static const UINT WM_BOBO_TERMINATED = WM_APP + 102;
static const UINT_PTR TIMER_STATS_ID = 1;

static bool ParseHex16(const std::string& hex, uint8_t out[16]) {
    if (hex.size() != 32) return false;
    auto hexVal = [](char c) -> int {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    };
    for (int i = 0; i < 16; ++i) {
        int hi = hexVal(hex[static_cast<size_t>(i * 2)]);
        int lo = hexVal(hex[static_cast<size_t>(i * 2 + 1)]);
        if (hi < 0 || lo < 0) return false;
        out[i] = static_cast<uint8_t>((hi << 4) | lo);
    }
    return true;
}

#ifdef BOBO_WITH_MOONLIGHT_COMMON_C
static bool VerifyCryptoBackend(std::string& outDetail) {
    // 1. AES-128-GCM 12-byte IV round-trip + tag tamper + replay window rejection
    unsigned char key[16] = { 0xfe,0xff,0xe9,0x92,0x86,0x65,0x73,0x1c,0x6d,0x6a,0x8f,0x94,0x67,0x30,0x83,0x08 };
    unsigned char iv12[12] = { 0xca,0xfe,0xba,0xbe,0xfa,0xce,0xdb,0xad,0xde,0xca,0xf8,0x88 };
    unsigned char plain[32] = "BoBoRemote-V5-Native-GCM-Check!";
    unsigned char cipher[32] = {};
    unsigned char decrypted[32] = {};
    unsigned char tag[16] = {};
    int outLen = 0;

    PPLT_CRYPTO_CONTEXT enc12 = PltCreateCryptoContext(1);
    PPLT_CRYPTO_CONTEXT dec12 = PltCreateCryptoContext(1);
    bool ok12 = enc12 && dec12
        && PltEncryptMessage(enc12, ALGORITHM_AES_GCM, 0, key, 16, iv12, 12, tag, 16, plain, 31, cipher, &outLen)
        && outLen == 31
        && PltDecryptMessage(dec12, ALGORITHM_AES_GCM, 0, 0, 100, key, 16, iv12, 12, tag, 16, cipher, 31, decrypted, &outLen)
        && outLen == 31 && std::memcmp(plain, decrypted, 31) == 0
        // Replay check must reject duplicate sequence 100
        && !PltDecryptMessage(dec12, ALGORITHM_AES_GCM, 0, 0, 100, key, 16, iv12, 12, tag, 16, cipher, 31, decrypted, &outLen);
    if (enc12) PltDestroyCryptoContext(enc12);
    if (dec12) PltDestroyCryptoContext(dec12);
    if (!ok12) {
        outDetail = "AES-128-GCM 12-byte IV or replay check failed";
        return false;
    }

    // 2. AES-128-GCM 16-byte IV (required by moonlight-common-c InputStream.c on Gen 7+ hosts!)
    unsigned char iv16[16] = { 0x00,0x00,0x00,0x01,0x02,0x03,0x04,0x05,0x06,0x07,0x08,0x09,0x0a,0x0b,0x0c,0x0d };
    unsigned char tag16[16] = {};
    std::memset(cipher, 0, sizeof(cipher));
    std::memset(decrypted, 0, sizeof(decrypted));
    PPLT_CRYPTO_CONTEXT enc16 = PltCreateCryptoContext(1);
    PPLT_CRYPTO_CONTEXT dec16 = PltCreateCryptoContext(1);
    bool ok16 = enc16 && dec16
        && PltEncryptMessage(enc16, ALGORITHM_AES_GCM, 0, key, 16, iv16, 16, tag16, 16, plain, 31, cipher, &outLen)
        && outLen == 31
        && PltDecryptMessage(dec16, ALGORITHM_AES_GCM, 0, 0, 1, key, 16, iv16, 16, tag16, 16, cipher, 31, decrypted, &outLen)
        && outLen == 31 && std::memcmp(plain, decrypted, 31) == 0;
    // Tampered tag must be rejected
    tag16[0] ^= 0xFF;
    bool tamperRejected = !PltDecryptMessage(dec16, ALGORITHM_AES_GCM, CIPHER_FLAG_NO_REPLAY_CHECK, 0, 2,
                                             key, 16, iv16, 16, tag16, 16, cipher, 31, decrypted, &outLen);
    if (enc16) PltDestroyCryptoContext(enc16);
    if (dec16) PltDestroyCryptoContext(dec16);
    if (!ok16 || !tamperRejected) {
        outDetail = "AES-128-GCM 16-byte IV or tag tamper check failed";
        return false;
    }

    // 3. AES-128-CBC with PKCS7 padding (AudioStream / Gen<7 InputStream)
    unsigned char cbcIn[48] = {};
    unsigned char cbcEnc[48] = {};
    unsigned char cbcDec[48] = {};
    std::memcpy(cbcIn, "HelloAudioPacket!", 17);
    PPLT_CRYPTO_CONTEXT encCbc = PltCreateCryptoContext(0);
    PPLT_CRYPTO_CONTEXT decCbc = PltCreateCryptoContext(0);
    int encLen = 0, decLen = 0;
    bool okCbc = encCbc && decCbc
        && PltEncryptMessage(encCbc, ALGORITHM_AES_CBC, CIPHER_FLAG_RESET_IV | CIPHER_FLAG_FINISH,
                             key, 16, iv16, 16, nullptr, 0, cbcIn, 17, cbcEnc, &encLen)
        && encLen == 32
        && PltDecryptMessage(decCbc, ALGORITHM_AES_CBC, CIPHER_FLAG_RESET_IV | CIPHER_FLAG_FINISH,
                             0, 0, key, 16, iv16, 16, nullptr, 0, cbcEnc, encLen, cbcDec, &decLen)
        && decLen == 17 && std::memcmp(cbcIn, cbcDec, 17) == 0;
    if (encCbc) PltDestroyCryptoContext(encCbc);
    if (decCbc) PltDestroyCryptoContext(decCbc);
    if (!okCbc) {
        outDetail = "AES-128-CBC PKCS7 round-trip failed";
        return false;
    }

    outDetail = "BCrypt AES-GCM(12B/16B) + Replay + AES-CBC OK";
    return true;
}
#endif

static int RunSelfTest() {
    int passed = 0;
    int failed = 0;
    auto check = [&](const char* name, bool ok, const std::string& detail) {
        if (ok) {
            ++passed;
            std::cout << "[PASS] " << name;
            if (!detail.empty()) std::cout << " (" << detail << ")";
            std::cout << "\n";
        } else {
            ++failed;
            std::cout << "[FAIL] " << name << " (" << detail << ")\n";
        }
    };

    // 1. IPC protocol round-trip
    IpcCommand c1 = ParseIpcCommand("{\"cmd\":\"switch_quality\",\"width\":2560,\"height\":1440,\"fps\":90,\"bitrateKbps\":22000}");
    check("IPC switch_quality parse",
          c1.type == IpcCommandType::SwitchQuality && c1.width == 2560 && c1.height == 1440 && c1.fps == 90 && c1.bitrateKbps == 22000,
          "2560x1440@90/22000");

    IpcCommand c2 = ParseIpcCommand("{\"cmd\":\"set_mouse_mode\",\"free\":true}");
    check("IPC set_mouse_mode parse", c2.type == IpcCommandType::SetMouseMode && c2.flag, "free=true");

    IpcCommand c3 = ParseIpcCommand("{\"cmd\":\"send_keys\",\"vks\":[17,16,27]}");
    check("IPC send_keys parse", c3.type == IpcCommandType::SendKeys && c3.vks.size() == 3 && c3.vks[2] == 27, "Ctrl+Shift+Esc");

    IpcCommand c4 = ParseIpcCommand("{\"cmd\":\"disconnect\"}");
    check("IPC disconnect parse", c4.type == IpcCommandType::Disconnect, "disconnect");

    StreamStats st;
    st.fps = 89.8;
    st.decodeMs = 1.42;
    st.renderMs = 0.65;
    st.rttMs = 3.2;
    st.bitrateKbps = 22000;
    st.width = 2560;
    st.height = 1440;
    std::string statsJson = FormatStatsJson(st);
    check("IPC stats JSON format",
          statsJson.find("\"event\":\"stats\"") != std::string::npos && statsJson.find("\"fps\":89.8") != std::string::npos,
          statsJson);

    // 2. moonlight-common-c linkage + Windows CNG BCrypt crypto verification
    check("moonlight-common-c statically linked", MoonlightBridge::IsMoonlightCommonCLinked(),
          MoonlightBridge::GetLaunchQueryParameters());
#ifdef BOBO_WITH_MOONLIGHT_COMMON_C
    std::string cryptoDetail;
    bool cryptoOk = VerifyCryptoBackend(cryptoDetail);
    check("Windows CNG BCrypt crypto (GCM 12B/16B + CBC + Replay)", cryptoOk, cryptoDetail);
#endif

    // 3. D3D11 hardware/WARP device + VideoProcessor pipeline
    D3D11Renderer renderer;
    std::string err;
    bool d3dOk = renderer.InitializeDevice(true, err);
    check("D3D11 device init", d3dOk, d3dOk ? renderer.Capabilities().adapterName : err);

    if (d3dOk) {
        const auto& caps = renderer.Capabilities();
        if (!caps.isSoftwareWarp) {
            check("D3D11 VideoDevice + NV12->BGRA VideoProcessor",
                  caps.hasVideoDevice && caps.supportsNv12ToBgra,
                  caps.hasVideoDevice ? "NV12->BGRA GPU VideoProcessor OK" : "no video device");
        } else {
            check("D3D11 WARP fallback device active (headless VM)", true, caps.adapterName);
        }

        MfHardwareDecoder decoder;
        std::string mfErr;
        bool mfOk = decoder.Initialize(renderer.Device(), mfErr);
        check("Media Foundation IMFDXGIDeviceManager bind", mfOk, mfOk ? "bound to D3D11" : mfErr);
        if (mfOk) {
            DecoderProbeResult probe = decoder.ProbeHardwareDecoders();
            std::string h264Desc = probe.h264Decoders.empty() ? "none" : probe.h264Decoders[0];
            std::string hevcDesc = probe.hevcDecoders.empty() ? "none" : probe.hevcDecoders[0];
            check("H.264 MFT decoder discovery", probe.h264HwAvailable, h264Desc);
            std::cout << "[INFO] HEVC MFT decoders detected: " << probe.hevcDecoders.size()
                      << (probe.hevcDecoders.empty() ? "" : (" (" + hevcDesc + ")")) << "\n";

            if (probe.h264HwAvailable && !caps.isSoftwareWarp) {
                std::string openErr;
                bool openOk = decoder.OpenCodec(VideoCodec::H264, 1920, 1080, openErr);
                check("H.264 Low-Latency D3D11VA MFT open (1920x1080 NV12)",
                      openOk,
                      openOk ? decoder.ActiveDecoderName() : openErr);
            }
        }
    }

    std::cout << "BoBoStreamPlayer self-test: passed=" << passed << " failed=" << failed << "\n";
    return failed == 0 ? 0 : 1;
}

static int RunProbeGpu() {
    D3D11Renderer renderer;
    std::string err;
    if (!renderer.InitializeDevice(true, err)) {
        EmitJsonLine("{\"event\":\"error\",\"message\":\"" + EscapeJson(err) + "\"}");
        return 1;
    }
    MfHardwareDecoder decoder;
    std::string mfErr;
    bool mfOk = decoder.Initialize(renderer.Device(), mfErr);
    DecoderProbeResult probe = mfOk ? decoder.ProbeHardwareDecoders() : DecoderProbeResult{};
    const auto& caps = renderer.Capabilities();
    EmitJsonLine(FormatReadyJson(
        caps.adapterName,
        caps.hasVideoDevice && caps.supportsNv12ToBgra,
        probe.h264HwAvailable,
        probe.hevcHwAvailable,
        MoonlightBridge::IsMoonlightCommonCLinked()));
    return 0;
}

struct StreamViewportState {
    D3D11Renderer* renderer = nullptr;
    MoonlightBridge* bridge = nullptr;
    HWND hwnd = nullptr;
    HWND parentHwnd = nullptr;
    bool freeMouse = true;
    bool rawMouseRegistered = false;
    bool hudEnabled = false;
    bool cursorHidden = false;
    DWORD lastTopEdgeTick = 0;
    std::atomic<bool> quitRequested{ false };
};

static void ApplyMouseMode(StreamViewportState* st, bool freeMouse) {
    if (!st || !st->hwnd) return;
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
        rid.hwndTarget = st->hwnd;
        if (RegisterRawInputDevices(&rid, 1, sizeof(rid))) {
            st->rawMouseRegistered = true;
        }
        RECT rc = {};
        if (GetClientRect(st->hwnd, &rc)) {
            POINT tl = { rc.left, rc.top };
            POINT br = { rc.right, rc.bottom };
            ClientToScreen(st->hwnd, &tl);
            ClientToScreen(st->hwnd, &br);
            RECT screenRc = { tl.x, tl.y, br.x, br.y };
            ClipCursor(&screenRc);
        }
        if (!st->cursorHidden) {
            ShowCursor(FALSE);
            st->cursorHidden = true;
        }
    }
    EmitJsonLine(FormatMouseModeJson(freeMouse));
}

static LRESULT CALLBACK ViewportWndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    auto* st = reinterpret_cast<StreamViewportState*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
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
            if (w > 0 && h > 0 && st->renderer) {
                std::string rErr;
                st->renderer->ResizeSwapChain(w, h, rErr);
            }
            return 0;
        }

        case WM_MOUSEMOVE: {
            if (st->freeMouse && st->bridge && st->renderer) {
                int x = GET_X_LPARAM(lParam);
                int y = GET_Y_LPARAM(lParam);
                if (y <= 6) {
                    DWORD nowTick = GetTickCount();
                    if (nowTick - st->lastTopEdgeTick >= 600) {
                        st->lastTopEdgeTick = nowTick;
                        EmitJsonLine("{\"event\":\"edge_top\"}");
                    }
                }
                RECT vp = st->renderer->GetVideoViewportRect();
                int vpW = vp.right - vp.left;
                int vpH = vp.bottom - vp.top;
                if (vpW > 0 && vpH > 0) {
                    int relX = x - vp.left;
                    int relY = y - vp.top;
                    if (relX >= 0 && relX <= vpW && relY >= 0 && relY <= vpH) {
                        st->bridge->SendMouseAbsolute(
                            static_cast<short>(relX),
                            static_cast<short>(relY),
                            static_cast<short>(vpW),
                            static_cast<short>(vpH));
                    }
                }
            }
            return 0;
        }

        case WM_INPUT: {
            if (!st->freeMouse && st->bridge) {
                UINT sz = 0;
                GetRawInputData(reinterpret_cast<HRAWINPUT>(lParam), RID_INPUT, nullptr, &sz, sizeof(RAWINPUTHEADER));
                if (sz > 0 && sz <= 512) {
                    uint8_t buf[512];
                    if (GetRawInputData(reinterpret_cast<HRAWINPUT>(lParam), RID_INPUT, buf, &sz, sizeof(RAWINPUTHEADER)) == sz) {
                        const RAWINPUT* ri = reinterpret_cast<const RAWINPUT*>(buf);
                        if (ri->header.dwType == RIM_TYPEMOUSE && (ri->data.mouse.usFlags & MOUSE_MOVE_ABSOLUTE) == 0) {
                            st->bridge->SendMouseRelative(
                                static_cast<short>(ri->data.mouse.lLastX),
                                static_cast<short>(ri->data.mouse.lLastY));
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
            if (st->bridge) st->bridge->SendMouseButton(true, 0x01);
            return 0;
        case WM_LBUTTONUP:
            if (st->bridge) st->bridge->SendMouseButton(false, 0x01);
            return 0;
        case WM_RBUTTONDOWN:
            SetFocus(hwnd);
            if (st->bridge) st->bridge->SendMouseButton(true, 0x03);
            return 0;
        case WM_RBUTTONUP:
            if (st->bridge) st->bridge->SendMouseButton(false, 0x03);
            return 0;
        case WM_MBUTTONDOWN:
            SetFocus(hwnd);
            if (st->bridge) st->bridge->SendMouseButton(true, 0x02);
            return 0;
        case WM_MBUTTONUP:
            if (st->bridge) st->bridge->SendMouseButton(false, 0x02);
            return 0;
        case WM_XBUTTONDOWN:
            SetFocus(hwnd);
            if (st->bridge) st->bridge->SendMouseButton(true, GET_XBUTTON_WPARAM(wParam) == XBUTTON1 ? 0x04 : 0x05);
            return TRUE;
        case WM_XBUTTONUP:
            if (st->bridge) st->bridge->SendMouseButton(false, GET_XBUTTON_WPARAM(wParam) == XBUTTON1 ? 0x04 : 0x05);
            return TRUE;
        case WM_MOUSEWHEEL:
            if (st->bridge) st->bridge->SendMouseScroll(static_cast<short>(GET_WHEEL_DELTA_WPARAM(wParam)));
            return 0;
        case WM_MOUSEHWHEEL:
            if (st->bridge) st->bridge->SendMouseHScroll(static_cast<short>(GET_WHEEL_DELTA_WPARAM(wParam)));
            return 0;

        case WM_KEYDOWN:
        case WM_SYSKEYDOWN: {
            uint16_t vk = static_cast<uint16_t>(wParam);
            bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            bool alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
            bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
            // Built-in emergency shortcuts: Ctrl+Alt+Shift+Q disconnects; Ctrl+Alt+Shift+Z unlocks mouse
            if (ctrl && alt && shift && vk == 'Q') {
                ApplyMouseMode(st, true);
                st->quitRequested.store(true);
                PostQuitMessage(0);
                return 0;
            }
            if (ctrl && alt && shift && vk == 'Z') {
                ApplyMouseMode(st, true);
                return 0;
            }
            if (ctrl && alt && shift && vk == 'B') {
                ApplyMouseMode(st, true);
                EmitJsonLine("{\"event\":\"hotkey\",\"name\":\"toolbar\"}");
                return 0;
            }
            bool extended = ((lParam & (1L << 24)) != 0);
            if (st->bridge) st->bridge->SendKey(vk, true, extended);
            return 0;
        }

        case WM_KEYUP:
        case WM_SYSKEYUP: {
            uint16_t vk = static_cast<uint16_t>(wParam);
            bool extended = ((lParam & (1L << 24)) != 0);
            if (st->bridge) st->bridge->SendKey(vk, false, extended);
            return 0;
        }

        case WM_KILLFOCUS:
            if (!st->freeMouse) {
                ApplyMouseMode(st, true);
            }
            if (st->bridge) st->bridge->ReleaseAllModifiers();
            return 0;

        case WM_TIMER:
            if (wParam == TIMER_STATS_ID && st->bridge && st->bridge->IsActive()) {
                StreamStats snap = st->bridge->SnapshotStats();
                if (st->hudEnabled || snap.fps > 0.0) {
                    EmitJsonLine(FormatStatsJson(snap));
                }
            }
            return 0;

        case WM_BOBO_IPC: {
            std::unique_ptr<IpcCommand> cmd(reinterpret_cast<IpcCommand*>(lParam));
            if (!cmd) return 0;
            switch (cmd->type) {
                case IpcCommandType::Disconnect:
                    ApplyMouseMode(st, true);
                    st->quitRequested.store(true);
                    PostQuitMessage(0);
                    break;
                case IpcCommandType::Resize:
                    if (cmd->width > 0 && cmd->height > 0) {
                        SetWindowPos(hwnd, nullptr, 0, 0,
                                     static_cast<int>(cmd->width), static_cast<int>(cmd->height),
                                     SWP_NOZORDER | SWP_NOACTIVATE);
                    }
                    break;
                case IpcCommandType::SetMouseMode:
                    ApplyMouseMode(st, cmd->flag);
                    break;
                case IpcCommandType::SetHud:
                    st->hudEnabled = cmd->flag;
                    break;
                case IpcCommandType::SendKeys:
                    if (st->bridge) st->bridge->SendKeyCombo(cmd->vks);
                    break;
                case IpcCommandType::SendText:
                    if (st->bridge) st->bridge->SendUtf8Text(cmd->text);
                    break;
                default:
                    break;
            }
            return 0;
        }

        case WM_BOBO_TERMINATED:
            ApplyMouseMode(st, true);
            PostQuitMessage(0);
            return 0;

        case WM_DESTROY:
            ApplyMouseMode(st, true);
            KillTimer(hwnd, TIMER_STATS_ID);
            PostQuitMessage(0);
            return 0;

        default:
            return DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}

static int RunStreamSession(HWND parentHwnd, const StreamSessionConfig& cfg, bool initialFreeMouse, bool initialHud) {
    D3D11Renderer renderer;
    std::string err;
    if (!renderer.InitializeDevice(true, err)) {
        EmitJsonLine("{\"event\":\"error\",\"message\":\"" + EscapeJson(err) + "\"}");
        return 1;
    }

    MfHardwareDecoder decoder;
    if (!decoder.Initialize(renderer.Device(), err)) {
        EmitJsonLine("{\"event\":\"error\",\"message\":\"" + EscapeJson(err) + "\"}");
        return 1;
    }

    StreamSessionConfig effectiveCfg = cfg;
    if (effectiveCfg.codec == VideoCodec::HEVC) {
        DecoderProbeResult probe = decoder.ProbeHardwareDecoders();
        if (!probe.hevcHwAvailable) {
            effectiveCfg.codec = VideoCodec::H264;
        }
    }

    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.style = CS_DBLCLKS;
    wc.lpfnWndProc = ViewportWndProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512));
    wc.lpszClassName = L"BoBoNativeStreamViewport";
    RegisterClassExW(&wc);

    RECT parentRc = { 0, 0, static_cast<LONG>(effectiveCfg.width), static_cast<LONG>(effectiveCfg.height) };
    DWORD style = WS_OVERLAPPEDWINDOW | WS_VISIBLE;
    if (parentHwnd && IsWindow(parentHwnd)) {
        GetClientRect(parentHwnd, &parentRc);
        style = WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS;
    }
    int initW = (parentRc.right - parentRc.left) > 16 ? (parentRc.right - parentRc.left) : 1280;
    int initH = (parentRc.bottom - parentRc.top) > 16 ? (parentRc.bottom - parentRc.top) : 720;

    MoonlightBridge bridge(renderer, decoder);
    StreamViewportState state;
    state.renderer = &renderer;
    state.bridge = &bridge;
    state.parentHwnd = parentHwnd;
    state.freeMouse = initialFreeMouse;
    state.hudEnabled = initialHud;

    HWND viewportHwnd = CreateWindowExW(
        0,
        wc.lpszClassName,
        L"BoBo Remote Viewport",
        style,
        0, 0, initW, initH,
        (parentHwnd && IsWindow(parentHwnd)) ? parentHwnd : nullptr,
        nullptr,
        wc.hInstance,
        &state);
    if (!viewportHwnd) {
        EmitJsonLine("{\"event\":\"error\",\"message\":\"Failed to create native viewport window\"}");
        return 1;
    }
    state.hwnd = viewportHwnd;

    if (!renderer.AttachToHwnd(viewportHwnd, static_cast<uint32_t>(initW), static_cast<uint32_t>(initH), err)) {
        EmitJsonLine("{\"event\":\"error\",\"message\":\"" + EscapeJson(err) + "\"}");
        DestroyWindow(viewportHwnd);
        return 1;
    }
    renderer.PresentClearFrame(0.055f, 0.082f, 0.125f, err);

    SetTimer(viewportHwnd, TIMER_STATS_ID, 500, nullptr);
    ApplyMouseMode(&state, initialFreeMouse);
    {
        char hwndBuf[96];
        std::snprintf(hwndBuf, sizeof(hwndBuf), "{\"event\":\"viewport_hwnd\",\"hwnd\":%llu}",
                      static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(viewportHwnd)));
        EmitJsonLine(hwndBuf);
    }

    // Background stdin IPC reader thread
    std::thread ipcThread([&state, viewportHwnd]() {
        std::string line;
        while (std::getline(std::cin, line)) {
            if (line.empty()) continue;
            IpcCommand parsed = ParseIpcCommand(line);
            if (parsed.type == IpcCommandType::None) continue;
            auto* heapCmd = new IpcCommand(std::move(parsed));
            if (!PostMessageW(viewportHwnd, WM_BOBO_IPC, 0, reinterpret_cast<LPARAM>(heapCmd))) {
                delete heapCmd;
                break;
            }
            if (heapCmd->type == IpcCommandType::Disconnect) {
                break;
            }
        }
        // Parent closed stdin pipe -> terminate cleanly
        if (!state.quitRequested.load()) {
            PostMessageW(viewportHwnd, WM_BOBO_TERMINATED, 0, 0);
        }
    });

    // Background stream connection starter thread so Win32 viewport remains responsive during RTSP handshake
    std::thread streamThread([&bridge, effectiveCfg, viewportHwnd, &state]() {
        std::string startErr;
        bool ok = bridge.StartSession(
            effectiveCfg,
            [viewportHwnd](int code, const std::string& reason) {
                (void)code;
                (void)reason;
                PostMessageW(viewportHwnd, WM_BOBO_TERMINATED, 0, 0);
            },
            startErr);
        if (!ok && !state.quitRequested.load()) {
            EmitJsonLine("{\"event\":\"error\",\"message\":\"" + EscapeJson(startErr) + "\"}");
            PostMessageW(viewportHwnd, WM_BOBO_TERMINATED, 0, 0);
        }
    });

    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    state.quitRequested.store(true);
    ApplyMouseMode(&state, true);
    bridge.InterruptSession();
    if (streamThread.joinable()) streamThread.join();
    bridge.StopSession();
    if (ipcThread.joinable()) ipcThread.detach();
    if (IsWindow(viewportHwnd)) DestroyWindow(viewportHwnd);
    return 0;
}

static void EnableDpiAwareness() {
    HMODULE hUser32 = GetModuleHandleW(L"user32.dll");
    if (hUser32) {
        typedef BOOL(WINAPI* SetDpiCtxFn)(void*);
        auto fnCtx = reinterpret_cast<SetDpiCtxFn>(GetProcAddress(hUser32, "SetProcessDpiAwarenessContext"));
        if (fnCtx) {
            // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 == (DPI_AWARENESS_CONTEXT)-4
            if (fnCtx(reinterpret_cast<void*>(static_cast<intptr_t>(-4)))) {
                return;
            }
            // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE == (DPI_AWARENESS_CONTEXT)-3
            if (fnCtx(reinterpret_cast<void*>(static_cast<intptr_t>(-3)))) {
                return;
            }
        }
        typedef BOOL(WINAPI* SetDpiAwareFn)();
        auto fnAware = reinterpret_cast<SetDpiAwareFn>(GetProcAddress(hUser32, "SetProcessDPIAware"));
        if (fnAware) {
            fnAware();
        }
    }
}

int main(int argc, char** argv) {
    EnableDpiAwareness();
    HRESULT hrCo = CoInitializeEx(nullptr, COINIT_MULTITHREADED);

    bool selfTest = false;
    bool probeGpu = false;
    bool preview = false;
    bool streamMode = false;
    bool freeMouse = true;
    bool initialHud = false;
    HWND parentHwnd = nullptr;
    StreamSessionConfig cfg;

    for (int i = 1; i < argc; ++i) {
        std::string arg = argv[i];
        if (arg == "--self-test") selfTest = true;
        else if (arg == "--probe-gpu") probeGpu = true;
        else if (arg == "--preview") preview = true;
        else if (arg == "--stream") streamMode = true;
        else if (arg.rfind("--parent-hwnd=", 0) == 0) {
            unsigned long long val = std::strtoull(arg.substr(14).c_str(), nullptr, 0);
            parentHwnd = reinterpret_cast<HWND>(static_cast<uintptr_t>(val));
        } else if (arg.rfind("--host=", 0) == 0) {
            cfg.host = arg.substr(7);
        } else if (arg.rfind("--rtsp-url=", 0) == 0) {
            cfg.rtspUrl = arg.substr(11);
        } else if (arg.rfind("--app-version=", 0) == 0) {
            cfg.appVersion = arg.substr(14);
        } else if (arg.rfind("--gfe-version=", 0) == 0) {
            cfg.gfeVersion = arg.substr(14);
        } else if (arg.rfind("--server-codec-support=", 0) == 0) {
            cfg.serverCodecModeSupport = static_cast<int>(std::strtol(arg.substr(23).c_str(), nullptr, 10));
        } else if (arg.rfind("--rikey=", 0) == 0) {
            ParseHex16(arg.substr(8), cfg.riKey);
        } else if (arg.rfind("--rikeyid=", 0) == 0) {
            cfg.riKeyId = static_cast<uint32_t>(std::strtoul(arg.substr(10).c_str(), nullptr, 10));
        } else if (arg.rfind("--width=", 0) == 0) {
            cfg.width = static_cast<uint32_t>(std::strtoul(arg.substr(8).c_str(), nullptr, 10));
        } else if (arg.rfind("--height=", 0) == 0) {
            cfg.height = static_cast<uint32_t>(std::strtoul(arg.substr(9).c_str(), nullptr, 10));
        } else if (arg.rfind("--fps=", 0) == 0) {
            cfg.fps = static_cast<uint32_t>(std::strtoul(arg.substr(6).c_str(), nullptr, 10));
        } else if (arg.rfind("--bitrate=", 0) == 0) {
            cfg.bitrateKbps = static_cast<uint32_t>(std::strtoul(arg.substr(10).c_str(), nullptr, 10));
        } else if (arg.rfind("--codec=", 0) == 0) {
            std::string c = arg.substr(8);
            cfg.codec = (c == "HEVC" || c == "hevc" || c == "H265" || c == "h265") ? VideoCodec::HEVC : VideoCodec::H264;
        } else if (arg.rfind("--free-mouse=", 0) == 0) {
            freeMouse = (arg.substr(13) != "0");
        } else if (arg.rfind("--audio=", 0) == 0) {
            cfg.enableAudio = (arg.substr(8) != "0");
        } else if (arg.rfind("--hud=", 0) == 0) {
            initialHud = (arg.substr(6) == "1");
        }
    }

    int exitCode = 0;
    if (selfTest) {
        exitCode = RunSelfTest();
    } else if (probeGpu) {
        exitCode = RunProbeGpu();
    } else if (streamMode) {
        exitCode = RunStreamSession(parentHwnd, cfg, freeMouse, initialHud);
    } else if (preview && parentHwnd) {
        D3D11Renderer renderer;
        std::string err;
        if (!renderer.InitializeDevice(true, err) || !renderer.AttachToHwnd(parentHwnd, cfg.width, cfg.height, err)) {
            EmitJsonLine("{\"event\":\"error\",\"message\":\"" + EscapeJson(err) + "\"}");
            exitCode = 1;
        } else {
            renderer.PresentClearFrame(0.941f, 0.961f, 0.973f, err);
            EmitJsonLine("{\"event\":\"preview_presented\"}");
        }
    } else {
        std::cout << "BoBoStreamPlayer (V5 Native Integrated D3D11VA + moonlight-common-c Core)\n"
                  << "Usage:\n"
                  << "  BoBoStreamPlayer.exe --self-test\n"
                  << "  BoBoStreamPlayer.exe --probe-gpu\n"
                  << "  BoBoStreamPlayer.exe --stream --parent-hwnd=<HWND> --host=<IP> --rtsp-url=<URL> --rikey=<HEX32> --rikeyid=<U32>\n";
    }

    if (SUCCEEDED(hrCo)) {
        CoUninitialize();
    }
    return exitCode;
}
