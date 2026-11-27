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
    public var expectedHostThumbprint: String = ""

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
    func streamClientDidUpdateCursorMode(hostCursorComposited: Bool)
    func streamClientDidReconfigure(monitorIndex: Int, width: Int, height: Int, fps: Int)
    func streamClientDidFail(code: String, message: String)
    func streamClientDidDisconnect(reason: String)
}

public extension BoBoStreamClientDelegate {
    func streamClientDidUpdateRtt(_ rttMs: Double) {}
    func streamClientDidUpdatePrivacy(active: Bool) {}
    func streamClientDidUpdateSuperDisplay(active: Bool, nativeMatch: Bool, width: Int, height: Int) {}
    func streamClientDidUpdateCursorMode(hostCursorComposited: Bool) {}
    func streamClientDidReconfigure(monitorIndex: Int, width: Int, height: Int, fps: Int) {}
}

/// BoBo V6.2 Apple 全原生主控客户端（macOS / iOS 共享）。
public final class BoBoStreamClient: @unchecked Sendable {
    private let identity: BoBoIdentityStore
    private let lock = NSLock()
    private let txLock = NSLock()
    private let txQueue = DispatchQueue(label: "com.bobo.remote.client.tx", qos: .userInteractive)

    private var framedConn: BoBoFramedConnection?
    private var udpChannel: BoBoUdpChannel?
    private var rxTask: Task<Void, Never>?
    private var pingTask: Task<Void, Never>?
    private var _isConnected = false

    private var _remotePlatform: String = BoBoProtocol.platformWindows
    private var _remoteHostControlMode: String = BoBoProtocol.controlModeFull
    private var _remoteMonitors: [BoBoMonitorDescriptor] = []
    private var _hostCursorComposited: Bool = true

    // Coalescing buffer for high-frequency mouse moves (MouseAbs / MouseRel)
    private var pendingAbs: BoBoInputPacket?
    private var hasPendingRel = false
    private var pendingRelDx = 0
    private var pendingRelDy = 0
    private var txFlushScheduled = false

    public var remotePlatform: String {
        lock.lock(); defer { lock.unlock() }; return _remotePlatform
    }
    public var remoteHostControlMode: String {
        lock.lock(); defer { lock.unlock() }; return _remoteHostControlMode
    }
    public var remoteMonitors: [BoBoMonitorDescriptor] {
        lock.lock(); defer { lock.unlock() }; return _remoteMonitors
    }
    public var hostCursorComposited: Bool {
        lock.lock(); defer { lock.unlock() }; return _hostCursorComposited
    }

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
        guard let port = Self.validPort(options.port) else {
            delegate.streamClientDidFail(code: "INVALID_PORT", message: "目标端口无效。")
            return
        }
        identity.ensureLoaded()
        let thumbBox = ThumbBox()
        let nwConn = NWConnection(host: NWEndpoint.Host(options.host), port: port,
                                  using: BoBoFramedConnection.makeClientParameters { thumbBox.set($0) })
        let conn = BoBoFramedConnection(connection: nwConn, queueLabel: "com.bobo.remote.client")
        lock.lock()
        framedConn = conn
        rxTask = Task.detached(priority: .userInteractive) { [weak self, weak delegate] in
            guard let self else { conn.close(); return }
            var disconnectReason = "连接已断开"
            var endedWithError = false
            defer {
                let wasConnected = self.finishConnection(conn)
                if wasConnected && !endedWithError {
                    delegate?.streamClientDidDisconnect(reason: disconnectReason)
                }
            }
            do {
                guard self.isCurrent(conn) else { return }
                try await conn.start(timeoutSeconds: 8.0)

                try await conn.sendJson(Self.buildHelloDict(identity: self.identity))
                guard let ackFrame = try await conn.readFrame(maxPayloadOverride: BoBoProtocol.maxPreAuthPayloadBytes, timeoutSeconds: 8.0),
                      ackFrame.type == .controlJson,
                      let ack = BoBoProtocol.parseJson(ackFrame.payload),
                      Self.isHelloAck(ack), self.isCurrent(conn) else {
                    delegate?.streamClientDidFail(code: "HANDSHAKE_FAILED", message: "目标主机未返回有效 BoBo V6 握手。")
                    return
                }

                let serverThumb = thumbBox.get()
                guard !serverThumb.isEmpty else {
                    delegate?.streamClientDidFail(code: "TLS_CERT_MISSING", message: "未能获取远端主机 TLS 证书指纹。")
                    return
                }

                let hostNodeId = (ack["nodeId"] as? String) ?? ""
                let hostCode = (ack["deviceCode"] as? String) ?? ""
                let hostName = (ack["deviceName"] as? String) ?? options.host
                let nonce = (ack["nonce"] as? String) ?? ""

                // 严格客户端 TOFU 证书钉扎：若本机已记录该主机 nodeId，证书指纹必须完全一致
                if Self.hasPinMismatch(identity: self.identity, nodeId: hostNodeId, thumb: serverThumb,
                                       expected: options.expectedHostThumbprint) {
                    delegate?.streamClientDidFail(
                        code: "CERT_MISMATCH",
                        message: "远端主机证书指纹与已配对记录不一致，疑似中间人攻击或主机已重装。请先删除旧配对记录后重新配对。"
                    )
                    return
                }

                if ((ack["allowIncoming"] as? NSNumber)?.boolValue ?? true) == false {
                    delegate?.streamClientDidFail(code: "HOST_DISABLED", message: "对方已关闭「允许本机被控」。")
                    return
                }
                if ((ack["authorized"] as? NSNumber)?.boolValue ?? false) == false ||
                    !self.identity.isPeerAuthorized(peerNodeId: hostNodeId, peerThumbprint: serverThumb) {
                    delegate?.streamClientDidFail(code: "PAIR_REQUIRED", message: "首次连接需输入对方 4 位临时配对码。")
                    return
                }

                // 服务端下发挑战时必须完成 RSA 私钥签名验证
                if ((ack["authChallenge"] as? NSNumber)?.boolValue ?? false) {
                    let sig = self.identity.signAuthChallenge(nonceHex: nonce, hostThumbprint: serverThumb)
                    try await conn.sendJson([
                        "type": "AUTH_PROOF",
                        "signature": sig,
                        "clientCertDer": self.identity.certificateDerBase64
                    ])
                    guard let authFrame = try await conn.readFrame(maxPayloadOverride: BoBoProtocol.maxPreAuthPayloadBytes, timeoutSeconds: 8.0),
                          authFrame.type == .controlJson,
                          let authRes = BoBoProtocol.parseJson(authFrame.payload),
                          (authRes["type"] as? String) == "AUTH_RESULT" else {
                        delegate?.streamClientDidFail(code: "AUTH_FAILED", message: "设备私钥身份校验未通过，请重新配对。")
                            return
                    }
                    if ((authRes["ok"] as? NSNumber)?.boolValue ?? false) == false {
                        let emsg = (authRes["error"] as? String) ?? "设备私钥身份校验未通过，请重新配对。"
                        delegate?.streamClientDidFail(code: "AUTH_FAILED", message: emsg)
                            return
                    }
                }

                self.lock.lock()
                self._remotePlatform = (ack["platform"] as? String) ?? BoBoProtocol.platformWindows
                self._remoteHostControlMode = (ack["hostControlMode"] as? String) ?? BoBoProtocol.controlModeFull
                self._remoteMonitors = BoBoProtocol.parseMonitors(from: ack)
                self._hostCursorComposited = true
                self.lock.unlock()

                if !hostNodeId.isEmpty {
                    self.identity.authorizeOrUpdatePeer(BoBoAuthorizedPeer(
                        nodeId: hostNodeId,
                        deviceCode: hostCode,
                        deviceName: hostName,
                        certThumbprint: serverThumb,
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
                    "clientHeight": options.clientHeight,
                    "udpTransport": true,
                    "udpCipher": "aes-128-gcm",
                    "udpCiphers": ["aes-128-gcm"],
                    "udpInterleavedFec": true,
                    "udpNack": true,
                    "audioCodecs": ["adpcm"]
                ]
                try await conn.sendJson(startReq)
                guard self.didConnect(conn) else { return }

                while !Task.isCancelled {
                    guard let frame = try await conn.readFrame(timeoutSeconds: 12.0), self.isCurrent(conn) else { break }
                    switch frame.type {
                    case .videoH264:
                        let udpOn = self.lock.withLock { self.udpChannel?.isActive == true }
                        if udpOn { continue }
                        if let vp = BoBoProtocol.parseVideoPayload(frame.payload) {
                            delegate?.streamClientDidReceiveVideo(vp, timestampMs: frame.flagsOrTimestamp)
                        }
                    case .audioAdpcm:
                        let udpOn = self.lock.withLock { self.udpChannel?.isActive == true }
                        if udpOn { continue }
                        if let ap = BoBoProtocol.parseAudioPayload(frame.payload) {
                            delegate?.streamClientDidReceiveAudio(ap)
                        }
                    case .cursorShape:
                        break
                    case .controlJson:
                        guard let msg = BoBoProtocol.parseJson(frame.payload),
                              let t = msg["type"] as? String else { continue }
                        switch t {
                        case "STREAM_STARTED":
                            let mons = BoBoProtocol.parseMonitors(from: msg)
                            let cursorComp = (msg["hostCursorComposited"] as? NSNumber)?.boolValue ?? true
                            self.lock.lock()
                            if !mons.isEmpty { self._remoteMonitors = mons }
                            if let sp = msg["platform"] as? String, !sp.isEmpty { self._remotePlatform = sp }
                            if let sm = msg["hostControlMode"] as? String, !sm.isEmpty { self._remoteHostControlMode = sm }
                            self._hostCursorComposited = cursorComp
                            let curMons = self._remoteMonitors
                            let curPlat = self._remotePlatform
                            let curMode = self._remoteHostControlMode
                            self.lock.unlock()

                            let w = (msg["width"] as? NSNumber)?.intValue ?? options.width
                            let h = (msg["height"] as? NSNumber)?.intValue ?? options.height
                            let fps = (msg["fps"] as? NSNumber)?.intValue ?? options.fps
                            let sdAct = (msg["superDisplayActive"] as? NSNumber)?.boolValue ?? false
                            let sdNat = (msg["superDisplayNativeMatch"] as? NSNumber)?.boolValue ?? false
                            let udpPortNum = (msg["udpPort"] as? NSNumber)?.intValue ?? 0
                            let udpSidNum = (msg["udpSessionId"] as? NSNumber)?.int64Value ?? 0
                            let udpKeyStr = (msg["udpKey"] as? String) ?? ""
                            let udpCipherStr = (msg["udpCipher"] as? String) ?? ""
                            let udpNackFlag = (msg["udpNack"] as? NSNumber)?.boolValue ?? false
                            if udpPortNum > 0 && udpPortNum <= 65535 && udpSidNum != 0 && !udpKeyStr.isEmpty &&
                               udpCipherStr.caseInsensitiveCompare("aes-128-gcm") == .orderedSame {
                                self.startUdpChannel(
                                    host: options.host,
                                    port: UInt16(udpPortNum),
                                    sessionId: UInt32(truncatingIfNeeded: udpSidNum),
                                    keyBase64: udpKeyStr,
                                    nackEnabled: udpNackFlag,
                                    conn: conn,
                                    delegate: delegate
                                )
                            }
                            delegate?.streamClientDidStart(
                                monitors: curMons,
                                width: w,
                                height: h,
                                fps: fps,
                                remotePlatform: curPlat,
                                hostControlMode: curMode
                            )
                            delegate?.streamClientDidUpdateCursorMode(hostCursorComposited: cursorComp)
                            if let privacyActive = msg["privacyActive"] as? NSNumber {
                                delegate?.streamClientDidUpdatePrivacy(active: privacyActive.boolValue)
                            }
                            delegate?.streamClientDidUpdateSuperDisplay(active: sdAct, nativeMatch: sdNat, width: w, height: h)
                        case "CURSOR_MODE_STATE":
                            if let cursor = msg["hostCursorComposited"] as? NSNumber {
                                self.lock.withLock { self._hostCursorComposited = cursor.boolValue }
                                delegate?.streamClientDidUpdateCursorMode(hostCursorComposited: cursor.boolValue)
                            }
                        case "RECONFIGURED":
                            let monitors = BoBoProtocol.parseMonitors(from: msg)
                            if !monitors.isEmpty {
                                self.lock.withLock { self._remoteMonitors = monitors }
                            }
                            let mon = (msg["monitorIndex"] as? NSNumber)?.intValue ?? 0
                            let w = (msg["width"] as? NSNumber)?.intValue ?? 0
                            let h = (msg["height"] as? NSNumber)?.intValue ?? 0
                            let fps = (msg["fps"] as? NSNumber)?.intValue ?? 0
                            delegate?.streamClientDidReconfigure(monitorIndex: mon, width: w, height: h, fps: fps)
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
                            endedWithError = true
                            delegate?.streamClientDidFail(code: code, message: emsg)
                            return
                        case "DISCONNECT":
                            disconnectReason = "对方已断开远控"
                            return
                        default:
                            break
                        }
                    default:
                        break
                    }
                }
            } catch {
                guard self.isCurrent(conn) else { return }
                if !self.isConnected {
                    delegate?.streamClientDidFail(code: "CONNECT_FAILED", message: "连接失败：\(error.localizedDescription)")
                }
                disconnectReason = error.localizedDescription
            }
        }
        lock.unlock()
    }

    public func sendInput(_ pkt: BoBoInputPacket) {
        guard let conn = connectedConnection() else { return }
        if pkt.kind == BoBoProtocol.inputKindMouseAbs {
            var schedule = false
            txLock.lock()
            pendingAbs = pkt
            if !txFlushScheduled {
                txFlushScheduled = true
                schedule = true
            }
            txLock.unlock()
            if schedule {
                txQueue.async { [weak self] in self?.flushPendingMoves(conn: conn) }
            }
            return
        }
        if pkt.kind == BoBoProtocol.inputKindMouseRel {
            var schedule = false
            txLock.lock()
            hasPendingRel = true
            pendingRelDx = max(-32768, min(32767, pendingRelDx + Int(pkt.a)))
            pendingRelDy = max(-32768, min(32767, pendingRelDy + Int(pkt.b)))
            if !txFlushScheduled {
                txFlushScheduled = true
                schedule = true
            }
            txLock.unlock()
            if schedule {
                txQueue.async { [weak self] in self?.flushPendingMoves(conn: conn) }
            }
            return
        }

        // Discrete input (click / wheel / key / touch / mobileNav / releaseAll):
        // Flush any buffered moves immediately ahead of it on the serial TX queue.
        txQueue.async { [weak self] in
            guard let self, self.isCurrent(conn) else { return }
            self.flushPendingMovesOnTxQueue(conn: conn)
            conn.sendFrameFireAndForget(type: .inputEvent, flagsOrTimestamp: 0, payload: BoBoProtocol.buildInputPayload(pkt))
        }
    }

    private func flushPendingMoves(conn: BoBoFramedConnection) {
        if isCurrent(conn) { flushPendingMovesOnTxQueue(conn: conn) }
    }

    private func flushPendingMovesOnTxQueue(conn: BoBoFramedConnection) {
        txLock.lock()
        let absPkt = pendingAbs
        pendingAbs = nil
        let rel = hasPendingRel
        let rdx = pendingRelDx
        let rdy = pendingRelDy
        hasPendingRel = false
        pendingRelDx = 0
        pendingRelDy = 0
        txFlushScheduled = false
        txLock.unlock()

        if let absPkt {
            conn.sendFrameFireAndForget(type: .inputEvent, flagsOrTimestamp: 0, payload: BoBoProtocol.buildInputPayload(absPkt))
        }
        if rel && (rdx != 0 || rdy != 0) {
            let relPkt = BoBoProtocol.buildMouseRel(dx: rdx, dy: rdy)
            conn.sendFrameFireAndForget(type: .inputEvent, flagsOrTimestamp: 0, payload: BoBoProtocol.buildInputPayload(relPkt))
        }
    }

    public func sendKeyCombo(_ vks: [UInt8]) {
        for vk in vks { sendInput(BoBoProtocol.buildKey(vk: vk, down: true, extended: Self.isExtendedVk(vk))) }
        for vk in vks.reversed() { sendInput(BoBoProtocol.buildKey(vk: vk, down: false, extended: Self.isExtendedVk(vk))) }
    }

    private static func isExtendedVk(_ vk: UInt8) -> Bool {
        switch vk {
        case 0x21...0x28, 0x2D, 0x2E, 0x5B, 0x5C, 0xA3, 0xA5:
            return true
        default:
            return false
        }
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
        lock.lock()
        let conn = framedConn
        let wasConnected = _isConnected
        clearConnectionLocked()
        lock.unlock()
        guard let conn else { return }
        if wasConnected, let disconnect = try? BoBoProtocol.encodeJsonFrame(["type": "DISCONNECT"]) {
            let release = BoBoProtocol.encodeFrame(type: .inputEvent,
                                                    payload: BoBoProtocol.buildInputPayload(BoBoProtocol.buildReleaseAll()))
            txQueue.async {
                conn.connection.send(content: release + disconnect, completion: .contentProcessed { _ in conn.close() })
            }
            DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 0.25) { conn.close() }
        } else {
            conn.close()
        }
    }

    private func sendJson(_ dict: [String: Any]) {
        guard let conn = connectedConnection(), let raw = try? JSONSerialization.data(withJSONObject: dict) else { return }
        txQueue.async { [weak self] in
            guard let self, self.isCurrent(conn) else { return }
            self.flushPendingMovesOnTxQueue(conn: conn)
            conn.sendFrameFireAndForget(type: .controlJson, flagsOrTimestamp: 0, payload: raw)
        }
    }

    private func connectedConnection() -> BoBoFramedConnection? {
        lock.withLock { _isConnected ? framedConn : nil }
    }

    private func isCurrent(_ conn: BoBoFramedConnection) -> Bool {
        lock.withLock { framedConn === conn }
    }

    private func didConnect(_ conn: BoBoFramedConnection) -> Bool {
        lock.withLock {
            guard framedConn === conn else { return false }
            _isConnected = true
            pingTask = Task.detached(priority: .utility) { [weak self] in
                while let self, self.isCurrent(conn), !Task.isCancelled {
                    let ts = Int64(DispatchTime.now().uptimeNanoseconds)
                    if let raw = try? JSONSerialization.data(withJSONObject: ["type": "PING", "ts": ts]) {
                        conn.sendFrameFireAndForget(type: .controlJson, payload: raw)
                    }
                    try? await Task.sleep(nanoseconds: 1_500_000_000)
                }
            }
            return true
        }
    }

    private func finishConnection(_ conn: BoBoFramedConnection) -> Bool {
        lock.withLock {
            guard framedConn === conn else { return false }
            let wasConnected = _isConnected
            clearConnectionLocked()
            conn.close()
            return wasConnected
        }
    }

    private func startUdpChannel(
        host: String,
        port: UInt16,
        sessionId: UInt32,
        keyBase64: String,
        nackEnabled: Bool,
        conn: BoBoFramedConnection,
        delegate: BoBoStreamClientDelegate?
    ) {
        guard let ch = BoBoUdpChannel(
            host: host,
            port: port,
            sessionId: sessionId,
            keyBase64: keyBase64,
            nackEnabled: nackEnabled,
            onVideo: { [weak self, weak delegate] vp, tsMs in
                guard let self, self.isCurrent(conn) else { return }
                delegate?.streamClientDidReceiveVideo(vp, timestampMs: tsMs)
            },
            onAudio: { [weak self, weak delegate] ap in
                guard let self, self.isCurrent(conn) else { return }
                delegate?.streamClientDidReceiveAudio(ap)
            },
            onRtt: { [weak self, weak delegate] rtt in
                guard let self, self.isCurrent(conn) else { return }
                delegate?.streamClientDidUpdateRtt(rtt)
            },
            onFallback: { [weak self] in
                guard let self, self.isCurrent(conn) else { return }
                self.sendJson(["type": "TRANSPORT_FALLBACK", "reason": "udp_timeout"])
            }
        ) else { return }

        let old: BoBoUdpChannel? = lock.withLock {
            guard framedConn === conn else { return ch }
            let prev = udpChannel
            udpChannel = ch
            return prev
        }
        old?.close()
        if old !== ch {
            ch.start()
        }
    }

    private func clearConnectionLocked() {
        framedConn = nil
        _isConnected = false
        udpChannel?.close()
        udpChannel = nil
        pingTask?.cancel()
        pingTask = nil
        rxTask?.cancel()
        rxTask = nil
        txLock.withLock {
            pendingAbs = nil
            hasPendingRel = false
            pendingRelDx = 0
            pendingRelDy = 0
            txFlushScheduled = false
        }
    }

    private static func validPort(_ port: Int) -> NWEndpoint.Port? {
        let value = port == 0 ? BoBoProtocol.defaultTcpPort : port
        guard (1...65535).contains(value) else { return nil }
        return NWEndpoint.Port(rawValue: UInt16(value))
    }

    private static func hasPinMismatch(identity: BoBoIdentityStore, nodeId: String, thumb: String, expected: String = "") -> Bool {
        if !expected.isEmpty && !BoBoIdentityStore.fixedTimeEqualsHex(expected, thumb) { return true }
        guard let peer = identity.findPeerByNodeId(nodeId) else { return false }
        return !BoBoIdentityStore.fixedTimeEqualsHex(peer.certThumbprint, thumb)
    }

    private static func isHelloAck(_ ack: [String: Any]) -> Bool {
        (ack["type"] as? String) == "HELLO_ACK" && (ack["protocol"] as? String) == BoBoProtocol.protocolMagic
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
        guard let endpointPort = validPort(port) else { res.error = "目标端口无效"; return res }
        res.port = Int(endpointPort.rawValue)

        let thumbBox = ThumbBox()
        let nwConn = NWConnection(
            host: NWEndpoint.Host(host),
            port: endpointPort,
            using: BoBoFramedConnection.makeClientParameters { thumbBox.set($0) }
        )
        let conn = BoBoFramedConnection(connection: nwConn, queueLabel: "com.bobo.remote.probe")
        defer { conn.close() }

        do {
            try await conn.start(timeoutSeconds: timeoutSeconds)
            res.reachable = true
            try await conn.sendJson(buildHelloDict(identity: identity))
            guard let reply = try await conn.readFrame(maxPayloadOverride: BoBoProtocol.maxPreAuthPayloadBytes, timeoutSeconds: timeoutSeconds),
                  reply.type == .controlJson,
                  let ack = BoBoProtocol.parseJson(reply.payload),
                  isHelloAck(ack) else {
                res.error = "目标不是 BoBo远程 V6 客户端"
                return res
            }
            let srvThumb = thumbBox.get()
            guard !srvThumb.isEmpty else {
                res.error = "未能获取远端主机 TLS 证书指纹"
                return res
            }
            res.hostNodeId = (ack["nodeId"] as? String) ?? ""
            if hasPinMismatch(identity: identity, nodeId: res.hostNodeId, thumb: srvThumb) {
                res.error = "目标设备证书与已配对记录不一致（CERT_MISMATCH）"
                return res
            }
            res.isBoBoHost = true
            res.hostDeviceCode = (ack["deviceCode"] as? String) ?? ""
            res.hostName = (ack["deviceName"] as? String) ?? host
            res.hostThumbprint = srvThumb
            res.allowIncoming = (ack["allowIncoming"] as? NSNumber)?.boolValue ?? true
            res.authorized = ((ack["authorized"] as? NSNumber)?.boolValue ?? false) &&
                identity.isPeerAuthorized(peerNodeId: res.hostNodeId, peerThumbprint: srvThumb)
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
        timeoutSeconds: Double = 10.0,
        expectedHostThumbprint: String = ""
    ) async -> (ok: Bool, hostName: String, error: String) {
        identity.ensureLoaded()
        guard let endpointPort = validPort(port) else { return (false, "", "目标端口无效") }
        let thumbBox = ThumbBox()
        let nwConn = NWConnection(
            host: NWEndpoint.Host(host),
            port: endpointPort,
            using: BoBoFramedConnection.makeClientParameters { thumbBox.set($0) }
        )
        let conn = BoBoFramedConnection(connection: nwConn, queueLabel: "com.bobo.remote.pair")
        defer { conn.close() }

        do {
            try await conn.start(timeoutSeconds: timeoutSeconds)
            try await conn.sendJson(buildHelloDict(identity: identity))
            guard let ackFrame = try await conn.readFrame(maxPayloadOverride: BoBoProtocol.maxPreAuthPayloadBytes, timeoutSeconds: timeoutSeconds),
                  ackFrame.type == .controlJson,
                  let ack = BoBoProtocol.parseJson(ackFrame.payload),
                  isHelloAck(ack) else {
                return (false, "", "被控端未返回有效 BoBo 握手")
            }
            if ((ack["allowIncoming"] as? NSNumber)?.boolValue ?? true) == false {
                return (false, "", "对方已关闭「允许本机被控」")
            }
            let hostNodeId = (ack["nodeId"] as? String) ?? ""
            let hostCode = (ack["deviceCode"] as? String) ?? ""
            let hostName = (ack["deviceName"] as? String) ?? host
            let srvThumb = thumbBox.get()
            guard !srvThumb.isEmpty else {
                return (false, hostName, "未能获取被控端 TLS 证书指纹")
            }
            if hasPinMismatch(identity: identity, nodeId: hostNodeId, thumb: srvThumb, expected: expectedHostThumbprint) {
                return (false, hostName, "目标设备证书与已配对记录不一致（CERT_MISMATCH）")
            }
            let nonce = (ack["nonce"] as? String) ?? ""
            guard !nonce.isEmpty, !hostNodeId.isEmpty else { return (false, hostName, "无效的配对身份") }

            let proof = BoBoIdentityStore.computePinProof(
                pin: pin.trimmingCharacters(in: .whitespacesAndNewlines),
                nonceHex: nonce,
                clientThumbprint: identity.certThumbprint,
                hostThumbprint: srvThumb
            )
            let authSig = identity.signAuthChallenge(nonceHex: nonce, hostThumbprint: srvThumb)
            try await conn.sendJson([
                "type": "PAIR_REQUEST",
                "proof": proof,
                "authSig": authSig,
                "clientCertDer": identity.certificateDerBase64
            ])
            guard let resFrame = try await conn.readFrame(maxPayloadOverride: BoBoProtocol.maxPreAuthPayloadBytes, timeoutSeconds: timeoutSeconds),
                  resFrame.type == .controlJson, let res = BoBoProtocol.parseJson(resFrame.payload),
                  (res["type"] as? String) == "PAIR_RESULT" else {
                return (false, hostName, "被控端未返回配对结果")
            }
            guard ((res["ok"] as? NSNumber)?.boolValue ?? false) else {
                return (false, hostName, (res["error"] as? String) ?? "4 位配对码校验失败")
            }
            guard identity.authorizeOrUpdatePeer(BoBoAuthorizedPeer(
                nodeId: hostNodeId,
                deviceCode: hostCode,
                deviceName: hostName,
                certThumbprint: srvThumb,
                lastHost: host
            )) else {
                return (false, hostName, "目标设备证书与已配对记录不一致（CERT_MISMATCH）")
            }
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
        guard let attrs = try? fileURL.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey]),
              attrs.isRegularFile == true, let size = attrs.fileSize else {
            return (false, "无法读取本地文件信息")
        }
        let total = Int64(size)
        guard total >= 0 && total <= BoBoProtocol.maxFileTransferBytes else {
            return (false, "文件体积超过 2 GiB 上限")
        }

        guard let endpointPort = validPort(port) else { return (false, "目标端口无效") }
        guard let fh = try? FileHandle(forReadingFrom: fileURL) else { return (false, "无法读取本地文件") }
        defer { try? fh.close() }
        guard let shaHex = computeFileSha256Streaming(handle: fh, total: total) else {
            return (false, "无法读取本地文件或文件已变化")
        }
        let tid = String(UUID().uuidString.replacingOccurrences(of: "-", with: "").prefix(16))

        let thumbBox = ThumbBox()
        let nwConn = NWConnection(
            host: NWEndpoint.Host(host),
            port: endpointPort,
            using: BoBoFramedConnection.makeClientParameters { thumbBox.set($0) }
        )
        let conn = BoBoFramedConnection(connection: nwConn, queueLabel: "com.bobo.remote.file")
        defer { conn.close() }

        do {
            try await conn.start(timeoutSeconds: 12.0)
            try await conn.sendJson(buildHelloDict(identity: identity))
            guard let ackFrame = try await conn.readFrame(maxPayloadOverride: BoBoProtocol.maxPreAuthPayloadBytes, timeoutSeconds: 15.0),
                  ackFrame.type == .controlJson,
                  let ack = BoBoProtocol.parseJson(ackFrame.payload),
                  isHelloAck(ack) else {
                return (false, "目标设备未返回有效 BoBo V6 握手")
            }

            let serverThumb = thumbBox.get()
            let hostNodeId = (ack["nodeId"] as? String) ?? ""
            guard !serverThumb.isEmpty else {
                return (false, "未能获取目标设备 TLS 证书指纹")
            }
            if !hostNodeId.isEmpty, let existing = identity.findPeerByNodeId(hostNodeId),
               existing.certThumbprint.caseInsensitiveCompare(serverThumb) != .orderedSame {
                return (false, "远端主机证书指纹与已配对记录不一致（CERT_MISMATCH）")
            }
            guard identity.isPeerAuthorized(peerNodeId: hostNodeId, peerThumbprint: serverThumb),
                  ((ack["authorized"] as? NSNumber)?.boolValue ?? false) else {
                return (false, "请先与目标设备完成 4 位配对授权")
            }

            if ((ack["authChallenge"] as? NSNumber)?.boolValue ?? false) {
                let nonce = (ack["nonce"] as? String) ?? ""
                let sig = identity.signAuthChallenge(nonceHex: nonce, hostThumbprint: serverThumb)
                try await conn.sendJson([
                    "type": "AUTH_PROOF",
                    "signature": sig,
                    "clientCertDer": identity.certificateDerBase64
                ])
                guard let authFrame = try await conn.readFrame(maxPayloadOverride: BoBoProtocol.maxPreAuthPayloadBytes, timeoutSeconds: 15.0),
                      authFrame.type == .controlJson, let authRes = BoBoProtocol.parseJson(authFrame.payload),
                      (authRes["type"] as? String) == "AUTH_RESULT",
                      ((authRes["ok"] as? NSNumber)?.boolValue ?? false) else {
                    return (false, "设备私钥身份校验未通过")
                }
            }

            try await conn.sendJson([
                "type": "FILE_OFFER",
                "transferId": tid,
                "fileName": fileURL.lastPathComponent,
                "totalBytes": total,
                "sha256": shaHex
            ])
            guard let accFrame = try await conn.readFrame(maxPayloadOverride: BoBoProtocol.maxPreAuthPayloadBytes, timeoutSeconds: 15.0),
                  accFrame.type == .controlJson, let acc = BoBoProtocol.parseJson(accFrame.payload) else {
                return (false, "对方未响应文件传输请求")
            }
            guard (acc["type"] as? String) == "FILE_ACCEPT", (acc["transferId"] as? String) == tid else {
                let errText = (acc["error"] as? String) ?? (acc["message"] as? String) ?? "对方拒绝了文件传输"
                return (false, errText)
            }

            try fh.seek(toOffset: 0)
            var offset: Int64 = 0
            while offset < total {
                guard let slice = try fh.read(upToCount: Int(min(65536, total - offset))), !slice.isEmpty else {
                    return (false, "文件在传输过程中发生变化")
                }
                let payload = BoBoProtocol.buildFileChunkPayload(transferId16: tid, offset: offset, data: slice)
                try await conn.sendFrame(type: .fileChunk, flagsOrTimestamp: 0, payload: payload)
                offset += Int64(slice.count)
                onProgress?(offset, total)
            }
            try await conn.sendJson(["type": "FILE_DONE", "transferId": tid])
            guard let doneFrame = try await conn.readFrame(maxPayloadOverride: BoBoProtocol.maxPreAuthPayloadBytes, timeoutSeconds: 30.0),
                  doneFrame.type == .controlJson, let done = BoBoProtocol.parseJson(doneFrame.payload),
                  (done["type"] as? String) == "FILE_ACK", (done["transferId"] as? String) == tid else {
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

    private static func computeFileSha256Streaming(handle: FileHandle, total: Int64) -> String? {
        do {
            var hasher = SHA256()
            var remaining = total
            while remaining > 0 {
                guard let chunk = try handle.read(upToCount: Int(min(65536, remaining))), !chunk.isEmpty else { return nil }
                hasher.update(data: chunk)
                remaining -= Int64(chunk.count)
            }
            return BoBoProtocol.bytesToHex(Data(hasher.finalize()))
        } catch {
            return nil
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
            "clientCertDer": identity.certificateDerBase64,
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
