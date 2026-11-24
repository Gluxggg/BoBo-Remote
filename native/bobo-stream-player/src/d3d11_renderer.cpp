#include "d3d11_renderer.h"

#include <cstdio>

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
    videoProcessor_.Reset();
    vpEnum_.Reset();
    swapChain_.Reset();
    videoContext_.Reset();
    videoDevice_.Reset();
    context_.Reset();
    device_.Reset();
}

bool D3D11Renderer::InitializeDevice(bool allowWarpFallback, std::string& outError) {
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

    // Required when Media Foundation D3D11VA decodes on background threads
    Microsoft::WRL::ComPtr<ID3D10Multithread> mt;
    if (SUCCEEDED(device_.As(&mt)) && mt) {
        mt->SetMultithreadProtected(TRUE);
    }

    // Query adapter metadata
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
        if (EnsureVideoProcessor(1920, 1080, 1920, 1080, vpErr)) {
            caps_.supportsNv12ToBgra = true;
        }
    }

    return true;
}

bool D3D11Renderer::AttachToHwnd(HWND targetHwnd, uint32_t width, uint32_t height, std::string& outError) {
    if (!device_) {
        outError = "D3D11 device not initialized";
        return false;
    }
    if (!targetHwnd || !IsWindow(targetHwnd)) {
        outError = "Invalid target HWND";
        return false;
    }

    swapW_ = width > 0 ? width : 1280;
    swapH_ = height > 0 ? height : 720;

    Microsoft::WRL::ComPtr<IDXGIDevice> dxgiDev;
    HRESULT hr = device_.As(&dxgiDev);
    if (FAILED(hr)) { outError = FormatHr("Query IDXGIDevice failed", hr); return false; }

    Microsoft::WRL::ComPtr<IDXGIAdapter> adapter;
    hr = dxgiDev->GetAdapter(adapter.GetAddressOf());
    if (FAILED(hr)) { outError = FormatHr("GetAdapter failed", hr); return false; }

    Microsoft::WRL::ComPtr<IDXGIFactory2> factory;
    hr = adapter->GetParent(IID_PPV_ARGS(factory.GetAddressOf()));
    if (FAILED(hr)) { outError = FormatHr("GetParent IDXGIFactory2 failed", hr); return false; }

    DXGI_SWAP_CHAIN_DESC1 scDesc = {};
    scDesc.Width = swapW_;
    scDesc.Height = swapH_;
    scDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    scDesc.SampleDesc.Count = 1;
    scDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    scDesc.BufferCount = 2;
    scDesc.Scaling = DXGI_SCALING_STRETCH;
    scDesc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    scDesc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;

    hr = factory->CreateSwapChainForHwnd(
        device_.Get(),
        targetHwnd,
        &scDesc,
        nullptr,
        nullptr,
        swapChain_.ReleaseAndGetAddressOf());
    if (FAILED(hr)) {
        // Fallback for older drivers / virtualized adapters without flip discard
        scDesc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
        scDesc.Scaling = DXGI_SCALING_STRETCH;
        hr = factory->CreateSwapChainForHwnd(
            device_.Get(),
            targetHwnd,
            &scDesc,
            nullptr,
            nullptr,
            swapChain_.ReleaseAndGetAddressOf());
    }
    if (FAILED(hr)) {
        outError = FormatHr("CreateSwapChainForHwnd failed", hr);
        return false;
    }

    factory->MakeWindowAssociation(targetHwnd, DXGI_MWA_NO_ALT_ENTER);
    return true;
}

bool D3D11Renderer::ResizeSwapChain(uint32_t width, uint32_t height, std::string& outError) {
    if (!swapChain_ || width == 0 || height == 0) return true;
    if (width == swapW_ && height == swapH_) return true;
    context_->OMSetRenderTargets(0, nullptr, nullptr);
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
    if (!videoDevice_ || !videoContext_) {
        outError = "ID3D11VideoDevice unavailable";
        return false;
    }
    if (videoProcessor_ && srcW == vpSrcW_ && srcH == vpSrcH_ && dstW == vpDstW_ && dstH == vpDstH_) {
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

    vpSrcW_ = srcW;
    vpSrcH_ = srcH;
    vpDstW_ = dstW;
    vpDstH_ = dstH;
    return true;
}

bool D3D11Renderer::PresentNv12Texture(ID3D11Texture2D* nv12Tex, uint32_t subresourceIndex, std::string& outError) {
    if (!swapChain_ || !nv12Tex || !videoDevice_ || !videoContext_) {
        outError = "Renderer not ready for NV12 presentation";
        return false;
    }

    D3D11_TEXTURE2D_DESC srcDesc = {};
    nv12Tex->GetDesc(&srcDesc);
    if (!EnsureVideoProcessor(srcDesc.Width, srcDesc.Height, swapW_, swapH_, outError)) {
        return false;
    }

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

bool D3D11Renderer::PresentClearFrame(float r, float g, float b, std::string& outError) {
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
