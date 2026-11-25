import CryptoKit
import Foundation
import Network

public protocol BoBoHostServerDelegate: AnyObject, Sendable {
    func hostMonitors() -> [BoBoMonitorDescriptor]
    func hostCompositesCursor() -> Bool
    func hostScreenOrientation() -> Int
    func hostStreamConfiguration(monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int)
        -> (monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int)?
    func hostStartStream(monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int, muteHost: Bool, requestPrivacy: Bool, clientPlatform: String) async throws
        -> (monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int, privacyActive: Bool)
    func hostReconfigureStream(monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int) async throws
        -> (monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int)
    func hostSetPrivacy(enable: Bool) async -> Bool
    func hostStopStream() async
    func hostDidStartStream(monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int, muteHost: Bool, requestPrivacy: Bool)
    func hostDidReconfigureStream(monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int)
    func hostDidStopAllStreams()
    func hostDidRequestKeyframe()
    func hostDidReceiveInput(_ packet: BoBoInputPacket)
    func hostDidSetPrivacy(enable: Bool)
    func hostDidSetMuteHost(mute: Bool)
    func hostDidSetSuperDisplay(enable: Bool, clientWidth: Int, clientHeight: Int) -> (active: Bool, nativeMatch: Bool, width: Int, height: Int)
    func hostDidRequestLockWorkstation()
    func hostDidReceiveClipboard(text: String, injectUnicode: Bool)
    func hostSessionStateChanged(active: Bool, peerName: String)
    func hostDidReceiveFile(url: URL)
    func hostDidAuthorizePeer(_ peer: BoBoAuthorizedPeer)
}

public extension BoBoHostServerDelegate {
    func hostStopStream() async { hostDidStopAllStreams() }
    func hostCompositesCursor() -> Bool { true }
    func hostScreenOrientation() -> Int { 0 }
    func hostStreamConfiguration(monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int)
        -> (monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int)? {
        (monitorIndex, width, height, fps, bitrateKbps)
    }
    func hostStartStream(monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int, muteHost: Bool, requestPrivacy: Bool, clientPlatform: String) async throws
        -> (monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int, privacyActive: Bool) {
        hostDidStartStream(monitorIndex: monitorIndex, width: width, height: height, fps: fps, bitrateKbps: bitrateKbps, muteHost: muteHost, requestPrivacy: requestPrivacy)
        return (monitorIndex, width, height, fps, bitrateKbps, false)
    }
    func hostReconfigureStream(monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int) async throws
        -> (monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int) {
        hostDidReconfigureStream(monitorIndex: monitorIndex, width: width, height: height, fps: fps, bitrateKbps: bitrateKbps)
        return (monitorIndex, width, height, fps, bitrateKbps)
    }
    func hostSetPrivacy(enable: Bool) async -> Bool {
        hostDidSetPrivacy(enable: enable)
        return false
    }
    func hostDidReconfigureStream(monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int) {}
    func hostDidRequestKeyframe() {}
    func hostDidReceiveInput(_ packet: BoBoInputPacket) {}
    func hostDidSetPrivacy(enable: Bool) {}
    func hostDidSetMuteHost(mute: Bool) {}
    func hostDidSetSuperDisplay(enable: Bool, clientWidth: Int, clientHeight: Int) -> (active: Bool, nativeMatch: Bool, width: Int, height: Int) {
        (false, false, 0, 0)
    }
    func hostDidRequestLockWorkstation() {}
    func hostDidReceiveClipboard(text: String, injectUnicode: Bool) {}
    func hostSessionStateChanged(active: Bool, peerName: String) {}
    func hostDidReceiveFile(url: URL) {}
    func hostDidAuthorizePeer(_ peer: BoBoAuthorizedPeer) {}
}

/// BoBo V6.2 Apple 被控端 TLS 1.2 服务端（供 macOS 全功能被控与 iOS ReplayKit 投屏被看共享）。
public final class BoBoHostServer: @unchecked Sendable {
    private static let maxConcurrentSessions = 8
    private static let authFailureDelayNs: UInt64 = 500_000_000

    private let identity: BoBoIdentityStore
    private let downloadDir: URL
    private let requestedPort: UInt16
    public let hostControlMode: String
    public weak var delegate: BoBoHostServerDelegate?

    private let stateLock = NSLock()
    private let pairLock = NSLock()
    private let listenerQueue = DispatchQueue(label: "com.bobo.remote.host.listener", qos: .userInteractive)

    private var listener: NWListener?
    private var sessions: [ObjectIdentifier: ClientSession] = [:]
    private var activeStream: ClientSession?
    private var activePairSession: BoBoLocalPairSession?
    private var _running = false
    private var _boundPort: Int = 0
    private var _allowIncoming = true

    public var isRunning: Bool {
        stateLock.lock(); defer { stateLock.unlock() }; return _running
    }

    public var boundPort: Int {
        stateLock.lock(); defer { stateLock.unlock() }; return _boundPort
    }

    public var allowIncomingControl: Bool {
        get { stateLock.lock(); defer { stateLock.unlock() }; return _allowIncoming }
        set {
            stateLock.lock()
            _allowIncoming = newValue
            stateLock.unlock()
            if !newValue { disconnectAllStreams(reason: "主机已关闭「允许本机被控」") }
        }
    }

    public init(
        identity: BoBoIdentityStore,
        downloadDir: URL,
        port: UInt16 = UInt16(BoBoProtocol.defaultTcpPort),
        hostControlMode: String = BoBoProtocol.controlModeFull
    ) {
        self.identity = identity
        self.downloadDir = downloadDir
        self.requestedPort = port
        self.hostControlMode = hostControlMode
    }

    public func start() async throws -> Int {
        if isRunning { return boundPort }
        identity.ensureLoaded()
        let secId = try identity.loadSecIdentity()
        let params = try BoBoFramedConnection.makeServerParameters(identity: secId)

        let nwListener: NWListener
        if requestedPort == 0 {
            nwListener = try NWListener(using: params)
        } else {
            nwListener = try NWListener(using: params, on: NWEndpoint.Port(rawValue: requestedPort)!)
        }

        return try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Int, Error>) in
            stateLock.lock()
            if _running {
                let port = _boundPort
                stateLock.unlock()
                cont.resume(returning: port)
                return
            }
            guard listener == nil else {
                stateLock.unlock()
                cont.resume(throwing: NSError(domain: "BoBoHost", code: 1, userInfo: [
                    NSLocalizedDescriptionKey: "监听服务正在启动"
                ]))
                return
            }
            listener = nwListener
            let done = FlagOnce()
            nwListener.stateUpdateHandler = { [weak self, weak nwListener] state in
                guard let self, let nwListener else {
                    if done.trySet() { cont.resume(throwing: CancellationError()) }
                    return
                }
                switch state {
                case .ready:
                    let p = Int(nwListener.port?.rawValue ?? 0)
                    let current = self.stateLock.withLock {
                        guard self.listener === nwListener else { return false }
                        self._boundPort = p
                        self._running = true
                        return true
                    }
                    if done.trySet() {
                        if current { cont.resume(returning: p) }
                        else { cont.resume(throwing: CancellationError()) }
                    }
                case .failed(let err):
                    self.listenerEnded(nwListener)
                    if done.trySet() { cont.resume(throwing: err) }
                case .cancelled:
                    self.listenerEnded(nwListener)
                    if done.trySet() { cont.resume(throwing: CancellationError()) }
                default:
                    break
                }
            }
            nwListener.newConnectionHandler = { [weak self] nwConn in
                self?.accept(nwConn)
            }
            nwListener.start(queue: listenerQueue)
            stateLock.unlock()
        }
    }

    private func listenerEnded(_ ended: NWListener) {
        let closing = stateLock.withLock { () -> [ClientSession] in
            guard listener === ended else { return [] }
            listener = nil
            _running = false
            _boundPort = 0
            let closing = Array(sessions.values)
            sessions.removeAll()
            return closing
        }
        closing.forEach { $0.close() }
    }

    public func stop() {
        cancelPairing()
        stateLock.lock()
        _running = false
        _boundPort = 0
        let l = listener
        listener = nil
        let closing = Array(sessions.values)
        sessions.removeAll()
        stateLock.unlock()
        l?.cancel()
        closing.forEach { $0.close() }
    }

    public func beginPairing(ttlSeconds: Int = 120) -> BoBoLocalPairSession {
        identity.ensureLoaded()
        let ttl = max(30, min(600, ttlSeconds))
        let now = Date()
        let sess = BoBoLocalPairSession(
            pin: BoBoIdentityStore.generateFourDigitPin(),
            createdAt: now,
            expiresAt: now.addingTimeInterval(TimeInterval(ttl))
        )
        pairLock.lock()
        activePairSession = sess
        pairLock.unlock()
        return sess
    }

    public func getActivePairing() -> BoBoLocalPairSession? {
        pairLock.lock()
        defer { pairLock.unlock() }
        if let cur = activePairSession, cur.isExpired {
            activePairSession = nil
            return nil
        }
        return activePairSession
    }

    public func cancelPairing() {
        pairLock.lock()
        activePairSession = nil
        pairLock.unlock()
    }

    public func disconnectAllStreams(reason: String) {
        stateLock.lock()
        let copy = Array(sessions.values)
        sessions.removeAll()
        stateLock.unlock()
        for s in copy { s.close() }
    }

    public func disconnectStreamingSessions(reason: String) {
        let session = stateLock.withLock { activeStream }
        session?.close()
    }

    public func notifyStreamReconfigured(monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int) {
        let monitors = delegate?.hostMonitors() ?? []
        sendStreamNotification([
            "type": "RECONFIGURED", "monitorIndex": monitorIndex, "width": width, "height": height,
            "fps": fps, "bitrateKbps": bitrateKbps, "monitors": BoBoProtocol.serializeMonitors(monitors)
        ])
    }

    public func notifyPrivacyState(active: Bool) {
        sendStreamNotification(["type": "PRIVACY_STATE", "active": active])
    }

    private func sendStreamNotification(_ message: [String: Any]) {
        let session = stateLock.withLock { activeStream }
        guard let session, session.isStreaming,
              let payload = try? JSONSerialization.data(withJSONObject: message) else { return }
        session.conn.sendFrameFireAndForget(type: .controlJson, payload: payload)
    }

    private func claimStream(_ session: ClientSession) -> Bool {
        stateLock.withLock {
            guard _running, _allowIncoming, activeStream == nil,
                  sessions[ObjectIdentifier(session)] === session else { return false }
            activeStream = session
            return true
        }
    }

    private func streamCanContinue(_ session: ClientSession) -> Bool {
        stateLock.withLock {
            _running && _allowIncoming && activeStream === session && sessions[ObjectIdentifier(session)] === session
        } && !session.isClosed
    }

    public func broadcastVideoFrame(width: Int, height: Int, isKeyframe: Bool, h264: Data, timestampMs: UInt32) {
        stateLock.lock()
        let copy = Array(sessions.values)
        stateLock.unlock()
        guard !copy.isEmpty else { return }
        let payload = BoBoProtocol.buildVideoPayload(width: width, height: height, isKeyframe: isKeyframe, h264Data: h264)
        for s in copy where s.isStreaming {
            s.enqueueVideo(payload: payload, isKeyframe: isKeyframe, timestampMs: timestampMs)
        }
    }

    public func broadcastAudioFrame(sampleRate: Int, channels: Int, sampleCount: Int, adpcm: Data) {
        stateLock.lock()
        let copy = Array(sessions.values)
        stateLock.unlock()
        guard !copy.isEmpty else { return }
        let payload = BoBoProtocol.buildAudioPayload(sampleRate: sampleRate, channels: channels, sampleCount: sampleCount, adpcmData: adpcm)
        for s in copy where s.isStreaming {
            s.enqueueAudio(payload: payload)
        }
    }

    private func accept(_ nwConn: NWConnection) {
        let conn = BoBoFramedConnection(connection: nwConn, queueLabel: "com.bobo.remote.host.conn")
        let session = ClientSession(conn: conn) { [weak self] in
            self?.delegate?.hostDidRequestKeyframe()
        }
        let key = ObjectIdentifier(session)
        stateLock.lock()
        if !_running || sessions.count >= Self.maxConcurrentSessions {
            stateLock.unlock()
            conn.close()
            return
        }
        sessions[key] = session
        stateLock.unlock()

        Task.detached(priority: .userInteractive) { [weak self] in
            await self?.runSession(session, key: key)
        }
    }

    private func runSession(_ session: ClientSession, key: ObjectIdentifier) async {
        await handleSession(session)
        session.close()
        let ownedStream = stateLock.withLock {
            sessions.removeValue(forKey: key)
            return activeStream === session
        }
        if ownedStream {
            await delegate?.hostStopStream()
            delegate?.hostSessionStateChanged(active: false, peerName: "")
            stateLock.withLock { activeStream = nil }
        }
    }

    private func handleSession(_ session: ClientSession) async {
        let remoteIp = extractRemoteIp(session.conn.connection.endpoint)
        var clientName = remoteIp
        var activeFileHandle: FileHandle?
        var activeFileTmpURL: URL?
        var activeFileId = ""
        var activeFileName = ""
        var activeFileSha = ""
        var activeFileTotalBytes: Int64 = 0

        func cleanupActiveFile() {
            try? activeFileHandle?.close()
            activeFileHandle = nil
            if let tmp = activeFileTmpURL {
                try? FileManager.default.removeItem(at: tmp)
                activeFileTmpURL = nil
            }
            activeFileId = ""
            activeFileName = ""
            activeFileSha = ""
            activeFileTotalBytes = 0
        }

        defer { cleanupActiveFile() }

        do {
            try await session.conn.start(timeoutSeconds: 8.0)
            let sessionNonce = BoBoIdentityStore.generateNonceHex(byteCount: 16)

            guard let first = try await session.conn.readFrame(
                maxPayloadOverride: BoBoProtocol.maxPreAuthPayloadBytes,
                timeoutSeconds: 8.0
            ),
                  first.type == .controlJson,
                  let hello = BoBoProtocol.parseJson(first.payload),
                  (hello["type"] as? String) == "HELLO",
                  (hello["protocol"] as? String) == BoBoProtocol.protocolMagic else {
                return
            }

            let clientNodeId = (hello["nodeId"] as? String) ?? ""
            let clientCode = (hello["deviceCode"] as? String) ?? ""
            let clientPlatform = (hello["platform"] as? String) ?? BoBoProtocol.platformWindows
            clientName = (hello["deviceName"] as? String) ?? remoteIp
            let clientThumb = (hello["certThumbprint"] as? String) ?? ""
            let helloClientCertDer = (hello["clientCertDer"] as? String) ?? ""

            // peerPaired 仅表示证书指纹已在信任表；真正授权必须通过 AUTH_PROOF 或带私钥签名的 PAIR_REQUEST
            let peerPaired = identity.isPeerAuthorized(peerNodeId: clientNodeId, peerThumbprint: clientThumb)
            var authorized = false
            let monitors = delegate?.hostMonitors() ?? [BoBoMonitorDescriptor()]

            try await session.conn.sendJson([
                "type": "HELLO_ACK",
                "protocol": BoBoProtocol.protocolMagic,
                "protocolVersion": BoBoProtocol.protocolVersion,
                "platform": BoBoProtocol.currentPlatform,
                "hostControlMode": hostControlMode,
                "screenOrientation": delegate?.hostScreenOrientation() ?? 0,
                "nodeId": identity.nodeId,
                "deviceCode": identity.deviceCode,
                "deviceName": identity.deviceName,
                "certThumbprint": identity.certThumbprint,
                "version": BoBoProtocol.appVersion,
                "allowIncoming": allowIncomingControl,
                "authorized": peerPaired,
                "authChallenge": true,
                "nonce": sessionNonce,
                "monitors": BoBoProtocol.serializeMonitors(monitors)
            ])

            while isRunning {
                let maxOverride = authorized ? 0 : BoBoProtocol.maxPreAuthPayloadBytes
                let readTimeout: Double = session.isStreaming ? 25.0 : 45.0
                guard let frame = try await session.conn.readFrame(
                    maxPayloadOverride: maxOverride,
                    timeoutSeconds: readTimeout
                ) else { break }

                switch frame.type {
                case .inputEvent:
                    if authorized && session.isStreaming && streamCanContinue(session) && hostControlMode == BoBoProtocol.controlModeFull,
                       let pkt = BoBoProtocol.parseInputPayload(frame.payload) {
                        delegate?.hostDidReceiveInput(pkt)
                    }
                case .fileChunk:
                    if authorized, let fh = activeFileHandle,
                       let chunk = BoBoProtocol.parseFileChunkPayload(frame.payload),
                       chunk.transferId == activeFileId, !chunk.data.isEmpty {
                        let chunkLen = Int64(chunk.data.count)
                        if chunk.offset < 0 || chunkLen > Int64(BoBoProtocol.maxFileChunkPayloadBytes)
                            || chunk.offset > activeFileTotalBytes
                            || chunkLen > (activeFileTotalBytes - chunk.offset) {
                            cleanupActiveFile()
                            try await session.conn.sendJson([
                                "type": "FILE_ACK",
                                "transferId": chunk.transferId,
                                "ok": false,
                                "error": "文件分片偏移或长度越界"
                            ])
                            continue
                        }
                        try fh.seek(toOffset: UInt64(chunk.offset))
                        try fh.write(contentsOf: chunk.data)
                    }
                case .controlJson:
                    guard let msg = BoBoProtocol.parseJson(frame.payload),
                          let t = msg["type"] as? String else { continue }
                    switch t {
                    case "PING":
                        let ts = (msg["ts"] as? NSNumber)?.int64Value ?? 0
                        try await session.conn.sendJson(["type": "PONG", "ts": ts])
                    case "AUTH_PROOF":
                        let sig = (msg["signature"] as? String) ?? ""
                        let certDer = (msg["clientCertDer"] as? String) ?? helloClientCertDer
                        let sigOk = identity.isPeerAuthorized(peerNodeId: clientNodeId, peerThumbprint: clientThumb) && BoBoIdentityStore.verifyAuthChallenge(
                            clientCertDerBase64: certDer,
                            expectedClientThumbprint: clientThumb,
                            nonceHex: sessionNonce,
                            hostThumbprint: identity.certThumbprint,
                            signatureBase64: sig
                        )
                        if sigOk {
                            authorized = true
                            try await session.conn.sendJson(["type": "AUTH_RESULT", "ok": true])
                        } else {
                            try? await Task.sleep(nanoseconds: Self.authFailureDelayNs)
                            try await session.conn.sendJson([
                                "type": "AUTH_RESULT",
                                "ok": false,
                                "error": "设备私钥身份校验失败，请重新配对"
                            ])
                            return
                        }
                    case "PAIR_REQUEST":
                        if !allowIncomingControl {
                            try await session.conn.sendJson([
                                "type": "PAIR_RESULT",
                                "ok": false,
                                "error": "对方已关闭「允许本机被控」"
                            ])
                            return
                        }
                        let proof = (msg["proof"] as? String) ?? ""
                        let authSig = (msg["authSig"] as? String) ?? ""
                        let certDer = (msg["clientCertDer"] as? String) ?? helloClientCertDer
                        let sigOk = !clientNodeId.isEmpty && BoBoIdentityStore.verifyAuthChallenge(
                            clientCertDerBase64: certDer,
                            expectedClientThumbprint: clientThumb,
                            nonceHex: sessionNonce,
                            hostThumbprint: identity.certThumbprint,
                            signatureBase64: authSig
                        )

                        var matched = false
                        var pinMatched = false
                        var lockedOut = false
                        pairLock.lock()
                        if var cur = activePairSession, !cur.isExpired {
                            let expected = BoBoIdentityStore.computePinProof(
                                pin: cur.pin,
                                nonceHex: sessionNonce,
                                clientThumbprint: clientThumb,
                                hostThumbprint: identity.certThumbprint
                            )
                            pinMatched = BoBoIdentityStore.fixedTimeEqualsHex(proof, expected)
                            if pinMatched && sigOk {
                                matched = true
                                activePairSession = nil
                            } else {
                                cur.failedAttempts += 1
                                if cur.failedAttempts >= BoBoLocalPairSession.maxFailedAttempts {
                                    activePairSession = nil
                                    lockedOut = true
                                } else {
                                    activePairSession = cur
                                }
                            }
                        } else {
                            activePairSession = nil
                        }
                        pairLock.unlock()

                        if !matched {
                            try? await Task.sleep(nanoseconds: Self.authFailureDelayNs)
                            let errMsg: String
                            if lockedOut {
                                errMsg = "错误次数过多，4 位配对码已失效，请在被控端重新生成"
                            } else if pinMatched && !sigOk {
                                errMsg = "客户端证书私钥签名验证失败，请升级客户端"
                            } else {
                                errMsg = "4 位配对码不正确或已失效"
                            }
                            try await session.conn.sendJson([
                                "type": "PAIR_RESULT",
                                "ok": false,
                                "error": errMsg
                            ])
                            return
                        }
                        let newPeer = BoBoAuthorizedPeer(
                            nodeId: clientNodeId,
                            deviceCode: clientCode,
                            deviceName: clientName,
                            certThumbprint: clientThumb,
                            lastHost: remoteIp
                        )
                        guard identity.authorizeOrUpdatePeer(newPeer) else {
                            try await session.conn.sendJson([
                                "type": "PAIR_RESULT", "ok": false, "error": "客户端证书与已配对记录不一致"
                            ])
                            return
                        }
                        delegate?.hostDidAuthorizePeer(newPeer)
                        authorized = true
                        try await session.conn.sendJson([
                            "type": "PAIR_RESULT",
                            "ok": true,
                            "hostName": identity.deviceName,
                            "hostDeviceCode": identity.deviceCode
                        ])
                    case "START_STREAM":
                        if !allowIncomingControl {
                            try await sendError(session, code: "HOST_DISABLED", message: "对方已关闭「允许本机被控」")
                            continue
                        }
                        if !authorized {
                            try await sendError(session, code: "PAIR_REQUIRED", message: "尚未完成 BoBo 双端配对授权或私钥认证")
                            continue
                        }
                        guard let delegate, let requested = streamConfiguration(msg, delegate: delegate) else {
                            try await sendError(session, code: "CAPTURE_NOT_READY", message: "被控端尚未准备好屏幕采集。")
                            return
                        }
                        guard claimStream(session) else {
                            try await sendError(session, code: "HOST_BUSY", message: "被控端已有一个远控会话。")
                            return
                        }
                        let muteHost = (msg["muteHost"] as? NSNumber)?.boolValue ?? false
                        let reqPrivacy = (msg["requestPrivacy"] as? NSNumber)?.boolValue ?? false
                        let capture: (monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int, privacyActive: Bool)
                        do {
                            capture = try await delegate.hostStartStream(
                                monitorIndex: requested.monitorIndex, width: requested.width, height: requested.height,
                                fps: requested.fps, bitrateKbps: requested.bitrateKbps,
                                muteHost: muteHost, requestPrivacy: reqPrivacy, clientPlatform: clientPlatform
                            )
                        } catch {
                            try await sendError(session, code: "CAPTURE_FAILED", message: error.localizedDescription)
                            return
                        }
                        guard streamCanContinue(session) else { return }
                        try await session.conn.sendJson([
                            "type": "STREAM_STARTED", "width": capture.width, "height": capture.height,
                            "fps": capture.fps, "bitrateKbps": capture.bitrateKbps, "monitorIndex": capture.monitorIndex,
                            "superDisplayActive": false, "superDisplayNativeMatch": false,
                            "privacyActive": capture.privacyActive, "hostCursorComposited": delegate.hostCompositesCursor(),
                            "platform": BoBoProtocol.currentPlatform, "hostControlMode": hostControlMode,
                            "monitors": BoBoProtocol.serializeMonitors(delegate.hostMonitors())
                        ])
                        guard streamCanContinue(session) else { return }
                        session.isStreaming = true
                        delegate.hostSessionStateChanged(active: true, peerName: clientName)
                        delegate.hostDidRequestKeyframe()
                    case "RECONFIGURE":
                        guard authorized, session.isStreaming, streamCanContinue(session),
                              let delegate, let requested = streamConfiguration(msg, delegate: delegate) else { continue }
                        do {
                            let capture = try await delegate.hostReconfigureStream(
                                monitorIndex: requested.monitorIndex, width: requested.width, height: requested.height,
                                fps: requested.fps, bitrateKbps: requested.bitrateKbps
                            )
                            guard streamCanContinue(session) else { return }
                            notifyStreamReconfigured(monitorIndex: capture.monitorIndex, width: capture.width,
                                                     height: capture.height, fps: capture.fps, bitrateKbps: capture.bitrateKbps)
                            delegate.hostDidRequestKeyframe()
                        } catch {
                            try await sendError(session, code: "CAPTURE_FAILED", message: error.localizedDescription)
                            return
                        }
                    case "SET_SUPER_DISPLAY":
                        guard authorized, session.isStreaming, streamCanContinue(session) else { continue }
                        let enable = (msg["enable"] as? NSNumber)?.boolValue ?? false
                        let cw = (msg["clientWidth"] as? NSNumber)?.intValue ?? 0
                        let ch = (msg["clientHeight"] as? NSNumber)?.intValue ?? 0
                        let sd: (active: Bool, nativeMatch: Bool, width: Int, height: Int) =
                            delegate?.hostDidSetSuperDisplay(enable: enable, clientWidth: cw, clientHeight: ch) ?? (false, false, 0, 0)
                        try await session.conn.sendJson([
                            "type": "SUPER_DISPLAY_STATE",
                            "active": sd.active,
                            "nativeMatch": sd.nativeMatch,
                            "width": sd.width,
                            "height": sd.height
                        ])
                    case "REQUEST_KEYFRAME":
                        if authorized && session.isStreaming && streamCanContinue(session) { delegate?.hostDidRequestKeyframe() }
                    case "SET_PRIVACY":
                        guard authorized, session.isStreaming, streamCanContinue(session), hostControlMode == BoBoProtocol.controlModeFull else { continue }
                        let en = (msg["enable"] as? NSNumber)?.boolValue ?? false
                        let active = await delegate?.hostSetPrivacy(enable: en) ?? false
                        try await session.conn.sendJson(["type": "PRIVACY_STATE", "active": active])
                    case "SET_MUTE_HOST":
                        if authorized && session.isStreaming && streamCanContinue(session) && hostControlMode == BoBoProtocol.controlModeFull {
                            delegate?.hostDidSetMuteHost(mute: (msg["mute"] as? NSNumber)?.boolValue ?? false)
                        }
                    case "LOCK_WORKSTATION":
                        if authorized && session.isStreaming && streamCanContinue(session) && hostControlMode == BoBoProtocol.controlModeFull {
                            delegate?.hostDidRequestLockWorkstation()
                        }
                    case "CLIPBOARD_TEXT":
                        if authorized, session.isStreaming, streamCanContinue(session), hostControlMode == BoBoProtocol.controlModeFull,
                           let text = msg["text"] as? String, !text.isEmpty {
                            let inj = (msg["injectUnicode"] as? NSNumber)?.boolValue ?? true
                            delegate?.hostDidReceiveClipboard(text: text, injectUnicode: inj)
                        }
                    case "FILE_OFFER":
                        guard authorized else {
                            try await sendError(session, code: "PAIR_REQUIRED", message: "未授权设备")
                            continue
                        }
                        cleanupActiveFile()
                        let tid = ((msg["transferId"] as? String) ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
                        let rawName = (msg["fileName"] as? String) ?? "received.bin"
                        let totalBytes = (msg["totalBytes"] as? NSNumber)?.int64Value ?? -1
                        let sha = ((msg["sha256"] as? String) ?? "").trimmingCharacters(in: .whitespacesAndNewlines).lowercased()

                        guard !tid.isEmpty, tid.utf8.count <= 16, tid.utf8.allSatisfy({ (33...126).contains($0) }) else {
                            try await session.conn.sendJson(["type": "FILE_ACK", "transferId": tid, "ok": false, "error": "无效的 transferId"])
                            continue
                        }
                        guard totalBytes >= 0 && totalBytes <= BoBoProtocol.maxFileTransferBytes else {
                            try await session.conn.sendJson(["type": "FILE_ACK", "transferId": tid, "ok": false, "error": "文件体积超过 2 GiB 上限或无效"])
                            continue
                        }
                        guard BoBoProtocol.isValidHexSha256(sha) else {
                            try await session.conn.sendJson(["type": "FILE_ACK", "transferId": tid, "ok": false, "error": "缺少有效的 SHA-256 校验摘要"])
                            continue
                        }

                        activeFileId = tid
                        activeFileName = BoBoProtocol.sanitizeFileName(rawName)
                        activeFileSha = sha
                        activeFileTotalBytes = totalBytes
                        do {
                            try FileManager.default.createDirectory(at: downloadDir, withIntermediateDirectories: true)
                            let tmp = downloadDir.appendingPathComponent(".bobo-\(UUID().uuidString).part")
                            guard FileManager.default.createFile(atPath: tmp.path, contents: nil, attributes: [.posixPermissions: 0o600]) else {
                                throw CocoaError(.fileWriteUnknown)
                            }
                            activeFileTmpURL = tmp
                            activeFileHandle = try FileHandle(forWritingTo: tmp)
                        } catch {
                            cleanupActiveFile()
                            try await session.conn.sendJson(["type": "FILE_ACK", "transferId": tid, "ok": false, "error": "无法创建接收文件"])
                            continue
                        }
                        try await session.conn.sendJson(["type": "FILE_ACCEPT", "transferId": activeFileId])
                    case "FILE_DONE":
                        guard authorized else { continue }
                        let tid = (msg["transferId"] as? String) ?? ""
                        try? activeFileHandle?.close()
                        activeFileHandle = nil
                        guard let tmp = activeFileTmpURL, tid == activeFileId else {
                            cleanupActiveFile()
                            try await session.conn.sendJson([
                                "type": "FILE_ACK",
                                "transferId": tid,
                                "ok": false,
                                "error": "无匹配的文件传输上下文"
                            ])
                            continue
                        }
                        let expectedBytes = activeFileTotalBytes
                        let expectedSha = activeFileSha
                        let safeName = activeFileName
                        activeFileTmpURL = nil
                        activeFileId = ""
                        activeFileName = ""
                        activeFileSha = ""
                        activeFileTotalBytes = 0

                        let actualSize = ((try? FileManager.default.attributesOfItem(atPath: tmp.path)[.size]) as? NSNumber)?.int64Value ?? -1
                        guard actualSize == expectedBytes else {
                            try? FileManager.default.removeItem(at: tmp)
                            try await session.conn.sendJson([
                                "type": "FILE_ACK",
                                "transferId": tid,
                                "ok": false,
                                "error": "文件字节数与声明不符（\(actualSize)/\(expectedBytes)）"
                            ])
                            continue
                        }

                        guard let actualSha = Self.computeFileSha256Streaming(url: tmp),
                              BoBoIdentityStore.fixedTimeEqualsHex(actualSha, expectedSha) else {
                            try? FileManager.default.removeItem(at: tmp)
                            try await session.conn.sendJson([
                                "type": "FILE_ACK",
                                "transferId": tid,
                                "ok": false,
                                "error": "SHA-256 完整性校验失败"
                            ])
                            continue
                        }

                        let dest = allocateUniqueURL(dir: downloadDir, fileName: safeName)
                        let dirCanonical = downloadDir.resolvingSymlinksInPath().standardizedFileURL.path
                        let dirPrefix = dirCanonical.hasSuffix("/") ? dirCanonical : (dirCanonical + "/")
                        let destCanonical = dest.resolvingSymlinksInPath().standardizedFileURL.path
                        guard destCanonical.hasPrefix(dirPrefix) else {
                            try? FileManager.default.removeItem(at: tmp)
                            try await session.conn.sendJson([
                                "type": "FILE_ACK",
                                "transferId": tid,
                                "ok": false,
                                "error": "目标保存路径越界"
                            ])
                            continue
                        }

                        do {
                            try FileManager.default.moveItem(at: tmp, to: dest)
                            try await session.conn.sendJson([
                                "type": "FILE_ACK",
                                "transferId": tid,
                                "ok": true,
                                "savedPath": dest.path
                            ])
                            delegate?.hostDidReceiveFile(url: dest)
                        } catch {
                            try? FileManager.default.removeItem(at: tmp)
                            try await session.conn.sendJson([
                                "type": "FILE_ACK",
                                "transferId": tid,
                                "ok": false,
                                "error": "保存文件失败：\(error.localizedDescription)"
                            ])
                        }
                    case "DISCONNECT":
                        return
                    default:
                        break
                    }
                default:
                    break
                }
            }
        } catch {
            // Connection terminated
        }
    }

    private static func computeFileSha256Streaming(url: URL) -> String? {
        guard let fh = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? fh.close() }
        do {
            var hasher = SHA256()
            while let chunk = try fh.read(upToCount: 65536), !chunk.isEmpty { hasher.update(data: chunk) }
            return BoBoProtocol.bytesToHex(Data(hasher.finalize()))
        } catch {
            return nil
        }
    }

    private func streamConfiguration(_ msg: [String: Any], delegate: BoBoHostServerDelegate)
        -> (monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int)? {
        delegate.hostStreamConfiguration(
            monitorIndex: max(0, (msg["monitorIndex"] as? NSNumber)?.intValue ?? 0),
            width: min(3840, max(320, (msg["width"] as? NSNumber)?.intValue ?? 1920)),
            height: min(3840, max(240, (msg["height"] as? NSNumber)?.intValue ?? 1080)),
            fps: min(120, max(15, (msg["fps"] as? NSNumber)?.intValue ?? 60)),
            bitrateKbps: min(60000, max(500, (msg["bitrateKbps"] as? NSNumber)?.intValue ?? 14000))
        )
    }

    private func sendError(_ session: ClientSession, code: String, message: String) async throws {
        try await session.conn.sendJson(["type": "ERROR", "code": code, "message": message])
    }

    private func extractRemoteIp(_ endpoint: NWEndpoint) -> String {
        if case let .hostPort(host, _) = endpoint {
            var s = "\(host)"
            if let pct = s.firstIndex(of: "%") {
                s = String(s[..<pct])
            }
            if s.lowercased().hasPrefix("::ffff:") {
                s = String(s.dropFirst(7))
            }
            return s
        }
        return ""
    }

    private func allocateUniqueURL(dir: URL, fileName: String) -> URL {
        let base = dir.appendingPathComponent(fileName)
        if !FileManager.default.fileExists(atPath: base.path) { return base }
        let stem = base.deletingPathExtension().lastPathComponent
        let ext = base.pathExtension
        for n in 1..<1000 {
            let name = ext.isEmpty ? "\(stem) (\(n))" : "\(stem) (\(n)).\(ext)"
            let cand = dir.appendingPathComponent(name)
            if !FileManager.default.fileExists(atPath: cand.path) { return cand }
        }
        return dir.appendingPathComponent("\(UUID().uuidString)_\(fileName)")
    }

    private final class ClientSession: @unchecked Sendable {
        let conn: BoBoFramedConnection
        private let onDroppedVideo: @Sendable () -> Void
        private let lock = NSLock()
        private var _streaming = false
        private var closed = false
        var isClosed: Bool { lock.withLock { closed } }
        private var videoInFlight = 0
        private var audioInFlight = 0
        private var lastRecoveryReqNs: UInt64 = 0

        var isStreaming: Bool {
            get { lock.lock(); defer { lock.unlock() }; return _streaming }
            set { lock.withLock { _streaming = newValue && !closed } }
        }

        init(conn: BoBoFramedConnection, onDroppedVideo: @escaping @Sendable () -> Void) {
            self.conn = conn
            self.onDroppedVideo = onDroppedVideo
        }

        func enqueueVideo(payload: Data, isKeyframe: Bool, timestampMs: UInt32) {
            var shouldRequestRecoveryIdr = false
            lock.lock()
            if closed || !_streaming {
                lock.unlock()
                return
            }
            if videoInFlight >= (isKeyframe ? 3 : 2) {
                let now = DispatchTime.now().uptimeNanoseconds
                if now &- lastRecoveryReqNs >= 350_000_000 {
                    lastRecoveryReqNs = now
                    shouldRequestRecoveryIdr = true
                }
                lock.unlock()
                if shouldRequestRecoveryIdr {
                    onDroppedVideo()
                }
                return
            }
            videoInFlight += 1
            lock.unlock()

            conn.sendFrameFireAndForget(type: .videoH264, flagsOrTimestamp: timestampMs, payload: payload) { [weak self] in
                self?.lock.lock()
                self?.videoInFlight = max(0, (self?.videoInFlight ?? 1) - 1)
                self?.lock.unlock()
            }
        }

        func enqueueAudio(payload: Data) {
            lock.lock()
            if closed || !_streaming || audioInFlight >= 6 {
                lock.unlock()
                return
            }
            audioInFlight += 1
            lock.unlock()

            conn.sendFrameFireAndForget(type: .audioAdpcm, flagsOrTimestamp: 0, payload: payload) { [weak self] in
                self?.lock.lock()
                self?.audioInFlight = max(0, (self?.audioInFlight ?? 1) - 1)
                self?.lock.unlock()
            }
        }

        func close() {
            lock.withLock {
                _streaming = false
                closed = true
            }
            conn.close()
        }
    }
}

private final class FlagOnce: @unchecked Sendable {
    private let lock = NSLock()
    private var set = false
    func trySet() -> Bool {
        lock.lock(); defer { lock.unlock() }
        if set { return false }
        set = true
        return true
    }
}
