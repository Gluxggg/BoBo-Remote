#include "wasapi_audio.h"

#include <algorithm>
#include <cstring>

namespace bobo {

static const CLSID kClsidMMDeviceEnumerator = __uuidof(MMDeviceEnumerator);
static const IID kIidIMMDeviceEnumerator = __uuidof(IMMDeviceEnumerator);
static const IID kIidIAudioClient = __uuidof(IAudioClient);
static const IID kIidIAudioRenderClient = __uuidof(IAudioRenderClient);

WasapiAudioPlayer::~WasapiAudioPlayer() {
    Shutdown();
}

bool WasapiAudioPlayer::LoadOpusLibrary() {
    if (opusModule_) return fnCreate_ && fnDecode_ && fnDestroy_;

    wchar_t exePath[MAX_PATH] = {};
    DWORD len = GetModuleFileNameW(nullptr, exePath, MAX_PATH);
    if (len > 0 && len < MAX_PATH) {
        std::wstring dir(exePath, len);
        size_t slash = dir.find_last_of(L"\\/");
        if (slash != std::wstring::npos) {
            std::wstring baseDir = dir.substr(0, slash + 1);
            std::wstring parentDir;
            size_t parentSlash = (slash > 0) ? dir.find_last_of(L"\\/", slash - 1) : std::wstring::npos;
            if (parentSlash != std::wstring::npos) {
                parentDir = dir.substr(0, parentSlash + 1);
            }
            const wchar_t* relCandidates[] = {
                L"opus.dll",
                L"engines\\MoonlightVPlus\\opus.dll",
                L"engines\\Moonlight\\opus.dll"
            };
            for (const wchar_t* rel : relCandidates) {
                std::wstring candidate = baseDir + rel;
                opusModule_ = LoadLibraryW(candidate.c_str());
                if (opusModule_) break;
            }
            if (!opusModule_ && !parentDir.empty()) {
                const wchar_t* parentCandidates[] = {
                    L"engines\\MoonlightVPlus\\opus.dll",
                    L"engines\\Moonlight\\opus.dll"
                };
                for (const wchar_t* rel : parentCandidates) {
                    std::wstring candidate = parentDir + rel;
                    opusModule_ = LoadLibraryW(candidate.c_str());
                    if (opusModule_) break;
                }
            }
        }
    }
    if (!opusModule_) {
        opusModule_ = LoadLibraryW(L"opus.dll");
    }
    if (!opusModule_) {
        return false;
    }

    fnCreate_ = reinterpret_cast<OpusMSCreateFn>(GetProcAddress(opusModule_, "opus_multistream_decoder_create"));
    fnDecode_ = reinterpret_cast<OpusMSDecodeFn>(GetProcAddress(opusModule_, "opus_multistream_decode"));
    fnDestroy_ = reinterpret_cast<OpusMSDestroyFn>(GetProcAddress(opusModule_, "opus_multistream_decoder_destroy"));
    return fnCreate_ && fnDecode_ && fnDestroy_;
}

bool WasapiAudioPlayer::Initialize(int sampleRate, int channels, int streams, int coupledStreams,
                                   const unsigned char* mapping, int samplesPerFrame, std::string& outError) {
    Shutdown();

    if (sampleRate <= 0 || channels <= 0 || channels > 8 || samplesPerFrame <= 0 || !mapping) {
        outError = "Invalid audio parameters";
        return false;
    }

    if (!LoadOpusLibrary()) {
        outError = "opus.dll not found alongside BoBoStreamPlayer.exe";
        return false;
    }

    int opusErr = 0;
    opusDecoder_ = fnCreate_(sampleRate, channels, streams, coupledStreams, mapping, &opusErr);
    if (!opusDecoder_ || opusErr != 0) {
        outError = "opus_multistream_decoder_create failed";
        Shutdown();
        return false;
    }

    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (SUCCEEDED(hr)) {
        comInitByUs_ = true;
    }

    hr = CoCreateInstance(kClsidMMDeviceEnumerator, nullptr, CLSCTX_ALL,
                          kIidIMMDeviceEnumerator, reinterpret_cast<void**>(deviceEnum_.ReleaseAndGetAddressOf()));
    if (FAILED(hr)) {
        outError = "IMMDeviceEnumerator creation failed";
        Shutdown();
        return false;
    }

    hr = deviceEnum_->GetDefaultAudioEndpoint(eRender, eConsole, device_.ReleaseAndGetAddressOf());
    if (FAILED(hr)) {
        outError = "No default audio render endpoint";
        Shutdown();
        return false;
    }

    hr = device_->Activate(kIidIAudioClient, CLSCTX_ALL, nullptr,
                           reinterpret_cast<void**>(audioClient_.ReleaseAndGetAddressOf()));
    if (FAILED(hr)) {
        outError = "IAudioClient activate failed";
        Shutdown();
        return false;
    }

    WAVEFORMATEX* mixFmt = nullptr;
    hr = audioClient_->GetMixFormat(&mixFmt);
    if (FAILED(hr) || !mixFmt) {
        outError = "GetMixFormat failed";
        Shutdown();
        return false;
    }

    // Request 48kHz stereo PCM/float matching Windows audio engine mix rate cleanly
    WAVEFORMATEXTENSIBLE desired = {};
    bool useMixFloat = (mixFmt->wFormatTag == WAVE_FORMAT_IEEE_FLOAT);
    if (mixFmt->wFormatTag == WAVE_FORMAT_EXTENSIBLE) {
        const WAVEFORMATEXTENSIBLE* ext = reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(mixFmt);
        useMixFloat = (ext->SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);
    }

    outputChannels_ = (mixFmt->nChannels >= 2) ? 2 : 1;
    outputFloat_ = useMixFloat;
    inputChannels_ = channels;
    samplesPerFrame_ = samplesPerFrame;

    WAVEFORMATEX* chosenFmt = nullptr;
    WAVEFORMATEX pcmFmt = {};
    if (useMixFloat) {
        desired.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
        desired.Format.nChannels = static_cast<WORD>(outputChannels_);
        desired.Format.nSamplesPerSec = static_cast<DWORD>(sampleRate);
        desired.Format.wBitsPerSample = 32;
        desired.Format.nBlockAlign = static_cast<WORD>(outputChannels_ * 4);
        desired.Format.nAvgBytesPerSec = desired.Format.nSamplesPerSec * desired.Format.nBlockAlign;
        desired.Format.cbSize = 22;
        desired.Samples.wValidBitsPerSample = 32;
        desired.dwChannelMask = (outputChannels_ == 2) ? (SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT) : SPEAKER_FRONT_CENTER;
        desired.SubFormat = KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
        chosenFmt = &desired.Format;
    } else {
        pcmFmt.wFormatTag = WAVE_FORMAT_PCM;
        pcmFmt.nChannels = static_cast<WORD>(outputChannels_);
        pcmFmt.nSamplesPerSec = static_cast<DWORD>(sampleRate);
        pcmFmt.wBitsPerSample = 16;
        pcmFmt.nBlockAlign = static_cast<WORD>(outputChannels_ * 2);
        pcmFmt.nAvgBytesPerSec = pcmFmt.nSamplesPerSec * pcmFmt.nBlockAlign;
        chosenFmt = &pcmFmt;
    }

    // 40ms shared buffer for jitter resilience with low latency; AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM handles non-48kHz devices cleanly
    const DWORD streamFlags = AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
    const REFERENCE_TIME bufferDuration = 40 * 10000; // 40ms in 100ns units
    hr = audioClient_->Initialize(AUDCLNT_SHAREMODE_SHARED, streamFlags, bufferDuration, 0, chosenFmt, nullptr);
    CoTaskMemFree(mixFmt);
    if (FAILED(hr)) {
        outError = "IAudioClient::Initialize failed";
        Shutdown();
        return false;
    }

    hr = audioClient_->GetBufferSize(&bufferFrameCount_);
    if (FAILED(hr) || bufferFrameCount_ == 0) {
        outError = "GetBufferSize failed";
        Shutdown();
        return false;
    }

    hr = audioClient_->GetService(kIidIAudioRenderClient,
                                  reinterpret_cast<void**>(renderClient_.ReleaseAndGetAddressOf()));
    if (FAILED(hr)) {
        outError = "GetService(IAudioRenderClient) failed";
        Shutdown();
        return false;
    }

    hr = audioClient_->Start();
    if (FAILED(hr)) {
        outError = "IAudioClient::Start failed";
        Shutdown();
        return false;
    }

    pcmBuf_.resize(static_cast<size_t>(samplesPerFrame_) * static_cast<size_t>(inputChannels_) * 2);
    active_ = true;
    return true;
}

void WasapiAudioPlayer::SetVolume(float gain) {
    if (gain < 0.0f) gain = 0.0f;
    if (gain > 1.0f) gain = 1.0f;
    volumeGain_.store(gain);
}

void WasapiAudioPlayer::DecodeAndPlay(const unsigned char* opusData, int opusLen) {
    if (!active_ || !opusDecoder_ || !fnDecode_ || !audioClient_ || !renderClient_) return;
    if (!opusData || opusLen <= 0) return;

    int maxFrameSamples = static_cast<int>(pcmBuf_.size() / static_cast<size_t>(inputChannels_));
    int decodedSamples = fnDecode_(opusDecoder_, opusData, opusLen, pcmBuf_.data(), maxFrameSamples, 0);
    if (decodedSamples <= 0) return;

    UINT32 padding = 0;
    if (FAILED(audioClient_->GetCurrentPadding(&padding))) return;

    // Keep latency bounded: if audio queue ever exceeds ~60ms, drop oldest backlog naturally by skipping frame
    if (padding + static_cast<UINT32>(decodedSamples) > bufferFrameCount_) {
        return;
    }

    BYTE* dst = nullptr;
    HRESULT hr = renderClient_->GetBuffer(static_cast<UINT32>(decodedSamples), &dst);
    if (FAILED(hr) || !dst) return;

    float gain = volumeGain_.load();
    if (gain <= 0.001f) {
        size_t bytesPerSample = outputFloat_ ? sizeof(float) : sizeof(int16_t);
        std::memset(dst, 0, static_cast<size_t>(decodedSamples) * static_cast<size_t>(outputChannels_) * bytesPerSample);
    } else if (outputFloat_) {
        float* fdst = reinterpret_cast<float*>(dst);
        for (int s = 0; s < decodedSamples; ++s) {
            const int16_t* frame = &pcmBuf_[static_cast<size_t>(s) * static_cast<size_t>(inputChannels_)];
            if (outputChannels_ == 2) {
                float left = (static_cast<float>(frame[0]) / 32768.0f) * gain;
                float right = ((inputChannels_ >= 2) ? (static_cast<float>(frame[1]) / 32768.0f) : (static_cast<float>(frame[0]) / 32768.0f)) * gain;
                fdst[s * 2] = left;
                fdst[s * 2 + 1] = right;
            } else {
                float mono = (inputChannels_ >= 2)
                    ? (static_cast<float>(frame[0]) + static_cast<float>(frame[1])) / 65536.0f
                    : (static_cast<float>(frame[0]) / 32768.0f);
                fdst[s] = mono * gain;
            }
        }
    } else {
        int16_t* sdst = reinterpret_cast<int16_t*>(dst);
        for (int s = 0; s < decodedSamples; ++s) {
            const int16_t* frame = &pcmBuf_[static_cast<size_t>(s) * static_cast<size_t>(inputChannels_)];
            if (outputChannels_ == 2) {
                sdst[s * 2] = static_cast<int16_t>(static_cast<float>(frame[0]) * gain);
                int16_t r = (inputChannels_ >= 2) ? frame[1] : frame[0];
                sdst[s * 2 + 1] = static_cast<int16_t>(static_cast<float>(r) * gain);
            } else {
                sdst[s] = static_cast<int16_t>(static_cast<float>(frame[0]) * gain);
            }
        }
    }

    renderClient_->ReleaseBuffer(static_cast<UINT32>(decodedSamples), 0);
}

void WasapiAudioPlayer::Shutdown() {
    active_ = false;
    if (audioClient_) {
        audioClient_->Stop();
    }
    renderClient_.Reset();
    audioClient_.Reset();
    device_.Reset();
    deviceEnum_.Reset();
    if (opusDecoder_ && fnDestroy_) {
        fnDestroy_(opusDecoder_);
        opusDecoder_ = nullptr;
    }
    if (opusModule_) {
        FreeLibrary(opusModule_);
        opusModule_ = nullptr;
        fnCreate_ = nullptr;
        fnDecode_ = nullptr;
        fnDestroy_ = nullptr;
    }
    if (comInitByUs_) {
        CoUninitialize();
        comInitByUs_ = false;
    }
}

} // namespace bobo
