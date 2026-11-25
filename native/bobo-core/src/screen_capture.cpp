#include "screen_capture.h"

#include <algorithm>
#include <cstdio>
#include <cstring>

namespace bobo {

static std::string WideToUtf8Short(const wchar_t* wstr) {
    if (!wstr || !*wstr) return std::string();
    int len = WideCharToMultiByte(CP_UTF8, 0, wstr, -1, nullptr, 0, nullptr, nullptr);
    if (len <= 1) return std::string();
    std::string out(static_cast<size_t>(len - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, wstr, -1, &out[0], len, nullptr, nullptr);
    return out;
}

static BOOL CALLBACK MonitorEnumCallback(HMONITOR hMon, HDC, LPRECT, LPARAM lParam) {
    auto* list = reinterpret_cast<std::vector<BoBoMonitorInfo>*>(lParam);
    MONITORINFOEXW mi = {};
    mi.cbSize = sizeof(mi);
    if (!GetMonitorInfoW(hMon, &mi)) return TRUE;

    BoBoMonitorInfo info = {};
    info.index = static_cast<int32_t>(list->size());
    info.left = mi.rcMonitor.left;
    info.top = mi.rcMonitor.top;
    info.width = mi.rcMonitor.right - mi.rcMonitor.left;
    info.height = mi.rcMonitor.bottom - mi.rcMonitor.top;
    info.isPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) ? 1 : 0;

    std::string devName = WideToUtf8Short(mi.szDevice);
    if (devName.rfind("\\\\.\\", 0) == 0) devName = devName.substr(4);
    char label[64] = {};
    std::snprintf(label, sizeof(label), "%s (%dx%d)",
                  devName.empty() ? "Display" : devName.c_str(), info.width, info.height);
    std::strncpy(info.name, label, sizeof(info.name) - 1);
    list->push_back(info);
    return TRUE;
}

std::vector<BoBoMonitorInfo> ScreenCapturer::EnumerateMonitors() {
    std::vector<BoBoMonitorInfo> list;
    EnumDisplayMonitors(nullptr, nullptr, MonitorEnumCallback, reinterpret_cast<LPARAM>(&list));
    std::stable_sort(list.begin(), list.end(), [](const BoBoMonitorInfo& a, const BoBoMonitorInfo& b) {
        return a.isPrimary > b.isPrimary;
    });
    for (size_t i = 0; i < list.size(); ++i) {
        list[i].index = static_cast<int32_t>(i);
    }
    return list;
}

ScreenCapturer::~ScreenCapturer() {
    Shutdown();
}

bool ScreenCapturer::Initialize(int monitorIndex, uint32_t outWidth, uint32_t outHeight, std::string& outError) {
    Shutdown();

    auto monitors = EnumerateMonitors();
    if (monitors.empty()) {
        outError = "No active desktop monitors detected";
        return false;
    }
    if (monitorIndex < 0 || monitorIndex >= static_cast<int>(monitors.size())) {
        monitorIndex = 0;
    }
    monitorIndex_ = monitorIndex;
    activeMonitor_ = monitors[static_cast<size_t>(monitorIndex)];

    uint32_t targetW = (outWidth >= 320 && outWidth <= 4096) ? outWidth : static_cast<uint32_t>(activeMonitor_.width);
    uint32_t targetH = (outHeight >= 240 && outHeight <= 2560) ? outHeight : static_cast<uint32_t>(activeMonitor_.height);
    outW_ = (targetW + 1u) & ~1u;
    outH_ = (targetH + 1u) & ~1u;
    if (outW_ < 320) outW_ = 320;
    if (outH_ < 240) outH_ = 240;

    dxgiReady_ = InitDxgiForMonitor(activeMonitor_);
    return true;
}

bool ScreenCapturer::InitDxgiForMonitor(const BoBoMonitorInfo& mon) {
    lastInitDxgiHr_ = S_OK;
    Microsoft::WRL::ComPtr<IDXGIFactory1> factory;
    HRESULT fhr = CreateDXGIFactory1(__uuidof(IDXGIFactory1), reinterpret_cast<void**>(factory.GetAddressOf()));
    if (FAILED(fhr)) {
        lastInitDxgiHr_ = fhr;
        return false;
    }

    // Two passes: pass 0 matches exact coordinates, pass 1 falls back to nth attached output (handles non-DPI-aware callers)
    for (int pass = 0; pass < 2; ++pass) {
        int attachedIdx = 0;
        for (UINT aIdx = 0;; ++aIdx) {
            Microsoft::WRL::ComPtr<IDXGIAdapter1> adapter;
            if (factory->EnumAdapters1(aIdx, adapter.GetAddressOf()) == DXGI_ERROR_NOT_FOUND) break;

            for (UINT oIdx = 0;; ++oIdx) {
                Microsoft::WRL::ComPtr<IDXGIOutput> output;
                if (adapter->EnumOutputs(oIdx, output.GetAddressOf()) == DXGI_ERROR_NOT_FOUND) break;

                DXGI_OUTPUT_DESC od = {};
                if (FAILED(output->GetDesc(&od)) || !od.AttachedToDesktop) continue;
                int curAttached = attachedIdx++;
                if (pass == 0) {
                    if (od.DesktopCoordinates.left != mon.left || od.DesktopCoordinates.top != mon.top) continue;
                } else {
                    if (curAttached != monitorIndex_ && monitorIndex_ != 0) continue;
                    if (monitorIndex_ == 0 && curAttached != 0) continue;
                }

                Microsoft::WRL::ComPtr<IDXGIOutput1> output1;
                if (FAILED(output.As(&output1))) continue;

                const D3D_FEATURE_LEVEL levels[] = {
                    D3D_FEATURE_LEVEL_11_1,
                    D3D_FEATURE_LEVEL_11_0,
                    D3D_FEATURE_LEVEL_10_1,
                    D3D_FEATURE_LEVEL_10_0
                };
                D3D_FEATURE_LEVEL chosen = D3D_FEATURE_LEVEL_11_0;
                UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
                HRESULT hr = D3D11CreateDevice(
                    adapter.Get(),
                    D3D_DRIVER_TYPE_UNKNOWN,
                    nullptr,
                    flags,
                    levels,
                    static_cast<UINT>(sizeof(levels) / sizeof(levels[0])),
                    D3D11_SDK_VERSION,
                    device_.ReleaseAndGetAddressOf(),
                    &chosen,
                    context_.ReleaseAndGetAddressOf());
                if (FAILED(hr)) {
                    flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
                    hr = D3D11CreateDevice(
                        adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, flags,
                        levels, static_cast<UINT>(sizeof(levels) / sizeof(levels[0])),
                        D3D11_SDK_VERSION, device_.ReleaseAndGetAddressOf(), &chosen, context_.ReleaseAndGetAddressOf());
                }
                if (FAILED(hr)) {
                    lastInitDxgiHr_ = hr;
                    continue;
                }

                hr = output1->DuplicateOutput(device_.Get(), duplication_.ReleaseAndGetAddressOf());
                if (FAILED(hr)) {
                    lastInitDxgiHr_ = hr;
                    device_.Reset();
                    context_.Reset();
                    continue;
                }

                device_.As(&videoDevice_);
                context_.As(&videoContext_);
                return true;
            }
        }
    }
    return false;
}

bool ScreenCapturer::EnsureColorConverter(uint32_t srcW, uint32_t srcH) {
    if (!device_ || !context_ || !videoDevice_ || !videoContext_) return false;
    if (vp_ && nv12GpuTex_ && nv12StagingTex_ && vpSrcW_ == srcW && vpSrcH_ == srcH) return true;

    vp_.Reset();
    vpEnum_.Reset();
    nv12GpuTex_.Reset();
    nv12StagingTex_.Reset();

    D3D11_VIDEO_PROCESSOR_CONTENT_DESC desc = {};
    desc.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
    desc.InputWidth = srcW;
    desc.InputHeight = srcH;
    desc.OutputWidth = outW_;
    desc.OutputHeight = outH_;
    desc.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;

    if (FAILED(videoDevice_->CreateVideoProcessorEnumerator(&desc, vpEnum_.GetAddressOf()))) return false;

    UINT nv12Support = 0;
    if (FAILED(vpEnum_->CheckVideoProcessorFormat(DXGI_FORMAT_NV12, &nv12Support)) ||
        (nv12Support & D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_OUTPUT) == 0) {
        return false;
    }

    if (FAILED(videoDevice_->CreateVideoProcessor(vpEnum_.Get(), 0, vp_.GetAddressOf()))) return false;

    D3D11_VIDEO_PROCESSOR_COLOR_SPACE inCs = {};
    inCs.Usage = 0;
    inCs.RGB_Range = 0; // Full 0-255
    inCs.YCbCr_Matrix = 1; // BT.709
    inCs.Nominal_Range = 2;
    videoContext_->VideoProcessorSetStreamColorSpace(vp_.Get(), 0, &inCs);

    D3D11_VIDEO_PROCESSOR_COLOR_SPACE outCs = {};
    outCs.Usage = 0;
    outCs.RGB_Range = 0;
    outCs.YCbCr_Matrix = 1; // BT.709
    outCs.Nominal_Range = 1; // Studio 16-235
    videoContext_->VideoProcessorSetOutputColorSpace(vp_.Get(), &outCs);

    D3D11_TEXTURE2D_DESC td = {};
    td.Width = outW_;
    td.Height = outH_;
    td.MipLevels = 1;
    td.ArraySize = 1;
    td.Format = DXGI_FORMAT_NV12;
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_RENDER_TARGET;
    if (FAILED(device_->CreateTexture2D(&td, nullptr, nv12GpuTex_.GetAddressOf()))) return false;

    D3D11_TEXTURE2D_DESC sd = td;
    sd.Usage = D3D11_USAGE_STAGING;
    sd.BindFlags = 0;
    sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    if (FAILED(device_->CreateTexture2D(&sd, nullptr, nv12StagingTex_.GetAddressOf()))) return false;

    vpSrcW_ = srcW;
    vpSrcH_ = srcH;
    return true;
}

bool ScreenCapturer::ConvertBgraTextureToNv12(ID3D11Texture2D* bgraTex, std::vector<uint8_t>& outNv12) {
    if (!bgraTex || !device_ || !context_) return false;
    D3D11_TEXTURE2D_DESC srcDesc = {};
    bgraTex->GetDesc(&srcDesc);

    if (EnsureColorConverter(srcDesc.Width, srcDesc.Height)) {
        bool vpOk = false;
        RECT srcRect = { 0, 0, static_cast<LONG>(srcDesc.Width), static_cast<LONG>(srcDesc.Height) };
        RECT dstRect = { 0, 0, static_cast<LONG>(outW_), static_cast<LONG>(outH_) };
        videoContext_->VideoProcessorSetStreamSourceRect(vp_.Get(), 0, TRUE, &srcRect);
        videoContext_->VideoProcessorSetStreamDestRect(vp_.Get(), 0, TRUE, &dstRect);
        videoContext_->VideoProcessorSetOutputTargetRect(vp_.Get(), TRUE, &dstRect);

        // Some GPU drivers reject CreateVideoProcessorInputView directly on raw DXGI duplication surfaces.
        ID3D11Texture2D* vpSrcTex = lastDesktopTex_ ? lastDesktopTex_.Get() : bgraTex;
        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC inDesc = {};
        inDesc.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
        Microsoft::WRL::ComPtr<ID3D11VideoProcessorInputView> inView;
        if (SUCCEEDED(videoDevice_->CreateVideoProcessorInputView(vpSrcTex, vpEnum_.Get(), &inDesc, inView.GetAddressOf()))) {
            D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC outDesc = {};
            outDesc.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
            Microsoft::WRL::ComPtr<ID3D11VideoProcessorOutputView> outView;
            if (SUCCEEDED(videoDevice_->CreateVideoProcessorOutputView(nv12GpuTex_.Get(), vpEnum_.Get(), &outDesc, outView.GetAddressOf()))) {
                D3D11_VIDEO_PROCESSOR_STREAM stream = {};
                stream.Enable = TRUE;
                stream.pInputSurface = inView.Get();
                if (SUCCEEDED(videoContext_->VideoProcessorBlt(vp_.Get(), outView.Get(), 0, 1, &stream))) {
                    context_->CopyResource(nv12StagingTex_.Get(), nv12GpuTex_.Get());
                    D3D11_MAPPED_SUBRESOURCE mapped = {};
                    if (SUCCEEDED(context_->Map(nv12StagingTex_.Get(), 0, D3D11_MAP_READ, 0, &mapped))) {
                        outNv12.resize(static_cast<size_t>(outW_) * static_cast<size_t>(outH_) * 3 / 2);
                        const uint8_t* srcY = static_cast<const uint8_t*>(mapped.pData);
                        uint8_t* dstY = outNv12.data();
                        for (uint32_t row = 0; row < outH_; ++row) {
                            std::memcpy(dstY + row * outW_, srcY + row * mapped.RowPitch, outW_);
                        }
                        const uint8_t* srcUv = srcY + mapped.RowPitch * outH_;
                        uint8_t* dstUv = dstY + outW_ * outH_;
                        uint32_t uvRows = outH_ / 2;
                        for (uint32_t row = 0; row < uvRows; ++row) {
                            std::memcpy(dstUv + row * outW_, srcUv + row * mapped.RowPitch, outW_);
                        }
                        context_->Unmap(nv12StagingTex_.Get(), 0);
                        vpOk = true;
                    }
                }
            }
        }
        if (vpOk) return true;
        // Disable GPU VP on failure and transparently fall through to CPU staging conversion below
        vp_.Reset();
        vpEnum_.Reset();
        videoContext_.Reset();
        videoDevice_.Reset();
    }

    // GPU VideoProcessor NV12 output unsupported -> copy BGRA staging and convert on CPU
    D3D11_TEXTURE2D_DESC sd = srcDesc;
    sd.Usage = D3D11_USAGE_STAGING;
    sd.BindFlags = 0;
    sd.MiscFlags = 0;
    sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> stage;
    if (FAILED(device_->CreateTexture2D(&sd, nullptr, stage.GetAddressOf()))) return false;
    context_->CopyResource(stage.Get(), bgraTex);

    D3D11_MAPPED_SUBRESOURCE mapped = {};
    if (FAILED(context_->Map(stage.Get(), 0, D3D11_MAP_READ, 0, &mapped))) return false;
    outNv12.resize(static_cast<size_t>(outW_) * static_cast<size_t>(outH_) * 3 / 2);
    BgraBufferToNv12(static_cast<const uint8_t*>(mapped.pData), srcDesc.Width, srcDesc.Height, mapped.RowPitch,
                     outNv12.data(), outW_, outH_);
    context_->Unmap(stage.Get(), 0);
    return true;
}

bool ScreenCapturer::CaptureNv12Frame(int timeoutMs, std::vector<uint8_t>& outNv12, bool& outChanged, std::string& outError) {
    outChanged = false;
    HRESULT lastDxgiHr = S_OK;
    if (dxgiReady_ && duplication_) {
        DXGI_OUTDUPL_FRAME_INFO frameInfo = {};
        Microsoft::WRL::ComPtr<IDXGIResource> desktopRes;
        HRESULT hr = duplication_->AcquireNextFrame(static_cast<UINT>(timeoutMs > 0 ? timeoutMs : 0), &frameInfo, desktopRes.GetAddressOf());
        lastDxgiHr = hr;
        if (SUCCEEDED(hr)) {
            Microsoft::WRL::ComPtr<ID3D11Texture2D> desktopTex;
            bool ok = false;
            if (SUCCEEDED(desktopRes.As(&desktopTex)) && desktopTex) {
                D3D11_TEXTURE2D_DESC td = {};
                desktopTex->GetDesc(&td);
                if (!lastDesktopTex_) {
                    D3D11_TEXTURE2D_DESC copyDesc = td;
                    copyDesc.Usage = D3D11_USAGE_DEFAULT;
                    copyDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
                    copyDesc.CPUAccessFlags = 0;
                    copyDesc.MiscFlags = 0;
                    if (FAILED(device_->CreateTexture2D(&copyDesc, nullptr, lastDesktopTex_.ReleaseAndGetAddressOf()))) {
                        copyDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
                        device_->CreateTexture2D(&copyDesc, nullptr, lastDesktopTex_.ReleaseAndGetAddressOf());
                    }
                }
                if (lastDesktopTex_) {
                    context_->CopyResource(lastDesktopTex_.Get(), desktopTex.Get());
                }
                ok = ConvertBgraTextureToNv12(desktopTex.Get(), outNv12);
                if (ok) {
                    hasFirstFrame_ = true;
                    outChanged = true;
                }
            }
            duplication_->ReleaseFrame();
            if (ok) return true;
        } else if (hr == DXGI_ERROR_WAIT_TIMEOUT) {
            if (hasFirstFrame_ && lastDesktopTex_) {
                if (ConvertBgraTextureToNv12(lastDesktopTex_.Get(), outNv12)) {
                    return true;
                }
            }
            // No new desktop dirty rect yet -> grab via GDI for immediate first frame
            if (CaptureViaGdi(outNv12)) {
                hasFirstFrame_ = true;
                outChanged = true;
                return true;
            }
            char diag[160];
            std::snprintf(diag, sizeof(diag), "DXGI timeout (0x%08lX) and GDI BitBlt failed (gle=%lu)",
                          static_cast<unsigned long>(hr), static_cast<unsigned long>(GetLastError()));
            outError = diag;
            return false;
        } else if (hr == DXGI_ERROR_ACCESS_LOST || hr == DXGI_ERROR_INVALID_CALL) {
            duplication_.Reset();
            lastDesktopTex_.Reset();
            dxgiReady_ = InitDxgiForMonitor(activeMonitor_);
        }
    }

    // Reliable GDI fallback (covers UAC/hybrid GPU/RDP transitions cleanly)
    if (CaptureViaGdi(outNv12)) {
        hasFirstFrame_ = true;
        outChanged = true;
        return true;
    }
    char diag[224];
    std::snprintf(diag, sizeof(diag), "DXGI (ready=%d initHr=0x%08lX acqHr=0x%08lX) and GDI BitBlt failed (gle=%lu)",
                  dxgiReady_ ? 1 : 0, static_cast<unsigned long>(lastInitDxgiHr_),
                  static_cast<unsigned long>(lastDxgiHr), static_cast<unsigned long>(GetLastError()));
    outError = diag;
    return false;
}

bool ScreenCapturer::CaptureViaGdi(std::vector<uint8_t>& outNv12) {
    uint32_t w = static_cast<uint32_t>(activeMonitor_.width > 0 ? activeMonitor_.width : GetSystemMetrics(SM_CXSCREEN));
    uint32_t h = static_cast<uint32_t>(activeMonitor_.height > 0 ? activeMonitor_.height : GetSystemMetrics(SM_CYSCREEN));
    if (w < 16 || h < 16) return false;

    HDC screenDc = GetDC(nullptr);
    if (!screenDc) return false;

    if (!memDc_ || !dibBmp_ || dibW_ != w || dibH_ != h) {
        if (dibBmp_) { DeleteObject(dibBmp_); dibBmp_ = nullptr; }
        if (memDc_) { DeleteDC(memDc_); memDc_ = nullptr; }
        memDc_ = CreateCompatibleDC(screenDc);
        BITMAPINFO bmi = {};
        bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = static_cast<LONG>(w);
        bmi.bmiHeader.biHeight = -static_cast<LONG>(h); // top-down DIB
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = BI_RGB;
        void* bits = nullptr;
        dibBmp_ = CreateDIBSection(screenDc, &bmi, DIB_RGB_COLORS, &bits, nullptr, 0);
        dibBits_ = static_cast<uint8_t*>(bits);
        dibW_ = w;
        dibH_ = h;
    }

    bool ok = false;
    if (memDc_ && dibBmp_ && dibBits_) {
        HGDIOBJ oldObj = SelectObject(memDc_, dibBmp_);
        if (BitBlt(memDc_, 0, 0, static_cast<int>(w), static_cast<int>(h),
                   screenDc, activeMonitor_.left, activeMonitor_.top, SRCCOPY)) {
            // Draw system cursor onto GDI fallback image when visible
            CURSORINFO ci = {};
            ci.cbSize = sizeof(ci);
            if (GetCursorInfo(&ci) && (ci.flags & CURSOR_SHOWING) && ci.hCursor) {
                ICONINFO ii = {};
                int hotX = 0, hotY = 0;
                if (GetIconInfo(ci.hCursor, &ii)) {
                    hotX = static_cast<int>(ii.xHotspot);
                    hotY = static_cast<int>(ii.yHotspot);
                    if (ii.hbmMask) DeleteObject(ii.hbmMask);
                    if (ii.hbmColor) DeleteObject(ii.hbmColor);
                }
                int cx = ci.ptScreenPos.x - activeMonitor_.left - hotX;
                int cy = ci.ptScreenPos.y - activeMonitor_.top - hotY;
                DrawIconEx(memDc_, cx, cy, ci.hCursor, 0, 0, 0, nullptr, DI_NORMAL);
            }

            outNv12.resize(static_cast<size_t>(outW_) * static_cast<size_t>(outH_) * 3 / 2);
            BgraBufferToNv12(dibBits_, w, h, w * 4, outNv12.data(), outW_, outH_);
            ok = true;
        }
        SelectObject(memDc_, oldObj);
    }

    ReleaseDC(nullptr, screenDc);
    return ok;
}

void ScreenCapturer::BgraBufferToNv12(const uint8_t* bgra, uint32_t srcW, uint32_t srcH, uint32_t srcStride,
                                      uint8_t* nv12Out, uint32_t dstW, uint32_t dstH) {
    uint8_t* dstY = nv12Out;
    uint8_t* dstUv = nv12Out + static_cast<size_t>(dstW) * static_cast<size_t>(dstH);

    // Fixed-point BT.709 limited-range conversion with nearest-neighbor scaling
    for (uint32_t y = 0; y < dstH; ++y) {
        uint32_t sy = static_cast<uint32_t>((static_cast<uint64_t>(y) * srcH) / dstH);
        if (sy >= srcH) sy = srcH - 1;
        const uint8_t* row = bgra + static_cast<size_t>(sy) * srcStride;
        uint8_t* yRow = dstY + static_cast<size_t>(y) * dstW;
        bool writeUv = ((y & 1u) == 0u);
        uint8_t* uvRow = writeUv ? (dstUv + static_cast<size_t>(y / 2) * dstW) : nullptr;

        for (uint32_t x = 0; x < dstW; ++x) {
            uint32_t sx = static_cast<uint32_t>((static_cast<uint64_t>(x) * srcW) / dstW);
            if (sx >= srcW) sx = srcW - 1;
            const uint8_t* px = row + static_cast<size_t>(sx) * 4;
            int b = px[0];
            int g = px[1];
            int r = px[2];
            int yVal = ((47 * r + 157 * g + 16 * b + 128) >> 8) + 16;
            if (yVal < 16) yVal = 16;
            else if (yVal > 235) yVal = 235;
            yRow[x] = static_cast<uint8_t>(yVal);

            if (writeUv && ((x & 1u) == 0u)) {
                int uVal = ((-26 * r - 87 * g + 112 * b + 128) >> 8) + 128;
                int vVal = ((112 * r - 102 * g - 10 * b + 128) >> 8) + 128;
                if (uVal < 16) uVal = 16; else if (uVal > 240) uVal = 240;
                if (vVal < 16) vVal = 16; else if (vVal > 240) vVal = 240;
                uvRow[x] = static_cast<uint8_t>(uVal);
                uvRow[x + 1] = static_cast<uint8_t>(vVal);
            }
        }
    }
}

void ScreenCapturer::Shutdown() {
    dxgiReady_ = false;
    hasFirstFrame_ = false;
    nv12StagingTex_.Reset();
    nv12GpuTex_.Reset();
    vp_.Reset();
    vpEnum_.Reset();
    lastDesktopTex_.Reset();
    duplication_.Reset();
    videoContext_.Reset();
    videoDevice_.Reset();
    context_.Reset();
    device_.Reset();
    vpSrcW_ = 0;
    vpSrcH_ = 0;
    if (dibBmp_) { DeleteObject(dibBmp_); dibBmp_ = nullptr; }
    if (memDc_) { DeleteDC(memDc_); memDc_ = nullptr; }
    dibBits_ = nullptr;
    dibW_ = 0;
    dibH_ = 0;
}

} // namespace bobo
