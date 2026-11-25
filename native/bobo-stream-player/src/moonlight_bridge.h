#pragma once

#include "d3d11_renderer.h"
#include "ipc_protocol.h"
#include "mf_decoder.h"
#include "wasapi_audio.h"

#include <atomic>
#include <chrono>
#include <cstdint>
#include <functional>
#include <mutex>
#include <string>
#include <vector>

namespace bobo {

struct StreamSessionConfig {
    std::string host;
    std::string rtspUrl;
    std::string appVersion = "7.1.431.-1";
    std::string gfeVersion = "3.23.0.74";
    int serverCodecModeSupport = 1;
    uint32_t width = 1920;
    uint32_t height = 1080;
    uint32_t fps = 60;
    uint32_t bitrateKbps = 15000;
    VideoCodec codec = VideoCodec::H264;
    bool enableAudio = true;
    uint8_t riKey[16] = {};
    uint32_t riKeyId = 0;
};

class MoonlightBridge {
public:
    MoonlightBridge(D3D11Renderer& renderer, MfHardwareDecoder& decoder);
    ~MoonlightBridge();

    static bool IsMoonlightCommonCLinked();
    static std::string GetLaunchQueryParameters();

    bool StartSession(const StreamSessionConfig& cfg,
                      std::function<void(int code, const std::string& reason)> onTerminated,
                      std::string& outError);
    void InterruptSession();
    void StopSession();
    bool IsActive() const { return active_.load(); }

    StreamStats SnapshotStats();

    void SendMouseAbsolute(short x, short y, short refWidth, short refHeight);
    void SendMouseRelative(short dx, short dy);
    void SendMouseButton(bool pressed, int button);
    void SendMouseScroll(short delta);
    void SendMouseHScroll(short delta);
    void SendKey(uint16_t vk, bool pressed, bool extended);
    void SendKeyCombo(const std::vector<uint32_t>& vks);
    void SendUtf8Text(const std::string& utf8);
    void ReleaseAllModifiers();

private:
    static int OnVideoSetup(int videoFormat, int width, int height, int redrawRate, void* context, int drFlags);
    static void OnVideoStart();
    static void OnVideoStop();
    static void OnVideoCleanup();
    static int OnVideoSubmitDecodeUnit(void* decodeUnitPtr);

    static int OnAudioInit(int audioConfiguration, const void* opusConfigPtr, void* context, int arFlags);
    static void OnAudioStart();
    static void OnAudioStop();
    static void OnAudioCleanup();
    static void OnAudioDecodeAndPlay(char* sampleData, int sampleLength);

    static void OnStageStarting(int stage);
    static void OnStageComplete(int stage);
    static void OnStageFailed(int stage, int errorCode);
    static void OnConnectionStarted();
    static void OnConnectionTerminated(int errorCode);
    static void OnLogMessage(const char* format, ...);

    char CurrentModifiers() const;

    D3D11Renderer& renderer_;
    MfHardwareDecoder& decoder_;
    WasapiAudioPlayer audio_;
    std::atomic<bool> active_{ false };
    std::atomic<bool> connected_{ false };
    std::atomic<bool> stopping_{ false };
    std::function<void(int, const std::string&)> onTerminated_;

    StreamSessionConfig activeCfg_;
    std::vector<uint8_t> frameAssembleBuf_;

    std::mutex statsMutex_;
    uint64_t framesPresentedWindow_ = 0;
    double decodeMsAccum_ = 0.0;
    double renderMsAccum_ = 0.0;
    uint64_t timingSamples_ = 0;
    std::chrono::steady_clock::time_point lastStatsWindow_ = std::chrono::steady_clock::now();
    double lastMeasuredFps_ = 0.0;
    double lastDecodeMs_ = 0.0;
    double lastRenderMs_ = 0.0;
    std::string activeCodecName_ = "H264";
};

} // namespace bobo
