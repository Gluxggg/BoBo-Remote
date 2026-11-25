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
    public var replayKitBridge: iOSReplayKitBridge?

    @Published public var lanPeers: [BoBoDiscoveredPeer] = []
    @Published public var authorizedPeers: [BoBoAuthorizedPeer] = []
    @Published public var localPairSession: BoBoLocalPairSession?
    @Published public var pairCountdown: Int = 0
    @Published public var allowIncoming: Bool = true
    @Published public var broadcastActive: Bool = false
    @Published public var activeViewerName: String?

    private var delegateBridge: iOSHostDelegate?
    private var timer: Timer?

    public init() {
        let docs = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        let storageDir = docs.appendingPathComponent("BoBoRemote/v6", isDirectory: true)
        let dlDir = docs.appendingPathComponent("BoBoDownloads", isDirectory: true)
        let devName = UIDevice.current.name

        self.identity = BoBoIdentityStore(storageDir: storageDir, defaultDeviceName: devName)
        self.identity.ensureLoaded()
        // iOS 遵循 Apple 沙盒铁律：本机投屏协助采用 view_only 模式，主控支持 100% 全功能
        self.hostServer = BoBoHostServer(
            identity: identity,
            downloadDir: dlDir,
            port: UInt16(BoBoProtocol.defaultTcpPort),
            hostControlMode: BoBoProtocol.controlModeViewOnly
        )
        self.discovery = BoBoLanDiscovery(
            identity: identity,
            hostControlMode: BoBoProtocol.controlModeViewOnly,
            tcpPortProvider: { [weak hostServer] in hostServer?.boundPort ?? BoBoProtocol.defaultTcpPort },
            allowIncomingProvider: { [weak hostServer] in hostServer?.allowIncomingControl ?? true },
            onPeersUpdated: nil
        )
        self.authorizedPeers = identity.listAuthorizedPeers()
        setupServices()
    }

    private func setupServices() {
        let bridge = iOSHostDelegate(runtime: self)
        delegateBridge = bridge
        hostServer.delegate = bridge

        let rk = iOSReplayKitBridge(
            onVideoFrame: { [weak hostServer] w, h, isKey, data, tsMs in
                hostServer?.broadcastVideoFrame(width: w, height: h, isKeyframe: isKey, h264: data, timestampMs: tsMs)
            },
            onBroadcastState: { [weak self] active in
                Task { @MainActor in self?.broadcastActive = active }
            }
        )
        replayKitBridge = rk
        rk.start()

        Task {
            _ = try? await hostServer.start()
            discovery.start()
        }

        timer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                self.lanPeers = self.discovery.getPeers()
                self.authorizedPeers = self.identity.listAuthorizedPeers()
                self.localPairSession = self.hostServer.getActivePairing()
                self.pairCountdown = self.localPairSession?.remainingSeconds ?? 0
            }
        }
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
    init(runtime: iOSAppRuntime) { self.runtime = runtime }

    func hostMonitors() -> [BoBoMonitorDescriptor] {
        [BoBoMonitorDescriptor(index: 0, name: "iOS Screen (ReplayKit)", width: 1170, height: 2532, isPrimary: true)]
    }

    func hostDidStartStream(monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int, muteHost: Bool, requestPrivacy: Bool) {}
    func hostDidStopAllStreams() {}

    func hostSessionStateChanged(active: Bool, peerName: String) {
        Task { @MainActor in runtime?.activeViewerName = active ? peerName : nil }
    }
}

/// 包装原生 ReplayKit RPSystemBroadcastPickerView，一键呼出系统录屏投屏面板
public struct ReplayKitPickerView: UIViewRepresentable {
    public func makeUIView(context: Context) -> RPSystemBroadcastPickerView {
        let picker = RPSystemBroadcastPickerView(frame: CGRect(x: 0, y: 0, width: 60, height: 60))
        picker.preferredExtension = "com.bobo.remote.ios.broadcast"
        picker.showsMicrophoneButton = false
        return picker
    }

    public func updateUIView(_ uiView: RPSystemBroadcastPickerView, context: Context) {}
}
