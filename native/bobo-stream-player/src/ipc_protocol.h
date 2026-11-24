#pragma once

#include <cstdint>
#include <string>

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
    std::string codec = "HEVC";
    std::string decoderName = "D3D11VA";
};

enum class IpcCommandType {
    None,
    Resize,
    SetHud,
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
};

std::string EscapeJson(const std::string& input);
std::string FormatReadyJson(const std::string& gpuName, bool d3d11Video, bool h264Hw, bool hevcHw, bool moonlightLinked);
std::string FormatStatsJson(const StreamStats& stats);
IpcCommand ParseIpcCommand(const std::string& line);
void EmitJsonLine(const std::string& json);

} // namespace bobo
