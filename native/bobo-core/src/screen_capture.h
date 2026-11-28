#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>

#include "bobo_core.h"

#include <cstdint>
#include <string>
#include <vector>

namespace bobo {

uint64_t GetLastRemoteMouseInjectMs();
bool IsPrivacyCursorMasked();
bool SyncThreadToInputDesktop(bool* outSwitched = nullptr);
void ResetThreadDesktopHandle();

constexpr int32_t kMaxCursorDimension = 128;
constexpr int32_t kDefaultCursorDimension = 32;
HCURSOR LoadStandardCursor(int32_t shapeId);
bool GetCursorBitmapSize(HCURSOR cursor, SIZE& size, POINT* hotspot = nullptr);

class ScreenCapturer {
public:
    ScreenCapturer() = default;
    ~ScreenCapturer();

    static std::vector<BoBoMonitorInfo> EnumerateMonitors();
    // Returns standard Windows cursor shape ID:
    // 0=Hidden, 1=Arrow, 2=IBeam, 3=Hand, 4=Wait, 5=AppStarting, 6=Cross,
    // 7=SizeNS, 8=SizeWE, 9=SizeNWSE, 10=SizeNESW, 11=SizeAll, 12=No, 13=Help
    static int32_t DetectStandardCursorShape(SIZE* bitmapSize = nullptr);

    bool Initialize(int monitorIndex, uint32_t outWidth, uint32_t outHeight, std::string& outError);
    void Shutdown();

    // Captures current desktop frame into an even-aligned NV12 CPU buffer (width=outW, height=outH, stride=outW).
    bool CaptureNv12Frame(int timeoutMs, std::vector<uint8_t>& outNv12, bool& outChanged, std::string& outError);

    // Low-latency unified capture supporting zero-copy GPU NV12 texture output (when preferGpuNv12 && !compositeCursor)
    // and cached-desktop cursor-only fast compositing (when compositeCursor == true).
    // When !forceProduce and neither desktop nor cursor changed, returns true with outChanged=false without GPU/CPU copy!
    bool CaptureFrame(int timeoutMs, bool preferGpuNv12, bool compositeCursor, bool forceProduce,
                      ID3D11Texture2D** outGpuNv12, std::vector<uint8_t>& outCpuNv12,
                      bool& outChanged, std::string& outError,
                      int targetGpuSlot = -1, int* outGpuSlot = nullptr,
                      bool* outDeviceResetRequested = nullptr);

    uint32_t OutputWidth() const { return outW_; }
    uint32_t OutputHeight() const { return outH_; }
    bool UsingDxgi() const { return dxgiReady_; }
    ID3D11Device* Device() const { return device_.Get(); }
    uint64_t DeviceGeneration() const { return deviceGen_; }
    const BoBoMonitorInfo& ActiveMonitor() const { return activeMonitor_; }

private:
    bool InitDxgiForMonitor(const BoBoMonitorInfo& mon);
    bool EnsureColorConverter(uint32_t srcW, uint32_t srcH);
    bool ConvertBgraTextureToGpuNv12(ID3D11Texture2D* bgraTex, ID3D11Texture2D** outGpuNv12,
                                     int targetGpuSlot = -1, int* outUsedGpuSlot = nullptr);
    bool ConvertBgraTextureToCpuNv12(ID3D11Texture2D* bgraTex, std::vector<uint8_t>& outNv12);
    bool CaptureViaGdi(std::vector<uint8_t>& outNv12);
    static void BgraBufferToNv12(const uint8_t* bgra, uint32_t srcW, uint32_t srcH, uint32_t srcStride,
                                 uint8_t* nv12Out, uint32_t dstW, uint32_t dstH);
    bool HasCursorMovedOrChanged(bool& outVisible, POINT& outPt, HCURSOR& outHandle) const;

    static constexpr int kGpuNv12Slots = 4;
    BoBoMonitorInfo activeMonitor_ = {};
    int monitorIndex_ = 0;
    uint32_t outW_ = 1920;
    uint32_t outH_ = 1080;
    bool dxgiReady_ = false;
    HRESULT lastInitDxgiHr_ = S_OK;
    bool hasFirstFrame_ = false;
    uint64_t deviceGen_ = 0;

    Microsoft::WRL::ComPtr<ID3D11Device> device_;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> context_;
    Microsoft::WRL::ComPtr<IDXGIOutputDuplication> duplication_;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> lastDesktopTex_;
    uint32_t lastDesktopW_ = 0;
    uint32_t lastDesktopH_ = 0;
    Microsoft::WRL::ComPtr<ID3D11VideoDevice> videoDevice_;
    Microsoft::WRL::ComPtr<ID3D11VideoContext> videoContext_;
    Microsoft::WRL::ComPtr<ID3D11VideoProcessorEnumerator> vpEnum_;
    Microsoft::WRL::ComPtr<ID3D11VideoProcessor> vp_;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> nv12GpuTex_[kGpuNv12Slots];
    int gpuSlot_ = 0;
    ID3D11Texture2D* lastProducedGpuTex_ = nullptr;
    int lastProducedGpuSlot_ = -1;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> nv12StagingTex_;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> bgraStagingTex_;
    uint32_t bgraStageW_ = 0;
    uint32_t bgraStageH_ = 0;
    uint32_t vpSrcW_ = 0;
    uint32_t vpSrcH_ = 0;
    std::vector<uint8_t> lastCleanNv12_;

    // Reusable GDI fallback resources
    HDC memDc_ = nullptr;
    HBITMAP dibBmp_ = nullptr;
    uint8_t* dibBits_ = nullptr;
    uint32_t dibW_ = 0;
    uint32_t dibH_ = 0;

    // Reusable Host cursor capture & NV12 compositing cache (used in Locked mode or legacy clients)
    void UpdateCursorShapeCache(HCURSOR hCur);
    void BuildFallbackArrowCursor();
    void CompositeHostCursorIntoNv12(std::vector<uint8_t>& nv12);
    static void BlendRgbaCursorToNv12(
        std::vector<uint8_t>& nv12, uint32_t dstW, uint32_t dstH,
        const uint8_t* rgba, const uint8_t* xorMask, int cw, int ch,
        int drawX, int drawY, int drawW, int drawH);

    HCURSOR cachedCursor_ = nullptr;
    int cursorW_ = 0;
    int cursorH_ = 0;
    int cursorHotX_ = 0;
    int cursorHotY_ = 0;
    std::vector<uint8_t> cursorRgba_;
    std::vector<uint8_t> cursorXor_;
    POINT lastCursorPos_ = { -99999, -99999 };
    HCURSOR lastCursorHandle_ = nullptr;
    bool lastCursorVisible_ = false;
};

} // namespace bobo
