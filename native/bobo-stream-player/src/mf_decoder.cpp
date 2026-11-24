#include "mf_decoder.h"

#include <codecapi.h>
#include <mferror.h>
#include <cstdio>

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
    decoder_.Reset();
    dxgiManager_.Reset();
    if (mfStarted_) {
        MFShutdown();
        mfStarted_ = false;
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
    const UINT32 hwFlags = MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER;
    const UINT32 anyFlags = MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_ASYNCMFT | MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER;

    res.h264Decoders = EnumMftNames(MFVideoFormat_H264, hwFlags);
    res.h264HwAvailable = !res.h264Decoders.empty();
    if (!res.h264HwAvailable) {
        // Check Microsoft inbox DXVA-capable H.264 MFT (sync/async MFT that accelerates via D3D11VA internally)
        res.h264Decoders = EnumMftNames(MFVideoFormat_H264, anyFlags);
        res.h264HwAvailable = !res.h264Decoders.empty();
    }

    res.hevcDecoders = EnumMftNames(MFVideoFormat_HEVC, hwFlags);
    res.hevcHwAvailable = !res.hevcDecoders.empty();
    if (!res.hevcHwAvailable) {
        res.hevcDecoders = EnumMftNames(MFVideoFormat_HEVC, anyFlags);
        res.hevcHwAvailable = !res.hevcDecoders.empty();
    }

    return res;
}

bool MfHardwareDecoder::OpenCodec(VideoCodec codec, uint32_t width, uint32_t height, std::string& outError) {
    if (!mfStarted_ || !dxgiManager_) {
        outError = "Media Foundation decoder not initialized";
        return false;
    }

    const GUID subtype = (codec == VideoCodec::HEVC) ? MFVideoFormat_HEVC : MFVideoFormat_H264;
    MFT_REGISTER_TYPE_INFO inType = { MFMediaType_Video, subtype };
    IMFActivate** activates = nullptr;
    UINT32 count = 0;

    HRESULT hr = MFTEnumEx(
        MFT_CATEGORY_VIDEO_DECODER,
        MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_ASYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER,
        &inType,
        nullptr,
        &activates,
        &count);
    if (FAILED(hr) || count == 0 || !activates) {
        outError = (codec == VideoCodec::HEVC) ? "No HEVC MFT video decoder installed" : "No H.264 MFT video decoder installed";
        return false;
    }

    hr = activates[0]->ActivateObject(IID_PPV_ARGS(decoder_.ReleaseAndGetAddressOf()));
    wchar_t* friendly = nullptr;
    UINT32 cch = 0;
    if (SUCCEEDED(activates[0]->GetAllocatedString(MFT_FRIENDLY_NAME_Attribute, &friendly, &cch)) && friendly) {
        activeDecoderName_ = WideToUtf8Mf(friendly);
        CoTaskMemFree(friendly);
    } else {
        activeDecoderName_ = (codec == VideoCodec::HEVC) ? "HEVC D3D11VA MFT" : "H.264 D3D11VA MFT";
    }
    for (UINT32 i = 0; i < count; ++i) activates[i]->Release();
    CoTaskMemFree(activates);

    if (FAILED(hr) || !decoder_) {
        outError = FormatMfHr("ActivateObject failed", hr);
        return false;
    }

    // Unlock async MFT if required and enable low-latency + D3D11VA
    Microsoft::WRL::ComPtr<IMFAttributes> attrs;
    if (SUCCEEDED(decoder_->GetAttributes(attrs.GetAddressOf())) && attrs) {
        UINT32 isAsync = 0;
        if (SUCCEEDED(attrs->GetUINT32(MF_TRANSFORM_ASYNC, &isAsync)) && isAsync) {
            attrs->SetUINT32(MF_TRANSFORM_ASYNC_UNLOCK, TRUE);
        }
        attrs->SetUINT32(CODECAPI_AVLowLatencyMode, TRUE);
        attrs->SetUINT32(MF_LOW_LATENCY, TRUE);
    }

    decoder_->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, reinterpret_cast<ULONG_PTR>(dxgiManager_.Get()));

    Microsoft::WRL::ComPtr<IMFMediaType> inputMt;
    hr = MFCreateMediaType(inputMt.GetAddressOf());
    if (FAILED(hr)) { outError = FormatMfHr("MFCreateMediaType input failed", hr); return false; }
    inputMt->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    inputMt->SetGUID(MF_MT_SUBTYPE, subtype);
    MFSetAttributeSize(inputMt.Get(), MF_MT_FRAME_SIZE, width, height);
    hr = decoder_->SetInputType(0, inputMt.Get(), 0);
    if (FAILED(hr)) { outError = FormatMfHr("SetInputType failed", hr); return false; }

    // Select NV12 output format for zero-copy D3D11VideoProcessor blt
    bool outputSet = false;
    for (DWORD idx = 0; ; ++idx) {
        Microsoft::WRL::ComPtr<IMFMediaType> outMt;
        hr = decoder_->GetOutputAvailableType(0, idx, outMt.GetAddressOf());
        if (hr == MF_E_NO_MORE_TYPES || FAILED(hr)) break;
        GUID outSub = GUID_NULL;
        if (SUCCEEDED(outMt->GetGUID(MF_MT_SUBTYPE, &outSub)) && outSub == MFVideoFormat_NV12) {
            if (SUCCEEDED(decoder_->SetOutputType(0, outMt.Get(), 0))) {
                outputSet = true;
                break;
            }
        }
    }
    if (!outputSet) {
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
    memcpy(dst, data, size);
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

bool MfHardwareDecoder::PullDecodedTexture(Microsoft::WRL::ComPtr<ID3D11Texture2D>& outTexture, uint32_t& outSubresource) {
    outTexture.Reset();
    outSubresource = 0;
    if (!decoder_) return false;

    MFT_OUTPUT_DATA_BUFFER outBuf = {};
    DWORD status = 0;
    HRESULT hr = decoder_->ProcessOutput(0, 1, &outBuf, &status);
    if (hr == MF_E_TRANSFORM_STREAM_CHANGE) {
        if (outBuf.pSample) outBuf.pSample->Release();
        if (outBuf.pEvents) outBuf.pEvents->Release();
        for (DWORD idx = 0; ; ++idx) {
            Microsoft::WRL::ComPtr<IMFMediaType> outMt;
            if (FAILED(decoder_->GetOutputAvailableType(0, idx, outMt.GetAddressOf()))) break;
            GUID outSub = GUID_NULL;
            if (SUCCEEDED(outMt->GetGUID(MF_MT_SUBTYPE, &outSub)) && outSub == MFVideoFormat_NV12) {
                decoder_->SetOutputType(0, outMt.Get(), 0);
                break;
            }
        }
        return false;
    }
    if (FAILED(hr) || !outBuf.pSample) {
        if (outBuf.pSample) outBuf.pSample->Release();
        if (outBuf.pEvents) outBuf.pEvents->Release();
        return false;
    }

    Microsoft::WRL::ComPtr<IMFSample> sample;
    sample.Attach(outBuf.pSample);
    if (outBuf.pEvents) outBuf.pEvents->Release();

    Microsoft::WRL::ComPtr<IMFMediaBuffer> mediaBuf;
    if (FAILED(sample->GetBufferByIndex(0, mediaBuf.GetAddressOf()))) return false;

    Microsoft::WRL::ComPtr<IMFDXGIBuffer> dxgiBuf;
    if (SUCCEEDED(mediaBuf.As(&dxgiBuf)) && dxgiBuf) {
        UINT sub = 0;
        dxgiBuf->GetSubresourceIndex(&sub);
        if (SUCCEEDED(dxgiBuf->GetResource(IID_PPV_ARGS(outTexture.GetAddressOf())))) {
            outSubresource = sub;
            return true;
        }
    }
    return false;
}

} // namespace bobo
