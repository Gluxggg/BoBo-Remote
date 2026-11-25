#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace bobo {

struct StreamStats {
    double fps = 0.0;
    double decodeMs = 0.0;
    double renderMs = 0.0;
    double rttMs = 0.0;
    double packetLossPct = 0.0;
    uint32_t bitrateKbps = 0;
    uint32_t width = 0;
    uint32_t height = 0;
    std::string codec = "H264";
    std::string decoderName = "D3D11VA";
};

enum class IpcCommandType {
    None,
    Resize,
    SetHud,
    SetMouseMode,
    SendKeys,
    SendText,
    SwitchQuality,
    TogglePrivacy,
    Disconnect
};

struct IpcCommand {
    IpcCommandType type = IpcCommandType::None;
    uint32_t width = 0;
    uint32_t height = 0;
    uint32_t fps = 0;
    uint32_t bitrateKbps = 0;
    bool flag = false;
    std::vector<uint32_t> vks;
    std::string text;
};

std::string EscapeJson(const std::string& input);
std::string FormatReadyJson(const std::string& gpuName, bool d3d11Video, bool h264Hw, bool hevcHw, bool moonlightLinked);
std::string FormatStatsJson(const StreamStats& stats);
std::string FormatConnectedJson(uint32_t width, uint32_t height, uint32_t fps, const std::string& codec, const std::string& decoderName);
std::string FormatStageJson(const std::string& stage, const std::string& status, int errorCode);
std::string FormatTerminatedJson(int code, const std::string& reason);
std::string FormatMouseModeJson(bool freeMouse);
IpcCommand ParseIpcCommand(const std::string& line);
void EmitJsonLine(const std::string& json);

} // namespace bobo
