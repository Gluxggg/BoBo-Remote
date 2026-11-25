#include "ipc_protocol.h"

#include <cstdio>
#include <cstdlib>
#include <iostream>
#include <mutex>
#include <sstream>

namespace bobo {

static std::mutex g_stdoutMutex;

std::string EscapeJson(const std::string& input) {
    std::string out;
    out.reserve(input.size() + 8);
    for (unsigned char c : input) {
        switch (c) {
            case '\"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\b': out += "\\b"; break;
            case '\f': out += "\\f"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (c < 0x20) {
                    char buf[8];
                    std::snprintf(buf, sizeof(buf), "\\u%04x", static_cast<unsigned int>(c));
                    out += buf;
                } else {
                    out.push_back(static_cast<char>(c));
                }
                break;
        }
    }
    return out;
}

std::string FormatReadyJson(const std::string& gpuName, bool d3d11Video, bool h264Hw, bool hevcHw, bool moonlightLinked) {
    std::ostringstream os;
    os << "{\"event\":\"ready\""
       << ",\"gpu\":\"" << EscapeJson(gpuName) << "\""
       << ",\"d3d11Video\":" << (d3d11Video ? "true" : "false")
       << ",\"h264Hw\":" << (h264Hw ? "true" : "false")
       << ",\"hevcHw\":" << (hevcHw ? "true" : "false")
       << ",\"moonlightCommonC\":" << (moonlightLinked ? "true" : "false")
       << "}";
    return os.str();
}

std::string FormatStatsJson(const StreamStats& stats) {
    char numBuf[256];
    std::snprintf(
        numBuf,
        sizeof(numBuf),
        "{\"event\":\"stats\",\"fps\":%.1f,\"decodeMs\":%.2f,\"renderMs\":%.2f,\"rttMs\":%.1f,"
        "\"packetLossPct\":%.2f,\"bitrateKbps\":%u,\"width\":%u,\"height\":%u",
        stats.fps,
        stats.decodeMs,
        stats.renderMs,
        stats.rttMs,
        stats.packetLossPct,
        stats.bitrateKbps,
        stats.width,
        stats.height);
    std::ostringstream os;
    os << numBuf
       << ",\"codec\":\"" << EscapeJson(stats.codec) << "\""
       << ",\"decoder\":\"" << EscapeJson(stats.decoderName) << "\"}";
    return os.str();
}

std::string FormatConnectedJson(uint32_t width, uint32_t height, uint32_t fps, const std::string& codec, const std::string& decoderName) {
    std::ostringstream os;
    os << "{\"event\":\"connected\""
       << ",\"width\":" << width
       << ",\"height\":" << height
       << ",\"fps\":" << fps
       << ",\"codec\":\"" << EscapeJson(codec) << "\""
       << ",\"decoder\":\"" << EscapeJson(decoderName) << "\"}";
    return os.str();
}

std::string FormatStageJson(const std::string& stage, const std::string& status, int errorCode) {
    std::ostringstream os;
    os << "{\"event\":\"stage\""
       << ",\"stage\":\"" << EscapeJson(stage) << "\""
       << ",\"status\":\"" << EscapeJson(status) << "\""
       << ",\"errorCode\":" << errorCode << "}";
    return os.str();
}

std::string FormatTerminatedJson(int code, const std::string& reason) {
    std::ostringstream os;
    os << "{\"event\":\"terminated\""
       << ",\"code\":" << code
       << ",\"reason\":\"" << EscapeJson(reason) << "\"}";
    return os.str();
}

std::string FormatMouseModeJson(bool freeMouse) {
    return std::string("{\"event\":\"mouse_mode\",\"free\":") + (freeMouse ? "true" : "false") + "}";
}

static bool ExtractUintField(const std::string& line, const char* key, uint32_t& outVal) {
    std::string pattern = std::string("\"") + key + "\":";
    size_t pos = line.find(pattern);
    if (pos == std::string::npos) return false;
    pos += pattern.size();
    while (pos < line.size() && (line[pos] == ' ' || line[pos] == '\t')) ++pos;
    if (pos >= line.size() || line[pos] < '0' || line[pos] > '9') return false;
    char* endPtr = nullptr;
    unsigned long v = std::strtoul(line.c_str() + pos, &endPtr, 10);
    if (endPtr == line.c_str() + pos) return false;
    outVal = static_cast<uint32_t>(v);
    return true;
}

static bool ExtractBoolField(const std::string& line, const char* key, bool& outVal) {
    std::string pattern = std::string("\"") + key + "\":";
    size_t pos = line.find(pattern);
    if (pos == std::string::npos) return false;
    pos += pattern.size();
    while (pos < line.size() && (line[pos] == ' ' || line[pos] == '\t')) ++pos;
    if (line.compare(pos, 4, "true") == 0) { outVal = true; return true; }
    if (line.compare(pos, 5, "false") == 0) { outVal = false; return true; }
    return false;
}

static bool ExtractUintArrayField(const std::string& line, const char* key, std::vector<uint32_t>& outVals) {
    outVals.clear();
    std::string pattern = std::string("\"") + key + "\":[";
    size_t pos = line.find(pattern);
    if (pos == std::string::npos) return false;
    pos += pattern.size();
    size_t end = line.find(']', pos);
    if (end == std::string::npos) return false;
    while (pos < end) {
        while (pos < end && (line[pos] == ' ' || line[pos] == ',' || line[pos] == '\t')) ++pos;
        if (pos >= end) break;
        char* endPtr = nullptr;
        unsigned long v = std::strtoul(line.c_str() + pos, &endPtr, 10);
        if (endPtr == line.c_str() + pos) break;
        outVals.push_back(static_cast<uint32_t>(v));
        pos = static_cast<size_t>(endPtr - line.c_str());
    }
    return !outVals.empty();
}

static bool ExtractStringField(const std::string& line, const char* key, std::string& outVal) {
    outVal.clear();
    std::string pattern = std::string("\"") + key + "\":\"";
    size_t pos = line.find(pattern);
    if (pos == std::string::npos) return false;
    pos += pattern.size();
    while (pos < line.size()) {
        char c = line[pos++];
        if (c == '\"') return true;
        if (c == '\\' && pos < line.size()) {
            char esc = line[pos++];
            switch (esc) {
                case '\"': outVal.push_back('\"'); break;
                case '\\': outVal.push_back('\\'); break;
                case 'n': outVal.push_back('\n'); break;
                case 'r': outVal.push_back('\r'); break;
                case 't': outVal.push_back('\t'); break;
                default: outVal.push_back(esc); break;
            }
        } else {
            outVal.push_back(c);
        }
    }
    return false;
}

IpcCommand ParseIpcCommand(const std::string& line) {
    IpcCommand cmd;
    if (line.find("\"cmd\":\"disconnect\"") != std::string::npos) {
        cmd.type = IpcCommandType::Disconnect;
    } else if (line.find("\"cmd\":\"toggle_privacy\"") != std::string::npos) {
        cmd.type = IpcCommandType::TogglePrivacy;
    } else if (line.find("\"cmd\":\"set_hud\"") != std::string::npos) {
        cmd.type = IpcCommandType::SetHud;
        ExtractBoolField(line, "enabled", cmd.flag);
    } else if (line.find("\"cmd\":\"set_mouse_mode\"") != std::string::npos) {
        cmd.type = IpcCommandType::SetMouseMode;
        ExtractBoolField(line, "free", cmd.flag);
    } else if (line.find("\"cmd\":\"send_keys\"") != std::string::npos) {
        cmd.type = IpcCommandType::SendKeys;
        ExtractUintArrayField(line, "vks", cmd.vks);
    } else if (line.find("\"cmd\":\"send_text\"") != std::string::npos) {
        cmd.type = IpcCommandType::SendText;
        ExtractStringField(line, "text", cmd.text);
    } else if (line.find("\"cmd\":\"resize\"") != std::string::npos) {
        cmd.type = IpcCommandType::Resize;
        ExtractUintField(line, "width", cmd.width);
        ExtractUintField(line, "height", cmd.height);
    } else if (line.find("\"cmd\":\"switch_quality\"") != std::string::npos) {
        cmd.type = IpcCommandType::SwitchQuality;
        ExtractUintField(line, "width", cmd.width);
        ExtractUintField(line, "height", cmd.height);
        ExtractUintField(line, "fps", cmd.fps);
        ExtractUintField(line, "bitrateKbps", cmd.bitrateKbps);
    }
    return cmd;
}

void EmitJsonLine(const std::string& json) {
    std::lock_guard<std::mutex> lock(g_stdoutMutex);
    std::cout << json << "\n" << std::flush;
}

} // namespace bobo
