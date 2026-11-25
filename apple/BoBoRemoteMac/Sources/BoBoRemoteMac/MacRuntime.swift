import AppKit
import BoBoAppleCore
import CoreAudio
import CoreGraphics
import Foundation
import SwiftUI

@MainActor
public final class MacAppRuntime: NSObject, ObservableObject {
    public let identity: BoBoIdentityStore
    public let hostServer: BoBoHostServer
    public let discovery: BoBoLanDiscovery
    public let captureEngine: MacScreenCaptureEngine
    public let inputInjector = MacInputInjector(doubleClickInterval: NSEvent.doubleClickInterval)
    public let privacyShield = MacPrivacyShield()

    @Published public var lanPeers: [BoBoDiscoveredPeer] = []
    @Published public var authorizedPeers: [BoBoAuthorizedPeer] = []
    @Published public var localPairSession: BoBoLocalPairSession?
    @Published public var pairCountdown: Int = 0
    @Published public var allowIncoming = true
    @Published public var hostRunning = false
    @Published public var activeIncomingPeer: String?
    @Published public private(set) var localPrivacyActive = false
    @Published public var accessibilityGranted = false
    @Published public var screenRecordingGranted = false

    private var hostDelegateBridge: HostDelegateBridge?
    private var timer: Timer?
    private var shuttingDown = false
    private var streamGeneration: UInt = 0
    private var privacyGeneration: UInt = 0
    private var swapControlAndCommand = true
    private var outputMute = MacOutputMute()

    public init(hostName: String = Host.current().localizedName ?? "Mac") {
        let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        let storageDir = appSupport.appendingPathComponent("BoBoRemote/v6", isDirectory: true)
        let downloads = (FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory).appendingPathComponent("BoBoRemote", isDirectory: true)
        let identity = BoBoIdentityStore(storageDir: storageDir, defaultDeviceName: hostName)
        identity.ensureLoaded()
        let hostServer = BoBoHostServer(identity: identity, downloadDir: downloads, port: UInt16(BoBoProtocol.defaultTcpPort))

        self.identity = identity
        self.hostServer = hostServer
        self.discovery = BoBoLanDiscovery(
            identity: identity,
            hostControlMode: BoBoProtocol.controlModeFull,
            tcpPortProvider: { [weak hostServer] in hostServer?.boundPort ?? BoBoProtocol.defaultTcpPort },
            allowIncomingProvider: { [weak hostServer] in hostServer?.allowIncomingControl ?? true },
            onPeersUpdated: nil
        )
        self.captureEngine = MacScreenCaptureEngine(
            onVideo: { [weak hostServer] width, height, keyframe, data, timestamp in
                hostServer?.broadcastVideoFrame(width: width, height: height, isKeyframe: keyframe, h264: data, timestampMs: timestamp)
            },
            onAudio: { [weak hostServer] rate, channels, samples, adpcm in
                hostServer?.broadcastAudioFrame(sampleRate: rate, channels: channels, sampleCount: samples, adpcm: adpcm)
            }
        )
        self.authorizedPeers = identity.listAuthorizedPeers()
        super.init()

        let bridge = HostDelegateBridge(runtime: self)
        hostDelegateBridge = bridge
        hostServer.delegate = bridge
        captureEngine.onCaptureError = { [weak self] _ in self?.disconnectIncoming(reason: "macOS 屏幕采集已停止") }
        inputInjector.onPrivacyEscapePressed = { [weak self] in self?.releasePrivacyLocally() }
        NotificationCenter.default.addObserver(self, selector: #selector(applicationWillTerminate(_:)),
                                               name: NSApplication.willTerminateNotification, object: nil)
        NotificationCenter.default.addObserver(self, selector: #selector(screenConfigurationChanged(_:)),
                                               name: NSApplication.didChangeScreenParametersNotification, object: nil)
        startServices()
    }

    private func startServices() {
        Task {
            do {
                _ = try await hostServer.start()
                guard !shuttingDown else { hostServer.stop(); return }
                hostRunning = true
                discovery.start()
            } catch {
                hostRunning = false
            }
        }
        refreshPermissions()
        timer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self, !self.shuttingDown else { return }
                self.lanPeers = self.discovery.getPeers()
                self.authorizedPeers = self.identity.listAuthorizedPeers()
                self.localPairSession = self.hostServer.getActivePairing()
                self.pairCountdown = self.localPairSession?.remainingSeconds ?? 0
                self.refreshPermissions()
                if self.localPrivacyActive && (!self.accessibilityGranted || !self.screenRecordingGranted) {
                    self.releasePrivacyLocally()
                }
            }
        }
    }

    public func refreshPermissions() {
        accessibilityGranted = MacInputInjector.hasAccessibilityPermission()
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
        if !allow { restoreLocalHostState() }
    }

    public func disconnectIncoming(reason: String = "Mac 主动断开") {
        restoreLocalHostState()
        hostServer.disconnectAllStreams(reason: reason)
    }

    fileprivate func startStream(format: MacCaptureFormat, muteHost: Bool, requestPrivacy: Bool,
                                 clientPlatform: String) async throws -> Bool {
        let ticket = restoreLocalHostState()
        swapControlAndCommand = clientPlatform.lowercased() != BoBoProtocol.platformMacOS
        do {
            try await captureEngine.start(format: format, excludeWindowIDs: [])
            try checkStreamGeneration(ticket)
            inputInjector.configure(displayID: format.displayID, mapControlToCommand: swapControlAndCommand)
            outputMute.setMuted(muteHost)
            let privacyActive = requestPrivacy ? await setPrivacy(true) : false
            try checkStreamGeneration(ticket)
            return privacyActive
        } catch {
            if streamGeneration == ticket { await stopStream() }
            throw error
        }
    }

    fileprivate func reconfigureStream(format: MacCaptureFormat) async throws {
        let ticket = streamGeneration
        try await captureEngine.start(format: format, excludeWindowIDs: privacyShield.excludedWindowIDs)
        try checkStreamGeneration(ticket)
        inputInjector.configure(displayID: format.displayID, mapControlToCommand: swapControlAndCommand)
    }

    fileprivate func stopStream() async {
        restoreLocalHostState()
        await captureEngine.stop()
    }

    fileprivate func setPrivacy(_ enable: Bool) async -> Bool {
        if !enable {
            restorePrivacy()
            try? await captureEngine.updateExcludedWindows([])
            return localPrivacyActive
        }
        guard !localPrivacyActive, !shuttingDown, MacInputInjector.hasAccessibilityPermission() else { return localPrivacyActive }
        privacyGeneration &+= 1
        let ticket = privacyGeneration
        let windows = privacyShield.prepare()
        guard !windows.isEmpty else { restorePrivacy(); return false }
        do {
            try await captureEngine.updateExcludedWindows(windows)
            guard ticket == privacyGeneration else { return localPrivacyActive }
            localPrivacyActive = privacyShield.activate(inputInjector: inputInjector)
        } catch {
            guard ticket == privacyGeneration else { return localPrivacyActive }
        }
        if !localPrivacyActive {
            restorePrivacy()
            try? await captureEngine.updateExcludedWindows([])
        }
        return localPrivacyActive
    }

    fileprivate func setHostMute(_ mute: Bool) {
        guard captureEngine.currentFormat != nil else { return }
        outputMute.setMuted(mute)
    }

    private func releasePrivacyLocally() {
        guard localPrivacyActive else { return }
        restorePrivacy()
        hostServer.notifyPrivacyState(active: false)
        Task { try? await captureEngine.updateExcludedWindows([]) }
    }

    private func restorePrivacy() {
        privacyGeneration &+= 1
        privacyShield.deactivate(inputInjector: inputInjector)
        localPrivacyActive = false
    }

    @discardableResult private func restoreLocalHostState() -> UInt {
        streamGeneration &+= 1
        restorePrivacy()
        inputInjector.releaseAll()
        outputMute.setMuted(false)
        return streamGeneration
    }

    private func checkStreamGeneration(_ ticket: UInt) throws {
        try Task.checkCancellation()
        guard ticket == streamGeneration, !shuttingDown, allowIncoming else { throw CancellationError() }
    }

    @objc private func screenConfigurationChanged(_ notification: Notification) { releasePrivacyLocally() }
    @objc private func applicationWillTerminate(_ notification: Notification) { shutdown() }

    public func shutdown() {
        guard !shuttingDown else { return }
        shuttingDown = true
        timer?.invalidate()
        timer = nil
        restoreLocalHostState()
        discovery.stop()
        hostServer.stop()
        Task { await captureEngine.stop() }
    }
}

private final class HostDelegateBridge: BoBoHostServerDelegate, @unchecked Sendable {
    private weak var runtime: MacAppRuntime?

    init(runtime: MacAppRuntime) { self.runtime = runtime }

    func hostMonitors() -> [BoBoMonitorDescriptor] { MacScreenCaptureEngine.monitors() }
    func hostCompositesCursor() -> Bool { true }

    func hostStreamConfiguration(monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int)
        -> (monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int)? {
        guard CGPreflightScreenCaptureAccess(),
              MacScreenCaptureEngine.configuration(monitorIndex: monitorIndex, width: width, height: height,
                                                   fps: fps, bitrateKbps: bitrateKbps) != nil else { return nil }
        return (monitorIndex, width, height, fps, bitrateKbps)
    }

    func hostStartStream(monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int,
                         muteHost: Bool, requestPrivacy: Bool, clientPlatform: String) async throws
        -> (monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int, privacyActive: Bool) {
        let format = try captureFormat(monitorIndex: monitorIndex, width: width, height: height, fps: fps, bitrateKbps: bitrateKbps)
        guard let runtime else { throw CancellationError() }
        let privacyActive = try await runtime.startStream(format: format, muteHost: muteHost, requestPrivacy: requestPrivacy,
                                                          clientPlatform: clientPlatform)
        return (format.monitorIndex, format.width, format.height, format.fps, format.bitrateKbps, privacyActive)
    }

    func hostReconfigureStream(monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int) async throws
        -> (monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int) {
        let format = try captureFormat(monitorIndex: monitorIndex, width: width, height: height, fps: fps, bitrateKbps: bitrateKbps)
        guard let runtime else { throw CancellationError() }
        try await runtime.reconfigureStream(format: format)
        return (format.monitorIndex, format.width, format.height, format.fps, format.bitrateKbps)
    }

    private func captureFormat(monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int) throws -> MacCaptureFormat {
        guard CGPreflightScreenCaptureAccess() else {
            throw NSError(domain: "MacCapture", code: -1, userInfo: [NSLocalizedDescriptionKey: "请在被控 Mac 上授予屏幕录制权限"])
        }
        guard let format = MacScreenCaptureEngine.configuration(monitorIndex: monitorIndex, width: width, height: height,
                                                                fps: fps, bitrateKbps: bitrateKbps) else {
            throw NSError(domain: "MacCapture", code: -1, userInfo: [NSLocalizedDescriptionKey: "未找到可采集的显示器"])
        }
        return format
    }

    func hostDidStartStream(monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int, muteHost: Bool, requestPrivacy: Bool) {
        Task { _ = try? await hostStartStream(monitorIndex: monitorIndex, width: width, height: height, fps: fps,
                                             bitrateKbps: bitrateKbps, muteHost: muteHost, requestPrivacy: requestPrivacy,
                                             clientPlatform: "") }
    }

    func hostStopStream() async { await runtime?.stopStream() }

    func hostDidStopAllStreams() {
        Task { await hostStopStream() }
    }

    func hostDidRequestKeyframe() { runtime?.captureEngine.requestKeyframe() }
    func hostDidReceiveInput(_ packet: BoBoInputPacket) { runtime?.inputInjector.inject(packet) }

    func hostSetPrivacy(enable: Bool) async -> Bool { await runtime?.setPrivacy(enable) ?? false }

    func hostDidSetMuteHost(mute: Bool) {
        Task { @MainActor in runtime?.setHostMute(mute) }
    }

    func hostDidSetSuperDisplay(enable: Bool, clientWidth: Int, clientHeight: Int)
        -> (active: Bool, nativeMatch: Bool, width: Int, height: Int) {
        let format = runtime?.captureEngine.currentFormat
        return (false, false, format?.width ?? 0, format?.height ?? 0)
    }

    func hostDidRequestLockWorkstation() { runtime?.inputInjector.lockWorkstation() }

    func hostDidReceiveClipboard(text: String, injectUnicode: Bool) {
        DispatchQueue.main.async { [weak runtime = self.runtime] in
            guard let runtime else { return }
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(text, forType: .string)
            if injectUnicode { runtime.inputInjector.injectUnicodeText(text) }
        }
    }

    func hostDidAuthorizePeer(_ peer: BoBoAuthorizedPeer) {
        Task { @MainActor in
            guard let runtime else { return }
            runtime.authorizedPeers = runtime.identity.listAuthorizedPeers()
        }
    }

    func hostSessionStateChanged(active: Bool, peerName: String) {
        Task { @MainActor in runtime?.activeIncomingPeer = active ? peerName : nil }
    }
}

private struct MacOutputMute {
    private var mutedDevice: AudioDeviceID?

    mutating func setMuted(_ mute: Bool) {
        guard mute else {
            if let device = mutedDevice, writeMute(0, device: device) { mutedDevice = nil }
            return
        }
        guard let device = Self.defaultOutputDevice(), mutedDevice == nil else { return }
        var address = Self.muteAddress
        var settable: DarwinBoolean = false
        guard AudioObjectIsPropertySettable(device, &address, &settable) == noErr, settable.boolValue else { return }
        var value: UInt32 = 0
        var size = UInt32(MemoryLayout<UInt32>.size)
        guard AudioObjectGetPropertyData(device, &address, 0, nil, &size, &value) == noErr, value == 0,
              writeMute(1, device: device) else { return }
        mutedDevice = device
    }

    private static var muteAddress: AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyMute, mScope: kAudioDevicePropertyScopeOutput,
                                   mElement: kAudioObjectPropertyElementMain)
    }

    private static func defaultOutputDevice() -> AudioDeviceID? {
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultOutputDevice,
                                                 mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var device: AudioDeviceID = kAudioObjectUnknown
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &device) == noErr,
              device != kAudioObjectUnknown else { return nil }
        return device
    }

    private func writeMute(_ value: UInt32, device: AudioDeviceID) -> Bool {
        var address = Self.muteAddress
        var value = value
        return AudioObjectSetPropertyData(device, &address, 0, nil, UInt32(MemoryLayout<UInt32>.size), &value) == noErr
    }
}
