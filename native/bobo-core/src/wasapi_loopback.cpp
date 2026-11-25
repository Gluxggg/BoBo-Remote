#include "wasapi_loopback.h"
#include "ima_adpcm.h"

#include <ksmedia.h>
#include <algorithm>
#include <cstring>

namespace bobo {

static const CLSID kClsidMmDevEnum = __uuidof(MMDeviceEnumerator);
static const IID kIidMmDevEnum = __uuidof(IMMDeviceEnumerator);
static const IID kIidAudioClient = __uuidof(IAudioClient);
static const IID kIidAudioCaptureClient = __uuidof(IAudioCaptureClient);
static const IID kIidAudioRenderClient = __uuidof(IAudioRenderClient);
static const IID kIidAudioEndpointVol = __uuidof(IAudioEndpointVolume);

WasapiLoopbackCapturer::~WasapiLoopbackCapturer() {
    Stop();
}

bool WasapiLoopbackCapturer::Start(bool muteSpeakers, BoBoAudioFrameCallback cb, void* userCtx) {
    Stop();
    callback_ = cb;
    userCtx_ = userCtx;
    desiredMute_.store(muteSpeakers);
    running_.store(true);
    worker_ = std::thread([this]() { CaptureLoop(); });
    return true;
}

void WasapiLoopbackCapturer::SetMuteSpeakers(bool mute) {
    desiredMute_.store(mute);
    ApplySpeakerMute(mute);
}

void WasapiLoopbackCapturer::ApplySpeakerMute(bool mute) {
    std::lock_guard<std::mutex> lk(muteMutex_);
    HRESULT hrCo = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    bool needUninit = SUCCEEDED(hrCo);
    {
        Microsoft::WRL::ComPtr<IMMDeviceEnumerator> en;
        Microsoft::WRL::ComPtr<IMMDevice> dev;
        Microsoft::WRL::ComPtr<IAudioEndpointVolume> epv;
        if (SUCCEEDED(CoCreateInstance(kClsidMmDevEnum, nullptr, CLSCTX_ALL, kIidMmDevEnum, reinterpret_cast<void**>(en.GetAddressOf()))) &&
            SUCCEEDED(en->GetDefaultAudioEndpoint(eRender, eConsole, dev.GetAddressOf())) &&
            SUCCEEDED(dev->Activate(kIidAudioEndpointVol, CLSCTX_ALL, nullptr, reinterpret_cast<void**>(epv.GetAddressOf())))) {
            if (!originalMuteSaved_) {
                if (SUCCEEDED(epv->GetMute(&originalMuteState_))) {
                    originalMuteSaved_ = true;
                }
            }
            epv->SetMute(mute ? TRUE : FALSE, nullptr);
        }
    }
    if (needUninit) CoUninitialize();
}

void WasapiLoopbackCapturer::Stop() {
    running_.store(false);
    if (worker_.joinable()) {
        worker_.join();
    }
    // Always restore original host speaker mute state when remote session ends
    std::lock_guard<std::mutex> lk(muteMutex_);
    if (originalMuteSaved_) {
        HRESULT hrCo = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        bool needUninit = SUCCEEDED(hrCo);
        {
            Microsoft::WRL::ComPtr<IMMDeviceEnumerator> en;
            Microsoft::WRL::ComPtr<IMMDevice> dev;
            Microsoft::WRL::ComPtr<IAudioEndpointVolume> epv;
            if (SUCCEEDED(CoCreateInstance(kClsidMmDevEnum, nullptr, CLSCTX_ALL, kIidMmDevEnum, reinterpret_cast<void**>(en.GetAddressOf()))) &&
                SUCCEEDED(en->GetDefaultAudioEndpoint(eRender, eConsole, dev.GetAddressOf())) &&
                SUCCEEDED(dev->Activate(kIidAudioEndpointVol, CLSCTX_ALL, nullptr, reinterpret_cast<void**>(epv.GetAddressOf())))) {
                epv->SetMute(originalMuteState_, nullptr);
            }
        }
        if (needUninit) CoUninitialize();
        originalMuteSaved_ = false;
    }
}

void WasapiLoopbackCapturer::CaptureLoop() {
    HRESULT hrCo = CoInitializeEx(nullptr, COINIT_MULTITHREADED);

    Microsoft::WRL::ComPtr<IMMDeviceEnumerator> en;
    Microsoft::WRL::ComPtr<IMMDevice> dev;
    Microsoft::WRL::ComPtr<IAudioClient> client;
    Microsoft::WRL::ComPtr<IAudioCaptureClient> cap;

    if (FAILED(CoCreateInstance(kClsidMmDevEnum, nullptr, CLSCTX_ALL, kIidMmDevEnum, reinterpret_cast<void**>(en.GetAddressOf()))) ||
        FAILED(en->GetDefaultAudioEndpoint(eRender, eConsole, dev.GetAddressOf())) ||
        FAILED(dev->Activate(kIidAudioClient, CLSCTX_ALL, nullptr, reinterpret_cast<void**>(client.GetAddressOf())))) {
        if (SUCCEEDED(hrCo)) CoUninitialize();
        return;
    }

    WAVEFORMATEX* mixFmt = nullptr;
    if (FAILED(client->GetMixFormat(&mixFmt)) || !mixFmt) {
        if (SUCCEEDED(hrCo)) CoUninitialize();
        return;
    }

    int srcRate = static_cast<int>(mixFmt->nSamplesPerSec);
    int srcCh = static_cast<int>(mixFmt->nChannels);
    bool isFloat = (mixFmt->wFormatTag == WAVE_FORMAT_IEEE_FLOAT);
    if (mixFmt->wFormatTag == WAVE_FORMAT_EXTENSIBLE && mixFmt->cbSize >= 22) {
        const auto* ext = reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(mixFmt);
        if (IsEqualGUID(ext->SubFormat, KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)) isFloat = true;
    }

    REFERENCE_TIME hnsBuf = 400000; // 40ms
    HRESULT hr = client->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, hnsBuf, 0, mixFmt, nullptr);
    CoTaskMemFree(mixFmt);
    if (FAILED(hr) ||
        FAILED(client->GetService(kIidAudioCaptureClient, reinterpret_cast<void**>(cap.GetAddressOf()))) ||
        FAILED(client->Start())) {
        if (SUCCEEDED(hrCo)) CoUninitialize();
        return;
    }

    std::vector<int16_t> stereoBuf;
    std::vector<uint8_t> adpcmBuf;
    const int kChunkFrames = 480; // 10ms @ 48kHz

    while (running_.load()) {
        UINT32 packetLen = 0;
        if (FAILED(cap->GetNextPacketSize(&packetLen)) || packetLen == 0) {
            Sleep(5);
            continue;
        }
        while (packetLen > 0 && running_.load()) {
            BYTE* data = nullptr;
            UINT32 numFrames = 0;
            DWORD flags = 0;
            if (FAILED(cap->GetBuffer(&data, &numFrames, &flags, nullptr, nullptr))) break;

            bool silent = ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0) || !data;
            if (!silent && numFrames > 0) {
                for (UINT32 f = 0; f < numFrames; ++f) {
                    int16_t sL = 0, sR = 0;
                    if (isFloat) {
                        const float* fp = reinterpret_cast<const float*>(data) + static_cast<size_t>(f) * static_cast<size_t>(srcCh);
                        float l = fp[0];
                        float r = (srcCh >= 2) ? fp[1] : l;
                        if (l > 1.0f) l = 1.0f; else if (l < -1.0f) l = -1.0f;
                        if (r > 1.0f) r = 1.0f; else if (r < -1.0f) r = -1.0f;
                        sL = static_cast<int16_t>(l * 32767.0f);
                        sR = static_cast<int16_t>(r * 32767.0f);
                    } else {
                        const int16_t* sp = reinterpret_cast<const int16_t*>(data) + static_cast<size_t>(f) * static_cast<size_t>(srcCh);
                        sL = sp[0];
                        sR = (srcCh >= 2) ? sp[1] : sL;
                    }
                    stereoBuf.push_back(sL);
                    stereoBuf.push_back(sR);
                }

                while (static_cast<int>(stereoBuf.size() / 2) >= kChunkFrames) {
                    ImaAdpcmCodec::EncodeStereo(stereoBuf.data(), kChunkFrames, adpcmBuf);
                    if (callback_ && !adpcmBuf.empty()) {
                        callback_(adpcmBuf.data(), static_cast<int32_t>(adpcmBuf.size()), srcRate, 2, kChunkFrames, userCtx_);
                    }
                    stereoBuf.erase(stereoBuf.begin(), stereoBuf.begin() + static_cast<ptrdiff_t>(kChunkFrames * 2));
                }
            }
            cap->ReleaseBuffer(numFrames);
            if (FAILED(cap->GetNextPacketSize(&packetLen))) break;
        }
    }

    client->Stop();
    if (SUCCEEDED(hrCo)) CoUninitialize();
}

// ==================== Client WASAPI ADPCM Player ====================

WasapiAdpcmPlayer::~WasapiAdpcmPlayer() {
    Shutdown();
}

void WasapiAdpcmPlayer::SetVolume(float gain) {
    if (gain < 0.0f) gain = 0.0f;
    if (gain > 1.0f) gain = 1.0f;
    volumeGain_.store(gain);
}

bool WasapiAdpcmPlayer::Initialize(int sampleRate, int channels) {
    Shutdown();
    if (sampleRate <= 0) sampleRate = 48000;
    if (channels <= 0) channels = 2;

    if (FAILED(CoCreateInstance(kClsidMmDevEnum, nullptr, CLSCTX_ALL, kIidMmDevEnum,
                                reinterpret_cast<void**>(deviceEnum_.ReleaseAndGetAddressOf())))) return false;
    if (FAILED(deviceEnum_->GetDefaultAudioEndpoint(eRender, eConsole, device_.ReleaseAndGetAddressOf()))) return false;
    if (FAILED(device_->Activate(kIidAudioClient, CLSCTX_ALL, nullptr,
                                 reinterpret_cast<void**>(audioClient_.ReleaseAndGetAddressOf())))) return false;

    WAVEFORMATEX* mixFmt = nullptr;
    if (FAILED(audioClient_->GetMixFormat(&mixFmt)) || !mixFmt) return false;

    outputChannels_ = (mixFmt->nChannels >= 2) ? 2 : 1;
    outputFloat_ = (mixFmt->wFormatTag == WAVE_FORMAT_IEEE_FLOAT);
    if (mixFmt->wFormatTag == WAVE_FORMAT_EXTENSIBLE && mixFmt->cbSize >= 22) {
        const auto* ext = reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(mixFmt);
        if (IsEqualGUID(ext->SubFormat, KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)) outputFloat_ = true;
    }

    WAVEFORMATEX desired = {};
    desired.wFormatTag = outputFloat_ ? WAVE_FORMAT_IEEE_FLOAT : WAVE_FORMAT_PCM;
    desired.nChannels = static_cast<WORD>(outputChannels_);
    desired.nSamplesPerSec = static_cast<DWORD>(sampleRate);
    desired.wBitsPerSample = outputFloat_ ? 32 : 16;
    desired.nBlockAlign = static_cast<WORD>(desired.nChannels * (desired.wBitsPerSample / 8));
    desired.nAvgBytesPerSec = desired.nSamplesPerSec * desired.nBlockAlign;

    WAVEFORMATEX* closest = nullptr;
    HRESULT supHr = audioClient_->IsFormatSupported(AUDCLNT_SHAREMODE_SHARED, &desired, &closest);
    const WAVEFORMATEX* useFmt = (supHr == S_OK) ? &desired : (closest ? closest : mixFmt);
    outputChannels_ = useFmt->nChannels >= 2 ? 2 : 1;
    outputFloat_ = (useFmt->wFormatTag == WAVE_FORMAT_IEEE_FLOAT);
    if (useFmt->wFormatTag == WAVE_FORMAT_EXTENSIBLE && useFmt->cbSize >= 22) {
        const auto* ext = reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(useFmt);
        if (IsEqualGUID(ext->SubFormat, KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)) outputFloat_ = true;
    }

    REFERENCE_TIME hnsDur = 600000; // 60ms bounded latency
    DWORD initFlags = AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
    HRESULT hr = audioClient_->Initialize(AUDCLNT_SHAREMODE_SHARED, initFlags, hnsDur, 0, useFmt, nullptr);
    if (FAILED(hr)) {
        hr = audioClient_->Initialize(AUDCLNT_SHAREMODE_SHARED, 0, hnsDur, 0, mixFmt, nullptr);
    }
    if (closest) CoTaskMemFree(closest);
    CoTaskMemFree(mixFmt);

    if (FAILED(hr) ||
        FAILED(audioClient_->GetBufferSize(&bufferFrameCount_)) ||
        FAILED(audioClient_->GetService(kIidAudioRenderClient, reinterpret_cast<void**>(renderClient_.ReleaseAndGetAddressOf()))) ||
        FAILED(audioClient_->Start())) {
        Shutdown();
        return false;
    }

    activeRate_ = sampleRate;
    active_ = true;
    return true;
}

void WasapiAdpcmPlayer::PlayAdpcm(const uint8_t* adpcmData, int len, int sampleRate, int channels) {
    std::lock_guard<std::mutex> lk(mutex_);
    if (!adpcmData || len < 9) return;
    if (!active_ || activeRate_ != sampleRate) {
        if (!Initialize(sampleRate, channels)) return;
    }

    if (!ImaAdpcmCodec::DecodeStereo(adpcmData, len, pcmBuf_) || pcmBuf_.empty()) return;
    uint32_t frames = static_cast<uint32_t>(pcmBuf_.size() / 2);

    UINT32 padding = 0;
    if (FAILED(audioClient_->GetCurrentPadding(&padding))) return;
    if (padding + frames > bufferFrameCount_) return; // drop backlog naturally to keep latency low

    BYTE* dst = nullptr;
    if (FAILED(renderClient_->GetBuffer(frames, &dst)) || !dst) return;

    float gain = volumeGain_.load();
    if (gain <= 0.001f) {
        size_t bps = outputFloat_ ? sizeof(float) : sizeof(int16_t);
        std::memset(dst, 0, static_cast<size_t>(frames) * static_cast<size_t>(outputChannels_) * bps);
    } else if (outputFloat_) {
        float* fdst = reinterpret_cast<float*>(dst);
        for (uint32_t i = 0; i < frames; ++i) {
            float l = (static_cast<float>(pcmBuf_[i * 2]) / 32768.0f) * gain;
            float r = (static_cast<float>(pcmBuf_[i * 2 + 1]) / 32768.0f) * gain;
            if (outputChannels_ == 2) {
                fdst[i * 2] = l;
                fdst[i * 2 + 1] = r;
            } else {
                fdst[i] = (l + r) * 0.5f;
            }
        }
    } else {
        int16_t* sdst = reinterpret_cast<int16_t*>(dst);
        for (uint32_t i = 0; i < frames; ++i) {
            int16_t l = static_cast<int16_t>(static_cast<float>(pcmBuf_[i * 2]) * gain);
            int16_t r = static_cast<int16_t>(static_cast<float>(pcmBuf_[i * 2 + 1]) * gain);
            if (outputChannels_ == 2) {
                sdst[i * 2] = l;
                sdst[i * 2 + 1] = r;
            } else {
                sdst[i] = static_cast<int16_t>((static_cast<int>(l) + static_cast<int>(r)) / 2);
            }
        }
    }
    renderClient_->ReleaseBuffer(frames, 0);
}

void WasapiAdpcmPlayer::Shutdown() {
    std::lock_guard<std::mutex> lk(mutex_);
    active_ = false;
    activeRate_ = 0;
    if (audioClient_) {
        audioClient_->Stop();
    }
    renderClient_.Reset();
    audioClient_.Reset();
    device_.Reset();
    deviceEnum_.Reset();
}

} // namespace bobo
