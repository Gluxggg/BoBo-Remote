import BoBoAppleCore
import CoreMedia
import Foundation
import Network
import ReplayKit
import VideoToolbox

/// iOS ReplayKit Broadcast Upload Extension：
/// 将系统录屏 CMSampleBuffer 通过硬件 VideoToolbox 编码为 H.264 Annex-B，
/// 并通过本地回环 127.0.0.1:49811 推送给主 App 的 BoBoHostServer（低内存 <25MB，兼容自签分发）。
public final class BoBoBroadcastSampleHandler: RPBroadcastSampleHandler, @unchecked Sendable {
    private let queue = DispatchQueue(label: "com.bobo.remote.ios.broadcast.enc", qos: .userInteractive)
    private var bridgeConn: NWConnection?
    private var compressionSession: VTCompressionSession?
    private var encWidth: Int32 = 0
    private var encHeight: Int32 = 0
    private var startMs: UInt64 = 0

    public override func broadcastStarted(withSetupInfo setupInfo: [String: NSObject]?) {
        startMs = DispatchTime.now().uptimeNanoseconds / 1_000_000
        let conn = NWConnection(host: .ipv4(.loopback), port: NWEndpoint.Port(rawValue: 49811)!, using: .tcp)
        conn.start(queue: queue)
        bridgeConn = conn
    }

    public override func broadcastFinished() {
        if let sess = compressionSession {
            VTCompressionSessionInvalidate(sess)
            compressionSession = nil
        }
        bridgeConn?.cancel()
        bridgeConn = nil
    }

    public override func processSampleBuffer(_ sampleBuffer: CMSampleBuffer, with sampleBufferType: RPSampleBufferType) {
        guard sampleBufferType == .video,
              let pixelBuf = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }

        let srcW = Int32(CVPixelBufferGetWidth(pixelBuf))
        let srcH = Int32(CVPixelBufferGetHeight(pixelBuf))
        guard srcW > 0, srcH > 0 else { return }

        // Keep memory well within iOS Broadcast Extension 50MB cap
        let maxSide: Int32 = 1280
        let scale = min(1.0, Double(maxSide) / Double(max(srcW, srcH)))
        let targetW = max(320, (Int32(Double(srcW) * scale) & ~1))
        let targetH = max(320, (Int32(Double(srcH) * scale) & ~1))

        if compressionSession == nil || targetW != encWidth || targetH != encHeight {
            recreateEncoder(width: targetW, height: targetH)
        }
        guard let sess = compressionSession else { return }
        let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        VTCompressionSessionEncodeFrame(sess, imageBuffer: pixelBuf, presentationTimeStamp: pts, duration: .invalid, frameProperties: nil, sourceFrameRefcon: nil, infoFlagsOut: nil)
    }

    private func recreateEncoder(width: Int32, height: Int32) {
        if let old = compressionSession {
            VTCompressionSessionInvalidate(old)
            compressionSession = nil
        }
        let cb: VTCompressionOutputCallback = { refcon, _, status, _, sb in
            guard status == noErr, let refcon, let sb else { return }
            let handler = Unmanaged<BoBoBroadcastSampleHandler>.fromOpaque(refcon).takeUnretainedValue()
            handler.emitEncoded(sb)
        }
        var sess: VTCompressionSession?
        guard VTCompressionSessionCreate(
            allocator: kCFAllocatorDefault,
            width: width,
            height: height,
            codecType: kCMVideoCodecType_H264,
            encoderSpecification: nil,
            imageBufferAttributes: nil,
            compressedDataAllocator: nil,
            outputCallback: cb,
            refcon: Unmanaged.passUnretained(self).toOpaque(),
            compressionSessionOut: &sess
        ) == noErr, let created = sess else { return }

        VTSessionSetProperty(created, key: kVTCompressionPropertyKey_RealTime, value: kCFBooleanTrue)
        VTSessionSetProperty(created, key: kVTCompressionPropertyKey_AllowFrameReordering, value: kCFBooleanFalse)
        VTSessionSetProperty(created, key: kVTCompressionPropertyKey_ExpectedFrameRate, value: NSNumber(value: 30))
        VTSessionSetProperty(created, key: kVTCompressionPropertyKey_MaxKeyFrameInterval, value: NSNumber(value: 60))
        VTSessionSetProperty(created, key: kVTCompressionPropertyKey_AverageBitRate, value: NSNumber(value: 4_000_000))
        VTCompressionSessionPrepareToEncodeFrames(created)

        compressionSession = created
        encWidth = width
        encHeight = height
    }

    private func emitEncoded(_ sampleBuffer: CMSampleBuffer) {
        guard let conn = bridgeConn else { return }
        var isKey = true
        if let arr = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[CFString: Any]],
           let first = arr.first,
           let notSync = first[kCMSampleAttachmentKey_NotSync] as? Bool {
            isKey = !notSync
        }

        let startCode = Data([0x00, 0x00, 0x00, 0x01])
        var annexB = Data()
        if isKey, let fmt = CMSampleBufferGetFormatDescription(sampleBuffer) {
            var spsPtr: UnsafePointer<UInt8>?
            var spsLen = 0
            var ppsPtr: UnsafePointer<UInt8>?
            var ppsLen = 0
            if CMVideoFormatDescriptionGetH264ParameterSetAtIndex(fmt, parameterSetIndex: 0, parameterSetPointerOut: &spsPtr, parameterSetSizeOut: &spsLen, parameterSetCountOut: nil, nalUnitHeaderLengthOut: nil) == noErr,
               let spsPtr, spsLen > 0 {
                annexB.append(startCode)
                annexB.append(spsPtr, count: spsLen)
            }
            if CMVideoFormatDescriptionGetH264ParameterSetAtIndex(fmt, parameterSetIndex: 1, parameterSetPointerOut: &ppsPtr, parameterSetSizeOut: &ppsLen, parameterSetCountOut: nil, nalUnitHeaderLengthOut: nil) == noErr,
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

        var off = 0
        while off + 4 <= totalLen {
            var nalLenBE: UInt32 = 0
            memcpy(&nalLenBE, base.advanced(by: off), 4)
            let nalLen = Int(UInt32(bigEndian: nalLenBE))
            off += 4
            guard nalLen > 0, off + nalLen <= totalLen else { break }
            annexB.append(startCode)
            base.advanced(by: off).withMemoryRebound(to: UInt8.self, capacity: nalLen) { u8 in
                annexB.append(u8, count: nalLen)
            }
            off += nalLen
        }

        guard !annexB.isEmpty else { return }
        let nowMs = DispatchTime.now().uptimeNanoseconds / 1_000_000
        let ts = UInt32(truncatingIfNeeded: nowMs >= startMs ? (nowMs - startMs) : 0)
        let payload = BoBoProtocol.buildVideoPayload(width: Int(encWidth), height: Int(encHeight), isKeyframe: isKey, h264Data: annexB)
        let frame = BoBoProtocol.encodeFrame(type: .videoH264, flagsOrTimestamp: ts, payload: payload)
        conn.send(content: frame, completion: .idempotent)
    }
}
