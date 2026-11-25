import AppKit
import AVFoundation
import BoBoAppleCore
import Foundation
import SwiftUI

@MainActor
public final class MacAppRuntime: ObservableObject {
    public let identity: BoBoIdentityStore
    public let hostServer: BoBoHostServer
    public let discovery: BoBoLanDiscovery
    public let captureEngine = MacScreenCaptureEngine()
    public let inputInjector = MacInputInjector()
    public let privacyShield = MacPrivacyShield()

    @Published public var lanPeers: [BoBoDiscoveredPeer] = []
    @Published public var authorizedPeers: [BoBoAuthorizedPeer] = []
    @Published public var localPairSession: BoBoLocalPairSession?
    @Published public var pairCountdown: Int = 0
    @Published public var allowIncoming: Bool = true
    @Published public var hostRunning: Bool = false
    @Published public var activeIncomingPeer: String?
    @Published public var localPrivacyActive: Bool = false
    @Published public var accessibilityGranted: Bool = false
    @Published public var screenRecordingGranted: Bool = false

    private var hostDelegateBridge: HostDelegateBridge?
    private var timer: Timer?

    public init() {
        let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        let storageDir = appSupport.appendingPathComponent("BoBoRemote/v6", isDirectory: true)
        let downloads = (FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory).appendingPathComponent("BoBoRemote", isDirectory: true)
        let hostName = Host.current().localizedName ?? "Mac"

        self.identity = BoBoIdentityStore(storageDir: storageDir, defaultDeviceName: hostName)
        self.identity.ensureLoaded()
        self.hostServer = BoBoHostServer(identity: identity, downloadDir: downloads, port: UInt16(BoBoProtocol.defaultTcpPort))
        self.discovery = BoBoLanDiscovery(
            identity: identity,
            hostControlMode: BoBoProtocol.controlModeFull,
            tcpPortProvider: { [weak hostServer] in hostServer?.boundPort ?? BoBoProtocol.defaultTcpPort },
            allowIncomingProvider: { [weak hostServer] in hostServer?.allowIncomingControl ?? true },
            onPeersUpdated: nil
        )
        self.authorizedPeers = identity.listAuthorizedPeers()
        setupBridges()
        startServices()
    }

    private func setupBridges() {
        let bridge = HostDelegateBridge(runtime: self)
        hostDelegateBridge = bridge
        hostServer.delegate = bridge

        captureEngine.onEncodedVideo = { [weak hostServer] w, h, isKey, data, tsMs in
            hostServer?.broadcastVideoFrame(width: w, height: h, isKeyframe: isKey, h264: data, timestampMs: tsMs)
        }
        captureEngine.onEncodedAudio = { [weak hostServer] sr, ch, sc, adpcm in
            hostServer?.broadcastAudioFrame(sampleRate: sr, channels: ch, sampleCount: sc, adpcm: adpcm)
        }
        inputInjector.onPrivacyEscapePressed = { [weak self] in
            Task { @MainActor in
                self?.setLocalPrivacy(false)
            }
        }
    }

    private func startServices() {
        Task {
            do {
                _ = try await hostServer.start()
                hostRunning = true
            } catch {
                hostRunning = false
            }
            discovery.start()
        }
        refreshPermissions()
        timer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                self.lanPeers = self.discovery.getPeers()
                self.authorizedPeers = self.identity.listAuthorizedPeers()
                self.localPairSession = self.hostServer.getActivePairing()
                self.pairCountdown = self.localPairSession?.remainingSeconds ?? 0
                self.refreshPermissions()
            }
        }
    }

    public func refreshPermissions() {
        accessibilityGranted = MacInputInjector.hasAccessibilityPermission(prompt: false)
        screenRecordingGranted = CGPreflightScreenCaptureAccess()
    }

    public func requestScreenRecordingPermission() {
        CGRequestScreenCaptureAccess()
        refreshPermissions()
    }

    public func requestAccessibilityPermission() {
        _ = MacInputInjector.hasAccessibilityPermission(prompt: true)
        refreshPermissions()
    }

    public func generatePairingPin() {
        localPairSession = hostServer.beginPairing(ttlSeconds: 120)
        pairCountdown = localPairSession?.remainingSeconds ?? 120
    }

    public func setAllowIncoming(_ allow: Bool) {
        allowIncoming = allow
        hostServer.allowIncomingControl = allow
    }

    public func setLocalPrivacy(_ enable: Bool) {
        if enable {
            let excluded = privacyShield.activate(inputInjector: inputInjector)
            localPrivacyActive = true
            Task { await captureEngine.updateExcludedWindows(excluded) }
        } else {
            privacyShield.deactivate(inputInjector: inputInjector)
            localPrivacyActive = false
            Task { await captureEngine.updateExcludedWindows([]) }
        }
    }

    public func shutdown() {
        timer?.invalidate()
        setLocalPrivacy(false)
        discovery.stop()
        hostServer.stop()
        Task { await captureEngine.stop() }
    }
}

private final class HostDelegateBridge: BoBoHostServerDelegate, @unchecked Sendable {
    private weak var runtime: MacAppRuntime?

    init(runtime: MacAppRuntime) { self.runtime = runtime }

    func hostMonitors() -> [BoBoMonitorDescriptor] {
        var displayCount: UInt32 = 0
        CGGetActiveDisplayList(0, nil, &displayCount)
        let count = Int(displayCount)
        guard count > 0 else { return [BoBoMonitorDescriptor()] }
        var displays = [CGDirectDisplayID](repeating: 0, count: count)
        CGGetActiveDisplayList(displayCount, &displays, &displayCount)
        return displays.enumerated().map { idx, did in
            let w = Int(CGDisplayPixelsWide(did))
            let h = Int(CGDisplayPixelsHigh(did))
            return BoBoMonitorDescriptor(
                index: idx,
                name: "Display \(idx + 1) (\(w)x\(h))",
                width: w > 0 ? w : 1920,
                height: h > 0 ? h : 1080,
                isPrimary: CGDisplayIsMain(did) != 0
            )
        }
    }

    func hostDidStartStream(monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int, muteHost: Bool, requestPrivacy: Bool) {
        Task { @MainActor in
            guard let rt = runtime else { return }
            var excluded: Set<CGWindowID> = []
            if requestPrivacy {
                excluded = rt.privacyShield.activate(inputInjector: rt.inputInjector)
                rt.localPrivacyActive = true
            }
            try? await rt.captureEngine.start(
                monitorIndex: monitorIndex,
                width: width,
                height: height,
                fps: fps,
                bitrateKbps: bitrateKbps,
                excludeWindowIDs: excluded
            )
        }
    }

    func hostDidReconfigureStream(monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int) {
        Task { @MainActor in
            guard let rt = runtime else { return }
            try? await rt.captureEngine.start(
                monitorIndex: monitorIndex,
                width: width,
                height: height,
                fps: fps,
                bitrateKbps: bitrateKbps,
                excludeWindowIDs: rt.privacyShield.excludedWindowIDs
            )
        }
    }

    func hostDidStopAllStreams() {
        Task { @MainActor in
            guard let rt = runtime else { return }
            await rt.captureEngine.stop()
            if rt.localPrivacyActive { rt.setLocalPrivacy(false) }
        }
    }

    func hostDidRequestKeyframe() {
        runtime?.captureEngine.requestKeyframe()
    }

    func hostDidReceiveInput(_ packet: BoBoInputPacket) {
        runtime?.inputInjector.inject(packet)
    }

    func hostDidSetPrivacy(enable: Bool) {
        Task { @MainActor in runtime?.setLocalPrivacy(enable) }
    }

    func hostDidRequestLockWorkstation() {
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/bin/pmset")
        task.arguments = ["displaysleepnow"]
        try? task.run()
    }

    func hostDidReceiveClipboard(text: String, injectUnicode: Bool) {
        DispatchQueue.main.async {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(text, forType: .string)
        }
        if injectUnicode {
            runtime?.inputInjector.injectUnicodeText(text)
        }
    }

    func hostSessionStateChanged(active: Bool, peerName: String) {
        Task { @MainActor in
            runtime?.activeIncomingPeer = active ? peerName : nil
        }
    }
}
