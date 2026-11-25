#include "mf_encoder.h"

#include <mferror.h>
#include <wmcodecdsp.h>

#include <cstdio>
#include <cstring>

namespace bobo {

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

void MfH264Encoder::Close() {
    if (encoder_) {
        encoder_->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0);
        encoder_->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
        encoder_->ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING, 0);
    }
    codecApi_.Reset();
    eventGen_.Reset();
    encoder_.Reset();
    cachedSpsPps_.clear();
    isAsync_ = false;
    isHardware_ = false;
    pendingInputRequests_ = 0;
    encoderName_.clear();
    if (mfStarted_) {
        MFShutdown();
        mfStarted_ = false;
    }
}

bool MfH264Encoder::Open(uint32_t width, uint32_t height, uint32_t fps, uint32_t bitrateKbps, std::string& outError) {
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
    if (bitrateKbps < 1000) bitrateKbps = 1000;
    if (bitrateKbps > 60000) bitrateKbps = 60000;
    keyframeRequested_.store(true);

    // 1. Try hardware H.264 encoder MFTs first (NVENC / AMD AMF / Intel QSV)
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
            if (TryOpenCandidate(activates[i], true, width_, height_, fps_, bitrateKbps, hwErr)) {
                for (UINT32 j = 0; j < count; ++j) activates[j]->Release();
                CoTaskMemFree(activates);
                return true;
            }
        }
        for (UINT32 j = 0; j < count; ++j) activates[j]->Release();
        CoTaskMemFree(activates);
    }

    // 2. Reliable universal fallback: Microsoft in-box synchronous H.264 Encoder MFT (present on 100% Win10/11)
    if (TryOpenBuiltinSoftware(width_, height_, fps_, bitrateKbps, outError)) {
        return true;
    }

    Close();
    return false;
}

bool MfH264Encoder::TryOpenCandidate(IMFActivate* activate, bool hardware, uint32_t width, uint32_t height,
                                     uint32_t fps, uint32_t bitrateKbps, std::string& outError) {
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
    }

    if (!ConfigureTransform(mft.Get(), asyncMft, width, height, fps, bitrateKbps, outError)) {
        activate->ShutdownObject();
        return false;
    }

    encoder_ = mft;
    isAsync_ = asyncMft;
    isHardware_ = hardware;
    pendingInputRequests_ = 0;
    encoderName_ = name;
    if (isAsync_) {
        encoder_.As(&eventGen_);
        if (!eventGen_) {
            encoder_.Reset();
            activate->ShutdownObject();
            outError = "Async MFT missing IMFMediaEventGenerator";
            return false;
        }
    }

    // Smoke-test hardware MFT with up to 3 blank NV12 frames so unusable GPU MFTs cleanly fall back to built-in sync MFT
    if (hardware) {
        std::vector<uint8_t> probeNv12(static_cast<size_t>(width) * static_cast<size_t>(height) * 3 / 2, 128);
        bool produced = false;
        for (int p = 0; p < 4 && !produced; ++p) {
            std::vector<uint8_t> probeOut;
            bool probeKey = false;
            std::string probeErr;
            if (!EncodeNv12(probeNv12.data(), probeNv12.size(), static_cast<int64_t>(p) * frameDurationHns_, p == 0, probeOut, probeKey, probeErr)) {
                outError = "Hardware MFT probe failed: " + probeErr;
                break;
            }
            if (!probeOut.empty()) produced = true;
        }
        if (!produced) {
            codecApi_.Reset();
            eventGen_.Reset();
            encoder_.Reset();
            activate->ShutdownObject();
            cachedSpsPps_.clear();
            isAsync_ = false;
            isHardware_ = false;
            pendingInputRequests_ = 0;
            encoderName_.clear();
            if (outError.empty()) outError = "Hardware MFT produced no output during probe";
            return false;
        }
        keyframeRequested_.store(true);
    }
    return true;
}

bool MfH264Encoder::TryOpenBuiltinSoftware(uint32_t width, uint32_t height, uint32_t fps, uint32_t bitrateKbps, std::string& outError) {
    Microsoft::WRL::ComPtr<IMFTransform> mft;
    HRESULT hr = CoCreateInstance(CLSID_CMSH264EncoderMFT, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(mft.GetAddressOf()));
    if (FAILED(hr) || !mft) {
        // Enumerate any sync software H.264 encoder as secondary lookup
        MFT_REGISTER_TYPE_INFO inType = { MFMediaType_Video, MFVideoFormat_NV12 };
        MFT_REGISTER_TYPE_INFO outType = { MFMediaType_Video, MFVideoFormat_H264 };
        IMFActivate** activates = nullptr;
        UINT32 count = 0;
        if (SUCCEEDED(MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER,
                                &inType, &outType, &activates, &count)) && activates) {
            for (UINT32 i = 0; i < count; ++i) {
                if (TryOpenCandidate(activates[i], false, width, height, fps, bitrateKbps, outError)) {
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

    if (!ConfigureTransform(mft.Get(), false, width, height, fps, bitrateKbps, outError)) {
        return false;
    }
    encoder_ = mft;
    isAsync_ = false;
    isHardware_ = false;
    encoderName_ = "Microsoft H264 Encoder MFT";
    return true;
}

bool MfH264Encoder::ConfigureTransform(IMFTransform* mft, bool isAsync, uint32_t width, uint32_t height,
                                       uint32_t fps, uint32_t bitrateKbps, std::string& outError) {
    (void)isAsync;
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

    // Configure low-latency real-time codec parameters via ICodecAPI
    codecApi_.Reset();
    if (SUCCEEDED(mft->QueryInterface(IID_PPV_ARGS(codecApi_.GetAddressOf()))) && codecApi_) {
        VARIANT v = {};
        v.vt = VT_BOOL;
        v.boolVal = VARIANT_TRUE;
        codecApi_->SetValue(&CODECAPI_AVLowLatencyMode, &v);

        v.vt = VT_UI4;
        v.ulVal = eAVEncCommonRateControlMode_CBR;
        codecApi_->SetValue(&CODECAPI_AVEncCommonRateControlMode, &v);

        v.vt = VT_UI4;
        v.ulVal = bitrateKbps * 1000u;
        codecApi_->SetValue(&CODECAPI_AVEncCommonMeanBitRate, &v);

        v.vt = VT_UI4;
        v.ulVal = 0; // Zero B-frames for minimum latency
        codecApi_->SetValue(&CODECAPI_AVEncMPVDefaultBPictureCount, &v);

        v.vt = VT_UI4;
        v.ulVal = fps * 2u; // Keyframe every 2 seconds
        codecApi_->SetValue(&CODECAPI_AVEncMPVGOPSize, &v);
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
        // Fallback to Baseline profile if Main rejected
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
    inMt->SetUINT32(MF_MT_DEFAULT_STRIDE, width);
    MFSetAttributeSize(inMt.Get(), MF_MT_FRAME_SIZE, width, height);
    MFSetAttributeRatio(inMt.Get(), MF_MT_FRAME_RATE, fps, 1);
    MFSetAttributeRatio(inMt.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);

    hr = mft->SetInputType(inStreamId_, inMt.Get(), 0);
    if (FAILED(hr)) {
        outError = FormatEncHr("SetInputType(NV12) failed", hr);
        return false;
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

void MfH264Encoder::ExtractSpsPpsAndFlagIdr(const uint8_t* data, size_t len, bool& hasIdr, bool& hasSps) {
    hasIdr = false;
    hasSps = false;
    if (!data || len < 5) return;

    std::vector<uint8_t> foundSpsPps;
    size_t i = 0;
    while (i + 4 < len) {
        size_t scLen = 0;
        if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1) scLen = 3;
        else if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1) scLen = 4;
        if (scLen == 0) { ++i; continue; }

        size_t nalStart = i + scLen;
        if (nalStart >= len) break;
        uint8_t nalType = data[nalStart] & 0x1F;
        if (nalType == 5) hasIdr = true;
        if (nalType == 7) hasSps = true;

        // Find end of this NAL unit
        size_t nextSc = nalStart + 1;
        while (nextSc + 3 < len) {
            if (data[nextSc] == 0 && data[nextSc + 1] == 0 &&
                (data[nextSc + 2] == 1 || (nextSc + 3 < len && data[nextSc + 2] == 0 && data[nextSc + 3] == 1))) {
                break;
            }
            ++nextSc;
        }
        if (nextSc + 3 >= len) nextSc = len;

        if (nalType == 7 || nalType == 8) {
            foundSpsPps.insert(foundSpsPps.end(), data + i, data + nextSc);
        }
        i = nextSc;
    }

    if (!foundSpsPps.empty()) {
        cachedSpsPps_ = std::move(foundSpsPps);
    }
}

bool MfH264Encoder::EncodeNv12(const uint8_t* nv12Data, size_t nv12Bytes, int64_t sampleTimeHns,
                               bool forceKeyframe, std::vector<uint8_t>& outAnnexB, bool& outIsKeyframe, std::string& outError) {
    outAnnexB.clear();
    outIsKeyframe = false;
    if (!encoder_ || !nv12Data || nv12Bytes == 0) {
        outError = "Encoder not open";
        return false;
    }

    bool wantKey = forceKeyframe || keyframeRequested_.exchange(false);
    if (wantKey && codecApi_) {
        VARIANT v = {};
        v.vt = VT_UI4;
        v.ulVal = 1;
        codecApi_->SetValue(&CODECAPI_AVEncVideoForceKeyFrame, &v);
    }

    Microsoft::WRL::ComPtr<IMFSample> inSample;
    Microsoft::WRL::ComPtr<IMFMediaBuffer> inBuf;
    HRESULT hr = MFCreateSample(inSample.GetAddressOf());
    if (FAILED(hr)) { outError = "MFCreateSample(in) failed"; return false; }
    hr = MFCreateMemoryBuffer(static_cast<DWORD>(nv12Bytes), inBuf.GetAddressOf());
    if (FAILED(hr)) { outError = "MFCreateMemoryBuffer(in) failed"; return false; }

    BYTE* dst = nullptr;
    hr = inBuf->Lock(&dst, nullptr, nullptr);
    if (FAILED(hr) || !dst) { outError = "Lock(in) failed"; return false; }
    std::memcpy(dst, nv12Data, nv12Bytes);
    inBuf->Unlock();
    inBuf->SetCurrentLength(static_cast<DWORD>(nv12Bytes));

    inSample->AddBuffer(inBuf.Get());
    inSample->SetSampleTime(sampleTimeHns);
    inSample->SetSampleDuration(frameDurationHns_);

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

    if (isAsync_ && eventGen_) {
        // Non-blocking event pump: never hang on GetEvent(0) when MFT buffers frames
        auto pumpEvents = [&](bool& gotOutput) -> bool {
            for (int iter = 0; iter < 16; ++iter) {
                Microsoft::WRL::ComPtr<IMFMediaEvent> ev;
                HRESULT ehr = eventGen_->GetEvent(MF_EVENT_FLAG_NO_WAIT, ev.GetAddressOf());
                if (ehr == MF_E_NO_EVENTS_AVAILABLE) return true;
                if (FAILED(ehr) || !ev) return false;
                MediaEventType met = MEUnknown;
                ev->GetType(&met);
                if (met == METransformNeedInput) {
                    ++pendingInputRequests_;
                } else if (met == METransformHaveOutput) {
                    if (SUCCEEDED(pullOutputSample(outAnnexB)) && !outAnnexB.empty()) {
                        gotOutput = true;
                    }
                } else if (met == MEError) {
                    return false;
                }
            }
            return true;
        };

        bool gotOutput = false;
        if (!pumpEvents(gotOutput)) {
            outError = "Async MFT event pump error";
            return false;
        }
        // Wait up to 80ms for an input slot if none is queued yet
        for (int wait = 0; wait < 80 && pendingInputRequests_ <= 0; ++wait) {
            Sleep(1);
            if (!pumpEvents(gotOutput)) {
                outError = "Async MFT wait input error";
                return false;
            }
        }
        if (pendingInputRequests_ <= 0) {
            outError = "Async MFT input queue timeout";
            return false;
        }
        hr = encoder_->ProcessInput(inStreamId_, inSample.Get(), 0);
        if (FAILED(hr)) {
            outError = FormatEncHr("Async ProcessInput failed", hr);
            return false;
        }
        --pendingInputRequests_;

        // Wait briefly (up to 40ms) for encoded output on low-latency frame
        for (int wait = 0; wait < 40 && outAnnexB.empty(); ++wait) {
            if (!pumpEvents(gotOutput)) break;
            if (!outAnnexB.empty()) break;
            if (pendingInputRequests_ > 0 && wait >= 4) break; // MFT wants another frame before flushing first output
            Sleep(1);
        }
    } else {
        hr = encoder_->ProcessInput(inStreamId_, inSample.Get(), 0);
        if (hr == MF_E_NOTACCEPTING) {
            // Drain pending output first, then submit
            pullOutputSample(outAnnexB);
            hr = encoder_->ProcessInput(inStreamId_, inSample.Get(), 0);
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
        ExtractSpsPpsAndFlagIdr(outAnnexB.data(), outAnnexB.size(), hasIdr, hasSps);
        outIsKeyframe = hasIdr;
        // Guarantee SPS/PPS precedes every IDR frame so decoder can start immediately
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
