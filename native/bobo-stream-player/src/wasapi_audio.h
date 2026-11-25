#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <audioclient.h>
#include <mmdeviceapi.h>
#include <wrl/client.h>

#include <atomic>
#include <cstdint>
#include <string>
#include <vector>

namespace bobo {

class WasapiAudioPlayer {
public:
    WasapiAudioPlayer() = default;
    ~WasapiAudioPlayer();

    bool Initialize(int sampleRate, int channels, int streams, int coupledStreams,
                    const unsigned char* mapping, int samplesPerFrame, std::string& outError);
    void DecodeAndPlay(const unsigned char* opusData, int opusLen);
    void SetVolume(float gain);
    float GetVolume() const { return volumeGain_.load(); }
    void Shutdown();

    bool IsActive() const { return active_; }

private:
    typedef void* (*OpusMSCreateFn)(int32_t Fs, int channels, int streams, int coupled_streams,
                                    const unsigned char* mapping, int* error);
    typedef int (*OpusMSDecodeFn)(void* st, const unsigned char* data, int32_t len,
                                  int16_t* pcm, int frame_size, int decode_fec);
    typedef void (*OpusMSDestroyFn)(void* st);

    bool LoadOpusLibrary();

    HMODULE opusModule_ = nullptr;
    OpusMSCreateFn fnCreate_ = nullptr;
    OpusMSDecodeFn fnDecode_ = nullptr;
    OpusMSDestroyFn fnDestroy_ = nullptr;
    void* opusDecoder_ = nullptr;

    Microsoft::WRL::ComPtr<IMMDeviceEnumerator> deviceEnum_;
    Microsoft::WRL::ComPtr<IMMDevice> device_;
    Microsoft::WRL::ComPtr<IAudioClient> audioClient_;
    Microsoft::WRL::ComPtr<IAudioRenderClient> renderClient_;

    uint32_t bufferFrameCount_ = 0;
    int inputChannels_ = 2;
    int outputChannels_ = 2;
    int samplesPerFrame_ = 240;
    bool outputFloat_ = false;
    bool comInitByUs_ = false;
    bool active_ = false;

    std::atomic<float> volumeGain_{ 1.0f };
    std::vector<int16_t> pcmBuf_;
};

} // namespace bobo
