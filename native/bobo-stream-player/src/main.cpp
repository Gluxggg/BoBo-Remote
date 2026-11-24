#include "d3d11_renderer.h"
#include "ipc_protocol.h"
#include "mf_decoder.h"
#include "moonlight_bridge.h"

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <iostream>
#include <string>

using namespace bobo;

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

    IpcCommand c2 = ParseIpcCommand("{\"cmd\":\"set_hud\",\"enabled\":true}");
    check("IPC set_hud parse", c2.type == IpcCommandType::SetHud && c2.flag, "enabled=true");

    IpcCommand c3 = ParseIpcCommand("{\"cmd\":\"disconnect\"}");
    check("IPC disconnect parse", c3.type == IpcCommandType::Disconnect, "disconnect");

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

    // 2. D3D11 hardware/WARP device + VideoProcessor pipeline
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

        // 3. Media Foundation DXGI Device Manager + HW decoder probe
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

int main(int argc, char** argv) {
    HRESULT hrCo = CoInitializeEx(nullptr, COINIT_MULTITHREADED);

    bool selfTest = false;
    bool probeGpu = false;
    bool preview = false;
    HWND parentHwnd = nullptr;
    uint32_t width = 1280;
    uint32_t height = 720;

    for (int i = 1; i < argc; ++i) {
        std::string arg = argv[i];
        if (arg == "--self-test") selfTest = true;
        else if (arg == "--probe-gpu") probeGpu = true;
        else if (arg == "--preview") preview = true;
        else if (arg.rfind("--parent-hwnd=", 0) == 0) {
            unsigned long long val = std::strtoull(arg.substr(14).c_str(), nullptr, 0);
            parentHwnd = reinterpret_cast<HWND>(static_cast<uintptr_t>(val));
        } else if (arg.rfind("--width=", 0) == 0) {
            width = static_cast<uint32_t>(std::strtoul(arg.substr(8).c_str(), nullptr, 10));
        } else if (arg.rfind("--height=", 0) == 0) {
            height = static_cast<uint32_t>(std::strtoul(arg.substr(9).c_str(), nullptr, 10));
        }
    }

    int exitCode = 0;
    if (selfTest) {
        exitCode = RunSelfTest();
    } else if (probeGpu) {
        exitCode = RunProbeGpu();
    } else if (preview && parentHwnd) {
        D3D11Renderer renderer;
        std::string err;
        if (!renderer.InitializeDevice(true, err) || !renderer.AttachToHwnd(parentHwnd, width, height, err)) {
            EmitJsonLine("{\"event\":\"error\",\"message\":\"" + EscapeJson(err) + "\"}");
            exitCode = 1;
        } else {
            // Render glacier ice-blue verification frame directly into parent HWND
            renderer.PresentClearFrame(0.941f, 0.961f, 0.973f, err);
            EmitJsonLine("{\"event\":\"preview_presented\"}");
        }
    } else {
        std::cout << "BoBoStreamPlayer (V5 Native D3D11VA Streaming Core)\n"
                  << "Usage:\n"
                  << "  BoBoStreamPlayer.exe --self-test\n"
                  << "  BoBoStreamPlayer.exe --probe-gpu\n"
                  << "  BoBoStreamPlayer.exe --preview --parent-hwnd=<HWND> --width=<W> --height=<H>\n";
    }

    if (SUCCEEDED(hrCo)) {
        CoUninitialize();
    }
    return exitCode;
}
