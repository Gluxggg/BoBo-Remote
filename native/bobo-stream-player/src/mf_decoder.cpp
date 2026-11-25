#include "mf_decoder.h"

#include <codecapi.h>
#include <mferror.h>
#include <cstdio>
#include <cstring>

namespace bobo {

static std::string WideToUtf8Mf(const wchar_t* wstr) {
    if (!wstr || !*wstr) return std::string();
    int len = WideCharToMultiByte(CP_UTF8, 0, wstr, -1, nullptr, 0, nullptr, nullptr);
    if (len <= 1) return std::string();
    std::string result(static_cast<size_t>(len - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, wstr, -1, &result[0], len, nullptr, nullptr);
    return result;
}

static std::string FormatMfHr(const char* label, HRESULT hr) {
    char buf[160];
    std::snprintf(buf, sizeof(buf), "%s (HRESULT=0x%08lX)", label, static_cast<unsigned long>(hr));
    return std::string(buf);
}

static std::vector<std::string> EnumMftNames(const GUID& subtype, UINT32 flags) {
    std::vector<std::string> names;
    MFT_REGISTER_TYPE_INFO inType = { MFMediaType_Video, subtype };
    IMFActivate** activates = nullptr;
    UINT32 count = 0;
    HRESULT hr = MFTEnumEx(MFT_CATEGORY_VIDEO_DECODER, flags, &inType, nullptr, &activates, &count);
    if (SUCCEEDED(hr) && activates) {
        for (UINT32 i = 0; i < count; ++i) {
            wchar_t* friendly = nullptr;
            UINT32 cch = 0;
            if (SUCCEEDED(activates[i]->GetAllocatedString(MFT_FRIENDLY_NAME_Attribute, &friendly, &cch)) && friendly) {
                names.push_back(WideToUtf8Mf(friendly));
                CoTaskMemFree(friendly);
            } else {
                names.push_back("Unnamed Video Decoder MFT");
            }
            activates[i]->Release();
        }
        CoTaskMemFree(activates);
    }
    return names;
}

MfHardwareDecoder::~MfHardwareDecoder() {
    CloseCodec();
    dxgiManager_.Reset();
    if (mfStarted_) {
        MFShutdown();
        mfStarted_ = false;
    }
}

void MfHardwareDecoder::CloseCodec() {
    if (decoder_) {
        decoder_->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
        decoder_->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0);
        decoder_.Reset();
    }
    activeDecoderName_.clear();
}

void MfHardwareDecoder::Flush() {
    if (decoder_) {
        decoder_->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0);
    }
}

bool MfHardwareDecoder::Initialize(ID3D11Device* d3dDevice, std::string& outError) {
    HRESULT hr = S_OK;
    if (!mfStarted_) {
        hr = MFStartup(MF_VERSION, MFSTARTUP_LITE);
        if (FAILED(hr)) {
            outError = FormatMfHr("MFStartup failed", hr);
            return false;
        }
        mfStarted_ = true;
    }

    hr = MFCreateDXGIDeviceManager(&resetToken_, dxgiManager_.ReleaseAndGetAddressOf());
    if (FAILED(hr)) {
        outError = FormatMfHr("MFCreateDXGIDeviceManager failed", hr);
        return false;
    }

    if (d3dDevice) {
        hr = dxgiManager_->ResetDevice(d3dDevice, resetToken_);
        if (FAILED(hr)) {
            outError = FormatMfHr("IMFDXGIDeviceManager::ResetDevice failed", hr);
            return false;
        }
    }
    return true;
}

DecoderProbeResult MfHardwareDecoder::ProbeHardwareDecoders() const {
    DecoderProbeResult res;
    res.dxgiManagerReady = (dxgiManager_ != nullptr);
    const UINT32 syncFlags = MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER;
    const UINT32 hwFlags = MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER;

    res.h264Decoders = EnumMftNames(MFVideoFormat_H264, syncFlags);
    if (res.h264Decoders.empty()) {
        res.h264Decoders = EnumMftNames(MFVideoFormat_H264, hwFlags);
    }
    res.h264HwAvailable = !res.h264Decoders.empty();

    res.hevcDecoders = EnumMftNames(MFVideoFormat_HEVC, syncFlags);
    if (res.hevcDecoders.empty()) {
        res.hevcDecoders = EnumMftNames(MFVideoFormat_HEVC, hwFlags);
    }
    res.hevcHwAvailable = !res.hevcDecoders.empty();

    return res;
}

bool MfHardwareDecoder::NegotiateNv12Output() {
    if (!decoder_) return false;
    for (DWORD idx = 0; ; ++idx) {
        Microsoft::WRL::ComPtr<IMFMediaType> outMt;
        HRESULT hr = decoder_->GetOutputAvailableType(0, idx, outMt.GetAddressOf());
        if (hr == MF_E_NO_MORE_TYPES || FAILED(hr)) break;
        GUID outSub = GUID_NULL;
        if (SUCCEEDED(outMt->GetGUID(MF_MT_SUBTYPE, &outSub)) && outSub == MFVideoFormat_NV12) {
            if (SUCCEEDED(decoder_->SetOutputType(0, outMt.Get(), 0))) {
                UINT32 w = 0, h = 0;
                if (SUCCEEDED(MFGetAttributeSize(outMt.Get(), MF_MT_FRAME_SIZE, &w, &h)) && w > 0 && h > 0) {
                    outWidth_ = w;
                    outHeight_ = h;
                }
                UINT32 stride = 0;
                if (SUCCEEDED(outMt->GetUINT32(MF_MT_DEFAULT_STRIDE, &stride)) && stride > 0) {
                    outStride_ = stride;
                } else {
                    outStride_ = outWidth_;
                }
                MFT_OUTPUT_STREAM_INFO info = {};
                if (SUCCEEDED(decoder_->GetOutputStreamInfo(0, &info))) {
                    mftProvidesSamples_ = ((info.dwFlags & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0);
                    minOutputSampleSize_ = info.cbSize;
                }
                return true;
            }
        }
    }
    return false;
}

bool MfHardwareDecoder::OpenCodec(VideoCodec codec, uint32_t width, uint32_t height, std::string& outError) {
    if (!mfStarted_ || !dxgiManager_) {
        outError = "Media Foundation decoder not initialized";
        return false;
    }

    CloseCodec();
    outWidth_ = width;
    outHeight_ = height;
    outStride_ = width;

    const GUID subtype = (codec == VideoCodec::HEVC) ? MFVideoFormat_HEVC : MFVideoFormat_H264;
    MFT_REGISTER_TYPE_INFO inType = { MFMediaType_Video, subtype };

    // Prefer synchronous DXVA/D3D11VA MFT (Microsoft H264/HEVC Video Decoder MFT works synchronously with D3D11VA across AMD/NV/Intel)
    IMFActivate** activates = nullptr;
    UINT32 count = 0;
    HRESULT hr = MFTEnumEx(
        MFT_CATEGORY_VIDEO_DECODER,
        MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER,
        &inType,
        nullptr,
        &activates,
        &count);

    if (FAILED(hr) || count == 0) {
        if (activates) CoTaskMemFree(activates);
        activates = nullptr;
        count = 0;
        hr = MFTEnumEx(
            MFT_CATEGORY_VIDEO_DECODER,
            MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_ASYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER,
            &inType,
            nullptr,
            &activates,
            &count);
    }

    if (FAILED(hr) || count == 0 || !activates) {
        outError = (codec == VideoCodec::HEVC)
            ? "No Media Foundation HEVC decoder MFT found"
            : "No Media Foundation H.264 decoder MFT found";
        return false;
    }

    bool activated = false;
    for (UINT32 i = 0; i < count; ++i) {
        if (!activated) {
            Microsoft::WRL::ComPtr<IMFTransform> candidate;
            if (SUCCEEDED(activates[i]->ActivateObject(IID_PPV_ARGS(candidate.GetAddressOf())))) {
                Microsoft::WRL::ComPtr<IMFAttributes> mftAttrs;
                if (SUCCEEDED(candidate->GetAttributes(mftAttrs.GetAddressOf())) && mftAttrs) {
                    UINT32 isAsync = 0;
                    if (SUCCEEDED(mftAttrs->GetUINT32(MF_TRANSFORM_ASYNC, &isAsync)) && isAsync != 0) {
                        mftAttrs->SetUINT32(MF_TRANSFORM_ASYNC_UNLOCK, TRUE);
                    }
                    mftAttrs->SetUINT32(CODECAPI_AVLowLatencyMode, TRUE);
                    mftAttrs->SetUINT32(MF_LOW_LATENCY, TRUE);
                }

                decoder_ = candidate;
                wchar_t* friendly = nullptr;
                UINT32 cch = 0;
                if (SUCCEEDED(activates[i]->GetAllocatedString(MFT_FRIENDLY_NAME_Attribute, &friendly, &cch)) && friendly) {
                    activeDecoderName_ = WideToUtf8Mf(friendly);
                    CoTaskMemFree(friendly);
                } else {
                    activeDecoderName_ = "MF Video Decoder MFT";
                }
                activated = true;
            }
        }
        activates[i]->Release();
    }
    CoTaskMemFree(activates);

    if (!activated || !decoder_) {
        outError = "Failed to activate video decoder MFT";
        return false;
    }

    // Bind D3D11VA DXGI Device Manager (ignore failure on pure software fallback)
    decoder_->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, reinterpret_cast<ULONG_PTR>(dxgiManager_.Get()));

    Microsoft::WRL::ComPtr<IMFMediaType> inputMt;
    hr = MFCreateMediaType(inputMt.GetAddressOf());
    if (FAILED(hr)) { outError = FormatMfHr("MFCreateMediaType failed", hr); return false; }
    inputMt->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    inputMt->SetGUID(MF_MT_SUBTYPE, subtype);
    MFSetAttributeSize(inputMt.Get(), MF_MT_FRAME_SIZE, width, height);
    inputMt->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);

    hr = decoder_->SetInputType(0, inputMt.Get(), 0);
    if (FAILED(hr)) { outError = FormatMfHr("SetInputType failed", hr); return false; }

    if (!NegotiateNv12Output()) {
        outError = "Failed to negotiate NV12 output on MFT decoder";
        return false;
    }

    decoder_->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
    decoder_->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
    return true;
}

bool MfHardwareDecoder::SubmitAccessUnit(const uint8_t* data, size_t size, int64_t pts100ns, std::string& outError) {
    if (!decoder_ || !data || size == 0) {
        outError = "Invalid decoder input";
        return false;
    }
    Microsoft::WRL::ComPtr<IMFMediaBuffer> buf;
    HRESULT hr = MFCreateMemoryBuffer(static_cast<DWORD>(size), buf.GetAddressOf());
    if (FAILED(hr)) { outError = FormatMfHr("MFCreateMemoryBuffer failed", hr); return false; }

    BYTE* dst = nullptr;
    hr = buf->Lock(&dst, nullptr, nullptr);
    if (FAILED(hr)) { outError = FormatMfHr("Buffer Lock failed", hr); return false; }
    std::memcpy(dst, data, size);
    buf->Unlock();
    buf->SetCurrentLength(static_cast<DWORD>(size));

    Microsoft::WRL::ComPtr<IMFSample> sample;
    hr = MFCreateSample(sample.GetAddressOf());
    if (FAILED(hr)) { outError = FormatMfHr("MFCreateSample failed", hr); return false; }
    sample->AddBuffer(buf.Get());
    sample->SetSampleTime(pts100ns);

    hr = decoder_->ProcessInput(0, sample.Get(), 0);
    if (FAILED(hr)) {
        outError = FormatMfHr("ProcessInput failed", hr);
        return false;
    }
    return true;
}

bool MfHardwareDecoder::PullDecodedFrame(DecodedFrame& outFrame, bool& outNeedMoreInput) {
    outFrame = DecodedFrame{};
    outNeedMoreInput = false;
    if (!decoder_) return false;

    MFT_OUTPUT_DATA_BUFFER outBuf = {};
    Microsoft::WRL::ComPtr<IMFSample> callerSample;
    if (!mftProvidesSamples_) {
        DWORD allocSize = minOutputSampleSize_;
        if (allocSize == 0) {
            allocSize = outStride_ * outHeight_ * 3 / 2;
        }
        if (SUCCEEDED(MFCreateSample(callerSample.GetAddressOf()))) {
            Microsoft::WRL::ComPtr<IMFMediaBuffer> memBuf;
            if (SUCCEEDED(MFCreateMemoryBuffer(allocSize, memBuf.GetAddressOf()))) {
                callerSample->AddBuffer(memBuf.Get());
                outBuf.pSample = callerSample.Get();
            }
        }
    }

    DWORD status = 0;
    HRESULT hr = decoder_->ProcessOutput(0, 1, &outBuf, &status);
    if (outBuf.pEvents) {
        outBuf.pEvents->Release();
        outBuf.pEvents = nullptr;
    }

    if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) {
        outNeedMoreInput = true;
        return false;
    }
    if (hr == MF_E_TRANSFORM_STREAM_CHANGE) {
        if (mftProvidesSamples_ && outBuf.pSample) outBuf.pSample->Release();
        NegotiateNv12Output();
        return false;
    }
    if (FAILED(hr) || !outBuf.pSample) {
        if (mftProvidesSamples_ && outBuf.pSample) outBuf.pSample->Release();
        return false;
    }

    Microsoft::WRL::ComPtr<IMFSample> sample;
    if (mftProvidesSamples_) {
        sample.Attach(outBuf.pSample);
    } else {
        sample = callerSample;
    }

    Microsoft::WRL::ComPtr<IMFMediaBuffer> mediaBuf;
    if (FAILED(sample->GetBufferByIndex(0, mediaBuf.GetAddressOf())) || !mediaBuf) {
        return false;
    }

    Microsoft::WRL::ComPtr<IMFDXGIBuffer> dxgiBuf;
    if (SUCCEEDED(mediaBuf.As(&dxgiBuf)) && dxgiBuf) {
        UINT sub = 0;
        dxgiBuf->GetSubresourceIndex(&sub);
        if (SUCCEEDED(dxgiBuf->GetResource(IID_PPV_ARGS(outFrame.gpuTexture.GetAddressOf()))) && outFrame.gpuTexture) {
            outFrame.isGpuTexture = true;
            outFrame.subresourceIndex = sub;
            outFrame.width = outWidth_;
            outFrame.height = outHeight_;
            outFrame.stride = outStride_;
            return true;
        }
    }

    // Fallback: system-memory NV12 buffer
    BYTE* raw = nullptr;
    DWORD curLen = 0;
    if (SUCCEEDED(mediaBuf->Lock(&raw, nullptr, &curLen)) && raw && curLen > 0) {
        outFrame.isGpuTexture = false;
        outFrame.width = outWidth_;
        outFrame.height = outHeight_;
        outFrame.stride = outStride_ >= outWidth_ ? outStride_ : outWidth_;
        outFrame.cpuNv12.assign(raw, raw + curLen);
        mediaBuf->Unlock();
        return true;
    }

    return false;
}

bool MfHardwareDecoder::PullDecodedTexture(Microsoft::WRL::ComPtr<ID3D11Texture2D>& outTexture, uint32_t& outSubresource) {
    outTexture.Reset();
    outSubresource = 0;
    DecodedFrame frame;
    bool needMore = false;
    if (PullDecodedFrame(frame, needMore) && frame.isGpuTexture && frame.gpuTexture) {
        outTexture = frame.gpuTexture;
        outSubresource = frame.subresourceIndex;
        return true;
    }
    return false;
}

} // namespace bobo
