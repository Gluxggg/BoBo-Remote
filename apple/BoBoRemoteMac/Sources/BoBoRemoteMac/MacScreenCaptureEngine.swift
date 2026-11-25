import AppKit
import AVFoundation
import BoBoAppleCore
import CoreMedia
import Foundation
import ScreenCaptureKit
import VideoToolbox

/// macOS 原生 ScreenCaptureKit + VideoToolbox H.264 硬件采编与免驱动系统音频采集引擎。
/// - 自动排除 BoBo 防窥黑屏遮罩窗口（远端主控仍看到完整清晰桌面，机前完全黑屏防窥）
/// - 原生包含主机光标（showsCursor = true）
/// - 硬件编码产出 H.264 Annex-B（每个 IDR 前自动附带 SPS+PPS），48kHz 立体声音频经 IMA-ADPCM 编码推送
public final class MacScreenCaptureEngine: NSObject, SCStreamOutput, @unchecked Sendable {
    private let videoQueue = DispatchQueue(label: "com.bobo.remote.mac.capture.video", qos: .userInteractive)
    private let audioQueue = DispatchQueue(label: "com.bobo.remote.mac.capture.audio", qos: .userInteractive)
    private let lock = NSLock()

    private var stream: SCStream?
    private var compressionSession: VTCompressionSession?
    private var startTimeMs: UInt64 = 0
    private var encodeWidth: Int = 1920
    private var encodeHeight: Int = 1080
    private var forceNextKeyframe = false
    private var audioPcmScratch: [Int16] = []

    public var onEncodedVideo: ((Int, Int, Bool, Data, UInt32) -> Void)?
    public var onEncodedAudio: ((Int, Int, Int, Data) -> Void)?

    public func enumerateMonitors() async -> [BoBoMonitorDescriptor] {
        guard let content = try? await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true) else {
            return [BoBoMonitorDescriptor()]
        }
        return content.displays.enumerated().map { idx, disp in
            BoBoMonitorDescriptor(
                index: idx,
                name: "Display \(idx + 1) (\(disp.width)x\(disp.height))",
                width: disp.width,
                height: disp.height,
                isPrimary: idx == 0
            )
        }
    }

    public func start(
        monitorIndex: Int,
        width: Int,
        height: Int,
        fps: Int,
        bitrateKbps: Int,
        excludeWindowIDs: Set<CGWindowID>
    ) async throws {
        await stop()

        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard !content.displays.isEmpty else {
            throw NSError(domain: "MacCapture", code: -1, userInfo: [NSLocalizedDescriptionKey: "未找到可采集显示器"])
        }
        let idx = max(0, min(monitorIndex, content.displays.count - 1))
        let display = content.displays[idx]

        let targetW = max(640, min(width > 0 ? width : display.width, 3840)) & ~1
        let targetH = max(360, min(height > 0 ? height : display.height, 2160)) & ~1
        let targetFps = max(15, min(fps, 120))

        let excluded = content.windows.filter { excludeWindowIDs.contains($0.windowID) }
        let filter = SCContentFilter(display: display, excludingWindows: excluded)

        let cfg = SCStreamConfiguration()
        cfg.width = targetW
        cfg.height = targetH
        cfg.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(targetFps))
        cfg.pixelFormat = kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
        cfg.showsCursor = true
        cfg.capturesAudio = true
        cfg.sampleRate = 48000
        cfg.channelCount = 2
        cfg.queueDepth = 3

        try createEncoder(width: targetW, height: targetH, fps: targetFps, bitrateKbps: bitrateKbps)

        lock.lock()
        encodeWidth = targetW
        encodeHeight = targetH
        startTimeMs = DispatchTime.now().uptimeNanoseconds / 1_000_000
        forceNextKeyframe = true
        audioPcmScratch.removeAll(keepingCapacity: true)
        lock.unlock()

        let scStream = SCStream(filter: filter, configuration: cfg, delegate: nil)
        try scStream.addStreamOutput(self, type: .screen, sampleHandlerQueue: videoQueue)
        try scStream.addStreamOutput(self, type: .audio, sampleHandlerQueue: audioQueue)
        try await scStream.startCapture()

        lock.lock()
        stream = scStream
        lock.unlock()
    }

    public func updateExcludedWindows(_ excludeWindowIDs: Set<CGWindowID>, monitorIndex: Int = 0) async {
        lock.lock()
        let curStream = stream
        lock.unlock()
        guard let curStream else { return }
        guard let content = try? await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true),
              !content.displays.isEmpty else { return }
        let idx = max(0, min(monitorIndex, content.displays.count - 1))
        let excluded = content.windows.filter { excludeWindowIDs.contains($0.windowID) }
        let filter = SCContentFilter(display: content.displays[idx], excludingWindows: excluded)
        try? await curStream.updateContentFilter(filter)
    }

    public func requestKeyframe() {
        lock.lock()
        forceNextKeyframe = true
        lock.unlock()
    }

    public func stop() async {
        lock.lock()
        let curStream = stream
        stream = nil
        let sess = compressionSession
        compressionSession = nil
        lock.unlock()

        if let curStream {
            try? await curStream.stopCapture()
        }
        if let sess {
            VTCompressionSessionInvalidate(sess)
        }
    }

    // MARK: - SCStreamOutput

    public func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard CMSampleBufferIsValid(sampleBuffer) else { return }
        switch type {
        case .screen:
            handleVideoSample(sampleBuffer)
        case .audio:
            handleAudioSample(sampleBuffer)
        @unknown default:
            break
        }
    }

    private func handleVideoSample(_ sampleBuffer: CMSampleBuffer) {
        guard let pixelBuf = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        lock.lock()
        guard let sess = compressionSession else { lock.unlock(); return }
        let forceKey = forceNextKeyframe
        forceNextKeyframe = false
        lock.unlock()

        let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        var props: CFDictionary?
        if forceKey {
            props = [kVTEncodeFrameOptionKey_ForceKeyFrame: kCFBooleanTrue] as CFDictionary
        }
        VTCompressionSessionEncodeFrame(sess, imageBuffer: pixelBuf, presentationTimeStamp: pts, duration: .invalid, frameProperties: props, sourceFrameRefcon: nil, infoFlagsOut: nil)
    }

    private func handleAudioSample(_ sampleBuffer: CMSampleBuffer) {
        guard let fmtDesc = CMSampleBufferGetFormatDescription(sampleBuffer),
              let asbdPtr = CMAudioFormatDescriptionGetStreamBasicDescription(fmtDesc) else { return }
        let asbd = asbdPtr.pointee
        let numFrames = CMSampleBufferGetNumSamples(sampleBuffer)
        guard numFrames > 0 else { return }

        var blockBuf: CMBlockBuffer?
        var abl = AudioBufferList()
        let status = CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
            sampleBuffer,
            bufferListSizeNeededOut: nil,
            bufferListOut: &abl,
            bufferListSize: MemoryLayout<AudioBufferList>.size,
            blockBufferAllocator: nil,
            blockBufferMemoryAllocator: nil,
            flags: 0,
            blockBufferOut: &blockBuf
        )
        guard status == noErr else { return }

        let isFloat = (asbd.mFormatFlags & kAudioFormatFlagIsFloat) != 0
        let isNonInterleaved = (asbd.mFormatFlags & kAudioFormatFlagIsNonInterleaved) != 0
        let channels = max(1, Int(asbd.mChannelsPerFrame))

        var interleaved = [Int16](repeating: 0, count: numFrames * 2)
        if isFloat && !isNonInterleaved, let rawPtr = abl.mBuffers.mData {
            let fPtr = rawPtr.assumingMemoryBound(to: Float.self)
            for i in 0..<numFrames {
                let l = fPtr[i * channels]
                let r = channels >= 2 ? fPtr[i * channels + 1] : l
                interleaved[i * 2] = Int16(max(-32768.0, min(32767.0, l * 32767.0)))
                interleaved[i * 2 + 1] = Int16(max(-32768.0, min(32767.0, r * 32767.0)))
            }
        } else if isFloat && isNonInterleaved, let rawPtr = abl.mBuffers.mData {
            let fPtr = rawPtr.assumingMemoryBound(to: Float.self)
            for i in 0..<numFrames {
                let s = Int16(max(-32768.0, min(32767.0, fPtr[i] * 32767.0)))
                interleaved[i * 2] = s
                interleaved[i * 2 + 1] = s
            }
        } else {
            return
        }

        lock.lock()
        audioPcmScratch.append(contentsOf: interleaved)
        let chunkFrames = 480 // 10ms @ 48kHz
        while audioPcmScratch.count >= chunkFrames * 2 {
            let slice = Array(audioPcmScratch.prefix(chunkFrames * 2))
            audioPcmScratch.removeFirst(chunkFrames * 2)
            let adpcm = ImaAdpcmCodec.encodeStereo(slice, sampleFrames: chunkFrames)
            let cb = onEncodedAudio
            lock.unlock()
            cb?(48000, 2, chunkFrames, adpcm)
            lock.lock()
        }
        lock.unlock()
    }

    // MARK: - VideoToolbox H.264 Encoder

    private func createEncoder(width: Int, height: Int, fps: Int, bitrateKbps: Int) throws {
        let callback: VTCompressionOutputCallback = { refcon, _, status, _, sampleBuffer in
            guard status == noErr, let refcon, let sb = sampleBuffer else { return }
            let engine = Unmanaged<MacScreenCaptureEngine>.fromOpaque(refcon).takeUnretainedValue()
            engine.emitEncodedSample(sb)
        }

        var sess: VTCompressionSession?
        let status = VTCompressionSessionCreate(
            allocator: kCFAllocatorDefault,
            width: Int32(width),
            height: Int32(height),
            codecType: kCMVideoCodecType_H264,
            encoderSpecification: nil,
            imageBufferAttributes: nil,
            compressedDataAllocator: nil,
            outputCallback: callback,
            refcon: Unmanaged.passUnretained(self).toOpaque(),
            compressionSessionOut: &sess
        )
        guard status == noErr, let session = sess else {
            throw NSError(domain: "MacCapture", code: Int(status), userInfo: [NSLocalizedDescriptionKey: "VideoToolbox H.264 编码器初始化失败"])
        }

        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_RealTime, value: kCFBooleanTrue)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_ProfileLevel, value: kVTProfileLevel_H264_High_AutoLevel)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AllowFrameReordering, value: kCFBooleanFalse)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_ExpectedFrameRate, value: NSNumber(value: fps))
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_MaxKeyFrameInterval, value: NSNumber(value: fps * 2))
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AverageBitRate, value: NSNumber(value: bitrateKbps * 1000))
        VTCompressionSessionPrepareToEncodeFrames(session)

        lock.lock()
        compressionSession = session
        lock.unlock()
    }

    private func emitEncodedSample(_ sampleBuffer: CMSampleBuffer) {
        var isKeyframe = true
        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[CFString: Any]],
           let first = attachments.first,
           let notSync = first[kCMSampleAttachmentKey_NotSync] as? Bool {
            isKeyframe = !notSync
        }

        var annexB = Data()
        let startCode = Data([0x00, 0x00, 0x00, 0x01])

        // Always prepend SPS + PPS before IDR keyframes per BOBO6-SPEC
        if isKeyframe, let fmtDesc = CMSampleBufferGetFormatDescription(sampleBuffer) {
            var spsPtr: UnsafePointer<UInt8>?
            var spsLen = 0
            var ppsPtr: UnsafePointer<UInt8>?
            var ppsLen = 0
            if CMVideoFormatDescriptionGetH264ParameterSetAtIndex(fmtDesc, parameterSetIndex: 0, parameterSetPointerOut: &spsPtr, parameterSetSizeOut: &spsLen, parameterSetCountOut: nil, nalUnitHeaderLengthOut: nil) == noErr,
               let spsPtr, spsLen > 0 {
                annexB.append(startCode)
                annexB.append(spsPtr, count: spsLen)
            }
            if CMVideoFormatDescriptionGetH264ParameterSetAtIndex(fmtDesc, parameterSetIndex: 1, parameterSetPointerOut: &ppsPtr, parameterSetSizeOut: &ppsLen, parameterSetCountOut: nil, nalUnitHeaderLengthOut: nil) == noErr,
               let ppsPtr, ppsLen > 0 {
                annexB.append(startCode)
                annexB.append(ppsPtr, count: ppsLen)
            }
        }

        guard let dataBuf = CMSampleBufferGetDataBuffer(sampleBuffer) else { return }
        var totalLen = 0
        var dataPtr: UnsafeMutablePointer<Int8>?
        guard CMBlockBufferGetDataPointer(dataBuf, atOffset: 0, lengthAtOffsetOut: nil, totalLengthOut: &totalLen, dataPointerOut: &dataPtr) == kCMBlockBufferNoErr,
              let base = dataPtr, totalLen > 4 else { return }

        var offset = 0
        while offset + 4 <= totalLen {
            var nalLenBE: UInt32 = 0
            memcpy(&nalLenBE, base.advanced(by: offset), 4)
            let nalLen = Int(UInt32(bigEndian: nalLenBE))
            offset += 4
            guard nalLen > 0, offset + nalLen <= totalLen else { break }
            annexB.append(startCode)
            base.advanced(by: offset).withMemoryRebound(to: UInt8.self, capacity: nalLen) { u8 in
                annexB.append(u8, count: nalLen)
            }
            offset += nalLen
        }

        guard !annexB.isEmpty else { return }
        lock.lock()
        let w = encodeWidth
        let h = encodeHeight
        let nowMs = DispatchTime.now().uptimeNanoseconds / 1_000_000
        let tsMs = UInt32(truncatingIfNeeded: nowMs >= startTimeMs ? (nowMs - startTimeMs) : 0)
        let cb = onEncodedVideo
        lock.unlock()
        cb?(w, h, isKeyframe, annexB, tsMs)
    }
}
