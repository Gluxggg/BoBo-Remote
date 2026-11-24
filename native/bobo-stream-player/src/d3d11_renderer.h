#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>

#include <cstdint>
#include <string>

namespace bobo {

struct GpuCapabilities {
    std::string adapterName;
    uint32_t vendorId = 0;
    uint32_t deviceId = 0;
    D3D_FEATURE_LEVEL featureLevel = D3D_FEATURE_LEVEL_11_0;
    bool isSoftwareWarp = false;
    bool hasVideoDevice = false;
    bool supportsNv12ToBgra = false;
};

class D3D11Renderer {
public:
    D3D11Renderer() = default;
    ~D3D11Renderer();

    bool InitializeDevice(bool allowWarpFallback, std::string& outError);
    bool AttachToHwnd(HWND targetHwnd, uint32_t width, uint32_t height, std::string& outError);
    bool ResizeSwapChain(uint32_t width, uint32_t height, std::string& outError);
    bool EnsureVideoProcessor(uint32_t srcW, uint32_t srcH, uint32_t dstW, uint32_t dstH, std::string& outError);
    bool PresentNv12Texture(ID3D11Texture2D* nv12Tex, uint32_t subresourceIndex, std::string& outError);
    bool PresentClearFrame(float r, float g, float b, std::string& outError);

    ID3D11Device* Device() const { return device_.Get(); }
    ID3D11DeviceContext* Context() const { return context_.Get(); }
    const GpuCapabilities& Capabilities() const { return caps_; }

private:
    Microsoft::WRL::ComPtr<ID3D11Device> device_;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> context_;
    Microsoft::WRL::ComPtr<ID3D11VideoDevice> videoDevice_;
    Microsoft::WRL::ComPtr<ID3D11VideoContext> videoContext_;
    Microsoft::WRL::ComPtr<ID3D11VideoProcessorEnumerator> vpEnum_;
    Microsoft::WRL::ComPtr<ID3D11VideoProcessor> videoProcessor_;
    Microsoft::WRL::ComPtr<IDXGISwapChain1> swapChain_;

    uint32_t vpSrcW_ = 0;
    uint32_t vpSrcH_ = 0;
    uint32_t vpDstW_ = 0;
    uint32_t vpDstH_ = 0;
    uint32_t swapW_ = 0;
    uint32_t swapH_ = 0;
    GpuCapabilities caps_;
};

} // namespace bobo
