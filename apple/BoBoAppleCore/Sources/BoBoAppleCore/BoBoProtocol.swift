import Foundation

public enum BoBoPacketType: UInt8, Sendable {
    case controlJson = 0x01
    case videoH264 = 0x02
    case audioAdpcm = 0x03
    case inputEvent = 0x04
    case fileChunk = 0x05
}

public struct BoBoMonitorDescriptor: Codable, Equatable, Identifiable, Sendable {
    public var id: Int { index }
    public var index: Int
    public var name: String
    public var width: Int
    public var height: Int
    public var isPrimary: Bool

    public init(index: Int = 0, name: String = "DISPLAY1", width: Int = 1920, height: Int = 1080, isPrimary: Bool = true) {
        self.index = index
        self.name = name
        self.width = width
        self.height = height
        self.isPrimary = isPrimary
    }

    enum CodingKeys: String, CodingKey {
        case index, name, width, height
        case isPrimary = "primary"
    }
}

public struct BoBoDiscoveredPeer: Equatable, Identifiable, Sendable {
    public var id: String { nodeId }
    public var nodeId: String
    public var deviceCode: String
    public var deviceName: String
    public var host: String
    public var port: Int
    public var version: String
    public var platform: String
    public var hostControlMode: String
    public var allowIncoming: Bool
    public var lastSeen: Date

    public init(
        nodeId: String,
        deviceCode: String,
        deviceName: String,
        host: String,
        port: Int = BoBoProtocol.defaultTcpPort,
        version: String = BoBoProtocol.appVersion,
        platform: String = BoBoProtocol.platformWindows,
        hostControlMode: String = BoBoProtocol.controlModeFull,
        allowIncoming: Bool = true,
        lastSeen: Date = Date()
    ) {
        self.nodeId = nodeId
        self.deviceCode = deviceCode
        self.deviceName = deviceName
        self.host = host
        self.port = port
        self.version = version
        self.platform = platform
        self.hostControlMode = hostControlMode
        self.allowIncoming = allowIncoming
        self.lastSeen = lastSeen
    }
}

public struct BoBoFrame: Sendable {
    public var type: BoBoPacketType
    public var flagsOrTimestamp: UInt32
    public var payload: Data

    public init(type: BoBoPacketType, flagsOrTimestamp: UInt32, payload: Data) {
        self.type = type
        self.flagsOrTimestamp = flagsOrTimestamp
        self.payload = payload
    }
}

public struct BoBoVideoPayload: Sendable {
    public var width: Int
    public var height: Int
    public var isKeyframe: Bool
    public var h264Data: Data

    public init(width: Int, height: Int, isKeyframe: Bool, h264Data: Data) {
        self.width = width
        self.height = height
        self.isKeyframe = isKeyframe
        self.h264Data = h264Data
    }
}

public struct BoBoAudioPayload: Sendable {
    public var sampleRate: Int
    public var channels: Int
    public var sampleCount: Int
    public var adpcmData: Data

    public init(sampleRate: Int, channels: Int, sampleCount: Int, adpcmData: Data) {
        self.sampleRate = sampleRate
        self.channels = channels
        self.sampleCount = sampleCount
        self.adpcmData = adpcmData
    }
}

public struct BoBoInputPacket: Equatable, Sendable {
    public var kind: UInt8
    public var flags: UInt8
    public var a: Int16
    public var b: Int16

    public init(kind: UInt8, flags: UInt8, a: Int16, b: Int16) {
        self.kind = kind
        self.flags = flags
        self.a = a
        self.b = b
    }
}

public struct BoBoFileChunk: Equatable, Sendable {
    public var transferId: String
    public var offset: Int64
    public var data: Data

    public init(transferId: String, offset: Int64, data: Data) {
        self.transferId = transferId
        self.offset = offset
        self.data = data
    }
}

/// BoBo V6.2 跨端二进制帧协议实现（与 Windows C# / Android Kotlin 字节级一致）
public enum BoBoProtocol {
    public static let protocolMagic = "BOBO6"
    public static let protocolVersion = "6.2"
    public static let appVersion = "6.2.1"
    public static let tlsSniHost = "BoBoRemote-V6-Node"

    public static let platformWindows = "windows"
    public static let platformMacOS = "macos"
    public static let platformAndroid = "android"
    public static let platformIOS = "ios"

    public static let controlModeFull = "full"
    public static let controlModeViewOnly = "view_only"

    public static let defaultTcpPort: Int = 49810
    public static let discoveryUdpPort: Int = 49812
    public static let headerBytes: Int = 9
    public static let maxControlPayloadBytes: Int = 2 * 1024 * 1024
    public static let maxVideoPayloadBytes: Int = 16 * 1024 * 1024

    public static let inputKindMouseAbs: UInt8 = 1
    public static let inputKindMouseRel: UInt8 = 2
    public static let inputKindMouseBtn: UInt8 = 3
    public static let inputKindMouseWheel: UInt8 = 4
    public static let inputKindKey: UInt8 = 5
    public static let inputKindReleaseAll: UInt8 = 6
    public static let inputKindTouch: UInt8 = 7
    public static let inputKindMobileNav: UInt8 = 8

    public static let touchActionDown: UInt8 = 0
    public static let touchActionMove: UInt8 = 1
    public static let touchActionUp: UInt8 = 2
    public static let touchActionCancel: UInt8 = 3

    public static let mobileNavBack: Int16 = 1
    public static let mobileNavHome: Int16 = 2
    public static let mobileNavRecents: Int16 = 3
    public static let mobileNavNotifications: Int16 = 4
    public static let mobileNavLock: Int16 = 5
    public static let mobileNavVolumeUp: Int16 = 6
    public static let mobileNavVolumeDown: Int16 = 7
    public static let mobileNavPowerDialog: Int16 = 8

    public static var currentPlatform: String {
        #if os(macOS)
        return platformMacOS
        #else
        return platformIOS
        #endif
    }

    public static func encodeFrame(type: BoBoPacketType, flagsOrTimestamp: UInt32 = 0, payload: Data) -> Data {
        var out = Data(capacity: headerBytes + payload.count)
        out.append(type.rawValue)
        appendInt32LE(&out, Int32(payload.count))
        appendUInt32LE(&out, flagsOrTimestamp)
        out.append(payload)
        return out
    }

    public static func encodeJsonFrame(_ object: [String: Any]) throws -> Data {
        let jsonData = try JSONSerialization.data(withJSONObject: object, options: [])
        return encodeFrame(type: .controlJson, flagsOrTimestamp: 0, payload: jsonData)
    }

    public static func parseJson(_ payload: Data) -> [String: Any]? {
        (try? JSONSerialization.jsonObject(with: payload, options: [])) as? [String: Any]
    }

    public static func buildVideoPayload(width: Int, height: Int, isKeyframe: Bool, h264Data: Data) -> Data {
        var buf = Data(capacity: 5 + h264Data.count)
        appendUInt16LE(&buf, UInt16(truncatingIfNeeded: width))
        appendUInt16LE(&buf, UInt16(truncatingIfNeeded: height))
        buf.append(isKeyframe ? 1 : 0)
        buf.append(h264Data)
        return buf
    }

    public static func parseVideoPayload(_ payload: Data) -> BoBoVideoPayload? {
        guard payload.count > 5 else { return nil }
        let w = Int(readUInt16LE(payload, offset: 0))
        let h = Int(readUInt16LE(payload, offset: 2))
        let isKey = (payload[payload.startIndex + 4] & 1) != 0
        let h264 = payload.subdata(in: (payload.startIndex + 5)..<payload.endIndex)
        return BoBoVideoPayload(width: w, height: h, isKeyframe: isKey, h264Data: h264)
    }

    public static func buildAudioPayload(sampleRate: Int, channels: Int, sampleCount: Int, adpcmData: Data) -> Data {
        var buf = Data(capacity: 7 + adpcmData.count)
        appendInt32LE(&buf, Int32(sampleRate))
        buf.append(UInt8(truncatingIfNeeded: channels))
        appendUInt16LE(&buf, UInt16(truncatingIfNeeded: sampleCount))
        buf.append(adpcmData)
        return buf
    }

    public static func parseAudioPayload(_ payload: Data) -> BoBoAudioPayload? {
        guard payload.count > 7 else { return nil }
        let sr = Int(readInt32LE(payload, offset: 0))
        let ch = Int(payload[payload.startIndex + 4])
        let sc = Int(readUInt16LE(payload, offset: 5))
        let adpcm = payload.subdata(in: (payload.startIndex + 7)..<payload.endIndex)
        return BoBoAudioPayload(sampleRate: sr, channels: ch, sampleCount: sc, adpcmData: adpcm)
    }

    public static func buildInputPayload(_ pkt: BoBoInputPacket) -> Data {
        var buf = Data(capacity: 6)
        buf.append(pkt.kind)
        buf.append(pkt.flags)
        appendInt16LE(&buf, pkt.a)
        appendInt16LE(&buf, pkt.b)
        return buf
    }

    public static func parseInputPayload(_ payload: Data) -> BoBoInputPacket? {
        guard payload.count >= 6 else { return nil }
        let base = payload.startIndex
        return BoBoInputPacket(
            kind: payload[base],
            flags: payload[base + 1],
            a: readInt16LE(payload, offset: 2),
            b: readInt16LE(payload, offset: 4)
        )
    }

    public static func buildMouseAbs(nx: UInt16, ny: UInt16) -> BoBoInputPacket {
        BoBoInputPacket(kind: inputKindMouseAbs, flags: 0, a: Int16(bitPattern: nx), b: Int16(bitPattern: ny))
    }

    public static func buildMouseRel(dx: Int, dy: Int) -> BoBoInputPacket {
        let cx = max(-32768, min(32767, dx))
        let cy = max(-32768, min(32767, dy))
        return BoBoInputPacket(kind: inputKindMouseRel, flags: 0, a: Int16(cx), b: Int16(cy))
    }

    public static func buildMouseButton(button: Int16, down: Bool) -> BoBoInputPacket {
        BoBoInputPacket(kind: inputKindMouseBtn, flags: down ? 1 : 0, a: button, b: 0)
    }

    public static func buildMouseWheel(delta: Int, horizontal: Bool = false) -> BoBoInputPacket {
        let cd = max(-32768, min(32767, delta))
        return BoBoInputPacket(kind: inputKindMouseWheel, flags: horizontal ? 1 : 0, a: Int16(cd), b: 0)
    }

    public static func buildKey(vk: UInt8, down: Bool, extended: Bool = false) -> BoBoInputPacket {
        let flags: UInt8 = (down ? 1 : 0) | (extended ? 2 : 0)
        return BoBoInputPacket(kind: inputKindKey, flags: flags, a: Int16(vk), b: 0)
    }

    public static func buildReleaseAll() -> BoBoInputPacket {
        BoBoInputPacket(kind: inputKindReleaseAll, flags: 0, a: 0, b: 0)
    }

    public static func buildTouch(pointerId: UInt8, action: UInt8, nx: UInt16, ny: UInt16) -> BoBoInputPacket {
        let flags = ((pointerId & 0x0F) << 4) | (action & 0x0F)
        return BoBoInputPacket(kind: inputKindTouch, flags: flags, a: Int16(bitPattern: nx), b: Int16(bitPattern: ny))
    }

    public static func buildMobileNav(_ action: Int16) -> BoBoInputPacket {
        BoBoInputPacket(kind: inputKindMobileNav, flags: 0, a: action, b: 0)
    }

    public static func buildFileChunkPayload(transferId16: String, offset: Int64, data: Data) -> Data {
        var idBytes = Data(repeating: 0, count: 16)
        if let ascii = transferId16.data(using: .ascii) {
            let n = min(16, ascii.count)
            idBytes.replaceSubrange(0..<n, with: ascii.prefix(n))
        }
        var buf = Data(capacity: 24 + data.count)
        buf.append(idBytes)
        appendInt64LE(&buf, offset)
        buf.append(data)
        return buf
    }

    public static func parseFileChunkPayload(_ payload: Data) -> BoBoFileChunk? {
        guard payload.count >= 24 else { return nil }
        let base = payload.startIndex
        var len = 0
        while len < 16 && payload[base + len] != 0 { len += 1 }
        let tid = String(data: payload.subdata(in: base..<(base + len)), encoding: .ascii) ?? ""
        let offset = readInt64LE(payload, offset: 16)
        let chunk = payload.subdata(in: (base + 24)..<payload.endIndex)
        return BoBoFileChunk(transferId: tid, offset: offset, data: chunk)
    }

    public static func serializeMonitors(_ monitors: [BoBoMonitorDescriptor]) -> [[String: Any]] {
        monitors.map { m in
            [
                "index": m.index,
                "name": m.name,
                "width": m.width,
                "height": m.height,
                "primary": m.isPrimary
            ]
        }
    }

    public static func parseMonitors(from dict: [String: Any], key: String = "monitors") -> [BoBoMonitorDescriptor] {
        guard let arr = dict[key] as? [[String: Any]] else { return [] }
        return arr.enumerated().map { idx, item in
            BoBoMonitorDescriptor(
                index: (item["index"] as? NSNumber)?.intValue ?? idx,
                name: (item["name"] as? String) ?? "DISPLAY\(idx + 1)",
                width: (item["width"] as? NSNumber)?.intValue ?? 1920,
                height: (item["height"] as? NSNumber)?.intValue ?? 1080,
                isPrimary: (item["primary"] as? NSNumber)?.boolValue ?? (idx == 0)
            )
        }
    }

    // MARK: - Little-Endian Helpers

    public static func appendInt16LE(_ data: inout Data, _ v: Int16) {
        var le = v.littleEndian
        withUnsafeBytes(of: &le) { data.append(contentsOf: $0) }
    }

    public static func appendUInt16LE(_ data: inout Data, _ v: UInt16) {
        var le = v.littleEndian
        withUnsafeBytes(of: &le) { data.append(contentsOf: $0) }
    }

    public static func appendInt32LE(_ data: inout Data, _ v: Int32) {
        var le = v.littleEndian
        withUnsafeBytes(of: &le) { data.append(contentsOf: $0) }
    }

    public static func appendUInt32LE(_ data: inout Data, _ v: UInt32) {
        var le = v.littleEndian
        withUnsafeBytes(of: &le) { data.append(contentsOf: $0) }
    }

    public static func appendInt64LE(_ data: inout Data, _ v: Int64) {
        var le = v.littleEndian
        withUnsafeBytes(of: &le) { data.append(contentsOf: $0) }
    }

    public static func readInt16LE(_ data: Data, offset: Int) -> Int16 {
        Int16(bitPattern: readUInt16LE(data, offset: offset))
    }

    public static func readUInt16LE(_ data: Data, offset: Int) -> UInt16 {
        let b = data.startIndex + offset
        return UInt16(data[b]) | (UInt16(data[b + 1]) << 8)
    }

    public static func readInt32LE(_ data: Data, offset: Int) -> Int32 {
        Int32(bitPattern: readUInt32LE(data, offset: offset))
    }

    public static func readUInt32LE(_ data: Data, offset: Int) -> UInt32 {
        let b = data.startIndex + offset
        return UInt32(data[b])
            | (UInt32(data[b + 1]) << 8)
            | (UInt32(data[b + 2]) << 16)
            | (UInt32(data[b + 3]) << 24)
    }

    public static func readInt64LE(_ data: Data, offset: Int) -> Int64 {
        let b = data.startIndex + offset
        var v: UInt64 = 0
        for i in 0..<8 {
            v |= UInt64(data[b + i]) << UInt64(i * 8)
        }
        return Int64(bitPattern: v)
    }

    public static func bytesToHex(_ data: Data) -> String {
        let hexChars = Array("0123456789abcdef".utf8)
        var out = [UInt8]()
        out.reserveCapacity(data.count * 2)
        for byte in data {
            out.append(hexChars[Int(byte >> 4)])
            out.append(hexChars[Int(byte & 0x0F)])
        }
        return String(bytes: out, encoding: .ascii) ?? ""
    }

    public static func hexToBytes(_ hex: String) -> Data {
        let clean = hex.trimmingCharacters(in: .whitespacesAndNewlines)
        var data = Data(capacity: clean.count / 2)
        var idx = clean.startIndex
        while idx < clean.endIndex {
            let next = clean.index(idx, offsetBy: 2, limitedBy: clean.endIndex) ?? clean.endIndex
            if let b = UInt8(clean[idx..<next], radix: 16) {
                data.append(b)
            }
            idx = next
        }
        return data
    }
}
