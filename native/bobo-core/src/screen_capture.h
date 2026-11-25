#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>

#include "bobo_core.h"

#include <cstdint>
#include <string>
#include <vector>

namespace bobo {

class ScreenCapturer {
public:
    ScreenCapturer() = default;
    ~ScreenCapturer();

    static std::vector<BoBoMonitorInfo> EnumerateMonitors();

    bool Initialize(int monitorIndex, uint32_t outWidth, uint32_t outHeight, std::string& outError);
    void Shutdown();

    // Captures current desktop frame into an even-aligned NV12 CPU buffer (width=outW, height=outH, stride=outW).
    // Uses DXGI Desktop Duplication + GPU D3D11 VideoProcessor (BGRA->NV12 + scaling) when available,
    // with automatic seamless GDI BitBlt fallback on access-lost/hybrid-GPU transitions.
    bool CaptureNv12Frame(int timeoutMs, std::vector<uint8_t>& outNv12, bool& outChanged, std::string& outError);

    uint32_t OutputWidth() const { return outW_; }
    uint32_t OutputHeight() const { return outH_; }
    bool UsingDxgi() const { return dxgiReady_; }
    const BoBoMonitorInfo& ActiveMonitor() const { return activeMonitor_; }

private:
    bool InitDxgiForMonitor(const BoBoMonitorInfo& mon);
    bool EnsureColorConverter(uint32_t srcW, uint32_t srcH);
    bool ConvertBgraTextureToNv12(ID3D11Texture2D* bgraTex, std::vector<uint8_t>& outNv12);
    bool CaptureViaGdi(std::vector<uint8_t>& outNv12);
    static void BgraBufferToNv12(const uint8_t* bgra, uint32_t srcW, uint32_t srcH, uint32_t srcStride,
                                 uint8_t* nv12Out, uint32_t dstW, uint32_t dstH);

    BoBoMonitorInfo activeMonitor_ = {};
    int monitorIndex_ = 0;
    uint32_t outW_ = 1920;
    uint32_t outH_ = 1080;
    bool dxgiReady_ = false;
    HRESULT lastInitDxgiHr_ = S_OK;
    bool hasFirstFrame_ = false;

    Microsoft::WRL::ComPtr<ID3D11Device> device_;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> context_;
    Microsoft::WRL::ComPtr<IDXGIOutputDuplication> duplication_;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> lastDesktopTex_;
    Microsoft::WRL::ComPtr<ID3D11VideoDevice> videoDevice_;
    Microsoft::WRL::ComPtr<ID3D11VideoContext> videoContext_;
    Microsoft::WRL::ComPtr<ID3D11VideoProcessorEnumerator> vpEnum_;
    Microsoft::WRL::ComPtr<ID3D11VideoProcessor> vp_;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> nv12GpuTex_;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> nv12StagingTex_;
    uint32_t vpSrcW_ = 0;
    uint32_t vpSrcH_ = 0;

    // Reusable GDI fallback resources
    HDC memDc_ = nullptr;
    HBITMAP dibBmp_ = nullptr;
    uint8_t* dibBits_ = nullptr;
    uint32_t dibW_ = 0;
    uint32_t dibH_ = 0;
};

} // namespace bobo
