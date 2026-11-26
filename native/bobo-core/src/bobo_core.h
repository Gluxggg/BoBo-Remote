#pragma once

#include <stdint.h>

#ifdef BOBOCORE_EXPORTS
#define BOBO_API __declspec(dllexport)
#else
#define BOBO_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

#pragma pack(push, 1)

typedef struct BoBoMonitorInfo {
    int32_t index;
    int32_t left;
    int32_t top;
    int32_t width;
    int32_t height;
    int32_t isPrimary;
    char name[64];
} BoBoMonitorInfo;

typedef struct BoBoClientStats {
    double fps;
    double decodeMs;
    double renderMs;
    int32_t width;
    int32_t height;
    char decoderName[64];
} BoBoClientStats;

typedef struct BoBoInputPacket {
    uint8_t kind;     // 1=MouseAbs, 2=MouseRel, 3=MouseBtn, 4=MouseWheel, 5=Key, 6=ReleaseAll
    uint8_t flags;    // down/extended/horizontal
    int16_t a;        // x(0..65535) / dx / button / delta / vk
    int16_t b;        // y(0..65535) / dy
} BoBoInputPacket;

#pragma pack(pop)

typedef void(__stdcall* BoBoVideoFrameCallback)(
    const uint8_t* data,
    int32_t length,
    int32_t isKeyframe,
    int32_t width,
    int32_t height,
    uint32_t timestampMs,
    void* userCtx);

typedef void(__stdcall* BoBoAudioFrameCallback)(
    const uint8_t* data,
    int32_t length,
    int32_t sampleRate,
    int32_t channels,
    int32_t sampleCount,
    void* userCtx);

typedef void(__stdcall* BoBoClientInputCallback)(
    const BoBoInputPacket* pkt,
    void* userCtx);

typedef void(__stdcall* BoBoClientHotkeyCallback)(
    int32_t hotkeyCode, // 1=ReleaseMouse, 2=Toolbar, 3=ToggleFullscreen, 4=ToggleFloatBall, 5=ToggleMini, 6=NextDisplay, 7=Disconnect, 8=EdgeTop
    int32_t param,
    void* userCtx);

typedef void(__stdcall* BoBoPrivacyEscapeCallback)(void* userCtx);

// ---- Self-Test & Capabilities ----
BOBO_API int32_t __stdcall BoBoCore_SelfTest(char* reportBuf, int32_t reportBufLen);
BOBO_API int32_t __stdcall BoBoCore_GetMonitorCount(void);
BOBO_API int32_t __stdcall BoBoCore_GetMonitorInfo(int32_t index, BoBoMonitorInfo* outInfo);

// ---- Host Engine (In-Process Capture + H.264 Encode + WASAPI Loopback + SendInput) ----
BOBO_API void* __stdcall BoBoHost_Create(
    int32_t monitorIndex,
    int32_t targetWidth,
    int32_t targetHeight,
    int32_t fps,
    int32_t bitrateKbps,
    int32_t muteHostSpeakers,
    BoBoVideoFrameCallback onVideo,
    BoBoAudioFrameCallback onAudio,
    void* userCtx);

BOBO_API void* __stdcall BoBoHost_CreateEx(
    int32_t monitorIndex,
    int32_t targetWidth,
    int32_t targetHeight,
    int32_t fps,
    int32_t bitrateKbps,
    int32_t muteHostSpeakers,
    int32_t compositeCursor,
    BoBoVideoFrameCallback onVideo,
    BoBoAudioFrameCallback onAudio,
    void* userCtx);

BOBO_API int32_t __stdcall BoBoHost_Reconfigure(
    void* handle,
    int32_t monitorIndex,
    int32_t targetWidth,
    int32_t targetHeight,
    int32_t fps,
    int32_t bitrateKbps);

BOBO_API int32_t __stdcall BoBoHost_SetBitrate(void* handle, int32_t bitrateKbps);
BOBO_API int32_t __stdcall BoBoHost_SetFps(void* handle, int32_t fps);
BOBO_API void __stdcall BoBoHost_SetCompositeCursor(void* handle, int32_t enable);
BOBO_API int32_t __stdcall BoBoHost_GetCursorShape(int32_t monitorIndex);
// Returns the logical shape ID and, when available, its desktop bitmap size in pixels.
BOBO_API int32_t __stdcall BoBoHost_GetCursorInfo(int32_t monitorIndex, int32_t* width, int32_t* height);

BOBO_API void __stdcall BoBoHost_RequestKeyframe(void* handle);
BOBO_API void __stdcall BoBoHost_SetMuteHost(void* handle, int32_t mute);
BOBO_API void __stdcall BoBoHost_Destroy(void* handle);

BOBO_API void __stdcall BoBoHost_InjectInput(int32_t monitorIndex, const BoBoInputPacket* pkt);
BOBO_API void __stdcall BoBoHost_InjectUnicodeText(const wchar_t* text, int32_t length);

// ---- Host Super Display (Adaptive Aspect Resolution Switch & Safe Auto-Restore) ----
BOBO_API int32_t __stdcall BoBoHost_ApplySuperDisplay(
    int32_t monitorIndex,
    int32_t clientWidth,
    int32_t clientHeight,
    int32_t* outActualWidth,
    int32_t* outActualHeight);
BOBO_API void __stdcall BoBoHost_RestoreSuperDisplay(void);

// ---- Host Hardware Physical Input Blocker (Privacy Shield: blocks local mouse/kb, allows tagged remote SendInput & Ctrl+Alt+Shift+P) ----
BOBO_API int32_t __stdcall BoBoHost_SetPrivacyInputBlock(
    int32_t enable,
    BoBoPrivacyEscapeCallback onEscape,
    void* userCtx);
BOBO_API int32_t __stdcall BoBoHost_IsPrivacyInputBlocked(void);
// Temporarily masks the hardware cursor on the host display. Capture's cursor path remains independent.
BOBO_API int32_t __stdcall BoBoHost_SetPrivacyCursorMask(int32_t enable);

// ---- Client Engine (In-Process D3D11VA H.264 Decode + D3D11 Render + WASAPI Audio + Viewport Input) ----
BOBO_API void* __stdcall BoBoClient_Create(
    void* parentHwnd,
    int32_t videoWidth,
    int32_t videoHeight,
    int32_t freeMouse,
    BoBoClientInputCallback onInput,
    BoBoClientHotkeyCallback onHotkey,
    void* userCtx);

BOBO_API int32_t __stdcall BoBoClient_PushVideo(
    void* handle,
    const uint8_t* h264Data,
    int32_t length,
    int32_t isKeyframe,
    int32_t srcWidth,
    int32_t srcHeight);

BOBO_API void __stdcall BoBoClient_PushAudio(
    void* handle,
    const uint8_t* adpcmData,
    int32_t length,
    int32_t sampleRate,
    int32_t channels,
    int32_t sampleCount);

BOBO_API void __stdcall BoBoClient_Resize(void* handle, int32_t width, int32_t height);
BOBO_API void __stdcall BoBoClient_Focus(void* handle);
BOBO_API void __stdcall BoBoClient_SetMouseMode(void* handle, int32_t freeMouse);
BOBO_API void __stdcall BoBoClient_SetVolume(void* handle, int32_t volumePercent);
BOBO_API void __stdcall BoBoClient_SetScaleMode(void* handle, int32_t scaleMode); // 0=1:1, 1=AspectFit, 2=SuperFill(zero black bars)
BOBO_API void __stdcall BoBoClient_SetViewOnly(void* handle, int32_t viewOnly);
BOBO_API void __stdcall BoBoClient_SetSysKeys(void* handle, int32_t sendSysKeys);
BOBO_API void __stdcall BoBoClient_SetHostCursorComposited(void* handle, int32_t composited);
BOBO_API void __stdcall BoBoClient_SetRemoteCursorShape(void* handle, int32_t shapeId);
BOBO_API void __stdcall BoBoClient_SetRemoteCursorInfo(void* handle, int32_t shapeId, int32_t width, int32_t height);
BOBO_API void __stdcall BoBoClient_GetStats(void* handle, BoBoClientStats* outStats);
BOBO_API void __stdcall BoBoClient_Destroy(void* handle);

#ifdef __cplusplus
}
#endif
