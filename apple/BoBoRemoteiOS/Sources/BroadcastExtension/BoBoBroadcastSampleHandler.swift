import BoBoAppleCore
import CoreImage
import CoreMedia
import Foundation
import Network
import ReplayKit
import VideoToolbox

public final class BoBoBroadcastSampleHandler: RPBroadcastSampleHandler, @unchecked Sendable {
    private static let frameIntervalNs = 1_000_000_000 / UInt64(ReplayKitLoopback.framesPerSecond)
    private static let connectionTimeoutNs: UInt64 = 2_000_000_000
    private static let startCode = Data([0, 0, 0, 1])

    // ReplayKit, VideoToolbox, and Network.framework call back on different threads.
    private let queue = DispatchQueue(label: "com.bobo.remote.ios.broadcast.enc", qos: .userInteractive)
    private var watchdog: DispatchSourceTimer?
    private var running = false
    private var broadcastStartNs: UInt64 = 0
    private var lastFrameSlot: UInt64?

    private var bridgeConnection: NWConnection?
    private var bridgeReady = false
    private var connectionDeadlineNs: UInt64?
    private var waitingForSend = false
    private var waitingForAck = false
    private var nextConnectionNs: UInt64 = 0
    private var retryDelayNs: UInt64 = 500_000_000

    private var compressionSession: VTCompressionSession?
    private var encoderSize = CMVideoDimensions(width: 0, height: 0)
    private var encoderUsesBgra = false
    private var imageContext: CIContext?
    private var lastOrientation: Int32?
    private var frameNumber: UInt64 = 0
    private var encodingFrame: UInt64?
    private var encodeDeadlineNs: UInt64 = 0
    private var needsKeyframe = true

    public override func broadcastStarted(withSetupInfo setupInfo: [String: NSObject]?) {
        queue.sync {
            broadcastStartNs = DispatchTime.now().uptimeNanoseconds
            lastFrameSlot = nil
            resumeCapture()
        }
    }

    public override func broadcastPaused() {
        queue.sync { pauseCapture() }
    }

    public override func broadcastResumed() {
        queue.sync { resumeCapture() }
    }

    public override func broadcastFinished() {
        queue.sync { pauseCapture() }
    }

    public override func processSampleBuffer(_ sampleBuffer: CMSampleBuffer, with sampleBufferType: RPSampleBufferType) {
        guard sampleBufferType == .video else { return }
        // Enter synchronously so a busy encoder or paused app cannot retain a queue of ReplayKit buffers.
        queue.sync { encodeIfReady(sampleBuffer) }
    }

    private func resumeCapture() {
        guard !running else { return }
        running = true
        nextConnectionNs = 0
        retryDelayNs = 500_000_000
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now(), repeating: .milliseconds(250), leeway: .milliseconds(50))
        timer.setEventHandler { [weak self] in self?.checkConnection() }
        watchdog = timer
        timer.resume()
    }

    private func pauseCapture() {
        running = false
        watchdog?.cancel()
        watchdog = nil
        closeConnection()
    }

    private func checkConnection() {
        guard running else { return }
        let now = DispatchTime.now().uptimeNanoseconds
        if let deadline = connectionDeadlineNs, now >= deadline {
            closeConnection()
        }
        guard bridgeConnection == nil, now >= nextConnectionNs else { return }

        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true
        let connection = NWConnection(
            host: .ipv4(.loopback), port: NWEndpoint.Port(rawValue: ReplayKitLoopback.port)!,
            using: NWParameters(tls: nil, tcp: tcp)
        )
        bridgeConnection = connection
        connectionDeadlineNs = now + Self.connectionTimeoutNs
        connection.stateUpdateHandler = { [weak self, weak connection] state in
            guard let self, let connection, self.bridgeConnection === connection else { return }
            switch state {
            case .ready:
                self.bridgeReady = true
                self.connectionDeadlineNs = nil
                self.receiveAcknowledgment(from: connection)
            case .waiting, .failed, .cancelled:
                self.closeConnection()
            default:
                break
            }
        }
        connection.start(queue: queue)
    }

    private func closeConnection() {
        let connection = bridgeConnection
        bridgeConnection = nil
        bridgeReady = false
        connectionDeadlineNs = nil
        waitingForSend = false
        waitingForAck = false
        connection?.cancel()
        nextConnectionNs = DispatchTime.now().uptimeNanoseconds + retryDelayNs
        retryDelayNs = min(8_000_000_000, retryDelayNs * 2)
        resetEncoder()
        imageContext = nil
    }

    private func receiveAcknowledgment(from connection: NWConnection) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 1) { [weak self, weak connection] data, _, complete, error in
            guard let self, let connection, self.bridgeConnection === connection else { return }
            guard error == nil, !complete else {
                self.closeConnection()
                return
            }
            if let flags = data?.first {
                guard self.waitingForAck, flags & ReplayKitLoopback.acknowledge != 0 else {
                    self.closeConnection()
                    return
                }
                self.waitingForAck = false
                if !self.waitingForSend { self.connectionDeadlineNs = nil }
                self.retryDelayNs = 500_000_000
                if flags & ReplayKitLoopback.requestKeyframe != 0 { self.needsKeyframe = true }
            }
            self.receiveAcknowledgment(from: connection)
        }
    }

    private func encodeIfReady(_ sampleBuffer: CMSampleBuffer) {
        guard bridgeReady, !waitingForSend, !waitingForAck,
              let source = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        let now = DispatchTime.now().uptimeNanoseconds
        if encodingFrame != nil {
            guard now >= encodeDeadlineNs else { return }
            resetEncoder()
        }
        let slot = (now - broadcastStartNs) / Self.frameIntervalNs
        guard slot != lastFrameSlot else { return }
        lastFrameSlot = slot

        let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        guard pts.isNumeric else { return }
        let exifOrientation = (CMGetAttachment(sampleBuffer, key: RPVideoSampleOrientationKey as CFString, attachmentModeOut: nil) as? NSNumber)?.int32Value ?? 1
        let orientation = (1...8).contains(exifOrientation) ? exifOrientation : 1
        let sourceWidth = CVPixelBufferGetWidth(source)
        let sourceHeight = CVPixelBufferGetHeight(source)
        let rotated = (5...8).contains(orientation)
        let width = rotated ? sourceHeight : sourceWidth
        let height = rotated ? sourceWidth : sourceHeight
        guard width > 0, height > 0 else { return }
        let scale = min(1, Double(ReplayKitLoopback.maxSide) / Double(max(width, height)))
        let target = CMVideoDimensions(
            width: max(2, Int32(Double(width) * scale) & ~1),
            height: max(2, Int32(Double(height) * scale) & ~1)
        )
        let requiresConversion = orientation != 1 || Int(target.width) != sourceWidth || Int(target.height) != sourceHeight
        if compressionSession == nil || target.width != encoderSize.width || target.height != encoderSize.height
            || lastOrientation != orientation || encoderUsesBgra != requiresConversion {
            resetEncoder()
            createEncoder(size: target, useBgraInput: requiresConversion)
            lastOrientation = orientation
            encoderUsesBgra = requiresConversion
        }
        guard let session = compressionSession else { return }
        let input: CVPixelBuffer
        if requiresConversion {
            guard let scaled = scaledImage(source, orientation: orientation, session: session, size: target) else { return }
            input = scaled
        } else {
            input = source
        }

        frameNumber &+= 1
        let number = frameNumber
        encodingFrame = number
        encodeDeadlineNs = now + Self.connectionTimeoutNs
        let timestampMs = UInt32(truncatingIfNeeded: (now - broadcastStartNs) / 1_000_000)
        let properties = needsKeyframe ? [kVTEncodeFrameOptionKey_ForceKeyFrame as String: true] as CFDictionary : nil
        let status = VTCompressionSessionEncodeFrame(
            session, imageBuffer: input, presentationTimeStamp: pts, duration: .invalid,
            frameProperties: properties, infoFlagsOut: nil
        ) { [weak self] status, flags, encoded in
            self?.queue.async { [weak self] in
                self?.finishEncoding(number: number, timestampMs: timestampMs, status: status, dropped: flags.contains(.frameDropped), sample: encoded)
            }
        }
        if status != noErr { resetEncoder() }
    }

    private func resetEncoder() {
        encodingFrame = nil
        if let session = compressionSession { VTCompressionSessionInvalidate(session) }
        compressionSession = nil
        needsKeyframe = true
    }

    private func createEncoder(size: CMVideoDimensions, useBgraInput: Bool) {
        let attributes: CFDictionary? = useBgraInput ? [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey as String: size.width,
            kCVPixelBufferHeightKey as String: size.height,
            kCVPixelBufferIOSurfacePropertiesKey as String: [:] as [String: Any]
        ] as CFDictionary : nil
        var session: VTCompressionSession?
        guard VTCompressionSessionCreate(
            allocator: kCFAllocatorDefault, width: size.width, height: size.height, codecType: kCMVideoCodecType_H264,
            encoderSpecification: nil, imageBufferAttributes: attributes, compressedDataAllocator: nil,
            outputCallback: nil, refcon: nil, compressionSessionOut: &session
        ) == noErr, let session else { return }
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_RealTime, value: kCFBooleanTrue)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AllowFrameReordering, value: kCFBooleanFalse)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_MaxFrameDelayCount, value: NSNumber(value: 0))
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_ExpectedFrameRate, value: NSNumber(value: ReplayKitLoopback.framesPerSecond))
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_MaxKeyFrameInterval, value: NSNumber(value: ReplayKitLoopback.framesPerSecond * 2))
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration, value: NSNumber(value: 2))
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AverageBitRate, value: NSNumber(value: ReplayKitLoopback.bitrateKbps * 1000))
        guard VTCompressionSessionPrepareToEncodeFrames(session) == noErr else {
            VTCompressionSessionInvalidate(session)
            return
        }
        encoderSize = size
        compressionSession = session
    }

    private func scaledImage(_ source: CVPixelBuffer, orientation: Int32, session: VTCompressionSession, size: CMVideoDimensions) -> CVPixelBuffer? {
        guard let pool = VTCompressionSessionGetPixelBufferPool(session) else { return nil }
        var destination: CVPixelBuffer?
        guard CVPixelBufferPoolCreatePixelBuffer(kCFAllocatorDefault, pool, &destination) == kCVReturnSuccess,
              let destination else { return nil }
        let oriented = CIImage(cvPixelBuffer: source).oriented(forExifOrientation: orientation)
        let extent = oriented.extent
        let image = oriented
            .transformed(by: CGAffineTransform(translationX: -extent.minX, y: -extent.minY))
            .transformed(by: CGAffineTransform(scaleX: CGFloat(size.width) / extent.width, y: CGFloat(size.height) / extent.height))
        let context = imageContext ?? CIContext(options: [.cacheIntermediates: false])
        imageContext = context
        context.render(image, to: destination)
        return destination
    }

    private func finishEncoding(number: UInt64, timestampMs: UInt32, status: OSStatus, dropped: Bool, sample: CMSampleBuffer?) {
        guard encodingFrame == number else { return }
        encodingFrame = nil
        guard let connection = bridgeConnection, bridgeReady else { return }
        guard status == noErr, !dropped, let sample, let format = CMSampleBufferGetFormatDescription(sample),
              let (annexB, isKeyframe) = Self.annexB(from: sample, format: format) else {
            needsKeyframe = true
            return
        }
        guard !needsKeyframe || isKeyframe else { return }
        needsKeyframe = false
        let size = CMVideoFormatDescriptionGetDimensions(format)
        let payload = BoBoProtocol.buildVideoPayload(width: Int(size.width), height: Int(size.height), isKeyframe: isKeyframe, h264Data: annexB)
        guard payload.count <= ReplayKitLoopback.maxVideoPayloadBytes else {
            needsKeyframe = true
            return
        }
        let frame = BoBoProtocol.encodeFrame(type: .videoH264, flagsOrTimestamp: timestampMs, payload: payload)
        waitingForSend = true
        waitingForAck = true
        connectionDeadlineNs = DispatchTime.now().uptimeNanoseconds + Self.connectionTimeoutNs
        connection.send(content: frame, completion: .contentProcessed { [weak self, weak connection] error in
            guard let self, let connection, self.bridgeConnection === connection else { return }
            if error != nil {
                self.closeConnection()
            } else {
                self.waitingForSend = false
                if !self.waitingForAck { self.connectionDeadlineNs = nil }
            }
        })
    }

    private static func annexB(from sample: CMSampleBuffer, format: CMFormatDescription) -> (Data, Bool)? {
        guard let block = CMSampleBufferGetDataBuffer(sample) else { return nil }
        let length = CMBlockBufferGetDataLength(block)
        guard length > 0, length < ReplayKitLoopback.maxVideoPayloadBytes else { return nil }
        var headerLength: Int32 = 0
        guard CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
            format, parameterSetIndex: 0, parameterSetPointerOut: nil, parameterSetSizeOut: nil,
            parameterSetCountOut: nil, nalUnitHeaderLengthOut: &headerLength
        ) == noErr, [1, 2, 4].contains(headerLength) else { return nil }

        var avcc = Data(count: length)
        let copied = avcc.withUnsafeMutableBytes { (bytes: UnsafeMutableRawBufferPointer) in
            CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: length, destination: bytes.baseAddress!)
        }
        guard copied == kCMBlockBufferNoErr else { return nil }
        var annexB = Data(capacity: length)
        var containsIdr = false
        let parsed = avcc.withUnsafeBytes { (bytes: UnsafeRawBufferPointer) in
            let base = bytes.bindMemory(to: UInt8.self).baseAddress!
            let prefixBytes = Int(headerLength)
            var offset = 0
            while offset < length {
                guard length - offset >= prefixBytes else { return false }
                var nalLength = 0
                for index in 0..<prefixBytes { nalLength = (nalLength << 8) | Int(base[offset + index]) }
                offset += prefixBytes
                guard nalLength > 0, nalLength <= length - offset,
                      annexB.count + startCode.count + nalLength < ReplayKitLoopback.maxVideoPayloadBytes else { return false }
                containsIdr = containsIdr || (base[offset] & 0x1f == 5)
                annexB.append(startCode)
                annexB.append(base.advanced(by: offset), count: nalLength)
                offset += nalLength
            }
            return true
        }
        guard parsed, !annexB.isEmpty else { return nil }
        if containsIdr {
            var parameterSets = Data()
            for index in 0...1 {
                var pointer: UnsafePointer<UInt8>?
                var count = 0
                guard CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
                    format, parameterSetIndex: index, parameterSetPointerOut: &pointer, parameterSetSizeOut: &count,
                    parameterSetCountOut: nil, nalUnitHeaderLengthOut: nil
                ) == noErr, let pointer, count > 0, count <= ReplayKitLoopback.maxVideoPayloadBytes - parameterSets.count - startCode.count else { return nil }
                parameterSets.append(startCode)
                parameterSets.append(pointer, count: count)
            }
            guard annexB.count + parameterSets.count < ReplayKitLoopback.maxVideoPayloadBytes else { return nil }
            annexB.insert(contentsOf: parameterSets, at: annexB.startIndex)
        }
        return (annexB, containsIdr)
    }
}
