import CryptoKit
import Foundation
import Network

public protocol BoBoHostServerDelegate: AnyObject, Sendable {
    func hostMonitors() -> [BoBoMonitorDescriptor]
    func hostDidStartStream(monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int, muteHost: Bool, requestPrivacy: Bool)
    func hostDidReconfigureStream(monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int)
    func hostDidStopAllStreams()
    func hostDidRequestKeyframe()
    func hostDidReceiveInput(_ packet: BoBoInputPacket)
    func hostDidSetPrivacy(enable: Bool)
    func hostDidSetMuteHost(mute: Bool)
    func hostDidRequestLockWorkstation()
    func hostDidReceiveClipboard(text: String, injectUnicode: Bool)
    func hostSessionStateChanged(active: Bool, peerName: String)
    func hostDidReceiveFile(url: URL)
}

public extension BoBoHostServerDelegate {
    func hostDidReconfigureStream(monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int) {}
    func hostDidRequestKeyframe() {}
    func hostDidReceiveInput(_ packet: BoBoInputPacket) {}
    func hostDidSetPrivacy(enable: Bool) {}
    func hostDidSetMuteHost(mute: Bool) {}
    func hostDidRequestLockWorkstation() {}
    func hostDidReceiveClipboard(text: String, injectUnicode: Bool) {}
    func hostSessionStateChanged(active: Bool, peerName: String) {}
    func hostDidReceiveFile(url: URL) {}
}

/// BoBo V6.2 Apple 被控端 TLS 1.2 服务端（供 macOS 全功能被控与 iOS ReplayKit 投屏被看共享）。
public final class BoBoHostServer: @unchecked Sendable {
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
        identity.ensureLoaded()
        let secId = try identity.loadSecIdentity()
        let params = BoBoFramedConnection.makeServerParameters(identity: secId)

        let nwListener: NWListener
        if requestedPort == 0 {
            nwListener = try NWListener(using: params)
        } else {
            nwListener = try NWListener(using: params, on: NWEndpoint.Port(rawValue: requestedPort)!)
        }

        let actualPort: Int = try await withCheckedThrowingContinuation { cont in
            let done = FlagOnce()
            nwListener.stateUpdateHandler = { [weak self] state in
                switch state {
                case .ready:
                    let p = Int(nwListener.port?.rawValue ?? 0)
                    self?.stateLock.lock()
                    self?._boundPort = p
                    self?._running = true
                    self?.listener = nwListener
                    self?.stateLock.unlock()
                    if done.trySet() { cont.resume(returning: p) }
                case .failed(let err):
                    if done.trySet() { cont.resume(throwing: err) }
                default:
                    break
                }
            }
            nwListener.newConnectionHandler = { [weak self] nwConn in
                self?.accept(nwConn)
            }
            nwListener.start(queue: listenerQueue)
        }
        return actualPort
    }

    public func stop() {
        cancelPairing()
        stateLock.lock()
        _running = false
        let l = listener
        listener = nil
        stateLock.unlock()
        l?.cancel()
        disconnectAllStreams(reason: "被控端已退出 BoBo远程")
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
        delegate?.hostDidStopAllStreams()
        delegate?.hostSessionStateChanged(active: false, peerName: "")
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
            s.conn.sendFrameFireAndForget(type: .audioAdpcm, flagsOrTimestamp: 0, payload: payload)
        }
    }

    private func accept(_ nwConn: NWConnection) {
        let conn = BoBoFramedConnection(connection: nwConn, queueLabel: "com.bobo.remote.host.conn")
        let session = ClientSession(conn: conn)
        let key = ObjectIdentifier(session)
        stateLock.lock()
        sessions[key] = session
        stateLock.unlock()

        Task.detached(priority: .userInteractive) { [weak self] in
            await self?.runSession(session, key: key)
        }
    }

    private func runSession(_ session: ClientSession, key: ObjectIdentifier) async {
        let remoteIp = extractRemoteIp(session.conn.connection.endpoint)
        var clientName = remoteIp
        var activeFileHandle: FileHandle?
        var activeFileTmpURL: URL?
        var activeFileId = ""
        var activeFileName = ""
        var activeFileSha = ""
        var startedStream = false

        defer {
            try? activeFileHandle?.close()
            if let tmp = activeFileTmpURL { try? FileManager.default.removeItem(at: tmp) }
            session.close()
            stateLock.lock()
            sessions.removeValue(forKey: key)
            let anyStillStreaming = sessions.values.contains { $0.isStreaming }
            stateLock.unlock()
            if startedStream && !anyStillStreaming {
                delegate?.hostDidStopAllStreams()
                delegate?.hostSessionStateChanged(active: false, peerName: "")
            }
        }

        do {
            try await session.conn.start(timeoutSeconds: 8.0)
            let sessionNonce = BoBoIdentityStore.generateNonceHex(byteCount: 16)

            guard let first = try await session.conn.readFrame(),
                  first.type == .controlJson,
                  let hello = BoBoProtocol.parseJson(first.payload),
                  (hello["type"] as? String) == "HELLO",
                  (hello["protocol"] as? String) == BoBoProtocol.protocolMagic else {
                return
            }

            let clientNodeId = (hello["nodeId"] as? String) ?? ""
            let clientCode = (hello["deviceCode"] as? String) ?? ""
            clientName = (hello["deviceName"] as? String) ?? remoteIp
            let clientThumb = (hello["certThumbprint"] as? String) ?? ""

            var authorized = identity.isPeerAuthorized(peerNodeId: clientNodeId, peerThumbprint: clientThumb)
            let monitors = delegate?.hostMonitors() ?? [BoBoMonitorDescriptor()]

            try await session.conn.sendJson([
                "type": "HELLO_ACK",
                "protocol": BoBoProtocol.protocolMagic,
                "protocolVersion": BoBoProtocol.protocolVersion,
                "platform": BoBoProtocol.currentPlatform,
                "hostControlMode": hostControlMode,
                "screenOrientation": 0,
                "nodeId": identity.nodeId,
                "deviceCode": identity.deviceCode,
                "deviceName": identity.deviceName,
                "certThumbprint": identity.certThumbprint,
                "version": BoBoProtocol.appVersion,
                "allowIncoming": allowIncomingControl,
                "authorized": authorized,
                "nonce": sessionNonce,
                "monitors": BoBoProtocol.serializeMonitors(monitors)
            ])

            while isRunning {
                guard let frame = try await session.conn.readFrame() else { break }
                switch frame.type {
                case .inputEvent:
                    if authorized && session.isStreaming && hostControlMode == BoBoProtocol.controlModeFull,
                       let pkt = BoBoProtocol.parseInputPayload(frame.payload) {
                        delegate?.hostDidReceiveInput(pkt)
                    }
                case .fileChunk:
                    if authorized, let fh = activeFileHandle,
                       let chunk = BoBoProtocol.parseFileChunkPayload(frame.payload),
                       chunk.transferId == activeFileId, !chunk.data.isEmpty {
                        try? fh.seek(toOffset: UInt64(max(0, chunk.offset)))
                        try? fh.write(contentsOf: chunk.data)
                    }
                case .controlJson:
                    guard let msg = BoBoProtocol.parseJson(frame.payload),
                          let t = msg["type"] as? String else { continue }
                    switch t {
                    case "PING":
                        let ts = (msg["ts"] as? NSNumber)?.int64Value ?? 0
                        try await session.conn.sendJson(["type": "PONG", "ts": ts])
                    case "PAIR_REQUEST":
                        if !allowIncomingControl {
                            try await session.conn.sendJson([
                                "type": "PAIR_RESULT",
                                "ok": false,
                                "error": "对方已关闭「允许本机被控」"
                            ])
                            continue
                        }
                        let proof = (msg["proof"] as? String) ?? ""
                        var matched = false
                        pairLock.lock()
                        if let cur = activePairSession, !cur.isExpired {
                            let expected = BoBoIdentityStore.computePinProof(
                                pin: cur.pin,
                                nonceHex: sessionNonce,
                                clientThumbprint: clientThumb,
                                hostThumbprint: identity.certThumbprint
                            )
                            if BoBoIdentityStore.fixedTimeEqualsHex(proof, expected) {
                                matched = true
                                activePairSession = nil
                            }
                        }
                        pairLock.unlock()

                        if !matched {
                            try await session.conn.sendJson([
                                "type": "PAIR_RESULT",
                                "ok": false,
                                "error": "4 位配对码不正确或已失效"
                            ])
                            continue
                        }
                        identity.authorizeOrUpdatePeer(BoBoAuthorizedPeer(
                            nodeId: clientNodeId,
                            deviceCode: clientCode,
                            deviceName: clientName,
                            certThumbprint: clientThumb,
                            lastHost: remoteIp
                        ))
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
                            try await sendError(session, code: "PAIR_REQUIRED", message: "尚未完成 BoBo 双端配对授权")
                            continue
                        }
                        let reqMon = (msg["monitorIndex"] as? NSNumber)?.intValue ?? 0
                        let reqW = (msg["width"] as? NSNumber)?.intValue ?? 1920
                        let reqH = (msg["height"] as? NSNumber)?.intValue ?? 1080
                        let reqFps = (msg["fps"] as? NSNumber)?.intValue ?? 60
                        let reqBps = (msg["bitrateKbps"] as? NSNumber)?.intValue ?? 14000
                        let muteHost = (msg["muteHost"] as? NSNumber)?.boolValue ?? false
                        let reqPrivacy = (msg["requestPrivacy"] as? NSNumber)?.boolValue ?? false

                        session.isStreaming = true
                        startedStream = true
                        delegate?.hostSessionStateChanged(active: true, peerName: clientName)
                        delegate?.hostDidStartStream(
                            monitorIndex: reqMon,
                            width: reqW,
                            height: reqH,
                            fps: reqFps,
                            bitrateKbps: reqBps,
                            muteHost: muteHost,
                            requestPrivacy: reqPrivacy
                        )
                        let curMons = delegate?.hostMonitors() ?? monitors
                        try await session.conn.sendJson([
                            "type": "STREAM_STARTED",
                            "width": reqW,
                            "height": reqH,
                            "fps": reqFps,
                            "bitrateKbps": reqBps,
                            "monitorIndex": reqMon,
                            "superDisplayActive": false,
                            "superDisplayNativeMatch": false,
                            "platform": BoBoProtocol.currentPlatform,
                            "hostControlMode": hostControlMode,
                            "monitors": BoBoProtocol.serializeMonitors(curMons)
                        ])
                    case "RECONFIGURE":
                        guard authorized else { continue }
                        let reqMon = (msg["monitorIndex"] as? NSNumber)?.intValue ?? 0
                        let reqW = (msg["width"] as? NSNumber)?.intValue ?? 1920
                        let reqH = (msg["height"] as? NSNumber)?.intValue ?? 1080
                        let reqFps = (msg["fps"] as? NSNumber)?.intValue ?? 60
                        let reqBps = (msg["bitrateKbps"] as? NSNumber)?.intValue ?? 14000
                        delegate?.hostDidReconfigureStream(monitorIndex: reqMon, width: reqW, height: reqH, fps: reqFps, bitrateKbps: reqBps)
                        try await session.conn.sendJson([
                            "type": "RECONFIGURED",
                            "monitorIndex": reqMon,
                            "width": reqW,
                            "height": reqH,
                            "fps": reqFps,
                            "bitrateKbps": reqBps
                        ])
                    case "REQUEST_KEYFRAME":
                        if authorized { delegate?.hostDidRequestKeyframe() }
                    case "SET_PRIVACY":
                        guard authorized else { continue }
                        let en = (msg["enable"] as? NSNumber)?.boolValue ?? false
                        delegate?.hostDidSetPrivacy(enable: en)
                        try await session.conn.sendJson(["type": "PRIVACY_STATE", "active": en])
                    case "SET_MUTE_HOST":
                        if authorized { delegate?.hostDidSetMuteHost(mute: (msg["mute"] as? NSNumber)?.boolValue ?? false) }
                    case "LOCK_WORKSTATION":
                        if authorized { delegate?.hostDidRequestLockWorkstation() }
                    case "CLIPBOARD_TEXT":
                        if authorized, let text = msg["text"] as? String, !text.isEmpty {
                            let inj = (msg["injectUnicode"] as? NSNumber)?.boolValue ?? true
                            delegate?.hostDidReceiveClipboard(text: text, injectUnicode: inj)
                        }
                    case "FILE_OFFER":
                        guard authorized else {
                            try await sendError(session, code: "PAIR_REQUIRED", message: "未授权设备")
                            continue
                        }
                        try? activeFileHandle?.close()
                        activeFileId = (msg["transferId"] as? String) ?? ""
                        let rawName = ((msg["fileName"] as? String) ?? "received.bin")
                        activeFileName = URL(fileURLWithPath: rawName).lastPathComponent
                        activeFileSha = (msg["sha256"] as? String) ?? ""
                        try? FileManager.default.createDirectory(at: downloadDir, withIntermediateDirectories: true)
                        let tmp = downloadDir.appendingPathComponent(".bobo-\(UUID().uuidString).part")
                        FileManager.default.createFile(atPath: tmp.path, contents: nil)
                        activeFileTmpURL = tmp
                        activeFileHandle = try FileHandle(forWritingTo: tmp)
                        try await session.conn.sendJson(["type": "FILE_ACCEPT", "transferId": activeFileId])
                    case "FILE_DONE":
                        let tid = (msg["transferId"] as? String) ?? ""
                        try? activeFileHandle?.close()
                        activeFileHandle = nil
                        if let tmp = activeFileTmpURL, tid == activeFileId, let received = try? Data(contentsOf: tmp) {
                            activeFileTmpURL = nil
                            let sha = BoBoProtocol.bytesToHex(Data(SHA256.hash(data: received)))
                            if activeFileSha.isEmpty || sha.caseInsensitiveCompare(activeFileSha) == .orderedSame {
                                let dest = allocateUniqueURL(dir: downloadDir, fileName: activeFileName)
                                try? FileManager.default.moveItem(at: tmp, to: dest)
                                try await session.conn.sendJson([
                                    "type": "FILE_ACK",
                                    "transferId": tid,
                                    "ok": true,
                                    "savedPath": dest.path
                                ])
                                delegate?.hostDidReceiveFile(url: dest)
                            } else {
                                try? FileManager.default.removeItem(at: tmp)
                                try await session.conn.sendJson([
                                    "type": "FILE_ACK",
                                    "transferId": tid,
                                    "ok": false,
                                    "error": "SHA-256 完整性校验失败"
                                ])
                            }
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

    private func sendError(_ session: ClientSession, code: String, message: String) async throws {
        try await session.conn.sendJson(["type": "ERROR", "code": code, "message": message])
    }

    private func extractRemoteIp(_ endpoint: NWEndpoint) -> String {
        if case let .hostPort(host, _) = endpoint {
            return "\(host)"
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
        private let lock = NSLock()
        private var _streaming = false
        private var videoInFlight = 0

        var isStreaming: Bool {
            get { lock.lock(); defer { lock.unlock() }; return _streaming }
            set { lock.lock(); _streaming = newValue; lock.unlock() }
        }

        init(conn: BoBoFramedConnection) { self.conn = conn }

        func enqueueVideo(payload: Data, isKeyframe: Bool, timestampMs: UInt32) {
            lock.lock()
            if !isKeyframe && videoInFlight >= 2 {
                lock.unlock()
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

        func close() {
            isStreaming = false
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
