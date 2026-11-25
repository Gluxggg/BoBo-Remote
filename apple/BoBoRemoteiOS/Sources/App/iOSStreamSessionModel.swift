import AVFoundation
import BoBoAppleCore
import SwiftUI
import UIKit

@MainActor
public final class iOSStreamSessionModel: ObservableObject, BoBoStreamClientDelegate {
    private struct Quality {
        let width: Int
        let height: Int
        let fps: Int
        let bitrateKbps: Int
    }

    public let host: String
    public let port: Int
    public let hostName: String
    public let displayLayer = AVSampleBufferDisplayLayer()

    private let client: BoBoStreamClient
    private let requestPrivacy: Bool
    private let expectedHostThumbprint: String
    private let converter = H264AnnexBToSampleBufferConverter()
    private let videoDelivery = VideoDelivery()
    private let audio = StreamAudio()
    private var started = false
    private var finished = false
    private var suspended = false
    private var clientSize = CGSize.zero
    private var lastPayloadSize: CGSize?
    private var lastKeyframeRequest: UInt64?
    private var quality = Quality(width: 1920, height: 1080, fps: 60, bitrateKbps: 12000)
    private var pendingQuality: Quality?
    private var reconfigureTimeout: Task<Void, Never>?

    @Published public private(set) var connected = false
    @Published public private(set) var videoSize = CGSize.zero
    @Published public private(set) var rttMs: Double = 0
    @Published public private(set) var remotePlatform = BoBoProtocol.platformWindows
    @Published public private(set) var viewOnly = false
    @Published public var touchpadMode = true
    @Published public private(set) var superDisplay = false
    @Published public private(set) var privacyActive = false
    @Published public private(set) var muted = false
    @Published public private(set) var closedReason: String?
    @Published public private(set) var monitors: [BoBoMonitorDescriptor] = []
    @Published public private(set) var monitorIndex = 0
    @Published public private(set) var negotiatedFps = 0
    @Published public private(set) var configurationPending = false
    @Published public private(set) var configurationNotice: String?

    public init(identity: BoBoIdentityStore, host: String, port: Int, hostName: String, autoPrivacy: Bool, expectedHostThumbprint: String) {
        self.host = host
        self.port = port
        self.hostName = hostName
        self.client = BoBoStreamClient(identity: identity)
        self.requestPrivacy = autoPrivacy
        self.expectedHostThumbprint = expectedHostThumbprint
    }

    func start(viewSize: CGSize, scale: CGFloat) {
        updateViewport(size: viewSize, scale: scale)
        guard !started, !finished else { return }
        started = true

        var options = BoBoStreamOptions(host: host, port: port)
        options.expectedHostThumbprint = expectedHostThumbprint
        options.width = quality.width
        options.height = quality.height
        options.fps = quality.fps
        options.bitrateKbps = quality.bitrateKbps
        options.requestPrivacy = requestPrivacy
        options.superDisplay = true
        options.clientWidth = Int(clientSize.width)
        options.clientHeight = Int(clientSize.height)
        client.connectAndStartStream(options: options, delegate: self)
    }

    func updateViewport(size: CGSize, scale: CGFloat) {
        clientSize = CGSize(width: (size.width * scale).rounded(), height: (size.height * scale).rounded())
    }

    func updateScenePhase(_ phase: ScenePhase) {
        guard !finished else { return }
        if phase == .background, !suspended {
            suspended = true
            if connected, !viewOnly { client.sendInput(BoBoProtocol.buildReleaseAll()) }
            audio.setActive(false)
            resetRendering(removeImage: true)
        } else if phase == .active, suspended {
            suspended = false
            audio.setActive(connected)
            requestKeyframe(force: true)
        }
    }

    public func sendInput(_ packet: BoBoInputPacket) {
        guard connected, !viewOnly, !suspended, !configurationPending else { return }
        client.sendInput(packet)
    }

    public func sendKeyCombo(_ keys: [UInt8]) {
        guard connected, !viewOnly else { return }
        client.sendKeyCombo(keys)
    }

    public func sendText(_ text: String) {
        guard connected, !viewOnly else { return }
        client.sendClipboardText(text, injectUnicode: true)
    }

    public func sendMobileNav(_ action: Int16) { sendInput(BoBoProtocol.buildMobileNav(action)) }

    public func toggleMute() {
        muted.toggle()
        audio.player.muted = muted
    }

    public func togglePrivacy() {
        guard connected, !viewOnly else { return }
        client.sendSetPrivacy(!privacyActive)
    }

    public func toggleSuperDisplay() {
        guard connected, !viewOnly else { return }
        client.sendSetSuperDisplay(enable: !superDisplay, clientWidth: Int(clientSize.width), clientHeight: Int(clientSize.height))
    }

    public func switchQuality(width: Int, height: Int, fps: Int, bitrateKbps: Int) {
        reconfigure(monitorIndex: monitorIndex, quality: Quality(width: width, height: height, fps: fps, bitrateKbps: bitrateKbps))
    }

    func selectMonitor(_ index: Int) {
        guard index != monitorIndex, monitors.contains(where: { $0.index == index }) else { return }
        reconfigure(monitorIndex: index, quality: quality)
    }

    private func reconfigure(monitorIndex: Int, quality: Quality) {
        guard connected, remotePlatform != BoBoProtocol.platformIOS, !configurationPending else { return }
        configurationPending = true
        configurationNotice = nil
        pendingQuality = quality
        if !viewOnly { client.sendInput(BoBoProtocol.buildReleaseAll()) }
        client.sendReconfigure(
            monitorIndex: monitorIndex, width: quality.width, height: quality.height,
            fps: quality.fps, bitrateKbps: quality.bitrateKbps
        )
        reconfigureTimeout = Task { [weak self] in
            do { try await Task.sleep(nanoseconds: 5_000_000_000) } catch { return }
            guard let self else { return }
            self.pendingQuality = nil
            self.configurationPending = false
            self.configurationNotice = "远端未确认画面切换"
        }
    }

    public func syncClipboardToRemote() {
        if let text = UIPasteboard.general.string { sendText(text) }
    }

    public func lockHost() {
        guard connected, !viewOnly else { return }
        client.sendLockWorkstation()
    }

    public func disconnect() { finish(reason: nil) }

    private func finish(reason: String?) {
        guard !finished else { return }
        finished = true
        closedReason = reason
        connected = false
        reconfigureTimeout?.cancel()
        reconfigureTimeout = nil
        configurationPending = false
        pendingQuality = nil
        client.disconnect()
        audio.setActive(false)
        resetRendering(removeImage: true)
    }

    private func resetRendering(removeImage: Bool) {
        converter.reset()
        if removeImage {
            displayLayer.flushAndRemoveImage()
            videoSize = .zero
            lastPayloadSize = nil
        } else {
            displayLayer.flush()
        }
    }

    private func requestKeyframe(force: Bool = false) {
        guard connected, !suspended else { return }
        let now = DispatchTime.now().uptimeNanoseconds
        if !force, let lastKeyframeRequest, now - lastKeyframeRequest < 500_000_000 { return }
        lastKeyframeRequest = now
        client.sendRequestKeyframe()
    }

    private func displayVideo(_ payload: BoBoVideoPayload, timestampMs: UInt32, followsDrop: Bool) {
        guard connected, !finished, !suspended else { return }
        let size = CGSize(width: CGFloat(payload.width), height: CGFloat(payload.height))
        let sizeChanged = lastPayloadSize.map { $0 != size } ?? false
        if sizeChanged || displayLayer.status == .failed || displayLayer.requiresFlushToResumeDecoding {
            resetRendering(removeImage: true)
        } else if followsDrop {
            resetRendering(removeImage: false)
        }
        lastPayloadSize = size
        guard displayLayer.isReadyForMoreMediaData else {
            resetRendering(removeImage: false)
            requestKeyframe()
            return
        }
        guard let sample = converter.convert(annexB: payload.h264Data, timestampMs: timestampMs, isKeyframeHint: payload.isKeyframe) else {
            requestKeyframe()
            return
        }
        displayLayer.enqueue(sample)
        let presentationSize = CMSampleBufferGetFormatDescription(sample).map {
            CMVideoFormatDescriptionGetPresentationDimensions($0, usePixelAspectRatio: true, useCleanAperture: true)
        } ?? size
        if videoSize != presentationSize { videoSize = presentationSize }
    }

    // Main-queue delivery preserves control and video order from the client's read loop.
    nonisolated public func streamClientDidStart(
        monitors: [BoBoMonitorDescriptor], width: Int, height: Int, fps: Int, remotePlatform: String, hostControlMode: String
    ) {
        DispatchQueue.main.async { [weak self] in
            guard let self, !self.finished else { return }
            self.remotePlatform = remotePlatform
            self.viewOnly = hostControlMode == BoBoProtocol.controlModeViewOnly
            self.touchpadMode = remotePlatform == BoBoProtocol.platformWindows || remotePlatform == BoBoProtocol.platformMacOS
            self.monitors = monitors
            self.monitorIndex = monitors.first(where: { $0.index == 0 })?.index
                ?? monitors.first(where: { $0.isPrimary })?.index ?? monitors.first?.index ?? 0
            self.negotiatedFps = fps
            self.connected = true
            self.audio.setActive(!self.suspended)
            self.requestKeyframe()
        }
    }

    nonisolated public func streamClientDidReceiveVideo(_ payload: BoBoVideoPayload, timestampMs: UInt32) {
        guard let followsDrop = videoDelivery.takeSlot() else { return }
        DispatchQueue.main.async { [weak self, videoDelivery] in
            defer { videoDelivery.releaseSlot() }
            self?.displayVideo(payload, timestampMs: timestampMs, followsDrop: followsDrop)
        }
    }

    nonisolated public func streamClientDidReceiveAudio(_ payload: BoBoAudioPayload) {
        audio.play(payload)
    }

    nonisolated public func streamClientDidUpdateRtt(_ rttMs: Double) {
        DispatchQueue.main.async { [weak self] in
            guard let self, !self.finished else { return }
            self.rttMs = rttMs
        }
    }

    nonisolated public func streamClientDidUpdatePrivacy(active: Bool) {
        DispatchQueue.main.async { [weak self] in
            guard let self, !self.finished else { return }
            self.privacyActive = active
        }
    }

    nonisolated public func streamClientDidUpdateSuperDisplay(active: Bool, nativeMatch: Bool, width: Int, height: Int) {
        DispatchQueue.main.async { [weak self] in
            guard let self, !self.finished, self.superDisplay != active else { return }
            self.superDisplay = active
            self.resetRendering(removeImage: true)
            self.requestKeyframe()
        }
    }

    nonisolated public func streamClientDidReconfigure(monitorIndex: Int, width: Int, height: Int, fps: Int) {
        DispatchQueue.main.async { [weak self] in
            guard let self, !self.finished else { return }
            self.reconfigureTimeout?.cancel()
            self.reconfigureTimeout = nil
            if let pendingQuality = self.pendingQuality { self.quality = pendingQuality }
            self.pendingQuality = nil
            self.configurationPending = false
            self.configurationNotice = nil
            self.monitorIndex = monitorIndex
            self.negotiatedFps = fps
            self.resetRendering(removeImage: true)
            self.requestKeyframe()
        }
    }

    nonisolated public func streamClientDidFail(code: String, message: String) {
        DispatchQueue.main.async { [weak self] in self?.finish(reason: message) }
    }

    nonisolated public func streamClientDidDisconnect(reason: String) {
        DispatchQueue.main.async { [weak self] in self?.finish(reason: reason) }
    }
}

private final class VideoDelivery: @unchecked Sendable {
    private let lock = NSLock()
    private var inFlight = 0
    private var dropped = false

    func takeSlot() -> Bool? {
        lock.lock()
        defer { lock.unlock() }
        guard inFlight < 2 else {
            dropped = true
            return nil
        }
        inFlight += 1
        let followsDrop = dropped
        dropped = false
        return followsDrop
    }

    func releaseSlot() {
        lock.lock()
        inFlight -= 1
        lock.unlock()
    }
}

private final class StreamAudio: @unchecked Sendable {
    let player = BoBoAudioPlayer()
    private let lock = NSLock()
    private var active = false

    func setActive(_ active: Bool) {
        lock.lock()
        defer { lock.unlock() }
        guard self.active != active else { return }
        self.active = active
        if !active { player.stop() }
    }

    func play(_ payload: BoBoAudioPayload) {
        lock.lock()
        defer { lock.unlock() }
        guard active else { return }
        player.playAdpcm(sampleRate: payload.sampleRate, channels: payload.channels, sampleCount: payload.sampleCount, adpcmData: payload.adpcmData)
    }
}
