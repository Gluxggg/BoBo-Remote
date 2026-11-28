import CryptoKit
import Foundation
import Network

public enum BoBoUdpPacketType: UInt8, Sendable {
    case hello = 1
    case helloAck = 2
    case videoShard = 3
    case audio = 4
    case input = 5
    case ping = 6
    case pong = 7
    case cursorShape = 8
    case feedback = 9
    case nack = 10
    case audioAac = 11
}

public struct OpenedUdpPacket: Sendable {
    public let packetType: UInt8
    public let seq: UInt32
    public let plaintext: Data

    public init(packetType: UInt8, seq: UInt32, plaintext: Data) {
        self.packetType = packetType
        self.seq = seq
        self.plaintext = plaintext
    }
}

public struct BoBoUdpNackRequest: Equatable, Sendable {
    public let frameId: UInt32
    public let shardIdx: UInt16

    public init(frameId: UInt32, shardIdx: UInt16) {
        self.frameId = frameId
        self.shardIdx = shardIdx
    }
}

public struct BoBoReassembledVideoFrame: Sendable {
    public let frameId: UInt32
    public let timestampMs: UInt32
    public let width: Int
    public let height: Int
    public let isKeyframe: Bool
    public let h264Data: Data
}

public enum BoBoUdpCryptoError: Error {
    case invalidKeyLength
    case nonceExhausted
    case disposed
}

/// Standard AEAD AES-128-GCM (128-bit tag) UDP transport crypto matching Windows C# CNG and Android JCA.
public final class BoBoUdpAesGcmCrypto: @unchecked Sendable {
    public static let headerBytes = 9
    public static let gcmTagBytes = 16
    public static let gcmOverheadBytes = headerBytes + gcmTagBytes
    public static let maxSafeSequence: UInt32 = 0xFFFF_FF00

    public let sessionId: UInt32
    private let sendDir: UInt8
    private let recvDir: UInt8
    private let encKey: SymmetricKey
    private let decKey: SymmetricKey

    private let encLock = NSLock()
    private let decLock = NSLock()
    private var sendSeq: UInt32 = 0
    private var hasRecvSeq = false
    private var maxRecvSeq: UInt32 = 0
    private var replayMask = [UInt64](repeating: 0, count: 8)
    private var closed = false

    public static func deriveDirectionKey(masterKey32: Data, sessionId: UInt32, label: String) -> Data {
        var msg = Data(label.utf8)
        var sidLE = sessionId.littleEndian
        withUnsafeBytes(of: &sidLE) { msg.append(contentsOf: $0) }
        let symMaster = SymmetricKey(data: masterKey32)
        let mac = HMAC<SHA256>.authenticationCode(for: msg, using: symMaster)
        return Data(mac.prefix(16))
    }

    public init(sessionId: UInt32, masterKey32: Data, isHost: Bool) throws {
        guard masterKey32.count >= 32 else { throw BoBoUdpCryptoError.invalidKeyLength }
        self.sessionId = sessionId
        self.sendDir = isHost ? 1 : 0
        self.recvDir = isHost ? 0 : 1
        var h2c = Self.deriveDirectionKey(masterKey32: masterKey32, sessionId: sessionId, label: "BoBo7.3-UDP-GCM-H2C")
        var c2h = Self.deriveDirectionKey(masterKey32: masterKey32, sessionId: sessionId, label: "BoBo7.3-UDP-GCM-C2H")
        defer {
            h2c.resetBytes(in: 0..<h2c.count)
            c2h.resetBytes(in: 0..<c2h.count)
        }
        self.encKey = SymmetricKey(data: isHost ? h2c : c2h)
        self.decKey = SymmetricKey(data: isHost ? c2h : h2c)
    }

    public func forceSendSeqForTest(_ nextBeforeIncrement: UInt32) {
        encLock.lock()
        sendSeq = nextBeforeIncrement
        encLock.unlock()
    }

    public func seal(packetType: UInt8, plaintext: Data) throws -> Data? {
        encLock.lock()
        defer { encLock.unlock() }
        guard !closed else { return nil }
        guard sendSeq < Self.maxSafeSequence else {
            throw BoBoUdpCryptoError.nonceExhausted
        }
        sendSeq += 1
        let seq = sendSeq

        var header = Data(capacity: Self.headerBytes)
        BoBoProtocol.appendUInt32LE(&header, sessionId)
        BoBoProtocol.appendUInt32LE(&header, seq)
        header.append(packetType)

        var nonceData = Data(capacity: 12)
        nonceData.append(header)
        nonceData.append(sendDir)
        nonceData.append(0)
        nonceData.append(0)

        let nonce = try AES.GCM.Nonce(data: nonceData)
        let sealed = try AES.GCM.seal(plaintext, using: encKey, nonce: nonce, authenticating: header)
        var out = Data(capacity: Self.gcmOverheadBytes + plaintext.count)
        out.append(header)
        out.append(sealed.ciphertext)
        out.append(sealed.tag)
        return out
    }

    public func open(datagram: Data) -> OpenedUdpPacket? {
        guard datagram.count >= Self.gcmOverheadBytes else { return nil }
        let dg = datagram.startIndex
        guard BoBoProtocol.readUInt32LE(datagram, offset: 0) == sessionId else { return nil }
        let seq = BoBoProtocol.readUInt32LE(datagram, offset: 4)
        let pktType = datagram[dg + 8]

        decLock.lock()
        defer { decLock.unlock() }
        guard !closed else { return nil }
        // Read-only pre-check: forged packets never mutate anti-replay state
        guard checkReplayAcceptableLocked(seq: seq) else { return nil }

        let header = datagram.subdata(in: dg..<(dg + Self.headerBytes))
        var nonceData = Data(capacity: 12)
        nonceData.append(header)
        nonceData.append(recvDir)
        nonceData.append(0)
        nonceData.append(0)

        let tagStart = dg + datagram.count - Self.gcmTagBytes
        let ciphertext = datagram.subdata(in: (dg + Self.headerBytes)..<tagStart)
        let tag = datagram.subdata(in: tagStart..<(dg + datagram.count))

        let plain: Data
        do {
            let nonce = try AES.GCM.Nonce(data: nonceData)
            let box = try AES.GCM.SealedBox(nonce: nonce, ciphertext: ciphertext, tag: tag)
            plain = try AES.GCM.open(box, using: decKey, authenticating: header)
        } catch {
            return nil
        }

        guard commitReplayLocked(seq: seq) else { return nil }
        return OpenedUdpPacket(packetType: pktType, seq: seq, plaintext: plain)
    }

    private func checkReplayAcceptableLocked(seq: UInt32) -> Bool {
        guard hasRecvSeq else { return true }
        let delta = Int32(bitPattern: seq &- maxRecvSeq)
        if delta == 0 { return false }
        if delta > 0 { return true }
        let back = Int(-Int64(delta))
        if back >= 512 { return false }
        let wordIdx = back >> 6
        let bit = UInt64(1) << UInt64(back & 63)
        return (replayMask[wordIdx] & bit) == 0
    }

    private func commitReplayLocked(seq: UInt32) -> Bool {
        if !hasRecvSeq {
            hasRecvSeq = true
            maxRecvSeq = seq
            replayMask = [UInt64](repeating: 0, count: 8)
            replayMask[0] = 1
            return true
        }
        let delta = Int32(bitPattern: seq &- maxRecvSeq)
        if delta == 0 { return false }
        if delta < 0 {
            let back = Int(-Int64(delta))
            if back >= 512 { return false }
            let wordIdx = back >> 6
            let bit = UInt64(1) << UInt64(back & 63)
            if (replayMask[wordIdx] & bit) != 0 { return false }
            replayMask[wordIdx] |= bit
            return true
        }
        shiftReplayWindowLeft(&replayMask, shift: Int(delta))
        replayMask[0] |= 1
        maxRecvSeq = seq
        return true
    }

    private func shiftReplayWindowLeft(_ words: inout [UInt64], shift: Int) {
        let n = words.count
        if shift >= n * 64 {
            for i in 0..<n { words[i] = 0 }
            return
        }
        let wordShift = shift >> 6
        let bitShift = shift & 63
        for i in stride(from: n - 1, through: 0, by: -1) {
            let src = i - wordShift
            var v: UInt64 = src >= 0 ? words[src] : 0
            if bitShift != 0 && src >= 0 {
                let carry: UInt64 = (src - 1 >= 0) ? (words[src - 1] >> UInt64(64 - bitShift)) : 0
                v = (v << UInt64(bitShift)) | carry
            }
            words[i] = v
        }
    }

    public func close() {
        encLock.lock()
        closed = true
        encLock.unlock()
        decLock.lock()
        closed = true
        decLock.unlock()
    }
}

public enum BoBoUdpNackCodec {
    public static let maxEntriesPerPacket = 48

    public static func encode(_ requests: [BoBoUdpNackRequest]) -> Data? {
        guard !requests.isEmpty else { return nil }
        let count = min(requests.count, maxEntriesPerPacket)
        var buf = Data(capacity: 2 + count * 6)
        BoBoProtocol.appendUInt16LE(&buf, UInt16(count))
        for i in 0..<count {
            BoBoProtocol.appendUInt32LE(&buf, requests[i].frameId)
            BoBoProtocol.appendUInt16LE(&buf, requests[i].shardIdx)
        }
        return buf
    }

    public static func decode(_ payload: Data) -> [BoBoUdpNackRequest] {
        guard payload.count >= 2 else { return [] }
        let count = min(Int(BoBoProtocol.readUInt16LE(payload, offset: 0)), maxEntriesPerPacket)
        var out = [BoBoUdpNackRequest]()
        out.reserveCapacity(count)
        var pos = 2
        for _ in 0..<count {
            if pos + 6 > payload.count { break }
            let fid = BoBoProtocol.readUInt32LE(payload, offset: pos)
            let sidx = BoBoProtocol.readUInt16LE(payload, offset: pos + 4)
            out.append(BoBoUdpNackRequest(frameId: fid, shardIdx: sidx))
            pos += 6
        }
        return out
    }
}

public enum BoBoUdpFec {
    public static let maxShardPayloadBytes = 1140
    public static let shardHeaderBytes = 19
    public static let flagKeyframe: UInt8 = 1
    public static let flagParity: UInt8 = 2
    public static let flagInterleaved: UInt8 = 4
    public static let flagRetransmit: UInt8 = 8

    public static func buildVideoShards(
        frameId: UInt32,
        timestampMs: UInt32,
        width: Int,
        height: Int,
        isKeyframe: Bool,
        h264Data: Data,
        groupSize: Int = 3,
        interleavedFec: Bool = true
    ) -> [Data] {
        guard !h264Data.isEmpty else { return [] }
        let totalLen = h264Data.count
        let dataCount = (totalLen + maxShardPayloadBytes - 1) / maxShardPayloadBytes
        var effGroup = max(1, groupSize)
        var parityCount = (dataCount + effGroup - 1) / effGroup
        if parityCount > 255 {
            effGroup = (dataCount + 254) / 255
            parityCount = (dataCount + effGroup - 1) / effGroup
        }
        let useInterleaved = interleavedFec && parityCount > 0
        let dataFlags: UInt8 = (isKeyframe ? flagKeyframe : 0) | (useInterleaved ? flagInterleaved : 0)
        var shards = [Data]()
        shards.reserveCapacity(dataCount + parityCount)

        let base = h264Data.startIndex
        for i in 0..<dataCount {
            let off = i * maxShardPayloadBytes
            let len = min(maxShardPayloadBytes, totalLen - off)
            var pkt = makeShardHeader(
                frameId: frameId, timestampMs: timestampMs, width: width, height: height,
                flags: dataFlags, shardIdx: UInt16(i), dataCount: UInt16(dataCount),
                groupSize: UInt8(effGroup), parityCount: UInt8(parityCount)
            )
            pkt.append(h264Data.subdata(in: (base + off)..<(base + off + len)))
            shards.append(pkt)
        }

        let parityFlags = dataFlags | flagParity
        for g in 0..<parityCount {
            let start = useInterleaved ? g : g * effGroup
            let end = useInterleaved ? dataCount : min(dataCount, start + effGroup)
            let step = useInterleaved ? parityCount : 1
            let maxMemberLen = min(maxShardPayloadBytes, totalLen - start * maxShardPayloadBytes)
            var xorBody = [UInt8](repeating: 0, count: 2 + maxMemberLen)
            var xorLen: UInt16 = 0
            var idx = start
            while idx < end {
                let off = idx * maxShardPayloadBytes
                let len = min(maxShardPayloadBytes, totalLen - off)
                xorLen ^= UInt16(len)
                for k in 0..<len {
                    xorBody[2 + k] ^= h264Data[base + off + k]
                }
                idx += step
            }
            xorBody[0] = UInt8(xorLen & 0xFF)
            xorBody[1] = UInt8((xorLen >> 8) & 0xFF)
            var pkt = makeShardHeader(
                frameId: frameId, timestampMs: timestampMs, width: width, height: height,
                flags: parityFlags, shardIdx: UInt16(g), dataCount: UInt16(dataCount),
                groupSize: UInt8(effGroup), parityCount: UInt8(parityCount)
            )
            pkt.append(contentsOf: xorBody)
            shards.append(pkt)
        }
        return shards
    }

    private static func makeShardHeader(
        frameId: UInt32,
        timestampMs: UInt32,
        width: Int,
        height: Int,
        flags: UInt8,
        shardIdx: UInt16,
        dataCount: UInt16,
        groupSize: UInt8,
        parityCount: UInt8
    ) -> Data {
        var pkt = Data(capacity: shardHeaderBytes)
        BoBoProtocol.appendUInt32LE(&pkt, frameId)
        BoBoProtocol.appendUInt32LE(&pkt, timestampMs)
        BoBoProtocol.appendUInt16LE(&pkt, UInt16(truncatingIfNeeded: width))
        BoBoProtocol.appendUInt16LE(&pkt, UInt16(truncatingIfNeeded: height))
        pkt.append(flags)
        BoBoProtocol.appendUInt16LE(&pkt, shardIdx)
        BoBoProtocol.appendUInt16LE(&pkt, dataCount)
        pkt.append(groupSize)
        pkt.append(parityCount)
        return pkt
    }
}

/// Reassembles encrypted UDP video shards with grouped/interleaved XOR FEC recovery + selective NACK + PLI.
public final class BoBoUdpVideoAssembler: @unchecked Sendable {
    private final class FrameBucket {
        let frameId: UInt32
        let timestampMs: UInt32
        let width: Int
        let height: Int
        let isKeyframe: Bool
        let dataCount: Int
        let groupSize: Int
        let parityCount: Int
        let interleavedFec: Bool
        var lastShardNs: UInt64
        var dataShards: [Data?]
        var parityShards: [Data?]
        var receivedDataCount = 0
        var highestDataIdxSeen = -1
        var anyParitySeen = false
        var isComplete = false
        var assembledData: Data?
        var nackSentNs: [UInt64]?
        var nackRetries: [UInt8]?

        init(
            frameId: UInt32, timestampMs: UInt32, width: Int, height: Int, isKeyframe: Bool,
            dataCount: Int, groupSize: Int, parityCount: Int, interleavedFec: Bool, nowNs: UInt64
        ) {
            self.frameId = frameId
            self.timestampMs = timestampMs
            self.width = width
            self.height = height
            self.isKeyframe = isKeyframe
            self.dataCount = dataCount
            self.groupSize = groupSize
            self.parityCount = parityCount
            self.interleavedFec = interleavedFec
            self.lastShardNs = nowNs
            self.dataShards = [Data?](repeating: nil, count: dataCount)
            self.parityShards = parityCount > 0 ? [Data?](repeating: nil, count: parityCount) : []
        }
    }

    private let nackEnabled: Bool
    private let lock = NSLock()
    private var buckets = [UInt32: FrameBucket]()
    private var hasDeliveredFrame = false
    private var lastDeliveredFrameId: UInt32 = 0
    private var awaitingKeyframe = true
    private var awaitingKeyframeSinceNs: UInt64 = 0
    private var lastPliNs: UInt64 = 0
    private var gapFirstSeenNs: UInt64 = 0

    private var hasSeqWindow = false
    private var seqWindowMax: UInt32 = 0
    private var seqWindowReceived: Int = 0
    private var seqWindowExpected: Int = 0
    private var seqWindowStartNs: UInt64 = 0

    private var _latestLossPct: Double = 0.0
    private var _fecRecoveredPackets: Int = 0
    private var _nackRequestsSent: Int = 0

    public var latestLossPct: Double { lock.withLock { _latestLossPct } }
    public var fecRecoveredPackets: Int { lock.withLock { _fecRecoveredPackets } }
    public var nackRequestsSent: Int { lock.withLock { _nackRequestsSent } }
    public var lastDeliveredId: UInt32 { lock.withLock { lastDeliveredFrameId } }

    public init(nackEnabled: Bool = true) {
        self.nackEnabled = nackEnabled
    }

    public func reset(nowNs: UInt64 = DispatchTime.now().uptimeNanoseconds) {
        lock.withLock {
            buckets.removeAll()
            hasDeliveredFrame = false
            lastDeliveredFrameId = 0
            awaitingKeyframe = true
            awaitingKeyframeSinceNs = nowNs
            gapFirstSeenNs = 0
            hasSeqWindow = false
            seqWindowReceived = 0
            seqWindowExpected = 0
            seqWindowStartNs = nowNs
            _latestLossPct = 0.0
        }
    }

    public func recordReceivedUdpSeq(_ seq: UInt32, nowNs: UInt64 = DispatchTime.now().uptimeNanoseconds) {
        lock.withLock {
            if !hasSeqWindow {
                hasSeqWindow = true
                seqWindowMax = seq
                seqWindowReceived = 1
                seqWindowExpected = 1
                seqWindowStartNs = nowNs
                return
            }
            seqWindowReceived += 1
            let delta = Int32(bitPattern: seq &- seqWindowMax)
            if delta > 0 {
                seqWindowExpected += Int(delta)
                seqWindowMax = seq
            }
            refreshLossLocked(nowNs: nowNs)
        }
    }

    private func refreshLossLocked(nowNs: UInt64) {
        guard hasSeqWindow, nowNs >= seqWindowStartNs else { return }
        let elapsedMs = Double(nowNs - seqWindowStartNs) / 1_000_000.0
        if elapsedMs >= 1000.0 && seqWindowExpected >= 10 {
            let lost = max(0, seqWindowExpected - seqWindowReceived)
            _latestLossPct = (Double(lost) * 100.0) / Double(seqWindowExpected)
            seqWindowReceived = 0
            seqWindowExpected = 0
            seqWindowStartNs = nowNs
        }
    }

    @discardableResult
    public func processShard(
        _ shardPayload: Data,
        currentRttMs: Double,
        outReady: inout [BoBoReassembledVideoFrame],
        nowNs: UInt64 = DispatchTime.now().uptimeNanoseconds
    ) -> Bool {
        guard shardPayload.count > BoBoUdpFec.shardHeaderBytes else { return false }
        let s = shardPayload.startIndex
        let frameId = BoBoProtocol.readUInt32LE(shardPayload, offset: 0)
        let tsMs = BoBoProtocol.readUInt32LE(shardPayload, offset: 4)
        let width = Int(BoBoProtocol.readUInt16LE(shardPayload, offset: 8))
        let height = Int(BoBoProtocol.readUInt16LE(shardPayload, offset: 10))
        let flags = shardPayload[s + 12]
        let isKey = (flags & BoBoUdpFec.flagKeyframe) != 0
        let isParity = (flags & BoBoUdpFec.flagParity) != 0
        let interleaved = (flags & BoBoUdpFec.flagInterleaved) != 0
        let shardIdx = Int(BoBoProtocol.readUInt16LE(shardPayload, offset: 13))
        let dataCount = Int(BoBoProtocol.readUInt16LE(shardPayload, offset: 15))
        let groupSize = max(1, Int(shardPayload[s + 17]))
        let parityCount = Int(shardPayload[s + 18])
        guard dataCount > 0 && dataCount <= 4096 else { return false }

        return lock.withLock {
            if hasDeliveredFrame && Int32(bitPattern: frameId &- lastDeliveredFrameId) <= 0 {
                return false
            }
            if awaitingKeyframe && !isKey {
                return shouldTriggerPliLocked(rttMs: currentRttMs, nowNs: nowNs)
            }

            let b: FrameBucket
            if let existing = buckets[frameId] {
                b = existing
                b.lastShardNs = nowNs
            } else {
                if buckets.count >= 12 {
                    pruneStaleLocked(newestFrameId: frameId, nowNs: nowNs)
                }
                let created = FrameBucket(
                    frameId: frameId, timestampMs: tsMs, width: width, height: height, isKeyframe: isKey,
                    dataCount: dataCount, groupSize: groupSize, parityCount: parityCount,
                    interleavedFec: interleaved, nowNs: nowNs
                )
                buckets[frameId] = created
                b = created
            }
            if b.isComplete { return false }

            let body = shardPayload.subdata(in: (s + BoBoUdpFec.shardHeaderBytes)..<shardPayload.endIndex)
            if isParity {
                if shardIdx >= 0 && shardIdx < b.parityShards.count && b.parityShards[shardIdx] == nil {
                    b.parityShards[shardIdx] = body
                    b.anyParitySeen = true
                    tryFecRecoverGroupLocked(b, groupIndex: shardIdx)
                }
            } else {
                if shardIdx >= 0 && shardIdx < b.dataCount && b.dataShards[shardIdx] == nil {
                    b.dataShards[shardIdx] = body
                    b.receivedDataCount += 1
                    if shardIdx > b.highestDataIdxSeen { b.highestDataIdxSeen = shardIdx }
                    if b.parityCount > 0 {
                        let g = b.interleavedFec ? (shardIdx % b.parityCount) : (shardIdx / b.groupSize)
                        tryFecRecoverGroupLocked(b, groupIndex: g)
                    }
                }
            }

            if b.receivedDataCount >= b.dataCount {
                assembleBucketLocked(b)
                if !hasDeliveredFrame || b.isKeyframe || Int32(bitPattern: b.frameId &- lastDeliveredFrameId) == 1 {
                    deliverFromBucketLocked(b, outReady: &outReady)
                }
            }
            return false
        }
    }

    public func pollRecovery(
        nowNs: UInt64,
        currentRttMs: Double,
        outNacks: inout [BoBoUdpNackRequest]
    ) -> Bool {
        outNacks.removeAll(keepingCapacity: true)
        return lock.withLock {
            refreshLossLocked(nowNs: nowNs)
            let baseRtt = max(6.0, min(350.0, currentRttMs > 0.5 ? currentRttMs : 20.0))
            if awaitingKeyframe {
                let waitMs = awaitingKeyframeSinceNs == 0 ? 1000.0 : Double(nowNs >= awaitingKeyframeSinceNs ? nowNs - awaitingKeyframeSinceNs : 0) / 1_000_000.0
                if waitMs >= max(260.0, min(900.0, baseRtt * 2.5 + 120.0)) {
                    return shouldTriggerPliLocked(rttMs: baseRtt, nowNs: nowNs)
                }
                return false
            }

            let ordered = buckets.values.sorted { Int32(bitPattern: $0.frameId &- $1.frameId) < 0 }
            if nackEnabled, let newestId = ordered.last?.frameId {
                let retryIntervalMs = max(14.0, min(80.0, baseRtt * 1.15))
                for b in ordered {
                    if b.isComplete || outNacks.count >= BoBoUdpNackCodec.maxEntriesPerPacket { continue }
                    let hasNewer = Int32(bitPattern: newestId &- b.frameId) > 0
                    let idleMs = Double(nowNs >= b.lastShardNs ? nowNs - b.lastShardNs : 0) / 1_000_000.0
                    let groupCount = max(1, b.parityCount)
                    for g in 0..<groupCount {
                        if outNacks.count >= BoBoUdpNackCodec.maxEntriesPerPacket { break }
                        let start = (b.interleavedFec && b.parityCount > 0) ? g : (b.parityCount > 0 ? g * b.groupSize : 0)
                        let step = (b.interleavedFec && b.parityCount > 0) ? b.parityCount : 1
                        let end = (b.interleavedFec && b.parityCount > 0) ? b.dataCount : (b.parityCount > 0 ? min(b.dataCount, start + b.groupSize) : b.dataCount)
                        var missingCount = 0
                        var firstMissing = -1
                        var lastMissing = -1
                        var idx = start
                        while idx < end {
                            if b.dataShards[idx] == nil {
                                if firstMissing < 0 { firstMissing = idx }
                                lastMissing = idx
                                missingCount += 1
                            }
                            idx += step
                        }
                        if missingCount == 0 { continue }
                        let eligible: Bool
                        if missingCount >= 2 {
                            eligible = hasNewer || b.anyParitySeen || idleMs >= 8.0 || b.highestDataIdxSeen >= lastMissing + 3
                        } else if b.parityCount == 0 {
                            eligible = hasNewer || idleMs >= 10.0 || b.highestDataIdxSeen >= firstMissing + 3
                        } else {
                            eligible = hasNewer || idleMs >= 14.0
                        }
                        if !eligible { continue }
                        if b.nackSentNs == nil {
                            b.nackSentNs = [UInt64](repeating: 0, count: b.dataCount)
                            b.nackRetries = [UInt8](repeating: 0, count: b.dataCount)
                        }
                        idx = start
                        while idx < end && outNacks.count < BoBoUdpNackCodec.maxEntriesPerPacket {
                            if b.dataShards[idx] == nil && (b.nackRetries?[idx] ?? 3) < 3 {
                                let prev = b.nackSentNs?[idx] ?? 0
                                let sinceMs = prev == 0 ? Double.greatestFiniteMagnitude : Double(nowNs >= prev ? nowNs - prev : 0) / 1_000_000.0
                                if prev == 0 || sinceMs >= retryIntervalMs {
                                    b.nackSentNs?[idx] = nowNs
                                    b.nackRetries?[idx] += 1
                                    outNacks.append(BoBoUdpNackRequest(frameId: b.frameId, shardIdx: UInt16(idx)))
                                    _nackRequestsSent += 1
                                }
                            }
                            idx += step
                        }
                    }
                }
            }

            let nackGraceMs = max(80.0, min(240.0, baseRtt * 2.2 + 35.0))
            if hasDeliveredFrame, let future = ordered.first(where: { $0.isComplete && Int32(bitPattern: $0.frameId &- lastDeliveredFrameId) > 1 }) {
                if gapFirstSeenNs == 0 { gapFirstSeenNs = nowNs }
                let waitMs = Double(nowNs >= gapFirstSeenNs ? nowNs - gapFirstSeenNs : 0) / 1_000_000.0
                let ahead = Int32(bitPattern: future.frameId &- lastDeliveredFrameId)
                if waitMs >= nackGraceMs || ahead >= 6 {
                    buckets.removeAll()
                    gapFirstSeenNs = 0
                    awaitingKeyframe = true
                    awaitingKeyframeSinceNs = nowNs
                    return shouldTriggerPliLocked(rttMs: baseRtt, nowNs: nowNs)
                }
            }

            if let head = ordered.first(where: { !$0.isComplete }) {
                let idleMs = Double(nowNs >= head.lastShardNs ? nowNs - head.lastShardNs : 0) / 1_000_000.0
                let limitMs = head.isKeyframe ? max(260.0, min(450.0, baseRtt * 2.6 + 80.0)) : max(180.0, nackGraceMs + 40.0)
                if idleMs >= limitMs {
                    buckets.removeAll()
                    gapFirstSeenNs = 0
                    awaitingKeyframe = true
                    awaitingKeyframeSinceNs = nowNs
                    return shouldTriggerPliLocked(rttMs: baseRtt, nowNs: nowNs)
                }
            }
            return false
        }
    }

    private func tryFecRecoverGroupLocked(_ b: FrameBucket, groupIndex g: Int) {
        guard g >= 0 && g < b.parityShards.count, let par = b.parityShards[g], par.count >= 2 else { return }
        let start = b.interleavedFec ? g : g * b.groupSize
        let step = b.interleavedFec ? b.parityCount : 1
        let end = b.interleavedFec ? b.dataCount : min(b.dataCount, start + b.groupSize)
        guard step > 0 else { return }

        var missingIdx = -1
        var missingCount = 0
        var i = start
        while i < end {
            if b.dataShards[i] == nil {
                missingIdx = i
                missingCount += 1
                if missingCount > 1 { return }
            }
            i += step
        }
        guard missingCount == 1 && missingIdx >= 0 else { return }

        var recLen = BoBoProtocol.readUInt16LE(par, offset: 0)
        i = start
        while i < end {
            if i != missingIdx, let s = b.dataShards[i] { recLen ^= UInt16(s.count) }
            i += step
        }
        guard recLen > 0 && Int(recLen) <= BoBoUdpFec.maxShardPayloadBytes && Int(recLen) + 2 <= par.count else { return }

        let pBase = par.startIndex
        var recovered = [UInt8](par[(pBase + 2)..<(pBase + 2 + Int(recLen))])
        i = start
        while i < end {
            if i != missingIdx, let other = b.dataShards[i] {
                let oBase = other.startIndex
                let n = min(Int(recLen), other.count)
                for k in 0..<n { recovered[k] ^= other[oBase + k] }
            }
            i += step
        }
        b.dataShards[missingIdx] = Data(recovered)
        b.receivedDataCount += 1
        _fecRecoveredPackets += 1
    }

    private func assembleBucketLocked(_ b: FrameBucket) {
        var total = 0
        for s in b.dataShards { total += s?.count ?? 0 }
        var full = Data(capacity: total)
        for s in b.dataShards {
            if let s { full.append(s) }
        }
        b.assembledData = full
        b.isComplete = true
    }

    private func deliverFromBucketLocked(_ first: FrameBucket, outReady: inout [BoBoReassembledVideoFrame]) {
        var cur: FrameBucket? = first
        while let b = cur, b.isComplete, let payload = b.assembledData {
            let fid = b.frameId
            outReady.append(BoBoReassembledVideoFrame(
                frameId: fid, timestampMs: b.timestampMs, width: b.width, height: b.height,
                isKeyframe: b.isKeyframe, h264Data: payload
            ))
            lastDeliveredFrameId = fid
            hasDeliveredFrame = true
            if b.isKeyframe {
                awaitingKeyframe = false
                awaitingKeyframeSinceNs = 0
            }
            buckets.removeValue(forKey: fid)
            let older = buckets.keys.filter { Int32(bitPattern: $0 &- fid) < 0 }
            for k in older { buckets.removeValue(forKey: k) }
            gapFirstSeenNs = 0
            let nextId = fid &+ 1
            if let nextB = buckets[nextId], nextB.isComplete {
                cur = nextB
            } else {
                cur = nil
            }
        }
    }

    private func pruneStaleLocked(newestFrameId: UInt32, nowNs: UInt64) {
        let toRemove = buckets.keys.filter { Int32(bitPattern: newestFrameId &- $0) >= 8 }
        for k in toRemove { buckets.removeValue(forKey: k) }
        if !toRemove.isEmpty && !awaitingKeyframe {
            awaitingKeyframe = true
            awaitingKeyframeSinceNs = nowNs
        }
    }

    private func shouldTriggerPliLocked(rttMs: Double, nowNs: UInt64) -> Bool {
        let elapsedMs = lastPliNs == 0 ? 10000.0 : Double(nowNs >= lastPliNs ? nowNs - lastPliNs : 0) / 1_000_000.0
        let cooldown = max(500.0, min(1200.0, rttMs * 4.0))
        if lastPliNs == 0 || elapsedMs >= cooldown {
            lastPliNs = nowNs
            return true
        }
        return false
    }
}

/// Encrypted UDP + AES-128-GCM + FEC/NACK client channel for macOS / iOS (Network.framework + CryptoKit).
public final class BoBoUdpChannel: @unchecked Sendable {
    private let connection: NWConnection
    private let queue = DispatchQueue(label: "com.bobo.remote.udp", qos: .userInteractive)
    private let crypto: BoBoUdpAesGcmCrypto
    private let assembler: BoBoUdpVideoAssembler
    private let stateLock = NSLock()

    private var timer: DispatchSourceTimer?
    private var started = false
    private var _activated = false
    private var _closed = false
    private var lastRxNs: UInt64 = 0
    private var startNs: UInt64 = 0
    private var latestRttMs: Double = 20.0
    private var prevRttMs: Double = 0.0
    private var latestJitterMs: Double = 0.0
    private var helloBurstRemaining = 15

    private let onVideo: @Sendable (BoBoVideoPayload, UInt32) -> Void
    private let onAudio: @Sendable (BoBoAudioPayload) -> Void
    private let onRtt: @Sendable (Double) -> Void
    private let onFallback: @Sendable () -> Void

    public var isActive: Bool {
        stateLock.withLock { !_closed && _activated }
    }

    public init?(
        host: String,
        port: UInt16,
        sessionId: UInt32,
        keyBase64: String,
        nackEnabled: Bool,
        onVideo: @escaping @Sendable (BoBoVideoPayload, UInt32) -> Void,
        onAudio: @escaping @Sendable (BoBoAudioPayload) -> Void,
        onRtt: @escaping @Sendable (Double) -> Void,
        onFallback: @escaping @Sendable () -> Void
    ) {
        guard let nwPort = NWEndpoint.Port(rawValue: port),
              let keyData = Data(base64Encoded: keyBase64),
              let cr = try? BoBoUdpAesGcmCrypto(sessionId: sessionId, masterKey32: keyData, isHost: false) else {
            return nil
        }
        self.crypto = cr
        self.assembler = BoBoUdpVideoAssembler(nackEnabled: nackEnabled)
        self.connection = NWConnection(host: NWEndpoint.Host(host), port: nwPort, using: .udp)
        self.onVideo = onVideo
        self.onAudio = onAudio
        self.onRtt = onRtt
        self.onFallback = onFallback
    }

    public func start() {
        stateLock.lock()
        guard !started && !_closed else { stateLock.unlock(); return }
        started = true
        startNs = DispatchTime.now().uptimeNanoseconds
        stateLock.unlock()

        connection.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            switch state {
            case .ready:
                self.sendHello()
                self.scheduleTimer()
                self.receiveNext()
            case .failed, .cancelled:
                self.close()
            default:
                break
            }
        }
        connection.start(queue: queue)
    }

    private func scheduleTimer() {
        stateLock.withLock {
            guard !_closed && timer == nil else { return }
            let t = DispatchSource.makeTimerSource(queue: queue)
            t.schedule(deadline: .now() + .milliseconds(50), repeating: .milliseconds(200))
            t.setEventHandler { [weak self] in self?.onTimerTick() }
            t.resume()
            timer = t
        }
    }

    private func onTimerTick() {
        let nowNs = DispatchTime.now().uptimeNanoseconds
        enum Action { case hello, pingAndFeedback, fallback, stop }
        let action: Action = stateLock.withLock {
            if _closed { return .stop }
            if _activated {
                let silenceMs = lastRxNs == 0 ? 0.0 : Double(nowNs >= lastRxNs ? nowNs - lastRxNs : 0) / 1_000_000.0
                if silenceMs > 3500.0 {
                    _activated = false
                    _closed = true
                    return .fallback
                }
                return .pingAndFeedback
            }
            let waitMs = Double(nowNs >= startNs ? nowNs - startNs : 0) / 1_000_000.0
            if waitMs > 3500.0 {
                _closed = true
                return .stop
            }
            if helloBurstRemaining > 0 { helloBurstRemaining -= 1 }
            return .hello
        }

        switch action {
        case .hello:
            sendHello()
        case .pingAndFeedback:
            var pingBody = Data(capacity: 8)
            BoBoProtocol.appendInt64LE(&pingBody, Int64(bitPattern: nowNs))
            sendRaw(type: .ping, payload: pingBody)
            sendFeedback(requestPli: false)
            var nacks = [BoBoUdpNackRequest]()
            let rtt = stateLock.withLock { latestRttMs }
            if assembler.pollRecovery(nowNs: nowNs, currentRttMs: rtt, outNacks: &nacks) {
                sendFeedback(requestPli: true)
            }
            if let nackPayload = BoBoUdpNackCodec.encode(nacks) {
                sendRaw(type: .nack, payload: nackPayload)
            }
        case .fallback:
            onFallback()
            close()
        case .stop:
            close()
        }
    }

    private func receiveNext() {
        connection.receiveMessage { [weak self] content, _, _, error in
            guard let self else { return }
            if let data = content, !data.isEmpty, let opened = self.crypto.open(datagram: data) {
                self.handleOpenedPacket(opened)
            }
            let keepGoing = self.stateLock.withLock { !self._closed }
            if error == nil && keepGoing {
                self.receiveNext()
            }
        }
    }

    private func handleOpenedPacket(_ opened: OpenedUdpPacket) {
        let nowNs = DispatchTime.now().uptimeNanoseconds
        guard let ptype = BoBoUdpPacketType(rawValue: opened.packetType) else { return }
        switch ptype {
        case .helloAck:
            let becameActive = stateLock.withLock { () -> Bool in
                guard !_closed else { return false }
                lastRxNs = nowNs
                if !_activated {
                    _activated = true
                    return true
                }
                return false
            }
            if becameActive { assembler.reset(nowNs: nowNs) }
        case .videoShard:
            let rtt: Double? = stateLock.withLock {
                guard !_closed && _activated else { return nil }
                lastRxNs = nowNs
                return latestRttMs
            }
            guard let curRtt = rtt else { return }
            assembler.recordReceivedUdpSeq(opened.seq, nowNs: nowNs)
            var ready = [BoBoReassembledVideoFrame]()
            let needPli = assembler.processShard(opened.plaintext, currentRttMs: curRtt, outReady: &ready, nowNs: nowNs)
            for f in ready {
                onVideo(BoBoVideoPayload(width: f.width, height: f.height, isKeyframe: f.isKeyframe, h264Data: f.h264Data), f.timestampMs)
            }
            var nacks = [BoBoUdpNackRequest]()
            let pollPli = assembler.pollRecovery(nowNs: nowNs, currentRttMs: curRtt, outNacks: &nacks)
            if let nackPayload = BoBoUdpNackCodec.encode(nacks) {
                sendRaw(type: .nack, payload: nackPayload)
            }
            if needPli || pollPli {
                sendFeedback(requestPli: true)
            }
        case .audio:
            let ok = stateLock.withLock {
                guard !_closed && _activated else { return false }
                lastRxNs = nowNs
                return true
            }
            guard ok else { return }
            assembler.recordReceivedUdpSeq(opened.seq, nowNs: nowNs)
            if let ap = BoBoProtocol.parseAudioPayload(opened.plaintext) {
                onAudio(ap)
            }
        case .pong:
            guard opened.plaintext.count >= 8 else { return }
            let sentNs = UInt64(bitPattern: BoBoProtocol.readInt64LE(opened.plaintext, offset: 0))
            guard nowNs >= sentNs else { return }
            let rtt = Double(nowNs - sentNs) / 1_000_000.0
            guard rtt >= 0 && rtt < 10_000 else { return }
            stateLock.withLock {
                lastRxNs = nowNs
                if prevRttMs > 0.1 {
                    latestJitterMs = latestJitterMs * 0.75 + abs(rtt - prevRttMs) * 0.25
                }
                prevRttMs = rtt
                latestRttMs = rtt
            }
            onRtt(rtt)
        default:
            break
        }
    }

    private func sendHello() {
        sendRaw(type: .hello, payload: Data([0x42, 0x4F, 0x36, 0x33]))
    }

    private func sendFeedback(requestPli: Bool) {
        let (rtt, jitter) = stateLock.withLock { (latestRttMs, latestJitterMs) }
        var fb = Data(capacity: 11)
        let lossX100 = UInt16(max(0, min(10000, Int((assembler.latestLossPct * 100.0).rounded()))))
        let rttX10 = UInt16(max(0, min(65000, Int((rtt * 10.0).rounded()))))
        let jitterX10 = UInt16(max(0, min(65535, Int((jitter * 10.0).rounded()))))
        BoBoProtocol.appendUInt16LE(&fb, lossX100)
        BoBoProtocol.appendUInt16LE(&fb, rttX10)
        BoBoProtocol.appendUInt16LE(&fb, jitterX10)
        BoBoProtocol.appendUInt32LE(&fb, assembler.lastDeliveredId)
        fb.append(requestPli ? 1 : 0)
        sendRaw(type: .feedback, payload: fb)
    }

    private func sendRaw(type: BoBoUdpPacketType, payload: Data) {
        let closedNow = stateLock.withLock { _closed }
        guard !closedNow else { return }
        do {
            guard let datagram = try crypto.seal(packetType: type.rawValue, plaintext: payload) else { return }
            connection.send(content: datagram, completion: .idempotent)
        } catch BoBoUdpCryptoError.nonceExhausted {
            onFallback()
            close()
        } catch {
        }
    }

    public func close() {
        let t: DispatchSourceTimer? = stateLock.withLock {
            _activated = false
            _closed = true
            let oldTimer = timer
            timer = nil
            return oldTimer
        }
        t?.cancel()
        connection.cancel()
        crypto.close()
    }
}
