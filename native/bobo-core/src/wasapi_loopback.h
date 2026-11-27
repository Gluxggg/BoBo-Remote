#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <audioclient.h>
#include <endpointvolume.h>
#include <mmdeviceapi.h>
#include <wrl/client.h>

#include "bobo_core.h"
#include "mf_audio_aac.h"

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <mutex>
#include <thread>
#include <vector>

namespace bobo {

// Host-side WASAPI Loopback system audio capture -> stereo 48kHz AAC-LC (ADTS) or IMA-ADPCM + safe host speaker mute/restore.
class WasapiLoopbackCapturer {
public:
    WasapiLoopbackCapturer() = default;
    ~WasapiLoopbackCapturer();

    bool Start(bool muteSpeakers, BoBoAudioFrameCallback cb, void* userCtx);
    bool Start(bool muteSpeakers, bool preferAac, BoBoAudioFrameCallback cb, void* userCtx);
    void SetMuteSpeakers(bool mute);
    void Stop();
    int ActiveCodec() const { return activeCodec_.load(std::memory_order_acquire); }

private:
    void CaptureLoop();
    void ApplySpeakerMute(bool mute);
    void SignalStartupComplete(int codecId);

    std::atomic<bool> running_{ false };
    std::atomic<bool> desiredMute_{ false };
    std::atomic<bool> preferAac_{ false };
    std::atomic<int> activeCodec_{ kAudioCodecAdpcm };
    std::mutex startMutex_;
    std::condition_variable startCv_;
    bool startReady_ = false;
    bool originalMuteSaved_ = false;
    BOOL originalMuteState_ = FALSE;
    std::mutex muteMutex_;

    BoBoAudioFrameCallback callback_ = nullptr;
    void* userCtx_ = nullptr;
    std::thread worker_;
};

// Client-side WASAPI low-latency audio player for IMA-ADPCM & AAC-LC (ADTS) 48kHz stereo packets + live volume slider gain.
class WasapiAdpcmPlayer {
public:
    WasapiAdpcmPlayer() = default;
    ~WasapiAdpcmPlayer();

    bool Initialize(int sampleRate, int channels);
    void PlayAdpcm(const uint8_t* adpcmData, int len, int sampleRate, int channels);
    void PlayAac(const uint8_t* aacData, int len, int sampleRate, int channels);
    void SetVolume(float gain);
    void Shutdown();

private:
    bool InitializeLocked(int sampleRate, int channels);
    void RenderPcmStereoLocked(const int16_t* pcmStereo, uint32_t frames);
    void ShutdownLocked();

    std::mutex mutex_;
    Microsoft::WRL::ComPtr<IMMDeviceEnumerator> deviceEnum_;
    Microsoft::WRL::ComPtr<IMMDevice> device_;
    Microsoft::WRL::ComPtr<IAudioClient> audioClient_;
    Microsoft::WRL::ComPtr<IAudioRenderClient> renderClient_;
    MfAacDecoder aacDec_;

    uint32_t bufferFrameCount_ = 0;
    int activeRate_ = 0;
    int outputChannels_ = 2;
    int outputBitsPerSample_ = 16;
    bool outputFloat_ = false;
    bool active_ = false;
    std::atomic<float> volumeGain_{ 1.0f };
    std::vector<int16_t> pcmBuf_;
};

} // namespace bobo
