#include "d3d11_renderer.h"

#include <algorithm>
#include <cstdio>
#include <cstring>

namespace bobo {

static std::string WideToUtf8(const wchar_t* wstr) {
    if (!wstr || !*wstr) return std::string();
    int len = WideCharToMultiByte(CP_UTF8, 0, wstr, -1, nullptr, 0, nullptr, nullptr);
    if (len <= 1) return std::string();
    std::string result(static_cast<size_t>(len - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, wstr, -1, &result[0], len, nullptr, nullptr);
    return result;
}

static std::string FormatHr(const char* label, HRESULT hr) {
    char buf[160];
    std::snprintf(buf, sizeof(buf), "%s (HRESULT=0x%08lX)", label, static_cast<unsigned long>(hr));
    return std::string(buf);
}

D3D11Renderer::~D3D11Renderer() {
    std::lock_guard<std::mutex> lock(mutex_);
    cpuUploadNv12_.Reset();
    videoProcessor_.Reset();
    vpEnum_.Reset();
    swapChain_.Reset();
    videoContext_.Reset();
    videoDevice_.Reset();
    context_.Reset();
    device_.Reset();
}

RECT D3D11Renderer::ComputeAspectFitRect(uint32_t srcW, uint32_t srcH, uint32_t dstW, uint32_t dstH) {
    RECT r = { 0, 0, static_cast<LONG>(dstW), static_cast<LONG>(dstH) };
    if (srcW == 0 || srcH == 0 || dstW == 0 || dstH == 0) return r;
    double scaleX = static_cast<double>(dstW) / static_cast<double>(srcW);
    double scaleY = static_cast<double>(dstH) / static_cast<double>(srcH);
    double s = (scaleX < scaleY) ? scaleX : scaleY;
    LONG fitW = static_cast<LONG>(srcW * s + 0.5);
    LONG fitH = static_cast<LONG>(srcH * s + 0.5);
    if (fitW < 1) fitW = 1;
    if (fitH < 1) fitH = 1;
    if (fitW > static_cast<LONG>(dstW)) fitW = static_cast<LONG>(dstW);
    if (fitH > static_cast<LONG>(dstH)) fitH = static_cast<LONG>(dstH);
    r.left = (static_cast<LONG>(dstW) - fitW) / 2;
    r.top = (static_cast<LONG>(dstH) - fitH) / 2;
    r.right = r.left + fitW;
    r.bottom = r.top + fitH;
    return r;
}

void D3D11Renderer::SetVideoSourceDimensions(uint32_t videoW, uint32_t videoH) {
    std::lock_guard<std::mutex> lock(mutex_);
    if (videoW > 0 && videoH > 0) {
        videoW_ = videoW;
        videoH_ = videoH;
    }
}

void D3D11Renderer::SetAspectFit(bool fit) {
    std::lock_guard<std::mutex> lock(mutex_);
    scaleMode_ = fit ? 1 : 0;
}

bool D3D11Renderer::IsAspectFit() const {
    std::lock_guard<std::mutex> lock(mutex_);
    return scaleMode_ != 0;
}

void D3D11Renderer::SetScaleMode(int scaleMode) {
    std::lock_guard<std::mutex> lock(mutex_);
    if (scaleMode < 0 || scaleMode > 2) scaleMode = 1;
    scaleMode_ = scaleMode;
}

int D3D11Renderer::ScaleMode() const {
    std::lock_guard<std::mutex> lock(mutex_);
    return scaleMode_;
}

RECT D3D11Renderer::GetVideoViewportRect() const {
    std::lock_guard<std::mutex> lock(mutex_);
    if (scaleMode_ == 2) {
        // SuperFill: edge-to-edge full screen with zero black bars
        RECT full = { 0, 0, static_cast<LONG>(swapW_), static_cast<LONG>(swapH_) };
        return full;
    }
    if (scaleMode_ == 1) {
        return ComputeAspectFitRect(videoW_, videoH_, swapW_, swapH_);
    }
    LONG left = (static_cast<LONG>(swapW_) - static_cast<LONG>(videoW_)) / 2;
    LONG top = (static_cast<LONG>(swapH_) - static_cast<LONG>(videoH_)) / 2;
    RECT r = { left, top, left + static_cast<LONG>(videoW_), top + static_cast<LONG>(videoH_) };
    return r;
}

bool D3D11Renderer::InitializeDevice(bool allowWarpFallback, std::string& outError) {
    std::lock_guard<std::mutex> lock(mutex_);
    const D3D_FEATURE_LEVEL levels[] = {
        D3D_FEATURE_LEVEL_11_1,
        D3D_FEATURE_LEVEL_11_0,
        D3D_FEATURE_LEVEL_10_1,
        D3D_FEATURE_LEVEL_10_0
    };

    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
    HRESULT hr = D3D11CreateDevice(
        nullptr,
        D3D_DRIVER_TYPE_HARDWARE,
        nullptr,
        flags,
        levels,
        static_cast<UINT>(sizeof(levels) / sizeof(levels[0])),
        D3D11_SDK_VERSION,
        device_.ReleaseAndGetAddressOf(),
        &caps_.featureLevel,
        context_.ReleaseAndGetAddressOf());

    if (FAILED(hr) && allowWarpFallback) {
        caps_.isSoftwareWarp = true;
        flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
        hr = D3D11CreateDevice(
            nullptr,
            D3D_DRIVER_TYPE_WARP,
            nullptr,
            flags,
            levels,
            static_cast<UINT>(sizeof(levels) / sizeof(levels[0])),
            D3D11_SDK_VERSION,
            device_.ReleaseAndGetAddressOf(),
            &caps_.featureLevel,
            context_.ReleaseAndGetAddressOf());
    }

    if (FAILED(hr)) {
        outError = FormatHr("D3D11CreateDevice failed", hr);
        return false;
    }

    Microsoft::WRL::ComPtr<ID3D10Multithread> mt;
    if (SUCCEEDED(device_.As(&mt)) && mt) {
        mt->SetMultithreadProtected(TRUE);
    }

    Microsoft::WRL::ComPtr<IDXGIDevice> dxgiDev;
    if (SUCCEEDED(device_.As(&dxgiDev)) && dxgiDev) {
        Microsoft::WRL::ComPtr<IDXGIAdapter> adapter;
        if (SUCCEEDED(dxgiDev->GetAdapter(adapter.GetAddressOf())) && adapter) {
            Microsoft::WRL::ComPtr<IDXGIAdapter1> adapter1;
            if (SUCCEEDED(adapter.As(&adapter1)) && adapter1) {
                DXGI_ADAPTER_DESC1 desc = {};
                if (SUCCEEDED(adapter1->GetDesc1(&desc))) {
                    caps_.adapterName = WideToUtf8(desc.Description);
                    caps_.vendorId = desc.VendorId;
                    caps_.deviceId = desc.DeviceId;
                    if ((desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0 || desc.VendorId == 0x1414) {
                        caps_.isSoftwareWarp = true;
                    }
                }
            }
        }
    }

    if (SUCCEEDED(device_.As(&videoDevice_)) && SUCCEEDED(context_.As(&videoContext_))) {
        caps_.hasVideoDevice = true;
        std::string vpErr;
        if (EnsureVideoProcessorLocked(1920, 1080, 1920, 1080, vpErr)) {
            caps_.supportsNv12ToBgra = true;
        }
    }

    return true;
}

bool D3D11Renderer::AttachToHwnd(HWND targetHwnd, uint32_t width, uint32_t height, std::string& outError) {
    std::lock_guard<std::mutex> lock(mutex_);
    if (!device_) {
        outError = "D3D11 device not initialized";
        return false;
    }
    if (!targetHwnd || !IsWindow(targetHwnd)) {
        outError = "Invalid target HWND";
        return false;
    }

    Microsoft::WRL::ComPtr<IDXGIDevice1> dxgiDev;
    HRESULT hr = device_.As(&dxgiDev);
    if (FAILED(hr)) { outError = FormatHr("QI IDXGIDevice1 failed", hr); return false; }

    Microsoft::WRL::ComPtr<IDXGIAdapter> adapter;
    hr = dxgiDev->GetAdapter(adapter.GetAddressOf());
    if (FAILED(hr)) { outError = FormatHr("GetAdapter failed", hr); return false; }

    Microsoft::WRL::ComPtr<IDXGIFactory2> factory;
    hr = adapter->GetParent(IID_PPV_ARGS(factory.GetAddressOf()));
    if (FAILED(hr)) { outError = FormatHr("GetParent IDXGIFactory2 failed", hr); return false; }

    DXGI_SWAP_CHAIN_DESC1 scDesc = {};
    scDesc.Width = width > 0 ? width : 1280;
    scDesc.Height = height > 0 ? height : 720;
    scDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    scDesc.SampleDesc.Count = 1;
    scDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    scDesc.BufferCount = 2;
    scDesc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    scDesc.Scaling = DXGI_SCALING_STRETCH;
    scDesc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;

    hr = factory->CreateSwapChainForHwnd(device_.Get(), targetHwnd, &scDesc, nullptr, nullptr, swapChain_.ReleaseAndGetAddressOf());
    if (FAILED(hr)) {
        scDesc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
        scDesc.Scaling = DXGI_SCALING_NONE;
        hr = factory->CreateSwapChainForHwnd(device_.Get(), targetHwnd, &scDesc, nullptr, nullptr, swapChain_.ReleaseAndGetAddressOf());
    }
    if (FAILED(hr)) {
        outError = FormatHr("CreateSwapChainForHwnd failed", hr);
        return false;
    }

    factory->MakeWindowAssociation(targetHwnd, DXGI_MWA_NO_ALT_ENTER);
    swapW_ = scDesc.Width;
    swapH_ = scDesc.Height;
    return true;
}

bool D3D11Renderer::ResizeSwapChain(uint32_t width, uint32_t height, std::string& outError) {
    std::lock_guard<std::mutex> lock(mutex_);
    if (!swapChain_ || width == 0 || height == 0) return true;
    if (swapW_ == width && swapH_ == height) return true;

    context_->ClearState();
    HRESULT hr = swapChain_->ResizeBuffers(0, width, height, DXGI_FORMAT_UNKNOWN, 0);
    if (FAILED(hr)) {
        outError = FormatHr("ResizeBuffers failed", hr);
        return false;
    }
    swapW_ = width;
    swapH_ = height;
    return true;
}

bool D3D11Renderer::EnsureVideoProcessor(uint32_t srcW, uint32_t srcH, uint32_t dstW, uint32_t dstH, std::string& outError) {
    std::lock_guard<std::mutex> lock(mutex_);
    return EnsureVideoProcessorLocked(srcW, srcH, dstW, dstH, outError);
}

bool D3D11Renderer::EnsureVideoProcessorLocked(uint32_t srcW, uint32_t srcH, uint32_t dstW, uint32_t dstH, std::string& outError) {
    if (!videoDevice_ || !videoContext_) {
        outError = "ID3D11VideoDevice unavailable";
        return false;
    }
    if (videoProcessor_ && vpEnum_ && vpSrcW_ == srcW && vpSrcH_ == srcH && vpDstW_ == dstW && vpDstH_ == dstH) {
        return true;
    }

    D3D11_VIDEO_PROCESSOR_CONTENT_DESC desc = {};
    desc.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
    desc.InputWidth = srcW;
    desc.InputHeight = srcH;
    desc.OutputWidth = dstW;
    desc.OutputHeight = dstH;
    desc.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;

    HRESULT hr = videoDevice_->CreateVideoProcessorEnumerator(&desc, vpEnum_.ReleaseAndGetAddressOf());
    if (FAILED(hr)) {
        outError = FormatHr("CreateVideoProcessorEnumerator failed", hr);
        return false;
    }

    UINT supportFlags = 0;
    hr = vpEnum_->CheckVideoProcessorFormat(DXGI_FORMAT_NV12, &supportFlags);
    if (FAILED(hr) || (supportFlags & D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_INPUT) == 0) {
        outError = "GPU VideoProcessor does not report NV12 input support";
        return false;
    }

    hr = videoDevice_->CreateVideoProcessor(vpEnum_.Get(), 0, videoProcessor_.ReleaseAndGetAddressOf());
    if (FAILED(hr)) {
        outError = FormatHr("CreateVideoProcessor failed", hr);
        return false;
    }

    D3D11_VIDEO_COLOR black = {};
    black.RGBA.R = 0.04f;
    black.RGBA.G = 0.06f;
    black.RGBA.B = 0.09f;
    black.RGBA.A = 1.0f;
    videoContext_->VideoProcessorSetOutputBackgroundColor(videoProcessor_.Get(), FALSE, &black);

    D3D11_VIDEO_PROCESSOR_COLOR_SPACE inCs = {};
    inCs.Usage = 0; // Playback
    inCs.RGB_Range = 0; // Full range
    inCs.YCbCr_Matrix = 1; // BT.709 (matches COLORSPACE_REC_709)
    inCs.YCbCr_xvYCC = 0;
    inCs.Nominal_Range = 1; // Studio 16-235 (matches COLOR_RANGE_LIMITED)
    videoContext_->VideoProcessorSetStreamColorSpace(videoProcessor_.Get(), 0, &inCs);

    D3D11_VIDEO_PROCESSOR_COLOR_SPACE outCs = {};
    outCs.Usage = 0;
    outCs.RGB_Range = 0; // Full range RGB output to swapchain
    outCs.Nominal_Range = 2; // 0-255
    videoContext_->VideoProcessorSetOutputColorSpace(videoProcessor_.Get(), &outCs);

    vpSrcW_ = srcW;
    vpSrcH_ = srcH;
    vpDstW_ = dstW;
    vpDstH_ = dstH;
    return true;
}

bool D3D11Renderer::PresentNv12Texture(ID3D11Texture2D* nv12Tex, uint32_t subresourceIndex, std::string& outError) {
    return PresentNv12Texture(nv12Tex, subresourceIndex, 0, 0, outError);
}

bool D3D11Renderer::PresentNv12Texture(ID3D11Texture2D* nv12Tex, uint32_t subresourceIndex,
                                       uint32_t visibleWidth, uint32_t visibleHeight, std::string& outError) {
    std::lock_guard<std::mutex> lock(mutex_);
    return PresentNv12TextureLocked(nv12Tex, subresourceIndex, visibleWidth, visibleHeight, outError);
}

bool D3D11Renderer::PresentNv12TextureLocked(ID3D11Texture2D* nv12Tex, uint32_t subresourceIndex,
                                             uint32_t visibleWidth, uint32_t visibleHeight, std::string& outError) {
    if (!swapChain_ || !nv12Tex || !videoDevice_ || !videoContext_) {
        outError = "Renderer not ready for NV12 presentation";
        return false;
    }

    D3D11_TEXTURE2D_DESC srcDesc = {};
    nv12Tex->GetDesc(&srcDesc);
    uint32_t visW = (visibleWidth > 0 && visibleWidth <= srcDesc.Width) ? visibleWidth : srcDesc.Width;
    uint32_t visH = (visibleHeight > 0 && visibleHeight <= srcDesc.Height) ? visibleHeight : srcDesc.Height;
    videoW_ = visW;
    videoH_ = visH;

    if (!EnsureVideoProcessorLocked(srcDesc.Width, srcDesc.Height, swapW_, swapH_, outError)) {
        return false;
    }

    RECT srcRect = { 0, 0, static_cast<LONG>(visW), static_cast<LONG>(visH) };
    RECT dstRect = {};
    if (scaleMode_ == 2) {
        // SuperFill: fill entire swapchain edge-to-edge (zero top/bottom/side black bars)
        dstRect = { 0, 0, static_cast<LONG>(swapW_), static_cast<LONG>(swapH_) };
    } else if (scaleMode_ == 1) {
        dstRect = ComputeAspectFitRect(visW, visH, swapW_, swapH_);
    } else {
        // 1:1 pixel mapping centered in swapchain (crop symmetrically if window is smaller than stream)
        LONG drawW = static_cast<LONG>(visW < swapW_ ? visW : swapW_);
        LONG drawH = static_cast<LONG>(visH < swapH_ ? visH : swapH_);
        LONG srcX = (static_cast<LONG>(visW) - drawW) / 2;
        LONG srcY = (static_cast<LONG>(visH) - drawH) / 2;
        LONG dstX = (static_cast<LONG>(swapW_) - drawW) / 2;
        LONG dstY = (static_cast<LONG>(swapH_) - drawH) / 2;
        srcRect = { srcX, srcY, srcX + drawW, srcY + drawH };
        dstRect = { dstX, dstY, dstX + drawW, dstY + drawH };
    }
    RECT targetRect = { 0, 0, static_cast<LONG>(swapW_), static_cast<LONG>(swapH_) };
    videoContext_->VideoProcessorSetStreamSourceRect(videoProcessor_.Get(), 0, TRUE, &srcRect);
    videoContext_->VideoProcessorSetStreamDestRect(videoProcessor_.Get(), 0, TRUE, &dstRect);
    videoContext_->VideoProcessorSetOutputTargetRect(videoProcessor_.Get(), TRUE, &targetRect);

    Microsoft::WRL::ComPtr<ID3D11Texture2D> backBuffer;
    HRESULT hr = swapChain_->GetBuffer(0, IID_PPV_ARGS(backBuffer.GetAddressOf()));
    if (FAILED(hr)) { outError = FormatHr("GetBuffer failed", hr); return false; }

    D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC inDesc = {};
    inDesc.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
    inDesc.Texture2D.ArraySlice = subresourceIndex;
    Microsoft::WRL::ComPtr<ID3D11VideoProcessorInputView> inView;
    hr = videoDevice_->CreateVideoProcessorInputView(nv12Tex, vpEnum_.Get(), &inDesc, inView.GetAddressOf());
    if (FAILED(hr)) { outError = FormatHr("CreateVideoProcessorInputView failed", hr); return false; }

    D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC outDesc = {};
    outDesc.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
    Microsoft::WRL::ComPtr<ID3D11VideoProcessorOutputView> outView;
    hr = videoDevice_->CreateVideoProcessorOutputView(backBuffer.Get(), vpEnum_.Get(), &outDesc, outView.GetAddressOf());
    if (FAILED(hr)) { outError = FormatHr("CreateVideoProcessorOutputView failed", hr); return false; }

    D3D11_VIDEO_PROCESSOR_STREAM stream = {};
    stream.Enable = TRUE;
    stream.pInputSurface = inView.Get();

    hr = videoContext_->VideoProcessorBlt(videoProcessor_.Get(), outView.Get(), 0, 1, &stream);
    if (FAILED(hr)) { outError = FormatHr("VideoProcessorBlt failed", hr); return false; }

    hr = swapChain_->Present(0, 0);
    if (FAILED(hr)) { outError = FormatHr("Present failed", hr); return false; }
    return true;
}

bool D3D11Renderer::PresentNv12CpuBuffer(const uint8_t* nv12Data, uint32_t width, uint32_t height, uint32_t stride, std::string& outError) {
    std::lock_guard<std::mutex> lock(mutex_);
    if (!device_ || !context_ || !nv12Data || width == 0 || height == 0) {
        outError = "Invalid CPU NV12 buffer";
        return false;
    }

    uint32_t alignedW = (width + 1u) & ~1u;
    uint32_t alignedH = (height + 1u) & ~1u;
    if (!cpuUploadNv12_ || cpuUploadW_ != alignedW || cpuUploadH_ != alignedH) {
        D3D11_TEXTURE2D_DESC desc = {};
        desc.Width = alignedW;
        desc.Height = alignedH;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_NV12;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DYNAMIC;
        desc.BindFlags = D3D11_BIND_DECODER;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        HRESULT hr = device_->CreateTexture2D(&desc, nullptr, cpuUploadNv12_.ReleaseAndGetAddressOf());
        if (FAILED(hr)) {
            desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            hr = device_->CreateTexture2D(&desc, nullptr, cpuUploadNv12_.ReleaseAndGetAddressOf());
        }
        if (FAILED(hr)) {
            outError = FormatHr("CreateTexture2D NV12 upload failed", hr);
            return false;
        }
        cpuUploadW_ = alignedW;
        cpuUploadH_ = alignedH;
    }

    D3D11_MAPPED_SUBRESOURCE mapped = {};
    HRESULT hr = context_->Map(cpuUploadNv12_.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
    if (FAILED(hr)) { outError = FormatHr("Map NV12 upload failed", hr); return false; }

    uint32_t srcStride = stride >= width ? stride : width;
    uint8_t* dst = static_cast<uint8_t*>(mapped.pData);
    for (uint32_t row = 0; row < height; ++row) {
        std::memcpy(dst + row * mapped.RowPitch, nv12Data + row * srcStride, width);
    }
    const uint8_t* srcUv = nv12Data + srcStride * height;
    uint8_t* dstUv = dst + mapped.RowPitch * alignedH;
    uint32_t uvRows = height / 2;
    for (uint32_t row = 0; row < uvRows; ++row) {
        std::memcpy(dstUv + row * mapped.RowPitch, srcUv + row * srcStride, width);
    }
    context_->Unmap(cpuUploadNv12_.Get(), 0);

    return PresentNv12TextureLocked(cpuUploadNv12_.Get(), 0, width, height, outError);
}

bool D3D11Renderer::PresentClearFrame(float r, float g, float b, std::string& outError) {
    std::lock_guard<std::mutex> lock(mutex_);
    if (!swapChain_ || !device_ || !context_) {
        outError = "SwapChain not attached";
        return false;
    }
    Microsoft::WRL::ComPtr<ID3D11Texture2D> backBuffer;
    HRESULT hr = swapChain_->GetBuffer(0, IID_PPV_ARGS(backBuffer.GetAddressOf()));
    if (FAILED(hr)) { outError = FormatHr("GetBuffer failed", hr); return false; }

    Microsoft::WRL::ComPtr<ID3D11RenderTargetView> rtv;
    hr = device_->CreateRenderTargetView(backBuffer.Get(), nullptr, rtv.GetAddressOf());
    if (FAILED(hr)) { outError = FormatHr("CreateRenderTargetView failed", hr); return false; }

    const float clearColor[4] = { r, g, b, 1.0f };
    context_->OMSetRenderTargets(1, rtv.GetAddressOf(), nullptr);
    context_->ClearRenderTargetView(rtv.Get(), clearColor);
    hr = swapChain_->Present(1, 0);
    if (FAILED(hr)) { outError = FormatHr("Present failed", hr); return false; }
    return true;
}

} // namespace bobo
