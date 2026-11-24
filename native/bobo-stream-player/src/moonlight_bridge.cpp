#include "moonlight_bridge.h"

#ifdef BOBO_WITH_MOONLIGHT_COMMON_C
#include <Limelight.h>
#endif

namespace bobo {

MoonlightBridge::MoonlightBridge(D3D11Renderer& renderer, MfHardwareDecoder& decoder)
    : renderer_(renderer), decoder_(decoder) {}

MoonlightBridge::~MoonlightBridge() {
    StopSession();
}

bool MoonlightBridge::IsMoonlightCommonCLinked() {
#ifdef BOBO_WITH_MOONLIGHT_COMMON_C
    return true;
#else
    return false;
#endif
}

bool MoonlightBridge::StartSession(const StreamSessionConfig& cfg, std::string& outError) {
    if (cfg.host.empty()) {
        outError = "Host address is empty";
        return false;
    }
    if (!decoder_.OpenCodec(cfg.codec, cfg.width, cfg.height, outError)) {
        return false;
    }

#ifdef BOBO_WITH_MOONLIGHT_COMMON_C
    // Full Limelight session initialization binds DECODER_RENDERER_CALLBACKS -> MfHardwareDecoder + D3D11Renderer.
    // RTSP session negotiation & AES key exchange from Sunshine /launch response will plug in at Phase 5.3.
    (void)renderer_;
    active_ = true;
    outError = "moonlight-common-c linked; GameStream HTTPS /launch handshake is scheduled for Phase 5.3";
    return false;
#else
    (void)renderer_;
    outError = "Built in standalone D3D11VA probe/viewport mode (pass -FetchMoonlightC to link moonlight-common-c)";
    return false;
#endif
}

void MoonlightBridge::StopSession() {
    if (!active_) return;
#ifdef BOBO_WITH_MOONLIGHT_COMMON_C
    LiStopConnection();
#endif
    active_ = false;
}

} // namespace bobo
