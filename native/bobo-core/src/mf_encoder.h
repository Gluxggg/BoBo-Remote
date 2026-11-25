#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <d3d11.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <strmif.h>
#include <codecapi.h>
#include <wrl/client.h>

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

namespace bobo {

struct AsyncMftEventState {
    std::mutex mtx;
    std::condition_variable cv;
    int needInput = 0;
    int haveOutput = 0;
    bool fatalError = false;
    bool closed = false;
};

class MfH264Encoder {
public:
    MfH264Encoder() = default;
    ~MfH264Encoder();

    bool Open(uint32_t width, uint32_t height, uint32_t fps, uint32_t bitrateKbps,
              std::string& outError, ID3D11Device* d3dDevice = nullptr);
    void Close();

    // Zero-stall live bitrate adjustment via ICodecAPI (works natively on AMD AMF / NVENC / QSV / MS MFT).
    bool UpdateBitrateLive(uint32_t bitrateKbps);

    // Encodes one GPU D3D11 NV12 texture directly (zero CPU copy) when IsD3D11ZeroCopy() is true.
    bool EncodeD3D11Texture(ID3D11Texture2D* nv12Tex, int64_t sampleTimeHns,
                            bool forceKeyframe, std::vector<uint8_t>& outAnnexB,
                            bool& outIsKeyframe, std::string& outError);

    // Encodes one CPU NV12 frame (width*height*3/2 bytes, stride=width) into Annex-B H.264 access unit.
    // Strips H.264 Filler Data (NAL 12) and AUD (NAL 9); guarantees SPS/PPS precedes every IDR keyframe.
    bool EncodeNv12(const uint8_t* nv12Data, size_t nv12Bytes, int64_t sampleTimeHns,
                    bool forceKeyframe, std::vector<uint8_t>& outAnnexB,
                    bool& outIsKeyframe, std::string& outError);

    std::string EncoderName() const { return encoderName_; }
    bool IsHardware() const { return isHardware_; }
    bool IsD3D11ZeroCopy() const { return isD3D11ZeroCopy_; }
    uint32_t BitrateKbps() const { return bitrateKbps_; }

private:
    bool TryOpenCandidate(IMFActivate* activate, bool hardware, uint32_t width, uint32_t height,
                          uint32_t fps, uint32_t bitrateKbps, ID3D11Device* d3dDevice, std::string& outError);
    bool TryOpenBuiltinSoftware(uint32_t width, uint32_t height, uint32_t fps, uint32_t bitrateKbps, std::string& outError);
    bool ConfigureTransform(IMFTransform* mft, bool isAsync, uint32_t width, uint32_t height,
                            uint32_t fps, uint32_t bitrateKbps, ID3D11Device* d3dDevice,
                            bool& outD3D11Bound, std::string& outError);
    void ApplyCodecApiLowLatency(ICodecAPI* api, uint32_t fps, uint32_t bitrateKbps);
    bool EncodeSampleInternal(IMFSample* inSample, bool forceKeyframe,
                              std::vector<uint8_t>& outAnnexB, bool& outIsKeyframe, std::string& outError);
    void StartAsyncEventPump();
    void StopAsyncEventPump();
    void FilterAndInspectAnnexB(std::vector<uint8_t>& annexB, bool& hasIdr, bool& hasSps);

    bool mfStarted_ = false;
    bool isAsync_ = false;
    bool isHardware_ = false;
    bool isD3D11ZeroCopy_ = false;
    bool useVbr_ = true;
    uint32_t width_ = 0;
    uint32_t height_ = 0;
    uint32_t fps_ = 60;
    uint32_t bitrateKbps_ = 10000;
    int64_t frameDurationHns_ = 166666;
    std::string encoderName_;

    UINT d3dResetToken_ = 0;
    Microsoft::WRL::ComPtr<IMFDXGIDeviceManager> d3dMgr_;
    Microsoft::WRL::ComPtr<IMFTransform> encoder_;
    Microsoft::WRL::ComPtr<IMFMediaEventGenerator> eventGen_;
    Microsoft::WRL::ComPtr<ICodecAPI> codecApi_;
    Microsoft::WRL::ComPtr<IMFAsyncCallback> asyncCb_;
    std::shared_ptr<AsyncMftEventState> asyncState_;
    Microsoft::WRL::ComPtr<IMFMediaBuffer> reusableInBuf_;
    DWORD reusableInBufCap_ = 0;

    DWORD inStreamId_ = 0;
    DWORD outStreamId_ = 0;
    DWORD outBufMinSize_ = 0;
    bool outProvidesSamples_ = false;

    std::vector<uint8_t> cachedSpsPps_;
    std::atomic<bool> keyframeRequested_{ true };
};

} // namespace bobo
