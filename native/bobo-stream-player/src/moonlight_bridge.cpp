#include "moonlight_bridge.h"

#include <cstdarg>
#include <cstdio>
#include <cstring>

#ifdef BOBO_WITH_MOONLIGHT_COMMON_C
#include <Limelight.h>
#endif

namespace bobo {

static MoonlightBridge* g_bridgeInstance = nullptr;
static std::mutex g_bridgeGlobalMutex;

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

std::string MoonlightBridge::GetLaunchQueryParameters() {
#ifdef BOBO_WITH_MOONLIGHT_COMMON_C
    const char* q = LiGetLaunchUrlQueryParameters();
    return q ? std::string(q) : std::string();
#else
    return std::string();
#endif
}

#ifdef BOBO_WITH_MOONLIGHT_COMMON_C

int MoonlightBridge::OnVideoSetup(int videoFormat, int width, int height, int redrawRate, void* context, int drFlags) {
    (void)redrawRate;
    (void)drFlags;
    auto* self = static_cast<MoonlightBridge*>(context);
    if (!self) return -1;

    VideoCodec codec = ((videoFormat & VIDEO_FORMAT_MASK_H265) != 0) ? VideoCodec::HEVC : VideoCodec::H264;
    self->activeCodecName_ = (codec == VideoCodec::HEVC) ? "HEVC" : "H264";
    self->renderer_.SetVideoSourceDimensions(static_cast<uint32_t>(width), static_cast<uint32_t>(height));

    std::string err;
    if (!self->decoder_.OpenCodec(codec, static_cast<uint32_t>(width), static_cast<uint32_t>(height), err)) {
        EmitJsonLine("{\"event\":\"error\",\"message\":\"Video decoder open failed: " + EscapeJson(err) + "\"}");
        return -1;
    }

    self->frameAssembleBuf_.reserve(512 * 1024);
    {
        std::lock_guard<std::mutex> lk(self->statsMutex_);
        self->framesPresentedWindow_ = 0;
        self->decodeMsAccum_ = 0.0;
        self->renderMsAccum_ = 0.0;
        self->timingSamples_ = 0;
        self->lastStatsWindow_ = std::chrono::steady_clock::now();
    }
    return 0;
}

void MoonlightBridge::OnVideoStart() {}

void MoonlightBridge::OnVideoStop() {
    auto* self = g_bridgeInstance;
    if (self) {
        self->decoder_.Flush();
    }
}

void MoonlightBridge::OnVideoCleanup() {
    auto* self = g_bridgeInstance;
    if (self) {
        self->decoder_.CloseCodec();
    }
}

int MoonlightBridge::OnVideoSubmitDecodeUnit(void* decodeUnitPtr) {
    auto* self = g_bridgeInstance;
    auto* du = static_cast<PDECODE_UNIT>(decodeUnitPtr);
    if (!self || !du || self->stopping_.load()) {
        return DR_OK;
    }

    self->frameAssembleBuf_.clear();
    if (du->fullLength > 0) {
        self->frameAssembleBuf_.reserve(static_cast<size_t>(du->fullLength));
    }
    for (PLENTRY entry = du->bufferList; entry != nullptr; entry = entry->next) {
        if (entry->data && entry->length > 0) {
            const auto* bytes = reinterpret_cast<const uint8_t*>(entry->data);
            self->frameAssembleBuf_.insert(self->frameAssembleBuf_.end(), bytes, bytes + entry->length);
        }
    }
    if (self->frameAssembleBuf_.empty()) {
        return DR_OK;
    }

    auto t0 = std::chrono::steady_clock::now();
    int64_t pts100ns = static_cast<int64_t>(du->presentationTimeUs) * 10LL;
    std::string err;
    if (!self->decoder_.SubmitAccessUnit(self->frameAssembleBuf_.data(), self->frameAssembleBuf_.size(), pts100ns, err)) {
        // Drain any pending output frames first in case decoder queue was full
        DecodedFrame drain;
        bool needMore = false;
        while (self->decoder_.PullDecodedFrame(drain, needMore)) {
            std::string rErr;
            if (drain.isGpuTexture && drain.gpuTexture) {
                self->renderer_.PresentNv12Texture(drain.gpuTexture.Get(), drain.subresourceIndex, drain.width, drain.height, rErr);
            } else if (!drain.cpuNv12.empty()) {
                self->renderer_.PresentNv12CpuBuffer(drain.cpuNv12.data(), drain.width, drain.height, drain.stride, rErr);
            }
        }
        if (!self->decoder_.SubmitAccessUnit(self->frameAssembleBuf_.data(), self->frameAssembleBuf_.size(), pts100ns, err)) {
            return DR_NEED_IDR;
        }
    }

    DecodedFrame frame;
    bool needMore = false;
    int presented = 0;
    double renderMs = 0.0;
    for (int iter = 0; iter < 8; ++iter) {
        if (!self->decoder_.PullDecodedFrame(frame, needMore)) {
            if (needMore) break;
            continue;
        }
        auto tDec = std::chrono::steady_clock::now();
        std::string rErr;
        bool ok = false;
        if (frame.isGpuTexture && frame.gpuTexture) {
            ok = self->renderer_.PresentNv12Texture(frame.gpuTexture.Get(), frame.subresourceIndex, frame.width, frame.height, rErr);
        } else if (!frame.cpuNv12.empty()) {
            ok = self->renderer_.PresentNv12CpuBuffer(frame.cpuNv12.data(), frame.width, frame.height, frame.stride, rErr);
        }
        auto tRen = std::chrono::steady_clock::now();
        if (ok) {
            ++presented;
            renderMs += std::chrono::duration<double, std::milli>(tRen - tDec).count();
        }
    }

    if (presented > 0) {
        auto tEnd = std::chrono::steady_clock::now();
        double totalMs = std::chrono::duration<double, std::milli>(tEnd - t0).count();
        double decodeMs = (totalMs > renderMs) ? (totalMs - renderMs) : 0.0;
        std::lock_guard<std::mutex> lk(self->statsMutex_);
        self->framesPresentedWindow_ += static_cast<uint64_t>(presented);
        self->decodeMsAccum_ += decodeMs;
        self->renderMsAccum_ += renderMs;
        self->timingSamples_ += 1;
    }

    return DR_OK;
}

int MoonlightBridge::OnAudioInit(int audioConfiguration, const void* opusConfigPtr, void* context, int arFlags) {
    (void)audioConfiguration;
    (void)arFlags;
    auto* self = static_cast<MoonlightBridge*>(context);
    const auto* cfg = static_cast<const OPUS_MULTISTREAM_CONFIGURATION*>(opusConfigPtr);
    if (!self || !cfg || !self->activeCfg_.enableAudio) return 0;

    std::string err;
    if (!self->audio_.Initialize(cfg->sampleRate, cfg->channelCount, cfg->streams,
                                 cfg->coupledStreams, cfg->mapping, cfg->samplesPerFrame, err)) {
        // Non-fatal: continue streaming video even if audio endpoint or opus.dll is unavailable
        EmitJsonLine("{\"event\":\"audio_warning\",\"message\":\"" + EscapeJson(err) + "\"}");
    }
    return 0;
}

void MoonlightBridge::OnAudioStart() {}

void MoonlightBridge::OnAudioStop() {}

void MoonlightBridge::OnAudioCleanup() {
    auto* self = g_bridgeInstance;
    if (self) {
        self->audio_.Shutdown();
    }
}

void MoonlightBridge::OnAudioDecodeAndPlay(char* sampleData, int sampleLength) {
    auto* self = g_bridgeInstance;
    if (!self || self->stopping_.load() || !sampleData || sampleLength <= 0) return;
    self->audio_.DecodeAndPlay(reinterpret_cast<const unsigned char*>(sampleData), sampleLength);
}

void MoonlightBridge::OnStageStarting(int stage) {
    const char* name = LiGetStageName(stage);
    EmitJsonLine(FormatStageJson(name ? name : "stage", "starting", 0));
}

void MoonlightBridge::OnStageComplete(int stage) {
    const char* name = LiGetStageName(stage);
    EmitJsonLine(FormatStageJson(name ? name : "stage", "complete", 0));
}

void MoonlightBridge::OnStageFailed(int stage, int errorCode) {
    const char* name = LiGetStageName(stage);
    EmitJsonLine(FormatStageJson(name ? name : "stage", "failed", errorCode));
}

void MoonlightBridge::OnConnectionStarted() {
    auto* self = g_bridgeInstance;
    if (!self) return;
    self->connected_.store(true);
    EmitJsonLine(FormatConnectedJson(
        self->activeCfg_.width,
        self->activeCfg_.height,
        self->activeCfg_.fps,
        self->activeCodecName_,
        self->decoder_.ActiveDecoderName()));
}

void MoonlightBridge::OnConnectionTerminated(int errorCode) {
    auto* self = g_bridgeInstance;
    if (!self) return;
    self->connected_.store(false);
    self->active_.store(false);
    std::string reason = (errorCode == ML_ERROR_GRACEFUL_TERMINATION)
        ? "Host ended remote session"
        : "Remote stream terminated unexpectedly";
    EmitJsonLine(FormatTerminatedJson(errorCode, reason));
    if (self->onTerminated_) {
        self->onTerminated_(errorCode, reason);
    }
}

void MoonlightBridge::OnLogMessage(const char* format, ...) {
    (void)format;
}

#endif // BOBO_WITH_MOONLIGHT_COMMON_C

bool MoonlightBridge::StartSession(const StreamSessionConfig& cfg,
                                   std::function<void(int code, const std::string& reason)> onTerminated,
                                   std::string& outError) {
    if (cfg.host.empty()) {
        outError = "Host address is empty";
        return false;
    }

#ifdef BOBO_WITH_MOONLIGHT_COMMON_C
    StopSession();

    {
        std::lock_guard<std::mutex> lk(g_bridgeGlobalMutex);
        g_bridgeInstance = this;
    }

    activeCfg_ = cfg;
    onTerminated_ = std::move(onTerminated);
    stopping_.store(false);
    connected_.store(false);

    SERVER_INFORMATION serverInfo;
    LiInitializeServerInformation(&serverInfo);
    serverInfo.address = activeCfg_.host.c_str();
    serverInfo.serverInfoAppVersion = activeCfg_.appVersion.c_str();
    serverInfo.serverInfoGfeVersion = activeCfg_.gfeVersion.c_str();
    serverInfo.rtspSessionUrl = activeCfg_.rtspUrl.empty() ? nullptr : activeCfg_.rtspUrl.c_str();
    serverInfo.serverCodecModeSupport = activeCfg_.serverCodecModeSupport;

    STREAM_CONFIGURATION streamCfg;
    LiInitializeStreamConfiguration(&streamCfg);
    streamCfg.width = static_cast<int>(activeCfg_.width);
    streamCfg.height = static_cast<int>(activeCfg_.height);
    streamCfg.fps = static_cast<int>(activeCfg_.fps);
    streamCfg.bitrate = static_cast<int>(activeCfg_.bitrateKbps);
    streamCfg.packetSize = 1024;
    streamCfg.streamingRemotely = STREAM_CFG_AUTO;
    streamCfg.audioConfiguration = AUDIO_CONFIGURATION_STEREO;
    streamCfg.supportedVideoFormats = (activeCfg_.codec == VideoCodec::HEVC)
        ? (VIDEO_FORMAT_H265 | VIDEO_FORMAT_H264)
        : VIDEO_FORMAT_H264;
    streamCfg.clientRefreshRateX100 = static_cast<int>(activeCfg_.fps * 100);
    streamCfg.colorSpace = COLORSPACE_REC_709;
    streamCfg.colorRange = COLOR_RANGE_LIMITED;
    streamCfg.encryptionFlags = ENCFLG_AUDIO;
    std::memcpy(streamCfg.remoteInputAesKey, activeCfg_.riKey, 16);
    std::memset(streamCfg.remoteInputAesIv, 0, 16);
    streamCfg.remoteInputAesIv[0] = static_cast<char>((activeCfg_.riKeyId >> 24) & 0xFF);
    streamCfg.remoteInputAesIv[1] = static_cast<char>((activeCfg_.riKeyId >> 16) & 0xFF);
    streamCfg.remoteInputAesIv[2] = static_cast<char>((activeCfg_.riKeyId >> 8) & 0xFF);
    streamCfg.remoteInputAesIv[3] = static_cast<char>(activeCfg_.riKeyId & 0xFF);

    DECODER_RENDERER_CALLBACKS videoCb;
    LiInitializeVideoCallbacks(&videoCb);
    videoCb.setup = &MoonlightBridge::OnVideoSetup;
    videoCb.start = &MoonlightBridge::OnVideoStart;
    videoCb.stop = &MoonlightBridge::OnVideoStop;
    videoCb.cleanup = &MoonlightBridge::OnVideoCleanup;
    videoCb.submitDecodeUnit = reinterpret_cast<DecoderRendererSubmitDecodeUnit>(&MoonlightBridge::OnVideoSubmitDecodeUnit);
    videoCb.capabilities = CAPABILITY_DIRECT_SUBMIT;

    AUDIO_RENDERER_CALLBACKS audioCb;
    LiInitializeAudioCallbacks(&audioCb);
    audioCb.init = reinterpret_cast<AudioRendererInit>(&MoonlightBridge::OnAudioInit);
    audioCb.start = &MoonlightBridge::OnAudioStart;
    audioCb.stop = &MoonlightBridge::OnAudioStop;
    audioCb.cleanup = &MoonlightBridge::OnAudioCleanup;
    audioCb.decodeAndPlaySample = &MoonlightBridge::OnAudioDecodeAndPlay;
    audioCb.capabilities = CAPABILITY_DIRECT_SUBMIT | CAPABILITY_SUPPORTS_ARBITRARY_AUDIO_DURATION;

    CONNECTION_LISTENER_CALLBACKS connCb;
    LiInitializeConnectionCallbacks(&connCb);
    connCb.stageStarting = &MoonlightBridge::OnStageStarting;
    connCb.stageComplete = &MoonlightBridge::OnStageComplete;
    connCb.stageFailed = &MoonlightBridge::OnStageFailed;
    connCb.connectionStarted = &MoonlightBridge::OnConnectionStarted;
    connCb.connectionTerminated = &MoonlightBridge::OnConnectionTerminated;
    connCb.logMessage = &MoonlightBridge::OnLogMessage;

    active_.store(true);
    int rc = LiStartConnection(&serverInfo, &streamCfg, &connCb, &videoCb, &audioCb, this, 0, this, 0);
    if (rc != 0) {
        active_.store(false);
        char buf[128];
        std::snprintf(buf, sizeof(buf), "LiStartConnection failed (error=%d)", rc);
        outError = buf;
        return false;
    }
    return true;
#else
    (void)onTerminated;
    outError = "Built without moonlight-common-c";
    return false;
#endif
}

void MoonlightBridge::InterruptSession() {
    stopping_.store(true);
    connected_.store(false);
#ifdef BOBO_WITH_MOONLIGHT_COMMON_C
    LiInterruptConnection();
#endif
}

void MoonlightBridge::StopSession() {
    if (!active_.exchange(false)) return;
    stopping_.store(true);
    connected_.store(false);
#ifdef BOBO_WITH_MOONLIGHT_COMMON_C
    LiInterruptConnection();
    LiStopConnection();
#endif
    audio_.Shutdown();
    decoder_.CloseCodec();
    {
        std::lock_guard<std::mutex> lk(g_bridgeGlobalMutex);
        if (g_bridgeInstance == this) {
            g_bridgeInstance = nullptr;
        }
    }
}

StreamStats MoonlightBridge::SnapshotStats() {
    StreamStats st;
    auto now = std::chrono::steady_clock::now();
    {
        std::lock_guard<std::mutex> lk(statsMutex_);
        double elapsed = std::chrono::duration<double>(now - lastStatsWindow_).count();
        if (elapsed >= 0.35) {
            lastMeasuredFps_ = static_cast<double>(framesPresentedWindow_) / elapsed;
            if (timingSamples_ > 0) {
                lastDecodeMs_ = decodeMsAccum_ / static_cast<double>(timingSamples_);
                lastRenderMs_ = renderMsAccum_ / static_cast<double>(timingSamples_);
            }
            framesPresentedWindow_ = 0;
            decodeMsAccum_ = 0.0;
            renderMsAccum_ = 0.0;
            timingSamples_ = 0;
            lastStatsWindow_ = now;
        }
        st.fps = lastMeasuredFps_;
        st.decodeMs = lastDecodeMs_;
        st.renderMs = lastRenderMs_;
        st.codec = activeCodecName_;
    }

#ifdef BOBO_WITH_MOONLIGHT_COMMON_C
    if (connected_.load()) {
        uint32_t rtt = 0;
        uint32_t rttVar = 0;
        if (LiGetEstimatedRttInfo(&rtt, &rttVar)) {
            st.rttMs = static_cast<double>(rtt);
        }
    }
#endif
    st.bitrateKbps = activeCfg_.bitrateKbps;
    st.width = activeCfg_.width;
    st.height = activeCfg_.height;
    st.decoderName = decoder_.ActiveDecoderName().empty() ? "D3D11VA" : decoder_.ActiveDecoderName();
    return st;
}

char MoonlightBridge::CurrentModifiers() const {
#ifdef BOBO_WITH_MOONLIGHT_COMMON_C
    char mods = 0;
    if (GetAsyncKeyState(VK_SHIFT) & 0x8000) mods |= MODIFIER_SHIFT;
    if (GetAsyncKeyState(VK_CONTROL) & 0x8000) mods |= MODIFIER_CTRL;
    if (GetAsyncKeyState(VK_MENU) & 0x8000) mods |= MODIFIER_ALT;
    if ((GetAsyncKeyState(VK_LWIN) & 0x8000) || (GetAsyncKeyState(VK_RWIN) & 0x8000)) mods |= MODIFIER_META;
    return mods;
#else
    return 0;
#endif
}

void MoonlightBridge::SendMouseAbsolute(short x, short y, short refWidth, short refHeight) {
#ifdef BOBO_WITH_MOONLIGHT_COMMON_C
    if (!connected_.load() || refWidth <= 0 || refHeight <= 0) return;
    LiSendMousePositionEvent(x, y, refWidth, refHeight);
#else
    (void)x; (void)y; (void)refWidth; (void)refHeight;
#endif
}

void MoonlightBridge::SendMouseRelative(short dx, short dy) {
#ifdef BOBO_WITH_MOONLIGHT_COMMON_C
    if (!connected_.load()) return;
    if (dx != 0 || dy != 0) {
        LiSendMouseMoveEvent(dx, dy);
    }
#else
    (void)dx; (void)dy;
#endif
}

void MoonlightBridge::SendMouseButton(bool pressed, int button) {
#ifdef BOBO_WITH_MOONLIGHT_COMMON_C
    if (!connected_.load()) return;
    LiSendMouseButtonEvent(pressed ? BUTTON_ACTION_PRESS : BUTTON_ACTION_RELEASE, button);
#else
    (void)pressed; (void)button;
#endif
}

void MoonlightBridge::SendMouseScroll(short delta) {
#ifdef BOBO_WITH_MOONLIGHT_COMMON_C
    if (!connected_.load() || delta == 0) return;
    LiSendHighResScrollEvent(delta);
#else
    (void)delta;
#endif
}

void MoonlightBridge::SendMouseHScroll(short delta) {
#ifdef BOBO_WITH_MOONLIGHT_COMMON_C
    if (!connected_.load() || delta == 0) return;
    LiSendHighResHScrollEvent(delta);
#else
    (void)delta;
#endif
}

void MoonlightBridge::SendKey(uint16_t vk, bool pressed, bool extended) {
#ifdef BOBO_WITH_MOONLIGHT_COMMON_C
    if (!connected_.load() || vk == 0) return;
    char mods = CurrentModifiers();
    if (extended) mods |= MODIFIER_EXTENDED;
    short keyCode = static_cast<short>(0x8000 | (vk & 0xFF));
    LiSendKeyboardEvent(keyCode, pressed ? KEY_ACTION_DOWN : KEY_ACTION_UP, mods);
#else
    (void)vk; (void)pressed; (void)extended;
#endif
}

void MoonlightBridge::SendKeyCombo(const std::vector<uint32_t>& vks) {
#ifdef BOBO_WITH_MOONLIGHT_COMMON_C
    if (!connected_.load() || vks.empty()) return;
    char mods = 0;
    for (uint32_t vk : vks) {
        if (vk == VK_CONTROL || vk == VK_LCONTROL || vk == VK_RCONTROL) mods |= MODIFIER_CTRL;
        else if (vk == VK_SHIFT || vk == VK_LSHIFT || vk == VK_RSHIFT) mods |= MODIFIER_SHIFT;
        else if (vk == VK_MENU || vk == VK_LMENU || vk == VK_RMENU) mods |= MODIFIER_ALT;
        else if (vk == VK_LWIN || vk == VK_RWIN) mods |= MODIFIER_META;
        LiSendKeyboardEvent(static_cast<short>(0x8000 | (vk & 0xFF)), KEY_ACTION_DOWN, mods);
    }
    for (size_t i = vks.size(); i > 0; --i) {
        uint32_t vk = vks[i - 1];
        LiSendKeyboardEvent(static_cast<short>(0x8000 | (vk & 0xFF)), KEY_ACTION_UP, mods);
        if (vk == VK_CONTROL || vk == VK_LCONTROL || vk == VK_RCONTROL) mods &= static_cast<char>(~MODIFIER_CTRL);
        else if (vk == VK_SHIFT || vk == VK_LSHIFT || vk == VK_RSHIFT) mods &= static_cast<char>(~MODIFIER_SHIFT);
        else if (vk == VK_MENU || vk == VK_LMENU || vk == VK_RMENU) mods &= static_cast<char>(~MODIFIER_ALT);
        else if (vk == VK_LWIN || vk == VK_RWIN) mods &= static_cast<char>(~MODIFIER_META);
    }
#else
    (void)vks;
#endif
}

void MoonlightBridge::SendUtf8Text(const std::string& utf8) {
#ifdef BOBO_WITH_MOONLIGHT_COMMON_C
    if (!connected_.load() || utf8.empty()) return;
    LiSendUtf8TextEvent(utf8.c_str(), static_cast<unsigned int>(utf8.size()));
#else
    (void)utf8;
#endif
}

void MoonlightBridge::ReleaseAllModifiers() {
#ifdef BOBO_WITH_MOONLIGHT_COMMON_C
    if (!connected_.load()) return;
    const uint16_t mods[] = { VK_LSHIFT, VK_RSHIFT, VK_LCONTROL, VK_RCONTROL, VK_LMENU, VK_RMENU, VK_LWIN, VK_RWIN };
    for (uint16_t vk : mods) {
        LiSendKeyboardEvent(static_cast<short>(0x8000 | vk), KEY_ACTION_UP, 0);
    }
#endif
}

} // namespace bobo
