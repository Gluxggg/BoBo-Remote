import BoBoAppleCore
import Foundation
import ReplayKit
import SwiftUI
import UIKit

@MainActor
public final class iOSAppRuntime: ObservableObject {
    public let identity: BoBoIdentityStore
    public let hostServer: BoBoHostServer
    public let discovery: BoBoLanDiscovery

    @Published public var lanPeers: [BoBoDiscoveredPeer] = []
    @Published public var authorizedPeers: [BoBoAuthorizedPeer] = []
    @Published public var localPairSession: BoBoLocalPairSession?
    @Published public var pairCountdown: Int = 0
    @Published public var allowIncoming: Bool = true
    @Published public var broadcastActive: Bool = false
    @Published public var activeViewerName: String?

    private var delegateBridge: iOSHostDelegate?
    private var replayKitBridge: iOSReplayKitBridge?
    private var timer: Timer?

    public init() {
        let docs = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        let storageDir = docs.appendingPathComponent("BoBoRemote/v6", isDirectory: true)
        let dlDir = docs.appendingPathComponent("BoBoDownloads", isDirectory: true)

        let identity = BoBoIdentityStore(storageDir: storageDir, defaultDeviceName: UIDevice.current.name)
        identity.ensureLoaded()
        let hostServer = BoBoHostServer(
            identity: identity,
            downloadDir: dlDir,
            port: UInt16(BoBoProtocol.defaultTcpPort),
            hostControlMode: BoBoProtocol.controlModeViewOnly
        )
        self.identity = identity
        self.hostServer = hostServer
        discovery = BoBoLanDiscovery(
            identity: identity,
            hostControlMode: BoBoProtocol.controlModeViewOnly,
            tcpPortProvider: { [weak hostServer] in hostServer?.boundPort ?? BoBoProtocol.defaultTcpPort },
            allowIncomingProvider: { [weak hostServer] in hostServer?.allowIncomingControl ?? true },
            onPeersUpdated: nil
        )
        authorizedPeers = identity.listAuthorizedPeers()
        setupServices()
    }

    private func setupServices() {
        let bridge = iOSReplayKitBridge(
            onVideoFrame: { [weak hostServer] frame, timestampMs in
                hostServer?.broadcastVideoFrame(
                    width: frame.width, height: frame.height, isKeyframe: frame.isKeyframe,
                    h264: frame.h264Data, timestampMs: timestampMs
                )
            },
            onCaptureChanged: { [weak self, weak hostServer] dimensions, hadCapture in
                if let dimensions, hadCapture {
                    hostServer?.notifyStreamReconfigured(
                        monitorIndex: 0, width: dimensions.width, height: dimensions.height,
                        fps: ReplayKitLoopback.framesPerSecond, bitrateKbps: ReplayKitLoopback.bitrateKbps
                    )
                } else if dimensions == nil {
                    hostServer?.disconnectStreamingSessions(reason: "iOS 屏幕共享已停止")
                }
                DispatchQueue.main.async { self?.broadcastActive = dimensions != nil }
            }
        )
        replayKitBridge = bridge
        let delegate = iOSHostDelegate(runtime: self, bridge: bridge)
        delegateBridge = delegate
        hostServer.delegate = delegate
        updateScreenMetrics()
        bridge.start()

        Task {
            _ = try? await hostServer.start()
            discovery.start()
        }

        timer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                self.updateScreenMetrics()
                self.lanPeers = self.discovery.getPeers()
                self.authorizedPeers = self.identity.listAuthorizedPeers()
                self.localPairSession = self.hostServer.getActivePairing()
                self.pairCountdown = self.localPairSession?.remainingSeconds ?? 0
            }
        }
    }

    func updateScenePhase(_ phase: ScenePhase) {
        switch phase {
        case .active:
            updateScreenMetrics()
            replayKitBridge?.start()
        case .background:
            replayKitBridge?.stop()
        default:
            break
        }
    }

    private func updateScreenMetrics() {
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first { $0.activationState == .foregroundActive || $0.activationState == .foregroundInactive }
        let nativeBounds = (scene?.screen ?? UIScreen.main).nativeBounds
        let orientation = scene?.interfaceOrientation ?? .portrait
        let shortSide = Int(min(nativeBounds.width, nativeBounds.height))
        let longSide = Int(max(nativeBounds.width, nativeBounds.height))
        let dimensions = iOSReplayKitBridge.Dimensions(
            width: orientation.isLandscape ? longSide : shortSide,
            height: orientation.isLandscape ? shortSide : longSide
        )
        let rotation: Int
        switch orientation {
        case .landscapeLeft: rotation = 1
        case .portraitUpsideDown: rotation = 2
        case .landscapeRight: rotation = 3
        default: rotation = 0
        }
        delegateBridge?.updateScreen(dimensions, rotation: rotation)
    }

    public func generatePairingPin() {
        localPairSession = hostServer.beginPairing(ttlSeconds: 120)
        pairCountdown = localPairSession?.remainingSeconds ?? 120
    }

    public func setAllowIncoming(_ allow: Bool) {
        allowIncoming = allow
        hostServer.allowIncomingControl = allow
    }
}

private final class iOSHostDelegate: BoBoHostServerDelegate, @unchecked Sendable {
    private weak var runtime: iOSAppRuntime?
    private let bridge: iOSReplayKitBridge
    private let screenLock = NSLock()
    private var screen = iOSReplayKitBridge.Dimensions(width: 0, height: 0)
    private var rotation = 0

    init(runtime: iOSAppRuntime, bridge: iOSReplayKitBridge) {
        self.runtime = runtime
        self.bridge = bridge
    }

    func updateScreen(_ dimensions: iOSReplayKitBridge.Dimensions, rotation: Int) {
        screenLock.lock()
        screen = dimensions
        self.rotation = rotation
        screenLock.unlock()
    }

    func hostMonitors() -> [BoBoMonitorDescriptor] {
        screenLock.lock()
        let screen = self.screen
        screenLock.unlock()
        let size = bridge.captureDimensions ?? screen
        return [BoBoMonitorDescriptor(index: 0, name: "iOS Screen (ReplayKit)", width: size.width, height: size.height, isPrimary: true)]
    }

    func hostScreenOrientation() -> Int {
        screenLock.lock()
        let rotation = self.rotation
        screenLock.unlock()
        guard let capture = bridge.captureDimensions else { return rotation }
        if capture.width > capture.height { return rotation == 3 ? 3 : 1 }
        return rotation == 2 ? 2 : 0
    }

    func hostStreamConfiguration(monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int)
        -> (monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int)? {
        guard let capture = bridge.captureDimensions else { return nil }
        return (0, capture.width, capture.height, ReplayKitLoopback.framesPerSecond, ReplayKitLoopback.bitrateKbps)
    }

    func hostCompositesCursor() -> Bool { false }
    func hostDidStartStream(monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int, muteHost: Bool, requestPrivacy: Bool) {}
    func hostDidStopAllStreams() {}
    func hostDidRequestKeyframe() { bridge.requestKeyframe() }

    func hostSessionStateChanged(active: Bool, peerName: String) {
        DispatchQueue.main.async { [weak runtime] in runtime?.activeViewerName = active ? peerName : nil }
    }

    func hostDidAuthorizePeer(_ peer: BoBoAuthorizedPeer) {
        DispatchQueue.main.async { [weak runtime] in
            guard let runtime else { return }
            runtime.authorizedPeers = runtime.identity.listAuthorizedPeers()
        }
    }
}

/// 包装原生 ReplayKit RPSystemBroadcastPickerView，一键呼出系统录屏投屏面板
public struct ReplayKitPickerView: UIViewRepresentable {
    public func makeUIView(context: Context) -> RPSystemBroadcastPickerView {
        let picker = RPSystemBroadcastPickerView(frame: CGRect(x: 0, y: 0, width: 60, height: 60))
        picker.preferredExtension = Bundle.main.bundleIdentifier.map { $0 + ".broadcast" }
        picker.showsMicrophoneButton = false
        return picker
    }

    public func updateUIView(_ uiView: RPSystemBroadcastPickerView, context: Context) {}
}
