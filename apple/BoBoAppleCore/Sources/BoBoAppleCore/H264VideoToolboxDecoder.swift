import CoreMedia
import Foundation
import VideoToolbox

/// Apple 零拷贝 H.264 Annex-B 硬件解复用与 CMSampleBuffer 生成器（macOS 与 iOS 共享）：
/// - 将 BoBo6 协议的 H.264 Annex-B (00 00 00 01) 实时拆分为 SPS/PPS + AVCC (4 字节大端长度前缀)
/// - 生成可直接送入 `AVSampleBufferDisplayLayer`（硬件 VideoToolbox + Metal 60/120Hz 零拷贝渲染）的 `CMSampleBuffer`
public final class H264AnnexBToSampleBufferConverter: @unchecked Sendable {
    private let lock = NSLock()
    private var formatDescription: CMVideoFormatDescription?
    private var cachedSps: Data?
    private var cachedPps: Data?
    private var gotKeyframe = false

    public init() {}

    public func reset() {
        lock.lock()
        formatDescription = nil
        cachedSps = nil
        cachedPps = nil
        gotKeyframe = false
        lock.unlock()
    }

    public func convert(annexB: Data, timestampMs: UInt32, isKeyframeHint: Bool) -> CMSampleBuffer? {
        guard !annexB.isEmpty else { return nil }
        let nalUnits = splitAnnexBNalUnits(annexB)
        guard !nalUnits.isEmpty else { return nil }

        lock.lock()
        defer { lock.unlock() }

        var spsChanged = false
        var ppsChanged = false
        var videoNals: [Data] = []
        var hasIdr = false

        for nal in nalUnits {
            guard let first = nal.first else { continue }
            let nalType = first & 0x1F
            switch nalType {
            case 7: // SPS (剥离末尾可能由 4 字节起始码切分残留的 0x00)
                let cleanSps = stripTrailingZeroBytes(nal)
                if !cleanSps.isEmpty && cachedSps != cleanSps {
                    cachedSps = cleanSps
                    spsChanged = true
                }
            case 8: // PPS
                let cleanPps = stripTrailingZeroBytes(nal)
                if !cleanPps.isEmpty && cachedPps != cleanPps {
                    cachedPps = cleanPps
                    ppsChanged = true
                }
            case 5: // IDR (仅依据实际 NAL=5 判断关键帧，防止误把非 IDR 当同步帧喂给 VideoToolbox)
                hasIdr = true
                videoNals.append(nal)
            case 1, 2, 3, 4: // Non-IDR VCL slices
                videoNals.append(nal)
            default:
                break // Skip AUD/SEI
            }
        }

        if (spsChanged || ppsChanged || formatDescription == nil),
           let sps = cachedSps, let pps = cachedPps {
            formatDescription = createFormatDescription(sps: sps, pps: pps)
            gotKeyframe = false
        }

        guard let fmtDesc = formatDescription, !videoNals.isEmpty else { return nil }
        if !gotKeyframe {
            guard hasIdr else { return nil }
            gotKeyframe = true
        }

        // Pack into AVCC (4-byte big-endian length prefix per NAL)
        var avcc = Data()
        for nal in videoNals {
            var lenBE = UInt32(nal.count).bigEndian
            withUnsafeBytes(of: &lenBE) { avcc.append(contentsOf: $0) }
            avcc.append(nal)
        }

        var blockBuffer: CMBlockBuffer?
        let totalLen = avcc.count
        var status = CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault,
            memoryBlock: nil,
            blockLength: totalLen,
            blockAllocator: kCFAllocatorDefault,
            customBlockSource: nil,
            offsetToData: 0,
            dataLength: totalLen,
            flags: 0,
            blockBufferOut: &blockBuffer
        )
        guard status == kCMBlockBufferNoErr, let bb = blockBuffer else { return nil }

        status = avcc.withUnsafeBytes { rawBuf in
            CMBlockBufferReplaceDataBytes(
                with: rawBuf.baseAddress!,
                blockBuffer: bb,
                offsetIntoDestination: 0,
                dataLength: totalLen
            )
        }
        guard status == kCMBlockBufferNoErr else { return nil }

        let pts = CMTime(value: CMTimeValue(timestampMs), timescale: 1000)
        var timing = CMSampleTimingInfo(duration: .invalid, presentationTimeStamp: pts, decodeTimeStamp: .invalid)
        var sampleSize = totalLen
        var sampleBuffer: CMSampleBuffer?
        status = CMSampleBufferCreateReady(
            allocator: kCFAllocatorDefault,
            dataBuffer: bb,
            formatDescription: fmtDesc,
            sampleCount: 1,
            sampleTimingEntryCount: 1,
            sampleTimingArray: &timing,
            sampleSizeEntryCount: 1,
            sampleSizeArray: &sampleSize,
            sampleBufferOut: &sampleBuffer
        )
        guard status == noErr, let sb = sampleBuffer else { return nil }

        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sb, createIfNecessary: true) as? [NSMutableDictionary],
           let firstDict = attachments.first {
            firstDict[kCMSampleAttachmentKey_DisplayImmediately] = kCFBooleanTrue
            if !hasIdr {
                firstDict[kCMSampleAttachmentKey_NotSync] = kCFBooleanTrue
            }
        }
        return sb
    }

    private func createFormatDescription(sps: Data, pps: Data) -> CMVideoFormatDescription? {
        var desc: CMVideoFormatDescription?
        let status: OSStatus = sps.withUnsafeBytes { spsBuf in
            pps.withUnsafeBytes { ppsBuf in
                guard let spsPtr = spsBuf.bindMemory(to: UInt8.self).baseAddress,
                      let ppsPtr = ppsBuf.bindMemory(to: UInt8.self).baseAddress else {
                    return -1
                }
                let ptrs: [UnsafePointer<UInt8>] = [spsPtr, ppsPtr]
                let sizes: [Int] = [sps.count, pps.count]
                return CMVideoFormatDescriptionCreateFromH264ParameterSets(
                    allocator: kCFAllocatorDefault,
                    parameterSetCount: 2,
                    parameterSetPointers: ptrs,
                    parameterSetSizes: sizes,
                    nalUnitHeaderLength: 4,
                    formatDescriptionOut: &desc
                )
            }
        }
        return status == noErr ? desc : nil
    }

    private func stripTrailingZeroBytes(_ data: Data) -> Data {
        var end = data.endIndex
        while end > data.startIndex && data[data.index(before: end)] == 0 {
            end = data.index(before: end)
        }
        return end == data.endIndex ? data : Data(data[data.startIndex..<end])
    }

    private func splitAnnexBNalUnits(_ data: Data) -> [Data] {
        let n = data.count
        guard n >= 4 else { return [] }
        return data.withUnsafeBytes { rawBuf -> [Data] in
            guard let base = rawBuf.bindMemory(to: UInt8.self).baseAddress else { return [] }
            var starts: [(offset: Int, codeLen: Int)] = []
            var i = 0
            while i + 3 < n {
                if base[i] == 0 && base[i + 1] == 0 {
                    if base[i + 2] == 1 {
                        starts.append((i, 3))
                        i += 3
                        continue
                    } else if base[i + 2] == 0 && base[i + 3] == 1 {
                        starts.append((i, 4))
                        i += 4
                        continue
                    }
                }
                i += 1
            }
            guard !starts.isEmpty else { return [] }
            var out: [Data] = []
            out.reserveCapacity(starts.count)
            for idx in 0..<starts.count {
                let nalStart = starts[idx].offset + starts[idx].codeLen
                let nalEnd = (idx + 1 < starts.count) ? starts[idx + 1].offset : n
                if nalEnd > nalStart {
                    out.append(Data(bytes: base.advanced(by: nalStart), count: nalEnd - nalStart))
                }
            }
            return out
        }
    }
}
