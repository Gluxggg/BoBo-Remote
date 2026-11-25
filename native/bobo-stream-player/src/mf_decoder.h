#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <d3d11.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <wrl/client.h>

#include <cstdint>
#include <string>
#include <vector>

namespace bobo {

enum class VideoCodec {
    H264,
    HEVC
};

struct DecoderProbeResult {
    bool dxgiManagerReady = false;
    bool h264HwAvailable = false;
    bool hevcHwAvailable = false;
    std::vector<std::string> h264Decoders;
    std::vector<std::string> hevcDecoders;
};

struct DecodedFrame {
    bool isGpuTexture = false;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> gpuTexture;
    uint32_t subresourceIndex = 0;
    std::vector<uint8_t> cpuNv12;
    uint32_t width = 0;
    uint32_t height = 0;
    uint32_t stride = 0;
};

class MfHardwareDecoder {
public:
    MfHardwareDecoder() = default;
    ~MfHardwareDecoder();

    bool Initialize(ID3D11Device* d3dDevice, std::string& outError);
    DecoderProbeResult ProbeHardwareDecoders() const;
    bool OpenCodec(VideoCodec codec, uint32_t width, uint32_t height, std::string& outError);
    void CloseCodec();
    void Flush();

    bool SubmitAccessUnit(const uint8_t* data, size_t size, int64_t pts100ns, std::string& outError);
    bool PullDecodedFrame(DecodedFrame& outFrame, bool& outNeedMoreInput);
    bool PullDecodedTexture(Microsoft::WRL::ComPtr<ID3D11Texture2D>& outTexture, uint32_t& outSubresource);

    const std::string& ActiveDecoderName() const { return activeDecoderName_; }
    uint32_t OutputWidth() const { return outWidth_; }
    uint32_t OutputHeight() const { return outHeight_; }

private:
    bool NegotiateNv12Output();

    bool mfStarted_ = false;
    UINT resetToken_ = 0;
    Microsoft::WRL::ComPtr<IMFDXGIDeviceManager> dxgiManager_;
    Microsoft::WRL::ComPtr<IMFTransform> decoder_;
    std::string activeDecoderName_;
    uint32_t outWidth_ = 0;
    uint32_t outHeight_ = 0;
    uint32_t outStride_ = 0;
    bool mftProvidesSamples_ = true;
    DWORD minOutputSampleSize_ = 0;
    Microsoft::WRL::ComPtr<IMFMediaBuffer> inputBuf_;
    DWORD inputBufCapacity_ = 0;
};

} // namespace bobo
