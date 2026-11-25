import CryptoKit
import Foundation
import Network

public struct BoBoProbeResult: Sendable {
    public var reachable = false
    public var isBoBoHost = false
    public var allowIncoming = false
    public var authorized = false
    public var host = ""
    public var port = BoBoProtocol.defaultTcpPort
    public var hostNodeId = ""
    public var hostDeviceCode = ""
    public var hostName = ""
    public var hostThumbprint = ""
    public var platform = BoBoProtocol.platformWindows
    public var hostControlMode = BoBoProtocol.controlModeFull
    public var screenOrientation = 0
    public var nonce = ""
    public var monitors: [BoBoMonitorDescriptor] = []
    public var error = ""

    public init() {}
}

public struct BoBoStreamOptions: Sendable {
    public var host: String
    public var port: Int = BoBoProtocol.defaultTcpPort
    public var monitorIndex: Int = 0
    public var width: Int = 1920
    public var height: Int = 1080
    public var fps: Int = 60
    public var bitrateKbps: Int = 14000
    public var muteHostAudio: Bool = false
    public var requestPrivacy: Bool = false
    public var superDisplay: Bool = true
    public var clientWidth: Int = 0
    public var clientHeight: Int = 0

    public init(host: String, port: Int = BoBoProtocol.defaultTcpPort) {
        self.host = host
        self.port = port
    }
}

public protocol BoBoStreamClientDelegate: AnyObject, Sendable {
    func streamClientDidStart(
        monitors: [BoBoMonitorDescriptor],
        width: Int,
        height: Int,
        fps: Int,
        remotePlatform: String,
        hostControlMode: String
    )
    func streamClientDidReceiveVideo(_ payload: BoBoVideoPayload, timestampMs: UInt32)
    func streamClientDidReceiveAudio(_ payload: BoBoAudioPayload)
    func streamClientDidUpdateRtt(_ rttMs: Double)
    func streamClientDidUpdatePrivacy(active: Bool)
    func streamClientDidUpdateSuperDisplay(active: Bool, nativeMatch: Bool, width: Int, height: Int)
    func streamClientDidFail(code: String, message: String)
    func streamClientDidDisconnect(reason: String)
}

public extension BoBoStreamClientDelegate {
    func streamClientDidUpdateRtt(_ rttMs: Double) {}
    func streamClientDidUpdatePrivacy(active: Bool) {}
    func streamClientDidUpdateSuperDisplay(active: Bool, nativeMatch: Bool, width: Int, height: Int) {}
}

/// BoBo V6.2 Apple 全原生主控客户端（macOS / iOS 共享）。
public final class BoBoStreamClient: @unchecked Sendable {
    private let identity: BoBoIdentityStore
    private let lock = NSLock()
    private var framedConn: BoBoFramedConnection?
    private var rxTask: Task<Void, Never>?
    private var pingTask: Task<Void, Never>?
    private var _isConnected = false

    public private(set) var remotePlatform: String = BoBoProtocol.platformWindows
    public private(set) var remoteHostControlMode: String = BoBoProtocol.controlModeFull
    public private(set) var remoteMonitors: [BoBoMonitorDescriptor] = []

    public var isConnected: Bool {
        lock.lock()
        defer { lock.unlock() }
        return _isConnected
    }

    public init(identity: BoBoIdentityStore) {
        self.identity = identity
    }

    public func connectAndStartStream(options: BoBoStreamOptions, delegate: BoBoStreamClientDelegate) {
        disconnect()
        identity.ensureLoaded()

        rxTask = Task.detached(priority: .userInteractive) { [weak self, weak delegate] in
            guard let self else { return }
            var disconnectReason = "连接已断开"
            do {
                let thumbBox = ThumbBox()
                let nwParams = BoBoFramedConnection.makeClientParameters { thumbBox.set($0) }
                let nwConn = NWConnection(
                    host: NWEndpoint.Host(options.host),
                    port: NWEndpoint.Port(integerLiteral: UInt16(options.port > 0 ? options.port : BoBoProtocol.defaultTcpPort)),
                    using: nwParams
                )
                let conn = BoBoFramedConnection(connection: nwConn, queueLabel: "com.bobo.remote.client")
                self.setConn(conn)
                try await conn.start(timeoutSeconds: 8.0)

                try await conn.sendJson(Self.buildHelloDict(identity: self.identity))
                guard let ackFrame = try await conn.readFrame(),
                      ackFrame.type == .controlJson,
                      let ack = BoBoProtocol.parseJson(ackFrame.payload),
                      (ack["protocol"] as? String) == BoBoProtocol.protocolMagic else {
                    delegate?.streamClientDidFail(code: "HANDSHAKE_FAILED", message: "目标主机未返回有效 BoBo V6 握手。")
                    self.disconnect()
                    return
                }

                if ((ack["allowIncoming"] as? NSNumber)?.boolValue ?? true) == false {
                    delegate?.streamClientDidFail(code: "HOST_DISABLED", message: "对方已关闭「允许本机被控」。")
                    self.disconnect()
                    return
                }
                if ((ack["authorized"] as? NSNumber)?.boolValue ?? false) == false {
                    delegate?.streamClientDidFail(code: "PAIR_REQUIRED", message: "首次连接需输入对方 4 位临时配对码。")
                    self.disconnect()
                    return
                }

                self.remotePlatform = (ack["platform"] as? String) ?? BoBoProtocol.platformWindows
                self.remoteHostControlMode = (ack["hostControlMode"] as? String) ?? BoBoProtocol.controlModeFull
                self.remoteMonitors = BoBoProtocol.parseMonitors(from: ack)

                let hostNodeId = (ack["nodeId"] as? String) ?? ""
                let hostCode = (ack["deviceCode"] as? String) ?? ""
                let hostName = (ack["deviceName"] as? String) ?? options.host
                let serverThumb = thumbBox.get()
                let hostThumb = serverThumb.isEmpty ? ((ack["certThumbprint"] as? String) ?? "") : serverThumb
                if !hostNodeId.isEmpty && !hostThumb.isEmpty {
                    self.identity.authorizeOrUpdatePeer(BoBoAuthorizedPeer(
                        nodeId: hostNodeId,
                        deviceCode: hostCode,
                        deviceName: hostName,
                        certThumbprint: hostThumb,
                        lastHost: options.host
                    ))
                }

                let startReq: [String: Any] = [
                    "type": "START_STREAM",
                    "monitorIndex": options.monitorIndex,
                    "width": options.width,
                    "height": options.height,
                    "fps": options.fps,
                    "bitrateKbps": options.bitrateKbps,
                    "muteHost": options.muteHostAudio,
                    "requestPrivacy": options.requestPrivacy,
                    "superDisplay": options.superDisplay,
                    "clientWidth": options.clientWidth,
                    "clientHeight": options.clientHeight
                ]
                try await conn.sendJson(startReq)
                self.setConnected(true)

                self.pingTask = Task.detached(priority: .utility) { [weak self] in
                    while let self, self.isConnected, !Task.isCancelled {
                        let ts = Int64(DispatchTime.now().uptimeNanoseconds)
                        self.sendJson(["type": "PING", "ts": ts])
                        try? await Task.sleep(nanoseconds: 1_500_000_000)
                    }
                }

                while self.isConnected, !Task.isCancelled {
                    guard let frame = try await conn.readFrame() else { break }
                    switch frame.type {
                    case .videoH264:
                        if let vp = BoBoProtocol.parseVideoPayload(frame.payload) {
                            delegate?.streamClientDidReceiveVideo(vp, timestampMs: frame.flagsOrTimestamp)
                        }
                    case .audioAdpcm:
                        if let ap = BoBoProtocol.parseAudioPayload(frame.payload) {
                            delegate?.streamClientDidReceiveAudio(ap)
                        }
                    case .controlJson:
                        guard let msg = BoBoProtocol.parseJson(frame.payload),
                              let t = msg["type"] as? String else { continue }
                        switch t {
                        case "STREAM_STARTED":
                            let mons = BoBoProtocol.parseMonitors(from: msg)
                            if !mons.isEmpty { self.remoteMonitors = mons }
                            if let sp = msg["platform"] as? String, !sp.isEmpty { self.remotePlatform = sp }
                            if let sm = msg["hostControlMode"] as? String, !sm.isEmpty { self.remoteHostControlMode = sm }
                            let w = (msg["width"] as? NSNumber)?.intValue ?? options.width
                            let h = (msg["height"] as? NSNumber)?.intValue ?? options.height
                            let fps = (msg["fps"] as? NSNumber)?.intValue ?? options.fps
                            let sdAct = (msg["superDisplayActive"] as? NSNumber)?.boolValue ?? false
                            let sdNat = (msg["superDisplayNativeMatch"] as? NSNumber)?.boolValue ?? false
                            delegate?.streamClientDidStart(
                                monitors: self.remoteMonitors,
                                width: w,
                                height: h,
                                fps: fps,
                                remotePlatform: self.remotePlatform,
                                hostControlMode: self.remoteHostControlMode
                            )
                            delegate?.streamClientDidUpdateSuperDisplay(active: sdAct, nativeMatch: sdNat, width: w, height: h)
                        case "SUPER_DISPLAY_STATE":
                            delegate?.streamClientDidUpdateSuperDisplay(
                                active: (msg["active"] as? NSNumber)?.boolValue ?? false,
                                nativeMatch: (msg["nativeMatch"] as? NSNumber)?.boolValue ?? false,
                                width: (msg["width"] as? NSNumber)?.intValue ?? 0,
                                height: (msg["height"] as? NSNumber)?.intValue ?? 0
                            )
                        case "PRIVACY_STATE":
                            delegate?.streamClientDidUpdatePrivacy(active: (msg["active"] as? NSNumber)?.boolValue ?? false)
                        case "PONG":
                            if let sent = (msg["ts"] as? NSNumber)?.int64Value, sent > 0 {
                                let nowNs = Int64(DispatchTime.now().uptimeNanoseconds)
                                let rtt = Double(nowNs - sent) / 1_000_000.0
                                if rtt >= 0 && rtt < 10_000 {
                                    delegate?.streamClientDidUpdateRtt(rtt)
                                }
                            }
                        case "ERROR":
                            let code = (msg["code"] as? String) ?? "ERROR"
                            let emsg = (msg["message"] as? String) ?? "远端返回错误"
                            disconnectReason = emsg
                            delegate?.streamClientDidFail(code: code, message: emsg)
                            self.setConnected(false)
                        case "DISCONNECT":
                            disconnectReason = "对方已断开远控"
                            self.setConnected(false)
                        default:
                            break
                        }
                    default:
                        break
                    }
                }
            } catch {
                if !self.isConnected {
                    delegate?.streamClientDidFail(code: "CONNECT_FAILED", message: "连接失败：\(error.localizedDescription)")
                    return
                }
                disconnectReason = error.localizedDescription
            }
            let wasConnected = self.takeConnectedFalse()
            self.closeConnectionOnly()
            if wasConnected {
                delegate?.streamClientDidDisconnect(reason: disconnectReason)
            }
        }
    }

    public func sendInput(_ pkt: BoBoInputPacket) {
        guard isConnected, let conn = currentConn() else { return }
        conn.sendFrameFireAndForget(type: .inputEvent, flagsOrTimestamp: 0, payload: BoBoProtocol.buildInputPayload(pkt))
    }

    public func sendKeyCombo(_ vks: [UInt8]) {
        for vk in vks { sendInput(BoBoProtocol.buildKey(vk: vk, down: true)) }
        for vk in vks.reversed() { sendInput(BoBoProtocol.buildKey(vk: vk, down: false)) }
    }

    public func sendReconfigure(monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int) {
        sendJson([
            "type": "RECONFIGURE",
            "monitorIndex": monitorIndex,
            "width": width,
            "height": height,
            "fps": fps,
            "bitrateKbps": bitrateKbps
        ])
    }

    public func sendRequestKeyframe() { sendJson(["type": "REQUEST_KEYFRAME"]) }
    public func sendSetMuteHost(_ mute: Bool) { sendJson(["type": "SET_MUTE_HOST", "mute": mute]) }
    public func sendSetPrivacy(_ enable: Bool) { sendJson(["type": "SET_PRIVACY", "enable": enable]) }
    public func sendLockWorkstation() { sendJson(["type": "LOCK_WORKSTATION"]) }
    public func sendSetSuperDisplay(enable: Bool, clientWidth: Int, clientHeight: Int) {
        sendJson([
            "type": "SET_SUPER_DISPLAY",
            "enable": enable,
            "clientWidth": clientWidth,
            "clientHeight": clientHeight
        ])
    }

    public func sendClipboardText(_ text: String, injectUnicode: Bool = true) {
        guard !text.isEmpty else { return }
        sendJson(["type": "CLIPBOARD_TEXT", "text": text, "injectUnicode": injectUnicode])
    }

    public func disconnect() {
        let was = takeConnectedFalse()
        if was, let conn = currentConn(), let raw = try? BoBoProtocol.encodeJsonFrame(["type": "DISCONNECT"]) {
            conn.connection.send(content: raw, completion: .idempotent)
        }
        pingTask?.cancel()
        pingTask = nil
        rxTask?.cancel()
        rxTask = nil
        closeConnectionOnly()
    }

    private func sendJson(_ dict: [String: Any]) {
        guard isConnected, let conn = currentConn(), let raw = try? JSONSerialization.data(withJSONObject: dict) else { return }
        conn.sendFrameFireAndForget(type: .controlJson, flagsOrTimestamp: 0, payload: raw)
    }

    private func setConn(_ conn: BoBoFramedConnection?) {
        lock.lock()
        framedConn = conn
        lock.unlock()
    }

    private func currentConn() -> BoBoFramedConnection? {
        lock.lock()
        defer { lock.unlock() }
        return framedConn
    }

    private func setConnected(_ val: Bool) {
        lock.lock()
        _isConnected = val
        lock.unlock()
    }

    private func takeConnectedFalse() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        let was = _isConnected
        _isConnected = false
        return was
    }

    private func closeConnectionOnly() {
        lock.lock()
        let c = framedConn
        framedConn = nil
        lock.unlock()
        c?.close()
    }

    // MARK: - One-Shot Probe, Pair & File Transfer

    public static func probeHost(
        host: String,
        port: Int = BoBoProtocol.defaultTcpPort,
        identity: BoBoIdentityStore,
        timeoutSeconds: Double = 5.0
    ) async -> BoBoProbeResult {
        identity.ensureLoaded()
        var res = BoBoProbeResult()
        res.host = host
        res.port = port > 0 ? port : BoBoProtocol.defaultTcpPort

        let thumbBox = ThumbBox()
        let nwConn = NWConnection(
            host: NWEndpoint.Host(host),
            port: NWEndpoint.Port(integerLiteral: UInt16(res.port)),
            using: BoBoFramedConnection.makeClientParameters { thumbBox.set($0) }
        )
        let conn = BoBoFramedConnection(connection: nwConn, queueLabel: "com.bobo.remote.probe")
        defer { conn.close() }

        do {
            try await conn.start(timeoutSeconds: timeoutSeconds)
            res.reachable = true
            try await conn.sendJson(buildHelloDict(identity: identity))
            guard let reply = try await conn.readFrame(),
                  reply.type == .controlJson,
                  let ack = BoBoProtocol.parseJson(reply.payload),
                  (ack["protocol"] as? String) == BoBoProtocol.protocolMagic else {
                res.error = "目标不是 BoBo远程 V6 客户端"
                return res
            }
            res.isBoBoHost = true
            res.hostNodeId = (ack["nodeId"] as? String) ?? ""
            res.hostDeviceCode = (ack["deviceCode"] as? String) ?? ""
            res.hostName = (ack["deviceName"] as? String) ?? host
            let srvThumb = thumbBox.get()
            res.hostThumbprint = srvThumb.isEmpty ? ((ack["certThumbprint"] as? String) ?? "") : srvThumb
            res.allowIncoming = (ack["allowIncoming"] as? NSNumber)?.boolValue ?? true
            res.authorized = (ack["authorized"] as? NSNumber)?.boolValue ?? false
            res.platform = (ack["platform"] as? String) ?? BoBoProtocol.platformWindows
            res.hostControlMode = (ack["hostControlMode"] as? String) ?? BoBoProtocol.controlModeFull
            res.screenOrientation = (ack["screenOrientation"] as? NSNumber)?.intValue ?? 0
            res.nonce = (ack["nonce"] as? String) ?? ""
            res.monitors = BoBoProtocol.parseMonitors(from: ack)
            return res
        } catch {
            res.error = "未检测到对方开启 BoBo远程：\(error.localizedDescription)"
            return res
        }
    }

    public static func pairWithPin(
        host: String,
        port: Int = BoBoProtocol.defaultTcpPort,
        pin: String,
        identity: BoBoIdentityStore,
        timeoutSeconds: Double = 10.0
    ) async -> (ok: Bool, hostName: String, error: String) {
        identity.ensureLoaded()
        let targetPort = port > 0 ? port : BoBoProtocol.defaultTcpPort
        let thumbBox = ThumbBox()
        let nwConn = NWConnection(
            host: NWEndpoint.Host(host),
            port: NWEndpoint.Port(integerLiteral: UInt16(targetPort)),
            using: BoBoFramedConnection.makeClientParameters { thumbBox.set($0) }
        )
        let conn = BoBoFramedConnection(connection: nwConn, queueLabel: "com.bobo.remote.pair")
        defer { conn.close() }

        do {
            try await conn.start(timeoutSeconds: timeoutSeconds)
            try await conn.sendJson(buildHelloDict(identity: identity))
            guard let ackFrame = try await conn.readFrame(),
                  let ack = BoBoProtocol.parseJson(ackFrame.payload),
                  (ack["protocol"] as? String) == BoBoProtocol.protocolMagic else {
                return (false, "", "被控端未返回有效 BoBo 握手")
            }
            if ((ack["allowIncoming"] as? NSNumber)?.boolValue ?? true) == false {
                return (false, "", "对方已关闭「允许本机被控」")
            }
            let hostNodeId = (ack["nodeId"] as? String) ?? ""
            let hostCode = (ack["deviceCode"] as? String) ?? ""
            let hostName = (ack["deviceName"] as? String) ?? host
            let srvThumb = thumbBox.get()
            let hostThumb = srvThumb.isEmpty ? ((ack["certThumbprint"] as? String) ?? "") : srvThumb
            let nonce = (ack["nonce"] as? String) ?? ""

            let proof = BoBoIdentityStore.computePinProof(
                pin: pin.trimmingCharacters(in: .whitespacesAndNewlines),
                nonceHex: nonce,
                clientThumbprint: identity.certThumbprint,
                hostThumbprint: hostThumb
            )
            try await conn.sendJson(["type": "PAIR_REQUEST", "proof": proof])
            guard let resFrame = try await conn.readFrame(),
                  let res = BoBoProtocol.parseJson(resFrame.payload) else {
                return (false, hostName, "被控端未返回配对结果")
            }
            guard ((res["ok"] as? NSNumber)?.boolValue ?? false) else {
                return (false, hostName, (res["error"] as? String) ?? "4 位配对码校验失败")
            }
            identity.authorizeOrUpdatePeer(BoBoAuthorizedPeer(
                nodeId: hostNodeId,
                deviceCode: hostCode,
                deviceName: hostName,
                certThumbprint: hostThumb,
                lastHost: host
            ))
            return (true, hostName, "")
        } catch {
            return (false, "", "配对失败：\(error.localizedDescription)")
        }
    }

    public static func sendFile(
        host: String,
        port: Int = BoBoProtocol.defaultTcpPort,
        fileURL: URL,
        identity: BoBoIdentityStore,
        onProgress: (@Sendable (Int64, Int64) -> Void)? = nil
    ) async -> (ok: Bool, info: String) {
        identity.ensureLoaded()
        guard let fileData = try? Data(contentsOf: fileURL) else {
            return (false, "无法读取本地文件")
        }
        let shaHex = BoBoProtocol.bytesToHex(Data(SHA256.hash(data: fileData)))
        let tid = String(UUID().uuidString.replacingOccurrences(of: "-", with: "").prefix(16))
        let targetPort = port > 0 ? port : BoBoProtocol.defaultTcpPort

        let nwConn = NWConnection(
            host: NWEndpoint.Host(host),
            port: NWEndpoint.Port(integerLiteral: UInt16(targetPort)),
            using: BoBoFramedConnection.makeClientParameters { _ in }
        )
        let conn = BoBoFramedConnection(connection: nwConn, queueLabel: "com.bobo.remote.file")
        defer { conn.close() }

        do {
            try await conn.start(timeoutSeconds: 12.0)
            try await conn.sendJson(buildHelloDict(identity: identity))
            guard let ackFrame = try await conn.readFrame(),
                  let ack = BoBoProtocol.parseJson(ackFrame.payload),
                  ((ack["authorized"] as? NSNumber)?.boolValue ?? false) else {
                return (false, "请先与目标设备完成 4 位配对授权")
            }
            try await conn.sendJson([
                "type": "FILE_OFFER",
                "transferId": tid,
                "fileName": fileURL.lastPathComponent,
                "totalBytes": fileData.count,
                "sha256": shaHex
            ])
            guard let accFrame = try await conn.readFrame(),
                  let acc = BoBoProtocol.parseJson(accFrame.payload),
                  (acc["type"] as? String) == "FILE_ACCEPT" else {
                return (false, "对方拒绝了文件传输")
            }

            let chunkSize = 65536
            var offset = 0
            let total = Int64(fileData.count)
            while offset < fileData.count {
                let end = min(offset + chunkSize, fileData.count)
                let slice = fileData.subdata(in: offset..<end)
                let payload = BoBoProtocol.buildFileChunkPayload(transferId16: tid, offset: Int64(offset), data: slice)
                try await conn.sendFrame(type: .fileChunk, flagsOrTimestamp: 0, payload: payload)
                offset = end
                onProgress?(Int64(offset), total)
            }
            try await conn.sendJson(["type": "FILE_DONE", "transferId": tid])
            guard let doneFrame = try await conn.readFrame(),
                  let done = BoBoProtocol.parseJson(doneFrame.payload) else {
                return (false, "未收到对方完成确认")
            }
            if ((done["ok"] as? NSNumber)?.boolValue ?? false) {
                return (true, (done["savedPath"] as? String) ?? fileURL.lastPathComponent)
            }
            return (false, (done["error"] as? String) ?? "对方保存文件失败")
        } catch {
            return (false, "文件传输失败：\(error.localizedDescription)")
        }
    }

    private static func buildHelloDict(identity: BoBoIdentityStore) -> [String: Any] {
        [
            "type": "HELLO",
            "protocol": BoBoProtocol.protocolMagic,
            "protocolVersion": BoBoProtocol.protocolVersion,
            "platform": BoBoProtocol.currentPlatform,
            "nodeId": identity.nodeId,
            "deviceCode": identity.deviceCode,
            "deviceName": identity.deviceName,
            "certThumbprint": identity.certThumbprint,
            "version": BoBoProtocol.appVersion
        ]
    }
}

private final class ThumbBox: @unchecked Sendable {
    private let lock = NSLock()
    private var val = ""
    func set(_ s: String) { lock.lock(); val = s; lock.unlock() }
    func get() -> String { lock.lock(); defer { lock.unlock() }; return val }
}
