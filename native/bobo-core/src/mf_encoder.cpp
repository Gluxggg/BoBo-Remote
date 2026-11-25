#include "mf_encoder.h"

#include <mferror.h>
#include <wmcodecdsp.h>

#include <chrono>
#include <cstdio>
#include <cstring>

namespace bobo {

namespace {

// Event-driven IMFAsyncCallback for hardware Async MFTs (AMD AMF / NVENC / QSV).
// Wakes the encoding thread via condition_variable in <0.05ms instead of polling with Sleep(1).
class MftAsyncEventCallback final : public IMFAsyncCallback {
public:
    MftAsyncEventCallback(IMFMediaEventGenerator* gen, std::shared_ptr<AsyncMftEventState> state)
        : gen_(gen), state_(std::move(state)) {}

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IMFAsyncCallback)) {
            *ppv = static_cast<IMFAsyncCallback*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    STDMETHODIMP_(ULONG) AddRef() override {
        return static_cast<ULONG>(refCount_.fetch_add(1, std::memory_order_relaxed) + 1);
    }

    STDMETHODIMP_(ULONG) Release() override {
        ULONG r = static_cast<ULONG>(refCount_.fetch_sub(1, std::memory_order_acq_rel) - 1);
        if (r == 0) delete this;
        return r;
    }

    STDMETHODIMP GetParameters(DWORD* pdwFlags, DWORD* pdwQueue) override {
        if (pdwFlags) *pdwFlags = 0;
        if (pdwQueue) *pdwQueue = MFASYNC_CALLBACK_QUEUE_MULTITHREADED;
        return S_OK;
    }

    STDMETHODIMP Invoke(IMFAsyncResult* pAsyncResult) override {
        if (!pAsyncResult || !state_) return S_OK;

        Microsoft::WRL::ComPtr<IMFMediaEventGenerator> gen;
        {
            std::lock_guard<std::mutex> lk(state_->mtx);
            if (state_->closed) return S_OK;
            gen = gen_;
        }
        if (!gen) return S_OK;

        Microsoft::WRL::ComPtr<IMFMediaEvent> ev;
        HRESULT hr = gen->EndGetEvent(pAsyncResult, ev.GetAddressOf());
        if (FAILED(hr) || !ev) {
            std::lock_guard<std::mutex> lk(state_->mtx);
            if (!state_->closed) {
                state_->fatalError = true;
                state_->cv.notify_all();
            }
            return S_OK;
        }

        MediaEventType met = MEUnknown;
        ev->GetType(&met);

        bool shouldReArm = false;
        {
            std::lock_guard<std::mutex> lk(state_->mtx);
            if (!state_->closed) {
                if (met == METransformNeedInput) {
                    ++state_->needInput;
                } else if (met == METransformHaveOutput) {
                    ++state_->haveOutput;
                } else if (met == MEError) {
                    state_->fatalError = true;
                }
                state_->cv.notify_all();
                shouldReArm = !state_->fatalError;
            }
        }

        if (shouldReArm) {
            HRESULT armHr = gen->BeginGetEvent(this, nullptr);
            if (FAILED(armHr)) {
                std::lock_guard<std::mutex> lk(state_->mtx);
                if (!state_->closed) {
                    state_->fatalError = true;
                    state_->cv.notify_all();
                }
            }
        }
        return S_OK;
    }

    void DetachGenerator() {
        Microsoft::WRL::ComPtr<IMFMediaEventGenerator> oldGen;
        if (state_) {
            std::lock_guard<std::mutex> lk(state_->mtx);
            state_->closed = true;
            oldGen = std::move(gen_);
            state_->cv.notify_all();
        } else {
            oldGen = std::move(gen_);
        }
    }

private:
    std::atomic<long> refCount_{ 1 };
    Microsoft::WRL::ComPtr<IMFMediaEventGenerator> gen_;
    std::shared_ptr<AsyncMftEventState> state_;
};

} // namespace

static std::string WideToUtf8Enc(const wchar_t* wstr) {
    if (!wstr || !*wstr) return std::string();
    int len = WideCharToMultiByte(CP_UTF8, 0, wstr, -1, nullptr, 0, nullptr, nullptr);
    if (len <= 1) return std::string();
    std::string res(static_cast<size_t>(len - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, wstr, -1, &res[0], len, nullptr, nullptr);
    return res;
}

static std::string FormatEncHr(const char* label, HRESULT hr) {
    char buf[160];
    std::snprintf(buf, sizeof(buf), "%s (0x%08lX)", label, static_cast<unsigned long>(hr));
    return std::string(buf);
}

MfH264Encoder::~MfH264Encoder() {
    Close();
}

void MfH264Encoder::StartAsyncEventPump() {
    StopAsyncEventPump();
    if (!isAsync_ || !eventGen_) return;

    asyncState_ = std::make_shared<AsyncMftEventState>();
    auto* rawCb = new MftAsyncEventCallback(eventGen_.Get(), asyncState_);
    asyncCb_.Attach(rawCb);
    HRESULT hr = eventGen_->BeginGetEvent(asyncCb_.Get(), nullptr);
    if (FAILED(hr)) {
        StopAsyncEventPump();
    }
}

void MfH264Encoder::StopAsyncEventPump() {
    if (asyncState_) {
        std::lock_guard<std::mutex> lk(asyncState_->mtx);
        asyncState_->closed = true;
        asyncState_->cv.notify_all();
    }
    if (asyncCb_) {
        static_cast<MftAsyncEventCallback*>(asyncCb_.Get())->DetachGenerator();
        asyncCb_.Reset();
    }
    asyncState_.reset();
}

void MfH264Encoder::Close() {
    StopAsyncEventPump();
    if (encoder_) {
        encoder_->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0);
        encoder_->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
        encoder_->ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING, 0);
    }
    reusableInBuf_.Reset();
    reusableInBufCap_ = 0;
    codecApi_.Reset();
    eventGen_.Reset();
    encoder_.Reset();
    d3dMgr_.Reset();
    d3dResetToken_ = 0;
    cachedSpsPps_.clear();
    isAsync_ = false;
    isHardware_ = false;
    isD3D11ZeroCopy_ = false;
    encoderName_.clear();
    if (mfStarted_) {
        MFShutdown();
        mfStarted_ = false;
    }
}

bool MfH264Encoder::Open(uint32_t width, uint32_t height, uint32_t fps, uint32_t bitrateKbps,
                         std::string& outError, ID3D11Device* d3dDevice) {
    Close();

    HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_LITE);
    if (FAILED(hr)) {
        outError = FormatEncHr("MFStartup failed", hr);
        return false;
    }
    mfStarted_ = true;

    width_ = (width + 1u) & ~1u;
    height_ = (height + 1u) & ~1u;
    fps_ = (fps >= 15 && fps <= 144) ? fps : 60;
    frameDurationHns_ = 10000000LL / static_cast<int64_t>(fps_);
    if (bitrateKbps < 500) bitrateKbps = 500;
    if (bitrateKbps > 60000) bitrateKbps = 60000;
    bitrateKbps_ = bitrateKbps;
    keyframeRequested_.store(true);

    // 1. Try hardware H.264 encoder MFTs first (NVENC / AMD AMF / Intel QSV).
    // When d3dDevice is provided, try zero-copy D3D11 surface mode first, then CPU NV12 hardware mode.
    MFT_REGISTER_TYPE_INFO inType = { MFMediaType_Video, MFVideoFormat_NV12 };
    MFT_REGISTER_TYPE_INFO outType = { MFMediaType_Video, MFVideoFormat_H264 };
    IMFActivate** activates = nullptr;
    UINT32 count = 0;
    hr = MFTEnumEx(
        MFT_CATEGORY_VIDEO_ENCODER,
        MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER,
        &inType,
        &outType,
        &activates,
        &count);
    if (SUCCEEDED(hr) && activates) {
        for (UINT32 i = 0; i < count; ++i) {
            std::string hwErr;
            if (d3dDevice && TryOpenCandidate(activates[i], true, width_, height_, fps_, bitrateKbps_, d3dDevice, hwErr)) {
                for (UINT32 j = 0; j < count; ++j) activates[j]->Release();
                CoTaskMemFree(activates);
                return true;
            }
            if (TryOpenCandidate(activates[i], true, width_, height_, fps_, bitrateKbps_, nullptr, hwErr)) {
                for (UINT32 j = 0; j < count; ++j) activates[j]->Release();
                CoTaskMemFree(activates);
                return true;
            }
        }
        for (UINT32 j = 0; j < count; ++j) activates[j]->Release();
        CoTaskMemFree(activates);
    }

    // 2. Reliable universal fallback: Microsoft in-box synchronous H.264 Encoder MFT (100% Win10/11)
    if (TryOpenBuiltinSoftware(width_, height_, fps_, bitrateKbps_, outError)) {
        return true;
    }

    Close();
    return false;
}

void MfH264Encoder::ApplyCodecApiLowLatency(ICodecAPI* api, uint32_t fps, uint32_t bitrateKbps) {
    if (!api) return;
    VARIANT v = {};

    v.vt = VT_BOOL;
    v.boolVal = VARIANT_TRUE;
    api->SetValue(&CODECAPI_AVLowLatencyMode, &v);
    api->SetValue(&CODECAPI_AVEncCommonRealTime, &v);

    // Use CBR with a small buffer to limit IDR bursts and apply live bitrate changes.
    // FilterAndInspectAnnexB removes CBR filler before the frame goes on the wire.
    useVbr_ = false;
    v.vt = VT_UI4;
    v.ulVal = eAVEncCommonRateControlMode_CBR;
    if (FAILED(api->SetValue(&CODECAPI_AVEncCommonRateControlMode, &v))) {
        v.ulVal = eAVEncCommonRateControlMode_PeakConstrainedVBR;
        if (SUCCEEDED(api->SetValue(&CODECAPI_AVEncCommonRateControlMode, &v))) {
            useVbr_ = true;
        }
    }

    uint64_t meanBps = static_cast<uint64_t>(bitrateKbps) * 1000ULL;
    if (meanBps > 80000000ULL) meanBps = 80000000ULL;
    v.vt = VT_UI4;
    v.ulVal = static_cast<ULONG>(meanBps);
    api->SetValue(&CODECAPI_AVEncCommonMeanBitRate, &v);

    if (useVbr_) {
        uint64_t maxBps = (meanBps * 120ULL) / 100ULL;
        if (maxBps > 80000000ULL) maxBps = 80000000ULL;
        v.vt = VT_UI4;
        v.ulVal = static_cast<ULONG>(maxBps);
        api->SetValue(&CODECAPI_AVEncCommonMaxBitRate, &v);
    }

    // Tight ~50ms HRD/VBV buffer (capped at 256 KB) prevents giant IDR bursts from tail-dropping Wi-Fi/campus/WireGuard queues
    uint64_t vbvBytes = (meanBps / 8ULL) / 20ULL;
    if (vbvBytes < 16384ULL) vbvBytes = 16384ULL;
    if (vbvBytes > 262144ULL) vbvBytes = 262144ULL;
    v.vt = VT_UI4;
    v.ulVal = static_cast<ULONG>(vbvBytes);
    api->SetValue(&CODECAPI_AVEncCommonBufferSize, &v);

    v.vt = VT_UI4;
    v.ulVal = 0; // Zero B-frames for zero reordering delay
    api->SetValue(&CODECAPI_AVEncMPVDefaultBPictureCount, &v);

    v.vt = VT_UI4;
    v.ulVal = 1; // Single reference frame minimizes encoder pipeline delay
    api->SetValue(&CODECAPI_AVEncVideoMaxNumRefFrame, &v);

    v.vt = VT_UI4;
    v.ulVal = 0; // Fastest speed preset for real-time low-latency streaming
    api->SetValue(&CODECAPI_AVEncCommonQualityVsSpeed, &v);

    v.vt = VT_UI4;
    v.ulVal = fps * 10u; // 10s GOP (on-demand PLI handles instant recovery without periodic IDR spikes)
    api->SetValue(&CODECAPI_AVEncMPVGOPSize, &v);
}

bool MfH264Encoder::UpdateBitrateLive(uint32_t bitrateKbps) {
    if (bitrateKbps < 500) bitrateKbps = 500;
    if (bitrateKbps > 60000) bitrateKbps = 60000;
    bitrateKbps_ = bitrateKbps;
    if (!codecApi_) return false;

    uint64_t meanBps = static_cast<uint64_t>(bitrateKbps) * 1000ULL;
    VARIANT v = {};
    v.vt = VT_UI4;
    v.ulVal = static_cast<ULONG>(meanBps);
    HRESULT hr = codecApi_->SetValue(&CODECAPI_AVEncCommonMeanBitRate, &v);

    if (useVbr_) {
        uint64_t maxBps = (meanBps * 120ULL) / 100ULL;
        if (maxBps > 80000000ULL) maxBps = 80000000ULL;
        v.ulVal = static_cast<ULONG>(maxBps);
        codecApi_->SetValue(&CODECAPI_AVEncCommonMaxBitRate, &v);
    }

    uint64_t vbvBytes = (meanBps / 8ULL) / 20ULL;
    if (vbvBytes < 16384ULL) vbvBytes = 16384ULL;
    if (vbvBytes > 262144ULL) vbvBytes = 262144ULL;
    v.ulVal = static_cast<ULONG>(vbvBytes);
    codecApi_->SetValue(&CODECAPI_AVEncCommonBufferSize, &v);

    return SUCCEEDED(hr);
}

bool MfH264Encoder::TryOpenCandidate(IMFActivate* activate, bool hardware, uint32_t width, uint32_t height,
                                     uint32_t fps, uint32_t bitrateKbps, ID3D11Device* d3dDevice,
                                     std::string& outError) {
    if (!activate) return false;
    wchar_t* friendly = nullptr;
    UINT32 friendlyLen = 0;
    std::string name = hardware ? "Hardware H.264 MFT" : "Software H.264 MFT";
    if (SUCCEEDED(activate->GetAllocatedString(MFT_FRIENDLY_NAME_Attribute, &friendly, &friendlyLen)) && friendly) {
        name = WideToUtf8Enc(friendly);
        CoTaskMemFree(friendly);
    }

    Microsoft::WRL::ComPtr<IMFTransform> mft;
    HRESULT hr = activate->ActivateObject(IID_PPV_ARGS(mft.GetAddressOf()));
    if (FAILED(hr) || !mft) {
        outError = FormatEncHr("ActivateObject failed", hr);
        return false;
    }

    bool asyncMft = false;
    Microsoft::WRL::ComPtr<IMFAttributes> attrs;
    if (SUCCEEDED(mft->GetAttributes(attrs.GetAddressOf())) && attrs) {
        UINT32 isAsyncVal = 0;
        if (SUCCEEDED(attrs->GetUINT32(MF_TRANSFORM_ASYNC, &isAsyncVal)) && isAsyncVal != 0) {
            asyncMft = true;
            attrs->SetUINT32(MF_TRANSFORM_ASYNC_UNLOCK, TRUE);
        }
        attrs->SetUINT32(MF_LOW_LATENCY, TRUE);
    }

    bool d3dBound = false;
    if (!ConfigureTransform(mft.Get(), asyncMft, width, height, fps, bitrateKbps, d3dDevice, d3dBound, outError)) {
        d3dMgr_.Reset();
        activate->ShutdownObject();
        return false;
    }

    encoder_ = mft;
    isAsync_ = asyncMft;
    isHardware_ = hardware;
    isD3D11ZeroCopy_ = d3dBound;
    encoderName_ = d3dBound ? (name + " [D3D11]") : name;

    if (isAsync_) {
        encoder_.As(&eventGen_);
        if (!eventGen_) {
            encoder_.Reset();
            d3dMgr_.Reset();
            activate->ShutdownObject();
            outError = "Async MFT missing IMFMediaEventGenerator";
            return false;
        }
        StartAsyncEventPump();
    }

    // Smoke-test hardware MFT with up to 3 frames so unusable GPU/D3D11 MFTs cleanly fall back
    if (hardware) {
        bool produced = false;
        if (isD3D11ZeroCopy_ && d3dDevice) {
            D3D11_TEXTURE2D_DESC td = {};
            td.Width = width;
            td.Height = height;
            td.MipLevels = 1;
            td.ArraySize = 1;
            td.Format = DXGI_FORMAT_NV12;
            td.SampleDesc.Count = 1;
            td.Usage = D3D11_USAGE_DEFAULT;
            td.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
            Microsoft::WRL::ComPtr<ID3D11Texture2D> probeTex;
            if (SUCCEEDED(d3dDevice->CreateTexture2D(&td, nullptr, probeTex.GetAddressOf())) && probeTex) {
                for (int p = 0; p < 3 && !produced; ++p) {
                    std::vector<uint8_t> probeOut;
                    bool probeKey = false;
                    std::string probeErr;
                    if (!EncodeD3D11Texture(probeTex.Get(), static_cast<int64_t>(p) * frameDurationHns_, p == 0,
                                            probeOut, probeKey, probeErr)) {
                        outError = "Hardware D3D11 MFT probe failed: " + probeErr;
                        break;
                    }
                    if (!probeOut.empty()) produced = true;
                }
            }
        } else {
            std::vector<uint8_t> probeNv12(static_cast<size_t>(width) * static_cast<size_t>(height) * 3 / 2, 128);
            for (int p = 0; p < 3 && !produced; ++p) {
                std::vector<uint8_t> probeOut;
                bool probeKey = false;
                std::string probeErr;
                if (!EncodeNv12(probeNv12.data(), probeNv12.size(), static_cast<int64_t>(p) * frameDurationHns_, p == 0,
                                probeOut, probeKey, probeErr)) {
                    outError = "Hardware MFT probe failed: " + probeErr;
                    break;
                }
                if (!probeOut.empty()) produced = true;
            }
        }

        if (!produced) {
            StopAsyncEventPump();
            reusableInBuf_.Reset();
            reusableInBufCap_ = 0;
            codecApi_.Reset();
            eventGen_.Reset();
            encoder_.Reset();
            d3dMgr_.Reset();
            activate->ShutdownObject();
            cachedSpsPps_.clear();
            isAsync_ = false;
            isHardware_ = false;
            isD3D11ZeroCopy_ = false;
            encoderName_.clear();
            if (outError.empty()) outError = "Hardware MFT produced no output during probe";
            return false;
        }
        keyframeRequested_.store(true);
    }
    return true;
}

bool MfH264Encoder::TryOpenBuiltinSoftware(uint32_t width, uint32_t height, uint32_t fps, uint32_t bitrateKbps,
                                           std::string& outError) {
    Microsoft::WRL::ComPtr<IMFTransform> mft;
    HRESULT hr = CoCreateInstance(CLSID_CMSH264EncoderMFT, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(mft.GetAddressOf()));
    if (FAILED(hr) || !mft) {
        MFT_REGISTER_TYPE_INFO inType = { MFMediaType_Video, MFVideoFormat_NV12 };
        MFT_REGISTER_TYPE_INFO outType = { MFMediaType_Video, MFVideoFormat_H264 };
        IMFActivate** activates = nullptr;
        UINT32 count = 0;
        if (SUCCEEDED(MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER,
                                &inType, &outType, &activates, &count)) && activates) {
            for (UINT32 i = 0; i < count; ++i) {
                if (TryOpenCandidate(activates[i], false, width, height, fps, bitrateKbps, nullptr, outError)) {
                    for (UINT32 j = 0; j < count; ++j) activates[j]->Release();
                    CoTaskMemFree(activates);
                    return true;
                }
            }
            for (UINT32 j = 0; j < count; ++j) activates[j]->Release();
            CoTaskMemFree(activates);
        }
        outError = FormatEncHr("CoCreateInstance(CLSID_CMSH264EncoderMFT) failed", hr);
        return false;
    }

    bool d3dBound = false;
    if (!ConfigureTransform(mft.Get(), false, width, height, fps, bitrateKbps, nullptr, d3dBound, outError)) {
        return false;
    }
    encoder_ = mft;
    isAsync_ = false;
    isHardware_ = false;
    isD3D11ZeroCopy_ = false;
    encoderName_ = "Microsoft H264 Encoder MFT";
    return true;
}

bool MfH264Encoder::ConfigureTransform(IMFTransform* mft, bool isAsync, uint32_t width, uint32_t height,
                                       uint32_t fps, uint32_t bitrateKbps, ID3D11Device* d3dDevice,
                                       bool& outD3D11Bound, std::string& outError) {
    (void)isAsync;
    outD3D11Bound = false;

    DWORD inCount = 0, outCount = 0;
    HRESULT hr = mft->GetStreamCount(&inCount, &outCount);
    if (FAILED(hr) || inCount == 0 || outCount == 0) {
        outError = "Encoder stream count invalid";
        return false;
    }

    DWORD inIds[4] = { 0 };
    DWORD outIds[4] = { 0 };
    if (SUCCEEDED(mft->GetStreamIDs(inCount > 4 ? 4 : inCount, inIds, outCount > 4 ? 4 : outCount, outIds))) {
        inStreamId_ = inIds[0];
        outStreamId_ = outIds[0];
    } else {
        inStreamId_ = 0;
        outStreamId_ = 0;
    }

    // Bind D3D11 DXGI Device Manager before media types if hardware MFT supports zero-copy GPU NV12 input
    d3dMgr_.Reset();
    d3dResetToken_ = 0;
    if (d3dDevice) {
        Microsoft::WRL::ComPtr<IMFAttributes> attrs;
        UINT32 d3dAware = 0;
        if (SUCCEEDED(mft->GetAttributes(attrs.GetAddressOf())) && attrs &&
            SUCCEEDED(attrs->GetUINT32(MF_SA_D3D11_AWARE, &d3dAware)) && d3dAware != 0) {
            if (SUCCEEDED(MFCreateDXGIDeviceManager(&d3dResetToken_, d3dMgr_.GetAddressOf())) && d3dMgr_ &&
                SUCCEEDED(d3dMgr_->ResetDevice(d3dDevice, d3dResetToken_)) &&
                SUCCEEDED(mft->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER,
                                              reinterpret_cast<ULONG_PTR>(d3dMgr_.Get())))) {
                outD3D11Bound = true;
            } else {
                d3dMgr_.Reset();
                d3dResetToken_ = 0;
            }
        }
    }

    // Configure low-latency real-time codec parameters BEFORE media type negotiation
    codecApi_.Reset();
    if (SUCCEEDED(mft->QueryInterface(IID_PPV_ARGS(codecApi_.GetAddressOf()))) && codecApi_) {
        ApplyCodecApiLowLatency(codecApi_.Get(), fps, bitrateKbps);
    }

    // Output type MUST be set BEFORE input type on MF video encoders
    Microsoft::WRL::ComPtr<IMFMediaType> outMt;
    hr = MFCreateMediaType(outMt.GetAddressOf());
    if (FAILED(hr)) { outError = "MFCreateMediaType(out) failed"; return false; }
    outMt->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    outMt->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
    outMt->SetUINT32(MF_MT_AVG_BITRATE, bitrateKbps * 1000u);
    outMt->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    outMt->SetUINT32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_Main);
    MFSetAttributeSize(outMt.Get(), MF_MT_FRAME_SIZE, width, height);
    MFSetAttributeRatio(outMt.Get(), MF_MT_FRAME_RATE, fps, 1);
    MFSetAttributeRatio(outMt.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);

    hr = mft->SetOutputType(outStreamId_, outMt.Get(), 0);
    if (FAILED(hr)) {
        outMt->SetUINT32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_Base);
        hr = mft->SetOutputType(outStreamId_, outMt.Get(), 0);
        if (FAILED(hr)) {
            outError = FormatEncHr("SetOutputType(H264) failed", hr);
            return false;
        }
    }

    // Input type: NV12 progressive
    Microsoft::WRL::ComPtr<IMFMediaType> inMt;
    hr = MFCreateMediaType(inMt.GetAddressOf());
    if (FAILED(hr)) { outError = "MFCreateMediaType(in) failed"; return false; }
    inMt->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    inMt->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    inMt->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    if (!outD3D11Bound) {
        inMt->SetUINT32(MF_MT_DEFAULT_STRIDE, width);
    }
    MFSetAttributeSize(inMt.Get(), MF_MT_FRAME_SIZE, width, height);
    MFSetAttributeRatio(inMt.Get(), MF_MT_FRAME_RATE, fps, 1);
    MFSetAttributeRatio(inMt.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);

    hr = mft->SetInputType(inStreamId_, inMt.Get(), 0);
    if (FAILED(hr)) {
        outError = FormatEncHr("SetInputType(NV12) failed", hr);
        return false;
    }

    // Re-apply ICodecAPI AFTER SetInputType because some GPU MFTs reset rate control / low-latency on type negotiation
    if (codecApi_) {
        ApplyCodecApiLowLatency(codecApi_.Get(), fps, bitrateKbps);
    }

    MFT_OUTPUT_STREAM_INFO osi = {};
    if (SUCCEEDED(mft->GetOutputStreamInfo(outStreamId_, &osi))) {
        outBufMinSize_ = osi.cbSize > 0 ? osi.cbSize : (width * height);
        outProvidesSamples_ = ((osi.dwFlags & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0);
    } else {
        outBufMinSize_ = width * height;
        outProvidesSamples_ = false;
    }

    mft->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
    mft->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
    return true;
}

void MfH264Encoder::FilterAndInspectAnnexB(std::vector<uint8_t>& annexB, bool& hasIdr, bool& hasSps) {
    hasIdr = false;
    hasSps = false;
    const uint8_t* data = annexB.data();
    size_t len = annexB.size();
    if (!data || len < 5) return;

    std::vector<uint8_t> foundSpsPps;
    std::vector<uint8_t> filtered;
    bool strippedAny = false;

    size_t i = 0;
    while (i + 3 < len) {
        size_t scLen = 0;
        if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1) scLen = 3;
        else if (i + 3 < len && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1) scLen = 4;
        if (scLen == 0) { ++i; continue; }

        size_t nalStart = i + scLen;
        if (nalStart >= len) break;
        uint8_t nalType = data[nalStart] & 0x1F;

        size_t nextSc = nalStart + 1;
        while (nextSc + 2 < len) {
            if (data[nextSc] == 0 && data[nextSc + 1] == 0 &&
                (data[nextSc + 2] == 1 || (nextSc + 3 < len && data[nextSc + 2] == 0 && data[nextSc + 3] == 1))) {
                break;
            }
            ++nextSc;
        }
        if (nextSc + 2 >= len) nextSc = len;

        // Strip H.264 Filler Data (NAL 12 - up to 99% waste on CBR!) and AUD (NAL 9)
        if (nalType == 12 || nalType == 9) {
            if (!strippedAny) {
                filtered.reserve(len);
                if (i > 0) filtered.insert(filtered.end(), data, data + i);
                strippedAny = true;
            }
            i = nextSc;
            continue;
        }

        if (nalType == 5) hasIdr = true;
        if (nalType == 7) hasSps = true;
        if (nalType == 7 || nalType == 8) {
            foundSpsPps.insert(foundSpsPps.end(), data + i, data + nextSc);
        }

        if (strippedAny) {
            filtered.insert(filtered.end(), data + i, data + nextSc);
        }
        i = nextSc;
    }

    if (!foundSpsPps.empty()) {
        cachedSpsPps_ = std::move(foundSpsPps);
    }
    if (strippedAny) {
        annexB.swap(filtered);
    }
}

bool MfH264Encoder::EncodeD3D11Texture(ID3D11Texture2D* nv12Tex, int64_t sampleTimeHns,
                                       bool forceKeyframe, std::vector<uint8_t>& outAnnexB,
                                       bool& outIsKeyframe, std::string& outError) {
    outAnnexB.clear();
    outIsKeyframe = false;
    if (!encoder_ || !isD3D11ZeroCopy_ || !nv12Tex) {
        outError = "D3D11 encoder not open";
        return false;
    }

    Microsoft::WRL::ComPtr<IMFSample> inSample;
    Microsoft::WRL::ComPtr<IMFMediaBuffer> dxBuf;
    HRESULT hr = MFCreateSample(inSample.GetAddressOf());
    if (FAILED(hr)) { outError = "MFCreateSample(d3d) failed"; return false; }
    hr = MFCreateDXGISurfaceBuffer(__uuidof(ID3D11Texture2D), nv12Tex, 0, FALSE, dxBuf.GetAddressOf());
    if (FAILED(hr)) { outError = FormatEncHr("MFCreateDXGISurfaceBuffer failed", hr); return false; }

    Microsoft::WRL::ComPtr<IMF2DBuffer> buf2d;
    if (SUCCEEDED(dxBuf.As(&buf2d)) && buf2d) {
        DWORD contLen = 0;
        if (SUCCEEDED(buf2d->GetContiguousLength(&contLen)) && contLen > 0) {
            dxBuf->SetCurrentLength(contLen);
        }
    }

    inSample->AddBuffer(dxBuf.Get());
    inSample->SetSampleTime(sampleTimeHns);
    inSample->SetSampleDuration(frameDurationHns_);
    return EncodeSampleInternal(inSample.Get(), forceKeyframe, outAnnexB, outIsKeyframe, outError);
}

bool MfH264Encoder::EncodeNv12(const uint8_t* nv12Data, size_t nv12Bytes, int64_t sampleTimeHns,
                               bool forceKeyframe, std::vector<uint8_t>& outAnnexB,
                               bool& outIsKeyframe, std::string& outError) {
    outAnnexB.clear();
    outIsKeyframe = false;
    if (!encoder_ || !nv12Data || nv12Bytes == 0) {
        outError = "Encoder not open";
        return false;
    }

    DWORD reqBytes = static_cast<DWORD>(nv12Bytes);
    if (!reusableInBuf_ || reusableInBufCap_ < reqBytes) {
        reusableInBuf_.Reset();
        HRESULT hr = MFCreateMemoryBuffer(reqBytes, reusableInBuf_.GetAddressOf());
        if (FAILED(hr)) { outError = "MFCreateMemoryBuffer(in) failed"; return false; }
        reusableInBufCap_ = reqBytes;
    }

    BYTE* dst = nullptr;
    HRESULT hr = reusableInBuf_->Lock(&dst, nullptr, nullptr);
    if (FAILED(hr) || !dst) { outError = "Lock(in) failed"; return false; }
    std::memcpy(dst, nv12Data, nv12Bytes);
    reusableInBuf_->Unlock();
    reusableInBuf_->SetCurrentLength(reqBytes);

    Microsoft::WRL::ComPtr<IMFSample> inSample;
    hr = MFCreateSample(inSample.GetAddressOf());
    if (FAILED(hr)) { outError = "MFCreateSample(in) failed"; return false; }
    inSample->AddBuffer(reusableInBuf_.Get());
    inSample->SetSampleTime(sampleTimeHns);
    inSample->SetSampleDuration(frameDurationHns_);
    return EncodeSampleInternal(inSample.Get(), forceKeyframe, outAnnexB, outIsKeyframe, outError);
}

bool MfH264Encoder::EncodeSampleInternal(IMFSample* inSample, bool forceKeyframe,
                                         std::vector<uint8_t>& outAnnexB, bool& outIsKeyframe,
                                         std::string& outError) {
    bool wantKey = forceKeyframe || keyframeRequested_.exchange(false);
    if (wantKey && codecApi_) {
        VARIANT v = {};
        v.vt = VT_UI4;
        v.ulVal = 1;
        codecApi_->SetValue(&CODECAPI_AVEncVideoForceKeyFrame, &v);
    }

    auto pullOutputSample = [&](std::vector<uint8_t>& accum) -> HRESULT {
        MFT_OUTPUT_DATA_BUFFER odb = {};
        odb.dwStreamID = outStreamId_;
        Microsoft::WRL::ComPtr<IMFSample> outSample;
        if (!outProvidesSamples_) {
            if (FAILED(MFCreateSample(outSample.GetAddressOf()))) return E_OUTOFMEMORY;
            Microsoft::WRL::ComPtr<IMFMediaBuffer> ob;
            DWORD sz = outBufMinSize_ > 65536 ? outBufMinSize_ : (width_ * height_);
            if (FAILED(MFCreateMemoryBuffer(sz, ob.GetAddressOf()))) return E_OUTOFMEMORY;
            outSample->AddBuffer(ob.Get());
            odb.pSample = outSample.Get();
        }
        DWORD status = 0;
        HRESULT ohr = encoder_->ProcessOutput(0, 1, &odb, &status);
        if (odb.pEvents) odb.pEvents->Release();
        if (SUCCEEDED(ohr) && odb.pSample) {
            Microsoft::WRL::ComPtr<IMFMediaBuffer> contig;
            if (SUCCEEDED(odb.pSample->ConvertToContiguousBuffer(contig.GetAddressOf())) && contig) {
                BYTE* p = nullptr;
                DWORD curLen = 0;
                if (SUCCEEDED(contig->Lock(&p, nullptr, &curLen)) && p && curLen > 0) {
                    accum.insert(accum.end(), p, p + curLen);
                    contig->Unlock();
                }
            }
            if (outProvidesSamples_) {
                odb.pSample->Release();
            }
        } else if (outProvidesSamples_ && odb.pSample) {
            odb.pSample->Release();
        }
        return ohr;
    };

    if (isAsync_ && asyncState_) {
        // 1. Drain any pending outputs & wait event-driven (<0.05ms wakeup) for an input slot
        auto deadlineIn = std::chrono::steady_clock::now() + std::chrono::milliseconds(100);
        while (true) {
            int drainOut = 0;
            bool hasIn = false;
            bool errFlag = false;
            {
                std::unique_lock<std::mutex> lk(asyncState_->mtx);
                if (asyncState_->haveOutput == 0 && asyncState_->needInput == 0 &&
                    !asyncState_->fatalError && !asyncState_->closed) {
                    asyncState_->cv.wait_until(lk, deadlineIn, [&]() {
                        return asyncState_->closed || asyncState_->fatalError ||
                               asyncState_->haveOutput > 0 || asyncState_->needInput > 0;
                    });
                }
                errFlag = asyncState_->fatalError || asyncState_->closed;
                if (asyncState_->haveOutput > 0) {
                    drainOut = asyncState_->haveOutput;
                    asyncState_->haveOutput = 0;
                }
                if (asyncState_->needInput > 0) {
                    --asyncState_->needInput;
                    hasIn = true;
                }
            }
            for (int d = 0; d < drainOut; ++d) {
                pullOutputSample(outAnnexB);
            }
            if (hasIn) break;
            if (errFlag) {
                outError = "Async MFT event error";
                return false;
            }
            if (std::chrono::steady_clock::now() >= deadlineIn) {
                outError = "Async MFT input queue timeout";
                return false;
            }
        }

        HRESULT hr = encoder_->ProcessInput(inStreamId_, inSample, 0);
        if (FAILED(hr)) {
            outError = FormatEncHr("Async ProcessInput failed", hr);
            return false;
        }

        // 2. Wait event-driven for the GPU to emit encoded output (wakes immediately on METransformHaveOutput)
        auto deadlineOut = std::chrono::steady_clock::now() + std::chrono::milliseconds(60);
        while (outAnnexB.empty()) {
            int drainOut = 0;
            bool anotherInputNeeded = false;
            bool errFlag = false;
            {
                std::unique_lock<std::mutex> lk(asyncState_->mtx);
                if (asyncState_->haveOutput == 0 && !asyncState_->fatalError && !asyncState_->closed) {
                    auto shortWait = (asyncState_->needInput > 0)
                        ? std::chrono::steady_clock::now() + std::chrono::milliseconds(6)
                        : deadlineOut;
                    if (shortWait > deadlineOut) shortWait = deadlineOut;
                    asyncState_->cv.wait_until(lk, shortWait, [&]() {
                        return asyncState_->closed || asyncState_->fatalError || asyncState_->haveOutput > 0;
                    });
                }
                errFlag = asyncState_->fatalError || asyncState_->closed;
                if (asyncState_->haveOutput > 0) {
                    drainOut = asyncState_->haveOutput;
                    asyncState_->haveOutput = 0;
                }
                anotherInputNeeded = (asyncState_->needInput > 0);
            }
            for (int d = 0; d < drainOut; ++d) {
                pullOutputSample(outAnnexB);
            }
            if (!outAnnexB.empty() || errFlag) break;
            if (anotherInputNeeded) break; // MFT explicitly asked for next input before flushing first output
            if (std::chrono::steady_clock::now() >= deadlineOut) break;
        }
    } else {
        HRESULT hr = encoder_->ProcessInput(inStreamId_, inSample, 0);
        if (hr == MF_E_NOTACCEPTING) {
            pullOutputSample(outAnnexB);
            hr = encoder_->ProcessInput(inStreamId_, inSample, 0);
        }
        if (FAILED(hr)) {
            outError = FormatEncHr("ProcessInput failed", hr);
            return false;
        }
        for (int i = 0; i < 4; ++i) {
            HRESULT ohr = pullOutputSample(outAnnexB);
            if (ohr == MF_E_TRANSFORM_NEED_MORE_INPUT) break;
            if (ohr == MF_E_TRANSFORM_STREAM_CHANGE) {
                MFT_OUTPUT_STREAM_INFO osi = {};
                if (SUCCEEDED(encoder_->GetOutputStreamInfo(outStreamId_, &osi))) {
                    outBufMinSize_ = osi.cbSize > 0 ? osi.cbSize : (width_ * height_);
                    outProvidesSamples_ = ((osi.dwFlags & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0);
                }
                continue;
            }
            if (FAILED(ohr)) break;
        }
    }

    if (!outAnnexB.empty()) {
        bool hasIdr = false, hasSps = false;
        FilterAndInspectAnnexB(outAnnexB, hasIdr, hasSps);
        outIsKeyframe = hasIdr;
        // Guarantee SPS/PPS precedes every IDR frame so decoder can start/recover immediately
        if (hasIdr && !hasSps && !cachedSpsPps_.empty()) {
            std::vector<uint8_t> combined;
            combined.reserve(cachedSpsPps_.size() + outAnnexB.size());
            combined.insert(combined.end(), cachedSpsPps_.begin(), cachedSpsPps_.end());
            combined.insert(combined.end(), outAnnexB.begin(), outAnnexB.end());
            outAnnexB.swap(combined);
        }
    }
    return true;
}

} // namespace bobo
