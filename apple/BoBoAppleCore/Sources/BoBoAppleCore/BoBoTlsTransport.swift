import CryptoKit
import Foundation
import Network
import Security

/// 封装 Network.framework (NWConnection) 的 BoBo6 9 字节定长头 + 变长载荷二进制帧读写器。
public final class BoBoFramedConnection: @unchecked Sendable {
    public let connection: NWConnection
    private let queue: DispatchQueue
    private let stateLock = NSLock()
    private var readBuffer = Data()
    private var closed = false

    public init(connection: NWConnection, queueLabel: String = "com.bobo.remote.conn") {
        self.connection = connection
        self.queue = DispatchQueue(label: queueLabel, qos: .userInteractive)
    }

    public func start(timeoutSeconds: Double = 8.0) async throws {
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            let resumed = LockedFlag()
            let timer = DispatchSource.makeTimerSource(queue: queue)
            timer.schedule(deadline: .now() + timeoutSeconds)
            timer.setEventHandler {
                if resumed.trySet() {
                    self.connection.cancel()
                    cont.resume(throwing: NSError(domain: "BoBoTls", code: -1001, userInfo: [
                        NSLocalizedDescriptionKey: "连接 BoBo远程 超时"
                    ]))
                }
            }
            timer.resume()

            connection.stateUpdateHandler = { [weak self] state in
                switch state {
                case .ready:
                    timer.cancel()
                    if resumed.trySet() { cont.resume() }
                case .failed(let err):
                    timer.cancel()
                    self?.markClosed()
                    if resumed.trySet() { cont.resume(throwing: err) }
                case .cancelled:
                    timer.cancel()
                    self?.markClosed()
                    if resumed.trySet() {
                        cont.resume(throwing: NSError(domain: "BoBoTls", code: -1002, userInfo: [
                            NSLocalizedDescriptionKey: "连接已取消"
                        ]))
                    }
                default:
                    break
                }
            }
            connection.start(queue: queue)
        }
    }

    public func sendRaw(_ data: Data) async throws {
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            connection.send(content: data, completion: .contentProcessed { error in
                if let error {
                    cont.resume(throwing: error)
                } else {
                    cont.resume()
                }
            })
        }
    }

    public func sendFrame(type: BoBoPacketType, flagsOrTimestamp: UInt32 = 0, payload: Data) async throws {
        let raw = BoBoProtocol.encodeFrame(type: type, flagsOrTimestamp: flagsOrTimestamp, payload: payload)
        try await sendRaw(raw)
    }

    public func sendJson(_ dict: [String: Any]) async throws {
        let raw = try BoBoProtocol.encodeJsonFrame(dict)
        try await sendRaw(raw)
    }

    public func sendFrameFireAndForget(type: BoBoPacketType, flagsOrTimestamp: UInt32 = 0, payload: Data, completion: (@Sendable () -> Void)? = nil) {
        let raw = BoBoProtocol.encodeFrame(type: type, flagsOrTimestamp: flagsOrTimestamp, payload: payload)
        connection.send(content: raw, completion: .contentProcessed { _ in
            completion?()
        })
    }

    public func readFrame(maxPayloadOverride: Int = 0, timeoutSeconds: Double? = nil) async throws -> BoBoFrame? {
        let deadline: DispatchTime? = timeoutSeconds.map { .now() + $0 }
        guard let header = try await readExact(count: BoBoProtocol.headerBytes, deadline: deadline) else {
            return nil
        }
        guard let type = BoBoPacketType(rawValue: header[header.startIndex]) else {
            throw NSError(domain: "BoBoTls", code: -2001, userInfo: [
                NSLocalizedDescriptionKey: "未知的 BoBo6 帧类型"
            ])
        }
        let length = Int(BoBoProtocol.readInt32LE(header, offset: 1))
        let flagsOrTs = BoBoProtocol.readUInt32LE(header, offset: 5)
        let defaultMaxLen: Int
        switch type {
        case .videoH264: defaultMaxLen = BoBoProtocol.maxVideoPayloadBytes
        case .fileChunk: defaultMaxLen = BoBoProtocol.maxFileChunkPayloadBytes
        default: defaultMaxLen = BoBoProtocol.maxControlPayloadBytes
        }
        let maxLen = maxPayloadOverride > 0 ? min(maxPayloadOverride, defaultMaxLen) : defaultMaxLen
        guard length >= 0 && length <= maxLen else {
            throw NSError(domain: "BoBoTls", code: -2002, userInfo: [
                NSLocalizedDescriptionKey: "BoBo6 帧长度越界: \(length) > \(maxLen)"
            ])
        }
        if length == 0 {
            return BoBoFrame(type: type, flagsOrTimestamp: flagsOrTs, payload: Data())
        }
        guard let payload = try await readExact(count: length, deadline: deadline) else {
            return nil
        }
        return BoBoFrame(type: type, flagsOrTimestamp: flagsOrTs, payload: payload)
    }

    private func readExact(count: Int, deadline: DispatchTime?) async throws -> Data? {
        while true {
            stateLock.lock()
            if readBuffer.count >= count {
                let slice = readBuffer.prefix(count)
                readBuffer.removeFirst(count)
                if readBuffer.isEmpty {
                    readBuffer = Data()
                } else if readBuffer.startIndex > 65536 {
                    readBuffer = Data(readBuffer)
                }
                stateLock.unlock()
                return Data(slice)
            }
            if closed {
                stateLock.unlock()
                return nil
            }
            stateLock.unlock()

            let remaining: Double?
            if let deadline {
                let remNs = Int64(deadline.uptimeNanoseconds) - Int64(DispatchTime.now().uptimeNanoseconds)
                if remNs <= 0 {
                    close()
                    throw NSError(domain: "BoBoTls", code: -1003, userInfo: [
                        NSLocalizedDescriptionKey: "等待远端数据超时"
                    ])
                }
                remaining = Double(remNs) / 1_000_000_000.0
            } else {
                remaining = nil
            }

            let chunk = try await receiveChunk(minLength: 1, maxLength: 65536, timeoutSeconds: remaining)
            guard let chunk, !chunk.isEmpty else {
                markClosed()
                return nil
            }
            stateLock.lock()
            readBuffer.append(chunk)
            stateLock.unlock()
        }
    }

    private func receiveChunk(minLength: Int, maxLength: Int, timeoutSeconds: Double?) async throws -> Data? {
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Data?, Error>) in
            let resumed = LockedFlag()
            var timer: DispatchSourceTimer?
            if let timeoutSeconds {
                let t = DispatchSource.makeTimerSource(queue: queue)
                t.schedule(deadline: .now() + timeoutSeconds)
                t.setEventHandler { [weak self] in
                    if resumed.trySet() {
                        self?.close()
                        cont.resume(throwing: NSError(domain: "BoBoTls", code: -1003, userInfo: [
                            NSLocalizedDescriptionKey: "等待远端数据超时"
                        ]))
                    }
                }
                t.resume()
                timer = t
            }

            func attemptReceive() {
                if self.isClosed() {
                    timer?.cancel()
                    if resumed.trySet() { cont.resume(returning: nil) }
                    return
                }
                self.connection.receive(minimumIncompleteLength: minLength, maximumLength: maxLength) { content, _, isComplete, error in
                    if let error {
                        timer?.cancel()
                        if resumed.trySet() { cont.resume(throwing: error) }
                    } else if let content, !content.isEmpty {
                        timer?.cancel()
                        if resumed.trySet() { cont.resume(returning: content) }
                    } else if isComplete {
                        timer?.cancel()
                        if resumed.trySet() { cont.resume(returning: nil) }
                    } else if !resumed.isSet() {
                        // Network.framework TLS 记录/控制帧边界可能返回空 content 且 isComplete == false，必须继续接收而非误判 EOF
                        attemptReceive()
                    }
                }
            }
            attemptReceive()
        }
    }

    private func markClosed() {
        stateLock.lock()
        closed = true
        stateLock.unlock()
    }

    private func isClosed() -> Bool {
        stateLock.lock()
        defer { stateLock.unlock() }
        return closed
    }

    public func close() {
        markClosed()
        connection.cancel()
    }

    // MARK: - TLS Parameters Factories

    public static func makeClientParameters(onServerThumbprint: @escaping @Sendable (String) -> Void) -> NWParameters {
        let tlsOptions = NWProtocolTLS.Options()
        sec_protocol_options_set_min_tls_protocol_version(tlsOptions.securityProtocolOptions, .TLSv12)
        sec_protocol_options_set_tls_server_name(tlsOptions.securityProtocolOptions, BoBoProtocol.tlsSniHost)
        let verifyQueue = DispatchQueue(label: "com.bobo.remote.tls.verify")
        sec_protocol_options_set_verify_block(tlsOptions.securityProtocolOptions, { _, secTrust, complete in
            let trust = sec_trust_copy_ref(secTrust).takeRetainedValue()
            if let chain = SecTrustCopyCertificateChain(trust) as? [SecCertificate],
               let leaf = chain.first {
                let der = SecCertificateCopyData(leaf) as Data
                onServerThumbprint(BoBoIdentityStore.computeCertSha256Hex(der))
                complete(true)
            } else {
                complete(false)
            }
        }, verifyQueue)

        let tcpOptions = NWProtocolTCP.Options()
        tcpOptions.noDelay = true
        tcpOptions.enableKeepalive = true
        tcpOptions.keepaliveIdle = 5
        tcpOptions.keepaliveInterval = 2
        tcpOptions.keepaliveCount = 4
        return NWParameters(tls: tlsOptions, tcp: tcpOptions)
    }

    public static func makeServerParameters(identity: SecIdentity) throws -> NWParameters {
        let tlsOptions = NWProtocolTLS.Options()
        sec_protocol_options_set_min_tls_protocol_version(tlsOptions.securityProtocolOptions, .TLSv12)
        guard let secId = sec_identity_create(identity) else {
            throw NSError(domain: "BoBoTls", code: -2003, userInfo: [
                NSLocalizedDescriptionKey: "无法从本地 SecIdentity 初始化 TLS 服务端身份"
            ])
        }
        sec_protocol_options_set_local_identity(tlsOptions.securityProtocolOptions, secId)
        let tcpOptions = NWProtocolTCP.Options()
        tcpOptions.noDelay = true
        tcpOptions.enableKeepalive = true
        tcpOptions.keepaliveIdle = 5
        tcpOptions.keepaliveInterval = 2
        tcpOptions.keepaliveCount = 4
        return NWParameters(tls: tlsOptions, tcp: tcpOptions)
    }
}

private final class LockedFlag: @unchecked Sendable {
    private let lock = NSLock()
    private var flag = false
    func trySet() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        if flag { return false }
        flag = true
        return true
    }
    func isSet() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return flag
    }
}
