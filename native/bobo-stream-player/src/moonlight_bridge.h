#pragma once

#include "d3d11_renderer.h"
#include "mf_decoder.h"

#include <cstdint>
#include <string>

namespace bobo {

struct StreamSessionConfig {
    std::string host;
    uint32_t width = 1920;
    uint32_t height = 1080;
    uint32_t fps = 60;
    uint32_t bitrateKbps = 15000;
    VideoCodec codec = VideoCodec::HEVC;
};

class MoonlightBridge {
public:
    MoonlightBridge(D3D11Renderer& renderer, MfHardwareDecoder& decoder);
    ~MoonlightBridge();

    static bool IsMoonlightCommonCLinked();
    bool StartSession(const StreamSessionConfig& cfg, std::string& outError);
    void StopSession();

private:
    D3D11Renderer& renderer_;
    MfHardwareDecoder& decoder_;
    bool active_ = false;
};

} // namespace bobo
