#pragma once

#include <cstdint>
#include <vector>

namespace bobo {

constexpr int kAudioCodecAdpcm = 0;
constexpr int kAudioCodecAac = 1;

// Zero-external-dependency OS-native Media Foundation AAC-LC (ADTS) encoder (stereo 44.1/48kHz @ 96 kbps).
class MfAacEncoder {
public:
    MfAacEncoder() = default;
    ~MfAacEncoder();

    bool Open(int sampleRate, int channels, int bitrateBps = 96000);
    bool PushPcm(const int16_t* stereoInterleaved, int frameCount, std::vector<std::vector<uint8_t>>& outAdtsFrames);
    bool Drain(std::vector<std::vector<uint8_t>>& outAdtsFrames);
    void Close();

    bool IsOpen() const { return open_; }
    int SampleRate() const { return sampleRate_; }
    int Channels() const { return channels_; }

private:
    bool PullOutputs(std::vector<std::vector<uint8_t>>& outAdtsFrames);

    bool open_ = false;
    bool mfStarted_ = false;
    int sampleRate_ = 48000;
    int channels_ = 2;
    int64_t nextPtsHns_ = 0;
    uint32_t outStreamMinBytes_ = 8192;
    bool providerAllocatesOut_ = false;
    void* mftRaw_ = nullptr;
    std::vector<int16_t> pcmAccum_;
};

// Zero-external-dependency OS-native Media Foundation AAC-LC (ADTS) decoder -> 16-bit interleaved PCM.
class MfAacDecoder {
public:
    MfAacDecoder() = default;
    ~MfAacDecoder();

    bool Open(int sampleRate, int channels);
    bool DecodeAdts(const uint8_t* adtsData, int len, std::vector<int16_t>& outPcm);
    bool Drain(std::vector<int16_t>& outPcm);
    void Close();

    bool IsOpen() const { return open_; }
    int SampleRate() const { return sampleRate_; }
    int Channels() const { return channels_; }
    uint32_t LastHresult() const { return lastHr_; }

private:
    bool ConfigureOutputType();
    bool PullOutputs(std::vector<int16_t>& outPcm);

    bool open_ = false;
    bool mfStarted_ = false;
    int sampleRate_ = 48000;
    int channels_ = 2;
    bool outIsFloat_ = false;
    int outChannels_ = 2;
    uint32_t outStreamMinBytes_ = 16384;
    bool providerAllocatesOut_ = false;
    int64_t nextPtsHns_ = 0;
    uint32_t lastHr_ = 0;
    void* mftRaw_ = nullptr;
};

// Probes whether the host Windows installation provides functional built-in MF AAC-LC encode + decode MFTs.
bool IsMfAacAudioSupported();

} // namespace bobo
