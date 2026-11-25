import Darwin
import Foundation

/// 局域网 UDP 49812 自动发现服务（基于 BSD Socket，macOS 与 iOS 通用）。
public final class BoBoLanDiscovery: @unchecked Sendable {
    private let identity: BoBoIdentityStore
    private let tcpPortProvider: @Sendable () -> Int
    private let allowIncomingProvider: @Sendable () -> Bool
    private let hostControlMode: String
    private let onPeersUpdated: (@Sendable ([BoBoDiscoveredPeer]) -> Void)?

    private let lock = NSLock()
    private var peers: [String: BoBoDiscoveredPeer] = [:]
    private var sockFd: Int32 = -1
    private var running = false
    private let rxQueue = DispatchQueue(label: "com.bobo.remote.udp.rx", qos: .utility)
    private let txQueue = DispatchQueue(label: "com.bobo.remote.udp.tx", qos: .utility)

    public init(
        identity: BoBoIdentityStore,
        hostControlMode: String = BoBoProtocol.controlModeFull,
        tcpPortProvider: @escaping @Sendable () -> Int = { BoBoProtocol.defaultTcpPort },
        allowIncomingProvider: @escaping @Sendable () -> Bool = { true },
        onPeersUpdated: (@Sendable ([BoBoDiscoveredPeer]) -> Void)? = nil
    ) {
        self.identity = identity
        self.hostControlMode = hostControlMode
        self.tcpPortProvider = tcpPortProvider
        self.allowIncomingProvider = allowIncomingProvider
        self.onPeersUpdated = onPeersUpdated
    }

    public func start() {
        lock.lock()
        if running { lock.unlock(); return }
        identity.ensureLoaded()

        let fd = Darwin.socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        guard fd >= 0 else { lock.unlock(); return }

        var yes: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &yes, socklen_t(MemoryLayout<Int32>.size))
        setsockopt(fd, SOL_SOCKET, SO_REUSEPORT, &yes, socklen_t(MemoryLayout<Int32>.size))
        setsockopt(fd, SOL_SOCKET, SO_BROADCAST, &yes, socklen_t(MemoryLayout<Int32>.size))

        var tv = timeval(tv_sec: 1, tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))

        var addr = sockaddr_in()
        addr.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = UInt16(BoBoProtocol.discoveryUdpPort).bigEndian
        addr.sin_addr = in_addr(s_addr: INADDR_ANY.bigEndian)

        let bindRes = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        if bindRes != 0 {
            Darwin.close(fd)
            lock.unlock()
            return
        }

        sockFd = fd
        running = true
        lock.unlock()

        rxQueue.async { [weak self] in self?.receiveLoop(fd: fd) }
        txQueue.async { [weak self] in self?.beaconLoop() }
    }

    public func stop() {
        lock.lock()
        if !running { lock.unlock(); return }
        running = false
        let fd = sockFd
        sockFd = -1
        lock.unlock()

        if fd >= 0 {
            sendPacket(fd: fd, kind: "BOBO6_BYE", ip: "255.255.255.255", port: BoBoProtocol.discoveryUdpPort)
            Darwin.close(fd)
        }
    }

    public func sendProbeNow() {
        lock.lock()
        let fd = sockFd
        let isRun = running
        lock.unlock()
        guard isRun, fd >= 0 else { return }
        txQueue.async { [weak self] in
            self?.sendPacket(fd: fd, kind: "BOBO6_PROBE", ip: "255.255.255.255", port: BoBoProtocol.discoveryUdpPort)
        }
    }

    public func getPeers() -> [BoBoDiscoveredPeer] {
        let cutoff = Date().addingTimeInterval(-18)
        lock.lock()
        peers = peers.filter { $0.value.lastSeen >= cutoff }
        let list = Array(peers.values).sorted { $0.deviceName.localizedCaseInsensitiveCompare($1.deviceName) == .orderedAscending }
        lock.unlock()
        return list
    }

    public func resolveHostByDeviceCode(_ input: String) -> String {
        let norm = BoBoIdentityStore.normalizeDeviceCode(input)
        if norm.count == 9 {
            if let live = getPeers().first(where: { BoBoIdentityStore.normalizeDeviceCode($0.deviceCode) == norm }) {
                return live.host
            }
            if let saved = identity.findPeerByCodeOrHost(norm), !saved.lastHost.isEmpty {
                return saved.lastHost
            }
        }
        return input.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private func beaconLoop() {
        lock.lock()
        let fd = sockFd
        lock.unlock()
        guard fd >= 0 else { return }
        sendPacket(fd: fd, kind: "BOBO6_PROBE", ip: "255.255.255.255", port: BoBoProtocol.discoveryUdpPort)
        while isRunning() {
            lock.lock()
            let curFd = sockFd
            lock.unlock()
            if curFd >= 0 {
                sendPacket(fd: curFd, kind: "BOBO6_HELLO", ip: "255.255.255.255", port: BoBoProtocol.discoveryUdpPort)
            }
            Thread.sleep(forTimeInterval: 3.5)
        }
    }

    private func receiveLoop(fd: Int32) {
        var buf = [UInt8](repeating: 0, count: 4096)
        while isRunning() {
            var srcAddr = sockaddr_in()
            var addrLen = socklen_t(MemoryLayout<sockaddr_in>.size)
            let n = withUnsafeMutablePointer(to: &srcAddr) {
                $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    Darwin.recvfrom(fd, &buf, buf.count, 0, $0, &addrLen)
                }
            }
            if n <= 0 { continue }
            let data = Data(bytes: buf, count: n)
            guard let map = BoBoProtocol.parseJson(data),
                  let kind = map["kind"] as? String,
                  let peerNodeId = map["nodeId"] as? String,
                  !peerNodeId.isEmpty,
                  peerNodeId.caseInsensitiveCompare(identity.nodeId) != .orderedSame else {
                continue
            }

            var ipBuf = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
            var sinAddr = srcAddr.sin_addr
            guard inet_ntop(AF_INET, &sinAddr, &ipBuf, socklen_t(INET_ADDRSTRLEN)) != nil else { continue }
            let senderIp = String(cString: ipBuf)

            if kind == "BOBO6_BYE" {
                lock.lock()
                peers.removeValue(forKey: peerNodeId)
                lock.unlock()
                onPeersUpdated?(getPeers())
                continue
            }

            if kind == "BOBO6_HELLO" || kind == "BOBO6_PROBE" {
                let peer = BoBoDiscoveredPeer(
                    nodeId: peerNodeId,
                    deviceCode: (map["deviceCode"] as? String) ?? "",
                    deviceName: (map["deviceName"] as? String) ?? senderIp,
                    host: senderIp,
                    port: (map["port"] as? NSNumber)?.intValue ?? BoBoProtocol.defaultTcpPort,
                    version: (map["version"] as? String) ?? BoBoProtocol.appVersion,
                    platform: (map["platform"] as? String) ?? BoBoProtocol.platformWindows,
                    hostControlMode: (map["hostControlMode"] as? String) ?? BoBoProtocol.controlModeFull,
                    allowIncoming: (map["allowIncoming"] as? NSNumber)?.boolValue ?? true,
                    lastSeen: Date()
                )
                lock.lock()
                peers[peerNodeId] = peer
                lock.unlock()
                if kind == "BOBO6_PROBE" {
                    sendPacket(fd: fd, kind: "BOBO6_HELLO", ip: senderIp, port: BoBoProtocol.discoveryUdpPort)
                }
                onPeersUpdated?(getPeers())
            }
        }
    }

    private func sendPacket(fd: Int32, kind: String, ip: String, port: Int) {
        let obj: [String: Any] = [
            "kind": kind,
            "protocol": BoBoProtocol.protocolMagic,
            "protocolVersion": BoBoProtocol.protocolVersion,
            "platform": BoBoProtocol.currentPlatform,
            "hostControlMode": hostControlMode,
            "nodeId": identity.nodeId,
            "deviceCode": identity.deviceCode,
            "deviceName": identity.deviceName,
            "port": tcpPortProvider(),
            "version": BoBoProtocol.appVersion,
            "allowIncoming": allowIncomingProvider()
        ]
        guard let payload = try? JSONSerialization.data(withJSONObject: obj) else { return }
        var dst = sockaddr_in()
        dst.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        dst.sin_family = sa_family_t(AF_INET)
        dst.sin_port = UInt16(port).bigEndian
        inet_pton(AF_INET, ip, &dst.sin_addr)

        payload.withUnsafeBytes { rawBuf in
            guard let base = rawBuf.baseAddress else { return }
            withUnsafePointer(to: &dst) {
                $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    _ = Darwin.sendto(fd, base, payload.count, 0, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
        }
    }

    private func isRunning() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return running
    }
}
