#include "mf_audio_aac.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mferror.h>
#include <wrl/client.h>

#include <algorithm>
#include <cstring>

namespace bobo {

namespace {

IMFTransform* AsMft(void* p) {
    return reinterpret_cast<IMFTransform*>(p);
}

void BuildAacUserDataAdts(int sampleRate, int channels, uint8_t outBuf[14]) {
    std::memset(outBuf, 0, 14);
    // HEAACWAVEINFO (12 bytes): wPayloadType = 1 (ADTS), wAudioProfileLevelIndication = 0x29 (AAC-LC L2)
    outBuf[0] = 1;
    outBuf[1] = 0;
    outBuf[2] = 0x29;
    outBuf[3] = 0;

    // 2-byte AudioSpecificConfig: audioObjectType = 2 (AAC-LC) -> top 5 bits = 00010
    uint8_t freqIdx = 3; // 48000 Hz
    if (sampleRate == 44100) freqIdx = 4;
    else if (sampleRate == 32000) freqIdx = 5;
    else if (sampleRate == 24000) freqIdx = 6;
    else if (sampleRate == 22050) freqIdx = 7;
    else if (sampleRate == 16000) freqIdx = 8;

    uint8_t chCfg = static_cast<uint8_t>(channels == 1 ? 1 : 2);
    uint16_t asc = static_cast<uint16_t>((2u << 11) | (static_cast<uint16_t>(freqIdx & 0x0Fu) << 7) | (static_cast<uint16_t>(chCfg & 0x0Fu) << 3));
    outBuf[12] = static_cast<uint8_t>((asc >> 8) & 0xFFu);
    outBuf[13] = static_cast<uint8_t>(asc & 0xFFu);
}

HRESULT CreateFirstMft(
    const GUID& category,
    const GUID* inMajor,
    const GUID* inSub,
    const GUID* outMajor,
    const GUID* outSub,
    IMFTransform** ppMft) {
    if (!ppMft) return E_POINTER;
    *ppMft = nullptr;

    MFT_REGISTER_TYPE_INFO inInfo = {};
    MFT_REGISTER_TYPE_INFO outInfo = {};
    MFT_REGISTER_TYPE_INFO* pIn = nullptr;
    MFT_REGISTER_TYPE_INFO* pOut = nullptr;
    if (inMajor && inSub) {
        inInfo.guidMajorType = *inMajor;
        inInfo.guidSubtype = *inSub;
        pIn = &inInfo;
    }
    if (outMajor && outSub) {
        outInfo.guidMajorType = *outMajor;
        outInfo.guidSubtype = *outSub;
        pOut = &outInfo;
    }

    IMFActivate** activates = nullptr;
    UINT32 count = 0;
    HRESULT hr = MFTEnumEx(
        category,
        MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_LOCALMFT | MFT_ENUM_FLAG_SORTANDFILTER,
        pIn,
        pOut,
        &activates,
        &count);
    if (FAILED(hr) || !activates || count == 0) {
        if (activates) CoTaskMemFree(activates);
        return FAILED(hr) ? hr : MF_E_TOPO_CODEC_NOT_FOUND;
    }

    HRESULT resHr = MF_E_TOPO_CODEC_NOT_FOUND;
    for (UINT32 i = 0; i < count; ++i) {
        if (activates[i] && !*ppMft) {
            HRESULT actHr = activates[i]->ActivateObject(__uuidof(IMFTransform), reinterpret_cast<void**>(ppMft));
            if (SUCCEEDED(actHr) && *ppMft) {
                resHr = S_OK;
            } else {
                *ppMft = nullptr;
            }
        }
        if (activates[i]) activates[i]->Release();
    }
    CoTaskMemFree(activates);
    return resHr;
}

} // namespace

// ==================== MfAacEncoder ====================

MfAacEncoder::~MfAacEncoder() {
    Close();
}

bool MfAacEncoder::Open(int sampleRate, int channels, int bitrateBps) {
    Close();
    if (sampleRate != 44100 && sampleRate != 48000) return false;
    if (channels != 1 && channels != 2) return false;

    HRESULT hrMf = MFStartup(MF_VERSION, MFSTARTUP_LITE);
    if (FAILED(hrMf)) return false;
    mfStarted_ = true;

    IMFTransform* rawMft = nullptr;
    HRESULT hr = CreateFirstMft(
        MFT_CATEGORY_AUDIO_ENCODER,
        &MFMediaType_Audio, &MFAudioFormat_PCM,
        &MFMediaType_Audio, &MFAudioFormat_AAC,
        &rawMft);
    if (FAILED(hr) || !rawMft) {
        Close();
        return false;
    }
    mftRaw_ = rawMft;

    // Normalize bitrate to a Windows MF AAC MFT supported byte rate (12000=96kbps, 16000=128kbps, 20000=160kbps, 24000=192kbps)
    uint32_t targetBytesPerSec = 12000u;
    if (bitrateBps >= 192000) targetBytesPerSec = 24000u;
    else if (bitrateBps >= 160000) targetBytesPerSec = 20000u;
    else if (bitrateBps >= 128000) targetBytesPerSec = 16000u;

    Microsoft::WRL::ComPtr<IMFMediaType> outMt;
    hr = MFCreateMediaType(outMt.GetAddressOf());
    if (SUCCEEDED(hr)) {
        outMt->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
        outMt->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC);
        outMt->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
        outMt->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, static_cast<UINT32>(sampleRate));
        outMt->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, static_cast<UINT32>(channels));
        outMt->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, targetBytesPerSec);
        outMt->SetUINT32(MF_MT_AAC_PAYLOAD_TYPE, 1); // ADTS framing per frame
        outMt->SetUINT32(MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x29);
        hr = rawMft->SetOutputType(0, outMt.Get(), 0);
    }
    if (FAILED(hr)) {
        Close();
        return false;
    }

    Microsoft::WRL::ComPtr<IMFMediaType> inMt;
    hr = MFCreateMediaType(inMt.GetAddressOf());
    if (SUCCEEDED(hr)) {
        UINT32 blockAlign = static_cast<UINT32>(channels * 2);
        inMt->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
        inMt->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM);
        inMt->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
        inMt->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, static_cast<UINT32>(sampleRate));
        inMt->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, static_cast<UINT32>(channels));
        inMt->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, blockAlign);
        inMt->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, static_cast<UINT32>(sampleRate) * blockAlign);
        hr = rawMft->SetInputType(0, inMt.Get(), 0);
    }
    if (FAILED(hr)) {
        Close();
        return false;
    }

    MFT_OUTPUT_STREAM_INFO osi = {};
    if (SUCCEEDED(rawMft->GetOutputStreamInfo(0, &osi))) {
        outStreamMinBytes_ = (osi.cbSize > 1024u) ? osi.cbSize : 8192u;
        providerAllocatesOut_ = ((osi.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0) ||
                                ((osi.dwFlags & MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES) != 0);
    }

    hr = rawMft->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
    if (SUCCEEDED(hr)) {
        hr = rawMft->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
    }
    if (FAILED(hr)) {
        Close();
        return false;
    }

    sampleRate_ = sampleRate;
    channels_ = channels;
    nextPtsHns_ = 0;
    pcmAccum_.clear();
    open_ = true;
    return true;
}

bool MfAacEncoder::PushPcm(const int16_t* stereoInterleaved, int frameCount, std::vector<std::vector<uint8_t>>& outAdtsFrames) {
    outAdtsFrames.clear();
    if (!open_ || !mftRaw_ || !stereoInterleaved || frameCount <= 0) return false;

    const size_t sampleCount = static_cast<size_t>(frameCount) * static_cast<size_t>(channels_);
    pcmAccum_.insert(pcmAccum_.end(), stereoInterleaved, stereoInterleaved + sampleCount);

    constexpr int kAacFrameSamples = 1024;
    const size_t chunkSamples = static_cast<size_t>(kAacFrameSamples) * static_cast<size_t>(channels_);
    const DWORD chunkBytes = static_cast<DWORD>(chunkSamples * sizeof(int16_t));
    const int64_t frameDurHns = (10000000LL * kAacFrameSamples) / sampleRate_;
    auto* mft = AsMft(mftRaw_);

    while (pcmAccum_.size() >= chunkSamples) {
        Microsoft::WRL::ComPtr<IMFMediaBuffer> buf;
        Microsoft::WRL::ComPtr<IMFSample> samp;
        if (FAILED(MFCreateMemoryBuffer(chunkBytes, buf.GetAddressOf())) ||
            FAILED(MFCreateSample(samp.GetAddressOf()))) {
            return false;
        }
        BYTE* dst = nullptr;
        if (FAILED(buf->Lock(&dst, nullptr, nullptr)) || !dst) return false;
        std::memcpy(dst, pcmAccum_.data(), chunkBytes);
        buf->Unlock();
        buf->SetCurrentLength(chunkBytes);
        samp->AddBuffer(buf.Get());
        samp->SetSampleTime(nextPtsHns_);
        samp->SetSampleDuration(frameDurHns);
        nextPtsHns_ += frameDurHns;

        pcmAccum_.erase(pcmAccum_.begin(), pcmAccum_.begin() + static_cast<ptrdiff_t>(chunkSamples));

        HRESULT hrIn = mft->ProcessInput(0, samp.Get(), 0);
        if (hrIn == MF_E_NOTACCEPTING) {
            if (!PullOutputs(outAdtsFrames)) return false;
            hrIn = mft->ProcessInput(0, samp.Get(), 0);
        }
        if (FAILED(hrIn)) return false;
        if (!PullOutputs(outAdtsFrames)) return false;
    }
    return true;
}

bool MfAacEncoder::Drain(std::vector<std::vector<uint8_t>>& outAdtsFrames) {
    outAdtsFrames.clear();
    if (!open_ || !mftRaw_) return false;
    auto* mft = AsMft(mftRaw_);
    mft->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0);
    return PullOutputs(outAdtsFrames);
}

bool MfAacEncoder::PullOutputs(std::vector<std::vector<uint8_t>>& outAdtsFrames) {
    auto* mft = AsMft(mftRaw_);
    for (;;) {
        MFT_OUTPUT_DATA_BUFFER odb = {};
        Microsoft::WRL::ComPtr<IMFSample> outSample;
        Microsoft::WRL::ComPtr<IMFMediaBuffer> outBuf;
        if (!providerAllocatesOut_) {
            if (FAILED(MFCreateSample(outSample.GetAddressOf())) ||
                FAILED(MFCreateMemoryBuffer(outStreamMinBytes_, outBuf.GetAddressOf()))) {
                return false;
            }
            outSample->AddBuffer(outBuf.Get());
            odb.pSample = outSample.Get();
        }
        DWORD status = 0;
        HRESULT hrOut = mft->ProcessOutput(0, 1, &odb, &status);
        if (odb.pEvents) odb.pEvents->Release();
        if (hrOut == MF_E_TRANSFORM_NEED_MORE_INPUT) {
            if (providerAllocatesOut_ && odb.pSample) odb.pSample->Release();
            return true;
        }
        if (FAILED(hrOut)) {
            if (providerAllocatesOut_ && odb.pSample) odb.pSample->Release();
            return false;
        }

        IMFSample* resSample = odb.pSample;
        if (resSample) {
            Microsoft::WRL::ComPtr<IMFMediaBuffer> contig;
            if (SUCCEEDED(resSample->ConvertToContiguousBuffer(contig.GetAddressOf()))) {
                BYTE* ptr = nullptr;
                DWORD curLen = 0;
                if (SUCCEEDED(contig->Lock(&ptr, nullptr, &curLen)) && ptr && curLen > 0) {
                    outAdtsFrames.emplace_back(ptr, ptr + curLen);
                }
                if (ptr) contig->Unlock();
            }
            if (providerAllocatesOut_) resSample->Release();
        }
    }
}

void MfAacEncoder::Close() {
    open_ = false;
    pcmAccum_.clear();
    if (mftRaw_) {
        auto* mft = AsMft(mftRaw_);
        mft->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
        mft->Release();
        mftRaw_ = nullptr;
    }
    if (mfStarted_) {
        MFShutdown();
        mfStarted_ = false;
    }
}

// ==================== MfAacDecoder ====================

MfAacDecoder::~MfAacDecoder() {
    Close();
}

bool MfAacDecoder::Open(int sampleRate, int channels) {
    Close();
    if (sampleRate != 44100 && sampleRate != 48000) return false;
    if (channels != 1 && channels != 2) return false;

    HRESULT hrMf = MFStartup(MF_VERSION, MFSTARTUP_LITE);
    if (FAILED(hrMf)) return false;
    mfStarted_ = true;

    IMFTransform* rawMft = nullptr;
    HRESULT hr = CreateFirstMft(
        MFT_CATEGORY_AUDIO_DECODER,
        &MFMediaType_Audio, &MFAudioFormat_AAC,
        nullptr, nullptr,
        &rawMft);
    if (FAILED(hr) || !rawMft) {
        Close();
        return false;
    }
    mftRaw_ = rawMft;

    uint8_t userData[14];
    BuildAacUserDataAdts(sampleRate, channels, userData);

    Microsoft::WRL::ComPtr<IMFMediaType> inMt;
    hr = MFCreateMediaType(inMt.GetAddressOf());
    if (SUCCEEDED(hr)) {
        inMt->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
        inMt->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC);
        inMt->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, static_cast<UINT32>(sampleRate));
        inMt->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, static_cast<UINT32>(channels));
        inMt->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
        inMt->SetUINT32(MF_MT_AAC_PAYLOAD_TYPE, 1); // ADTS input
        inMt->SetUINT32(MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x29);
        inMt->SetBlob(MF_MT_USER_DATA, userData, sizeof(userData));
        hr = rawMft->SetInputType(0, inMt.Get(), 0);
    }
    if (FAILED(hr)) {
        Close();
        return false;
    }

    sampleRate_ = sampleRate;
    channels_ = channels;
    outChannels_ = channels;

    if (!ConfigureOutputType()) {
        Close();
        return false;
    }

    hr = rawMft->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
    if (SUCCEEDED(hr)) {
        hr = rawMft->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
    }
    if (FAILED(hr)) {
        Close();
        return false;
    }

    nextPtsHns_ = 0;
    open_ = true;
    return true;
}

bool MfAacDecoder::ConfigureOutputType() {
    auto* mft = AsMft(mftRaw_);
    if (!mft) return false;

    Microsoft::WRL::ComPtr<IMFMediaType> bestFloat;
    for (DWORD idx = 0; ; ++idx) {
        Microsoft::WRL::ComPtr<IMFMediaType> avail;
        HRESULT hr = mft->GetOutputAvailableType(0, idx, avail.GetAddressOf());
        if (FAILED(hr)) break;
        GUID sub = {};
        UINT32 ch = 0;
        UINT32 bits = 0;
        avail->GetGUID(MF_MT_SUBTYPE, &sub);
        avail->GetUINT32(MF_MT_AUDIO_NUM_CHANNELS, &ch);
        avail->GetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, &bits);
        if (IsEqualGUID(sub, MFAudioFormat_PCM) && bits == 16 && static_cast<int>(ch) == channels_) {
            if (SUCCEEDED(mft->SetOutputType(0, avail.Get(), 0))) {
                outIsFloat_ = false;
                outChannels_ = static_cast<int>(ch);
                MFT_OUTPUT_STREAM_INFO osi = {};
                if (SUCCEEDED(mft->GetOutputStreamInfo(0, &osi))) {
                    outStreamMinBytes_ = (osi.cbSize > 1024u) ? osi.cbSize : 16384u;
                    providerAllocatesOut_ = ((osi.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0);
                }
                return true;
            }
        } else if (IsEqualGUID(sub, MFAudioFormat_Float) && bits == 32 && static_cast<int>(ch) == channels_ && !bestFloat) {
            bestFloat = avail;
        }
    }

    if (bestFloat && SUCCEEDED(mft->SetOutputType(0, bestFloat.Get(), 0))) {
        outIsFloat_ = true;
        outChannels_ = channels_;
        MFT_OUTPUT_STREAM_INFO osi = {};
        if (SUCCEEDED(mft->GetOutputStreamInfo(0, &osi))) {
            outStreamMinBytes_ = (osi.cbSize > 1024u) ? osi.cbSize : 16384u;
            providerAllocatesOut_ = ((osi.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0);
        }
        return true;
    }
    return false;
}

bool MfAacDecoder::DecodeAdts(const uint8_t* adtsData, int len, std::vector<int16_t>& outPcm) {
    outPcm.clear();
    if (!open_ || !mftRaw_ || !adtsData || len < 7) return false;

    Microsoft::WRL::ComPtr<IMFMediaBuffer> buf;
    Microsoft::WRL::ComPtr<IMFSample> samp;
    if (FAILED(MFCreateMemoryBuffer(static_cast<DWORD>(len), buf.GetAddressOf())) ||
        FAILED(MFCreateSample(samp.GetAddressOf()))) {
        return false;
    }
    BYTE* dst = nullptr;
    if (FAILED(buf->Lock(&dst, nullptr, nullptr)) || !dst) return false;
    std::memcpy(dst, adtsData, static_cast<size_t>(len));
    buf->Unlock();
    buf->SetCurrentLength(static_cast<DWORD>(len));
    samp->AddBuffer(buf.Get());
    const int64_t frameDurHns = (10000000LL * 1024LL) / sampleRate_;
    samp->SetSampleTime(nextPtsHns_);
    samp->SetSampleDuration(frameDurHns);
    nextPtsHns_ += frameDurHns;

    auto* mft = AsMft(mftRaw_);
    HRESULT hrIn = mft->ProcessInput(0, samp.Get(), 0);
    if (hrIn == MF_E_NOTACCEPTING) {
        if (!PullOutputs(outPcm)) return false;
        hrIn = mft->ProcessInput(0, samp.Get(), 0);
    }
    if (FAILED(hrIn)) return false;
    return PullOutputs(outPcm);
}

bool MfAacDecoder::Drain(std::vector<int16_t>& outPcm) {
    outPcm.clear();
    if (!open_ || !mftRaw_) return false;
    AsMft(mftRaw_)->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0);
    return PullOutputs(outPcm);
}

bool MfAacDecoder::PullOutputs(std::vector<int16_t>& outPcm) {
    auto* mft = AsMft(mftRaw_);
    for (;;) {
        MFT_OUTPUT_DATA_BUFFER odb = {};
        Microsoft::WRL::ComPtr<IMFSample> outSample;
        Microsoft::WRL::ComPtr<IMFMediaBuffer> outBuf;
        if (!providerAllocatesOut_) {
            if (FAILED(MFCreateSample(outSample.GetAddressOf())) ||
                FAILED(MFCreateMemoryBuffer(outStreamMinBytes_, outBuf.GetAddressOf()))) {
                return false;
            }
            outSample->AddBuffer(outBuf.Get());
            odb.pSample = outSample.Get();
        }
        DWORD status = 0;
        HRESULT hrOut = mft->ProcessOutput(0, 1, &odb, &status);
        if (odb.pEvents) odb.pEvents->Release();
        if (hrOut == MF_E_TRANSFORM_STREAM_CHANGE) {
            if (providerAllocatesOut_ && odb.pSample) odb.pSample->Release();
            if (!ConfigureOutputType()) return false;
            continue;
        }
        if (hrOut == MF_E_TRANSFORM_NEED_MORE_INPUT) {
            if (providerAllocatesOut_ && odb.pSample) odb.pSample->Release();
            return true;
        }
        if (FAILED(hrOut)) {
            if (providerAllocatesOut_ && odb.pSample) odb.pSample->Release();
            return false;
        }

        IMFSample* resSample = odb.pSample;
        if (resSample) {
            Microsoft::WRL::ComPtr<IMFMediaBuffer> contig;
            if (SUCCEEDED(resSample->ConvertToContiguousBuffer(contig.GetAddressOf()))) {
                BYTE* ptr = nullptr;
                DWORD curLen = 0;
                if (SUCCEEDED(contig->Lock(&ptr, nullptr, &curLen)) && ptr && curLen > 0) {
                    if (outIsFloat_) {
                        size_t nFloats = curLen / sizeof(float);
                        const float* fp = reinterpret_cast<const float*>(ptr);
                        size_t base = outPcm.size();
                        outPcm.resize(base + nFloats);
                        for (size_t i = 0; i < nFloats; ++i) {
                            float v = fp[i];
                            if (v > 1.0f) v = 1.0f; else if (v < -1.0f) v = -1.0f;
                            outPcm[base + i] = static_cast<int16_t>(v * 32767.0f);
                        }
                    } else {
                        size_t nShorts = curLen / sizeof(int16_t);
                        const int16_t* sp = reinterpret_cast<const int16_t*>(ptr);
                        outPcm.insert(outPcm.end(), sp, sp + nShorts);
                    }
                }
                if (ptr) contig->Unlock();
            }
            if (providerAllocatesOut_) resSample->Release();
        }
    }
}

void MfAacDecoder::Close() {
    open_ = false;
    if (mftRaw_) {
        auto* mft = AsMft(mftRaw_);
        mft->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
        mft->Release();
        mftRaw_ = nullptr;
    }
    if (mfStarted_) {
        MFShutdown();
        mfStarted_ = false;
    }
}

bool IsMfAacAudioSupported() {
    HRESULT hrCo = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    bool ok = false;
    {
        MfAacEncoder enc;
        MfAacDecoder dec;
        ok = enc.Open(48000, 2, 96000) && dec.Open(48000, 2);
    }
    if (SUCCEEDED(hrCo)) CoUninitialize();
    return ok;
}

} // namespace bobo
