import BoBoAppleCore
import Foundation
import Network

/// 本地回环桥接服务（127.0.0.1:49811）：
/// 接收 ReplayKit Broadcast Upload Extension 推入的 H.264 帧并转交给 BoBoHostServer，
/// 无需付费 Apple Developer App Group 签名即可在 AltStore / SideStore / Sideloadly 自签环境中稳定工作！
public final class iOSReplayKitBridge: @unchecked Sendable {
    public static let loopbackPort: UInt16 = 49811

    private let queue = DispatchQueue(label: "com.bobo.remote.ios.replaykit.bridge", qos: .userInteractive)
    private var listener: NWListener?
    private let onVideoFrame: @Sendable (Int, Int, Bool, Data, UInt32) -> Void
    private let onBroadcastState: @Sendable (Bool) -> Void

    public init(
        onVideoFrame: @escaping @Sendable (Int, Int, Bool, Data, UInt32) -> Void,
        onBroadcastState: @escaping @Sendable (Bool) -> Void
    ) {
        self.onVideoFrame = onVideoFrame
        self.onBroadcastState = onBroadcastState
    }

    public func start() {
        guard listener == nil else { return }
        let params = NWParameters.tcp
        params.requiredLocalEndpoint = NWEndpoint.hostPort(host: .ipv4(.loopback), port: NWEndpoint.Port(rawValue: Self.loopbackPort)!)
        guard let l = try? NWListener(using: params) else { return }
        l.newConnectionHandler = { [weak self] nwConn in
            self?.handleExtensionConnection(nwConn)
        }
        l.start(queue: queue)
        listener = l
    }

    public func stop() {
        listener?.cancel()
        listener = nil
        onBroadcastState(false)
    }

    private func handleExtensionConnection(_ nwConn: NWConnection) {
        let framed = BoBoFramedConnection(connection: nwConn, queueLabel: "com.bobo.remote.ios.rk.rx")
        onBroadcastState(true)
        Task.detached(priority: .userInteractive) { [weak self] in
            defer {
                framed.close()
                self?.onBroadcastState(false)
            }
            do {
                try await framed.start(timeoutSeconds: 5.0)
                while let frame = try await framed.readFrame() {
                    if frame.type == .videoH264, let vp = BoBoProtocol.parseVideoPayload(frame.payload) {
                        self?.onVideoFrame(vp.width, vp.height, vp.isKeyframe, vp.h264Data, frame.flagsOrTimestamp)
                    }
                }
            } catch {
                // Extension finished broadcasting
            }
        }
    }
}
