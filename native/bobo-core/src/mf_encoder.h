#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <strmif.h>
#include <codecapi.h>
#include <wrl/client.h>

#include <atomic>
#include <cstdint>
#include <string>
#include <vector>

namespace bobo {

class MfH264Encoder {
public:
    MfH264Encoder() = default;
    ~MfH264Encoder();

    bool Open(uint32_t width, uint32_t height, uint32_t fps, uint32_t bitrateKbps, std::string& outError);
    void Close();

    // Encodes one NV12 frame (width*height*3/2 bytes, stride=width) into Annex-B H.264 access unit.
    // May produce 0 or 1+ access units; guarantees SPS/PPS NALs precede every IDR keyframe.
    bool EncodeNv12(const uint8_t* nv12Data, size_t nv12Bytes, int64_t sampleTimeHns,
                    bool forceKeyframe, std::vector<uint8_t>& outAnnexB, bool& outIsKeyframe, std::string& outError);

    std::string EncoderName() const { return encoderName_; }
    bool IsHardware() const { return isHardware_; }

private:
    bool TryOpenCandidate(IMFActivate* activate, bool hardware, uint32_t width, uint32_t height,
                          uint32_t fps, uint32_t bitrateKbps, std::string& outError);
    bool TryOpenBuiltinSoftware(uint32_t width, uint32_t height, uint32_t fps, uint32_t bitrateKbps, std::string& outError);
    bool ConfigureTransform(IMFTransform* mft, bool isAsync, uint32_t width, uint32_t height,
                            uint32_t fps, uint32_t bitrateKbps, std::string& outError);
    void ExtractSpsPpsAndFlagIdr(const uint8_t* data, size_t len, bool& hasIdr, bool& hasSps);

    bool mfStarted_ = false;
    bool isAsync_ = false;
    bool isHardware_ = false;
    uint32_t width_ = 0;
    uint32_t height_ = 0;
    uint32_t fps_ = 60;
    int64_t frameDurationHns_ = 166666;
    std::string encoderName_;

    Microsoft::WRL::ComPtr<IMFTransform> encoder_;
    Microsoft::WRL::ComPtr<IMFMediaEventGenerator> eventGen_;
    Microsoft::WRL::ComPtr<ICodecAPI> codecApi_;
    DWORD inStreamId_ = 0;
    DWORD outStreamId_ = 0;
    DWORD outBufMinSize_ = 0;
    bool outProvidesSamples_ = false;
    int pendingInputRequests_ = 0;

    std::vector<uint8_t> cachedSpsPps_;
    std::atomic<bool> keyframeRequested_{ true };
};

} // namespace bobo
