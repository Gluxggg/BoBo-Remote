import BoBoAppleCore
import Foundation
import Network

/// Receives ReplayKit frames while the app is running and grants one frame of credit at a time.
final class iOSReplayKitBridge: @unchecked Sendable {
    struct Dimensions: Equatable, Sendable {
        let width: Int
        let height: Int
    }

    private let queue = DispatchQueue(label: "com.bobo.remote.ios.replaykit.bridge", qos: .userInteractive)
    private var listener: NWListener?
    private var shouldListen = false
    private var listenerRetryScheduled = false
    private var currentConnection: BoBoFramedConnection?
    private var wantsKeyframe = false
    private let dimensionsLock = NSLock()
    private var dimensions: Dimensions?

    private let onVideoFrame: @Sendable (BoBoVideoPayload, UInt32) -> Void
    private let onCaptureChanged: @Sendable (Dimensions?, Bool) -> Void

    init(
        onVideoFrame: @escaping @Sendable (BoBoVideoPayload, UInt32) -> Void,
        onCaptureChanged: @escaping @Sendable (Dimensions?, Bool) -> Void
    ) {
        self.onVideoFrame = onVideoFrame
        self.onCaptureChanged = onCaptureChanged
    }

    var captureDimensions: Dimensions? {
        dimensionsLock.lock()
        defer { dimensionsLock.unlock() }
        return dimensions
    }

    func start() {
        queue.async { [weak self] in
            guard let self else { return }
            self.shouldListen = true
            self.startListener()
        }
    }

    func stop() {
        queue.async { [weak self] in
            guard let self else { return }
            self.shouldListen = false
            let listener = self.listener
            self.listener = nil
            listener?.cancel()
            self.endConnection()
        }
    }

    func requestKeyframe() {
        queue.async { [weak self] in self?.wantsKeyframe = true }
    }

    private func startListener() {
        guard shouldListen, listener == nil else { return }
        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true
        let parameters = NWParameters(tls: nil, tcp: tcp)
        parameters.allowLocalEndpointReuse = true
        parameters.requiredLocalEndpoint = .hostPort(host: .ipv4(.loopback), port: NWEndpoint.Port(rawValue: ReplayKitLoopback.port)!)
        guard let listener = try? NWListener(using: parameters) else {
            scheduleListenerRetry()
            return
        }
        self.listener = listener
        listener.stateUpdateHandler = { [weak self, weak listener] state in
            guard let self, let listener, self.listener === listener else { return }
            switch state {
            case .failed, .cancelled:
                self.listener = nil
                listener.cancel()
                self.endConnection()
                self.scheduleListenerRetry()
            default:
                break
            }
        }
        listener.newConnectionHandler = { [weak self, weak listener] connection in
            guard let self, let listener, self.listener === listener, self.currentConnection == nil else {
                connection.cancel()
                return
            }
            self.receiveFrames(from: connection)
        }
        listener.start(queue: queue)
    }

    private func scheduleListenerRetry() {
        guard shouldListen, !listenerRetryScheduled else { return }
        listenerRetryScheduled = true
        queue.asyncAfter(deadline: .now() + 1) { [weak self] in
            guard let self else { return }
            self.listenerRetryScheduled = false
            self.startListener()
        }
    }

    private func receiveFrames(from connection: NWConnection) {
        let framed = BoBoFramedConnection(connection: connection, queueLabel: "com.bobo.remote.ios.rk.rx")
        currentConnection = framed
        Task.detached(priority: .userInteractive) { [weak self] in
            defer {
                framed.close()
                if let self {
                    self.queue.async {
                        if self.currentConnection === framed { self.endConnection() }
                    }
                }
            }
            do {
                try await framed.start(timeoutSeconds: 3)
                while let frame = try await framed.readFrame(maxPayloadOverride: ReplayKitLoopback.maxVideoPayloadBytes, timeoutSeconds: 5) {
                    guard let self, let acknowledgment = self.queue.sync(execute: { self.consume(frame, from: framed) }) else { break }
                    try await framed.sendRaw(Data([acknowledgment]))
                }
            } catch {
                // Closing this connection releases its capture state and lets ReplayKit reconnect.
            }
        }
    }

    private func consume(_ frame: BoBoFrame, from connection: BoBoFramedConnection) -> UInt8? {
        guard currentConnection === connection, frame.type == .videoH264,
              let video = BoBoProtocol.parseVideoPayload(frame.payload), !video.h264Data.isEmpty,
              (2...ReplayKitLoopback.maxSide).contains(video.width), (2...ReplayKitLoopback.maxSide).contains(video.height) else { return nil }
        let size = Dimensions(width: video.width, height: video.height)
        if captureDimensions != size {
            guard video.isKeyframe else { return ReplayKitLoopback.acknowledge | ReplayKitLoopback.requestKeyframe }
            setCaptureDimensions(size)
        }
        onVideoFrame(video, frame.flagsOrTimestamp)
        let acknowledgment = ReplayKitLoopback.acknowledge | (wantsKeyframe ? ReplayKitLoopback.requestKeyframe : 0)
        wantsKeyframe = false
        return acknowledgment
    }

    private func endConnection() {
        currentConnection?.close()
        currentConnection = nil
        wantsKeyframe = false
        setCaptureDimensions(nil)
    }

    private func setCaptureDimensions(_ size: Dimensions?) {
        dimensionsLock.lock()
        let previous = dimensions
        dimensions = size
        dimensionsLock.unlock()
        if previous != size { onCaptureChanged(size, previous != nil) }
    }
}
