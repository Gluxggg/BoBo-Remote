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

#include <atomic>
#include <cstdint>
#include <mutex>
#include <thread>
#include <vector>

namespace bobo {

// Host-side WASAPI Loopback system audio capture -> stereo 48kHz IMA-ADPCM + safe host speaker mute/restore.
class WasapiLoopbackCapturer {
public:
    WasapiLoopbackCapturer() = default;
    ~WasapiLoopbackCapturer();

    bool Start(bool muteSpeakers, BoBoAudioFrameCallback cb, void* userCtx);
    void SetMuteSpeakers(bool mute);
    void Stop();

private:
    void CaptureLoop();
    void ApplySpeakerMute(bool mute);

    std::atomic<bool> running_{ false };
    std::atomic<bool> desiredMute_{ false };
    bool originalMuteSaved_ = false;
    BOOL originalMuteState_ = FALSE;
    std::mutex muteMutex_;

    BoBoAudioFrameCallback callback_ = nullptr;
    void* userCtx_ = nullptr;
    std::thread worker_;
};

// Client-side WASAPI low-latency audio player for IMA-ADPCM 48kHz stereo packets + live volume slider gain.
class WasapiAdpcmPlayer {
public:
    WasapiAdpcmPlayer() = default;
    ~WasapiAdpcmPlayer();

    bool Initialize(int sampleRate, int channels);
    void PlayAdpcm(const uint8_t* adpcmData, int len, int sampleRate, int channels);
    void SetVolume(float gain);
    void Shutdown();

private:
    std::mutex mutex_;
    Microsoft::WRL::ComPtr<IMMDeviceEnumerator> deviceEnum_;
    Microsoft::WRL::ComPtr<IMMDevice> device_;
    Microsoft::WRL::ComPtr<IAudioClient> audioClient_;
    Microsoft::WRL::ComPtr<IAudioRenderClient> renderClient_;

    uint32_t bufferFrameCount_ = 0;
    int activeRate_ = 0;
    int outputChannels_ = 2;
    bool outputFloat_ = false;
    bool active_ = false;
    std::atomic<float> volumeGain_{ 1.0f };
    std::vector<int16_t> pcmBuf_;
};

} // namespace bobo
