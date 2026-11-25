import AudioToolbox
import BoBoAppleCore
import CoreGraphics
import CoreMedia
import CoreVideo
import Foundation
import ScreenCaptureKit
import VideoToolbox

struct MacCaptureFormat: Sendable {
    let displayID: CGDirectDisplayID
    let monitorIndex: Int
    let width: Int
    let height: Int
    let fps: Int
    let bitrateKbps: Int
}

public final class MacScreenCaptureEngine: NSObject, SCStreamOutput, SCStreamDelegate, @unchecked Sendable {
    private struct Capture {
        let stream: SCStream
        let encoder: VTCompressionSession
        let format: MacCaptureFormat
        let generation: UInt
        let startTimeMs: UInt64
        var ready = false
        var forceNextKeyframe = true
        var keyframeScheduled = false
        var lastImage: CVImageBuffer?
        var lastImageTime = CMTime.invalid
        var lastEncodedTime = CMTime.invalid
    }

    private let videoQueue = DispatchQueue(label: "com.bobo.remote.mac.capture.video", qos: .userInteractive)
    private let audioQueue = DispatchQueue(label: "com.bobo.remote.mac.capture.audio", qos: .userInteractive)
    private let lock = NSLock()
    private var capture: Capture?
    private var audioSampleRate = 0
    private var audioPcmScratch: [Int16] = []
    @MainActor private var generation: UInt = 0

    private let onVideo: @Sendable (Int, Int, Bool, Data, UInt32) -> Void
    private let onAudio: @Sendable (Int, Int, Int, Data) -> Void
    @MainActor var onCaptureError: ((Error) -> Void)?

    init(onVideo: @escaping @Sendable (Int, Int, Bool, Data, UInt32) -> Void,
         onAudio: @escaping @Sendable (Int, Int, Int, Data) -> Void) {
        self.onVideo = onVideo
        self.onAudio = onAudio
        super.init()
    }

    private static func displays() -> [CGDirectDisplayID] {
        var count: UInt32 = 0
        guard CGGetActiveDisplayList(0, nil, &count) == .success, count > 0 else { return [] }
        var ids = [CGDirectDisplayID](repeating: 0, count: Int(count))
        guard CGGetActiveDisplayList(count, &ids, &count) == .success else { return [] }
        let primary = CGMainDisplayID()
        return ids.prefix(Int(count)).filter { $0 != 0 }.sorted { a, b in
            if a == primary { return b != primary }
            if b == primary { return false }
            return a < b
        }
    }

    private static func pixelSize(of id: CGDirectDisplayID) -> (width: Int, height: Int) {
        if let mode = CGDisplayCopyDisplayMode(id) { return (mode.pixelWidth, mode.pixelHeight) }
        return (CGDisplayPixelsWide(id), CGDisplayPixelsHigh(id))
    }

    static func monitors() -> [BoBoMonitorDescriptor] {
        displays().enumerated().map { index, id in
            let size = pixelSize(of: id)
            return BoBoMonitorDescriptor(index: index, name: "Display \(index + 1) (\(size.width)x\(size.height))",
                                         width: size.width, height: size.height, isPrimary: id == CGMainDisplayID())
        }
    }

    static func configuration(monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int) -> MacCaptureFormat? {
        let ids = displays()
        guard !ids.isEmpty else { return nil }
        let index = min(max(0, monitorIndex), ids.count - 1)
        let id = ids[index]
        let native = pixelSize(of: id)
        guard native.width > 1, native.height > 1 else { return nil }
        let maxWidth = min(3840, width > 0 ? width : native.width)
        let maxHeight = min(2160, height > 0 ? height : native.height)
        let scale = min(1, Double(maxWidth) / Double(native.width), Double(maxHeight) / Double(native.height))
        return MacCaptureFormat(displayID: id, monitorIndex: index,
                                width: max(2, Int(Double(native.width) * scale) & ~1),
                                height: max(2, Int(Double(native.height) * scale) & ~1),
                                fps: max(15, min(120, fps)), bitrateKbps: max(250, min(100_000, bitrateKbps)))
    }

    var currentFormat: MacCaptureFormat? {
        lock.withLock { capture?.ready == true ? capture?.format : nil }
    }

    @MainActor func start(format: MacCaptureFormat, excludeWindowIDs: Set<CGWindowID>) async throws {
        generation &+= 1
        let ticket = generation
        if let previous = detachCapture() { await retire(previous) }
        try checkGeneration(ticket)

        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        try checkGeneration(ticket)
        let filter = try makeFilter(content: content, displayID: format.displayID, excludeWindowIDs: excludeWindowIDs)
        let config = SCStreamConfiguration()
        config.width = format.width
        config.height = format.height
        config.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(format.fps))
        config.pixelFormat = kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
        config.showsCursor = true
        config.capturesAudio = true
        config.sampleRate = 48_000
        config.channelCount = 2
        config.queueDepth = 3

        let stream = SCStream(filter: filter, configuration: config, delegate: self)
        try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: videoQueue)
        try stream.addStreamOutput(self, type: .audio, sampleHandlerQueue: audioQueue)
        let encoder = try createEncoder(format: format)
        lock.withLock {
            capture = Capture(stream: stream, encoder: encoder, format: format, generation: ticket,
                              startTimeMs: DispatchTime.now().uptimeNanoseconds / 1_000_000)
        }
        do {
            try await stream.startCapture()
            try checkGeneration(ticket)
            lock.withLock { capture?.ready = true }
        } catch {
            if let failed = detachCapture(generation: ticket) { await retire(failed) }
            else { try? await stream.stopCapture() }
            throw error
        }
    }

    @MainActor func updateExcludedWindows(_ ids: Set<CGWindowID>) async throws {
        guard let active = lock.withLock({ capture }), active.ready else { throw CancellationError() }
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        try checkGeneration(active.generation)
        let filter = try makeFilter(content: content, displayID: active.format.displayID, excludeWindowIDs: ids)
        try await active.stream.updateContentFilter(filter)
        try checkGeneration(active.generation)
    }

    private func makeFilter(content: SCShareableContent, displayID: CGDirectDisplayID,
                            excludeWindowIDs: Set<CGWindowID>) throws -> SCContentFilter {
        guard let display = content.displays.first(where: { $0.displayID == displayID }) else {
            throw captureError("所选屏幕已断开或无法采集")
        }
        let excluded = content.windows.filter { excludeWindowIDs.contains($0.windowID) }
        if excluded.count == excludeWindowIDs.count { return SCContentFilter(display: display, excludingWindows: excluded) }

        // Some macOS versions omit windows that are hidden or marked as nonshareable.
        // Exclude our application and include its other known windows before ordering the curtain front.
        let pid = ProcessInfo.processInfo.processIdentifier
        guard let app = content.applications.first(where: { $0.processID == pid }) else {
            throw captureError("无法从远程画面中排除防窥窗口")
        }
        let visibleAppWindows = content.windows.filter {
            $0.owningApplication?.processID == pid && !excludeWindowIDs.contains($0.windowID)
        }
        return SCContentFilter(display: display, excludingApplications: [app], exceptingWindows: visibleAppWindows)
    }

    @MainActor private func checkGeneration(_ ticket: UInt) throws {
        try Task.checkCancellation()
        guard generation == ticket else { throw CancellationError() }
    }

    func requestKeyframe() {
        let ticket = lock.withLock { () -> UInt? in
            guard let active = capture, active.ready else { return nil }
            capture?.forceNextKeyframe = true
            guard active.lastImage != nil, !active.keyframeScheduled else { return nil }
            capture?.keyframeScheduled = true
            return active.generation
        }
        if let ticket {
            // ScreenCaptureKit may deliver only idle frames until something changes on screen.
            videoQueue.async { [weak self] in self?.encodeLatestFrame(generation: ticket, keyframeOnly: true) }
        }
    }

    @MainActor public func stop() async {
        generation &+= 1
        if let active = detachCapture() { await retire(active) }
    }

    private func detachCapture(generation ticket: UInt? = nil) -> Capture? {
        lock.withLock {
            guard let active = capture, ticket == nil || active.generation == ticket else { return nil }
            capture = nil
            audioSampleRate = 0
            audioPcmScratch.removeAll(keepingCapacity: true)
            return active
        }
    }

    private func retire(_ active: Capture) async {
        await withCheckedContinuation { continuation in
            // Do not invalidate the encoder while the capture queue is submitting a frame.
            videoQueue.async {
                VTCompressionSessionInvalidate(active.encoder)
                continuation.resume()
            }
        }
        try? await active.stream.stopCapture()
    }

    public func stream(_ stream: SCStream, didStopWithError error: Error) {
        Task { @MainActor in
            guard self.lock.withLock({ self.capture?.stream === stream }) else { return }
            self.onCaptureError?(error)
        }
    }

    public func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard CMSampleBufferIsValid(sampleBuffer), CMSampleBufferDataIsReady(sampleBuffer) else { return }
        switch type {
        case .screen:
            handleVideoSample(sampleBuffer, stream: stream)
        case .audio:
            guard let ticket = lock.withLock({ capture?.stream === stream && capture?.ready == true ? capture?.generation : nil }) else { return }
            handleAudioSample(sampleBuffer, generation: ticket)
        @unknown default:
            break
        }
    }

    private func handleVideoSample(_ sampleBuffer: CMSampleBuffer, stream: SCStream) {
        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
           let status = attachments.first?[.status] as? Int, status != SCFrameStatus.complete.rawValue { return }
        guard let image = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        let ticket = lock.withLock { () -> UInt? in
            guard let active = capture, active.stream === stream else { return nil }
            capture?.lastImage = image
            capture?.lastImageTime = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
            return active.generation
        }
        if let ticket { encodeLatestFrame(generation: ticket, keyframeOnly: false) }
    }

    // All frame submission and encoder invalidation run on the video queue.
    private func encodeLatestFrame(generation ticket: UInt, keyframeOnly: Bool) {
        let submission = lock.withLock { () -> (VTCompressionSession, CVImageBuffer, CMTime, Bool)? in
            guard let active = capture, active.generation == ticket else { return nil }
            if keyframeOnly { capture?.keyframeScheduled = false }
            guard active.ready, let image = active.lastImage,
                  !keyframeOnly || active.forceNextKeyframe else { return nil }
            var time = active.lastImageTime
            if !time.isValid { time = CMClockGetTime(CMClockGetHostTimeClock()) }
            if active.lastEncodedTime.isValid, CMTimeCompare(time, active.lastEncodedTime) <= 0 {
                time = CMTimeAdd(active.lastEncodedTime, CMTime(value: 1, timescale: 1000))
            }
            capture?.lastEncodedTime = time
            capture?.forceNextKeyframe = false
            return (active.encoder, image, time, active.forceNextKeyframe)
        }
        guard let (encoder, image, time, forceKeyframe) = submission else { return }
        let properties = forceKeyframe ? [kVTEncodeFrameOptionKey_ForceKeyFrame: kCFBooleanTrue] as CFDictionary : nil
        let status = VTCompressionSessionEncodeFrame(encoder, imageBuffer: image,
                                                      presentationTimeStamp: time, duration: .invalid, frameProperties: properties,
                                                      sourceFrameRefcon: UnsafeMutableRawPointer(bitPattern: ticket), infoFlagsOut: nil)
        if status != noErr, forceKeyframe {
            lock.withLock {
                if capture?.generation == ticket { capture?.forceNextKeyframe = true }
            }
        }
    }

    private func handleAudioSample(_ sampleBuffer: CMSampleBuffer, generation ticket: UInt) {
        guard let desc = CMSampleBufferGetFormatDescription(sampleBuffer),
              let format = CMAudioFormatDescriptionGetStreamBasicDescription(desc)?.pointee,
              format.mFormatID == kAudioFormatLinearPCM,
              format.mSampleRate.isFinite, (1...192_000).contains(format.mSampleRate),
              format.mFormatFlags & kAudioFormatFlagIsBigEndian == 0 else { return }
        let sampleRate = Int(format.mSampleRate.rounded())
        let frames = CMSampleBufferGetNumSamples(sampleBuffer)
        guard frames > 0, frames <= sampleRate else { return }

        let isFloat = format.mFormatFlags & kAudioFormatFlagIsFloat != 0
        let planar = format.mFormatFlags & kAudioFormatFlagIsNonInterleaved != 0
        let channels = Int(format.mChannelsPerFrame)
        let sampleBytes = isFloat ? MemoryLayout<Float>.size : MemoryLayout<Int16>.size
        let stride = Int(format.mBytesPerFrame)
        guard channels > 0, Int(format.mBitsPerChannel) == sampleBytes * 8,
              isFloat || (format.mFormatFlags & kAudioFormatFlagIsSignedInteger != 0),
              stride == sampleBytes * (planar ? 1 : channels) else { return }

        var listSize = 0
        _ = CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
            sampleBuffer, bufferListSizeNeededOut: &listSize, bufferListOut: nil, bufferListSize: 0,
            blockBufferAllocator: nil, blockBufferMemoryAllocator: nil, flags: 0, blockBufferOut: nil
        )
        guard listSize >= MemoryLayout<AudioBufferList>.size else { return }
        let memory = UnsafeMutableRawPointer.allocate(byteCount: listSize, alignment: MemoryLayout<AudioBufferList>.alignment)
        defer { memory.deallocate() }
        let list = memory.bindMemory(to: AudioBufferList.self, capacity: 1)
        var block: CMBlockBuffer?
        let status = CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
            sampleBuffer, bufferListSizeNeededOut: nil, bufferListOut: list, bufferListSize: listSize,
            blockBufferAllocator: nil, blockBufferMemoryAllocator: nil,
            flags: UInt32(kCMSampleBufferFlag_AudioBufferList_Assure16ByteAlignment), blockBufferOut: &block
        )
        guard status == noErr else { return }
        let buffers = UnsafeMutableAudioBufferListPointer(list)
        guard let first = buffers.first, let left = first.mData, Int(first.mDataByteSize) / stride >= frames,
              first.mNumberChannels == (planar ? 1 : format.mChannelsPerFrame) else { return }
        var right = left
        if channels > 1 {
            if planar {
                guard buffers.count > 1, buffers[1].mNumberChannels == 1, let second = buffers[1].mData,
                      Int(buffers[1].mDataByteSize) / stride >= frames else { return }
                right = second
            } else {
                right = left.advanced(by: sampleBytes)
            }
        }

        let pcm = withExtendedLifetime(block) {
            var pcm = [Int16](repeating: 0, count: frames * 2)
            for frame in 0..<frames {
                let offset = frame * stride
                if isFloat {
                    pcm[frame * 2] = Self.pcmSample(left.loadUnaligned(fromByteOffset: offset, as: Float.self))
                    pcm[frame * 2 + 1] = Self.pcmSample(right.loadUnaligned(fromByteOffset: offset, as: Float.self))
                } else {
                    pcm[frame * 2] = left.loadUnaligned(fromByteOffset: offset, as: Int16.self)
                    pcm[frame * 2 + 1] = right.loadUnaligned(fromByteOffset: offset, as: Int16.self)
                }
            }
            return pcm
        }
        let chunkFrames = max(1, sampleRate / 100)
        let chunks: [[Int16]] = lock.withLock {
            guard capture?.generation == ticket else { return [] }
            if audioSampleRate != sampleRate {
                audioPcmScratch.removeAll(keepingCapacity: true)
                audioSampleRate = sampleRate
            }
            audioPcmScratch.append(contentsOf: pcm)
            var chunks: [[Int16]] = []
            let chunkSamples = chunkFrames * 2
            let consumed = audioPcmScratch.count / chunkSamples * chunkSamples
            for start in Swift.stride(from: 0, to: consumed, by: chunkSamples) {
                chunks.append(Array(audioPcmScratch[start..<(start + chunkSamples)]))
            }
            audioPcmScratch.removeFirst(consumed)
            return chunks
        }
        for chunk in chunks {
            let adpcm = ImaAdpcmCodec.encodeStereo(chunk, sampleFrames: chunkFrames)
            guard lock.withLock({ capture?.generation == ticket }) else { return }
            onAudio(sampleRate, 2, chunkFrames, adpcm)
        }
    }

    private static func pcmSample(_ value: Float) -> Int16 {
        guard value.isFinite else { return 0 }
        return Int16(max(-32_768, min(32_767, value * 32_768)))
    }

    private func createEncoder(format: MacCaptureFormat) throws -> VTCompressionSession {
        let callback: VTCompressionOutputCallback = { refcon, frameRefcon, status, _, sampleBuffer in
            guard status == noErr, let refcon, let frameRefcon, let sampleBuffer else { return }
            let engine = Unmanaged<MacScreenCaptureEngine>.fromOpaque(refcon).takeUnretainedValue()
            engine.emitEncodedSample(sampleBuffer, generation: UInt(bitPattern: frameRefcon))
        }
        var encoder: VTCompressionSession?
        let status = VTCompressionSessionCreate(allocator: kCFAllocatorDefault,
                                                 width: Int32(format.width), height: Int32(format.height),
                                                 codecType: kCMVideoCodecType_H264, encoderSpecification: nil,
                                                 imageBufferAttributes: nil, compressedDataAllocator: nil,
                                                 outputCallback: callback, refcon: Unmanaged.passUnretained(self).toOpaque(),
                                                 compressionSessionOut: &encoder)
        guard status == noErr, let encoder else { throw captureError("无法初始化 VideoToolbox H.264 编码器", status: status) }
        VTSessionSetProperty(encoder, key: kVTCompressionPropertyKey_RealTime, value: kCFBooleanTrue)
        VTSessionSetProperty(encoder, key: kVTCompressionPropertyKey_ProfileLevel, value: kVTProfileLevel_H264_High_AutoLevel)
        VTSessionSetProperty(encoder, key: kVTCompressionPropertyKey_AllowFrameReordering, value: kCFBooleanFalse)
        VTSessionSetProperty(encoder, key: kVTCompressionPropertyKey_ExpectedFrameRate, value: NSNumber(value: format.fps))
        VTSessionSetProperty(encoder, key: kVTCompressionPropertyKey_MaxKeyFrameInterval, value: NSNumber(value: format.fps * 2))
        VTSessionSetProperty(encoder, key: kVTCompressionPropertyKey_AverageBitRate, value: NSNumber(value: format.bitrateKbps * 1000))
        let prepareStatus = VTCompressionSessionPrepareToEncodeFrames(encoder)
        guard prepareStatus == noErr else {
            VTCompressionSessionInvalidate(encoder)
            throw captureError("无法启动 VideoToolbox H.264 编码器", status: prepareStatus)
        }
        return encoder
    }

    private func emitEncodedSample(_ sampleBuffer: CMSampleBuffer, generation ticket: UInt) {
        guard let active = lock.withLock({ capture }), active.generation == ticket, active.ready,
              let description = CMSampleBufferGetFormatDescription(sampleBuffer),
              let block = CMSampleBufferGetDataBuffer(sampleBuffer) else { return }
        var parameterCount = 0
        var headerLength: Int32 = 0
        guard CMVideoFormatDescriptionGetH264ParameterSetAtIndex(description, parameterSetIndex: 0,
                                                                 parameterSetPointerOut: nil, parameterSetSizeOut: nil,
                                                                 parameterSetCountOut: &parameterCount, nalUnitHeaderLengthOut: &headerLength) == noErr,
              (1...4).contains(headerLength) else { return }

        let startCode: [UInt8] = [0, 0, 0, 1]
        let total = CMBlockBufferGetDataLength(block)
        guard total > Int(headerLength), total <= BoBoProtocol.maxVideoPayloadBytes else { return }
        var annexB = Data(capacity: total)
        var idr = false
        func appendNALUnits(_ bytes: UnsafeRawBufferPointer) -> Bool {
            guard let base = bytes.bindMemory(to: UInt8.self).baseAddress else { return false }
            let header = Int(headerLength)
            var offset = 0
            while offset < total {
                guard total - offset >= header else { return false }
                var length = 0
                for _ in 0..<header {
                    length = length * 256 + Int(base[offset])
                    offset += 1
                }
                guard length > 0, length <= total - offset else { return false }
                idr = idr || base[offset] & 0x1F == 5
                annexB.append(contentsOf: startCode)
                annexB.append(base.advanced(by: offset), count: length)
                offset += length
            }
            return true
        }

        var contiguousLength = 0
        var pointer: UnsafeMutablePointer<Int8>?
        if CMBlockBufferGetDataPointer(block, atOffset: 0, lengthAtOffsetOut: &contiguousLength,
                                       totalLengthOut: nil, dataPointerOut: &pointer) == kCMBlockBufferNoErr,
           contiguousLength >= total, let pointer {
            guard appendNALUnits(UnsafeRawBufferPointer(start: pointer, count: total)) else { return }
        } else {
            var bytes = Data(count: total)
            let status = bytes.withUnsafeMutableBytes { raw in
                CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: total, destination: raw.baseAddress!)
            }
            guard status == kCMBlockBufferNoErr, bytes.withUnsafeBytes(appendNALUnits) else { return }
        }

        if idr {
            var parameters = Data()
            for index in 0..<parameterCount {
                var pointer: UnsafePointer<UInt8>?
                var length = 0
                guard CMVideoFormatDescriptionGetH264ParameterSetAtIndex(description, parameterSetIndex: index,
                                                                         parameterSetPointerOut: &pointer, parameterSetSizeOut: &length,
                                                                         parameterSetCountOut: nil, nalUnitHeaderLengthOut: nil) == noErr,
                      let pointer, length > 0 else { return }
                parameters.append(contentsOf: startCode)
                parameters.append(pointer, count: length)
            }
            parameters.append(annexB)
            annexB = parameters
        }
        guard lock.withLock({ capture?.generation == ticket }) else { return }
        let timestamp = DispatchTime.now().uptimeNanoseconds / 1_000_000 - active.startTimeMs
        onVideo(active.format.width, active.format.height, idr, annexB, UInt32(truncatingIfNeeded: timestamp))
    }

    private func captureError(_ message: String, status: OSStatus = -1) -> NSError {
        NSError(domain: "MacCapture", code: Int(status), userInfo: [NSLocalizedDescriptionKey: message])
    }
}
