import AppKit
import AVFoundation
import BoBoAppleCore
import CoreMedia
import SwiftUI

private struct MacStreamQuality {
    var width = 2560
    var height = 1440
    var fps = 60
    var bitrateKbps = 18_000
}

@MainActor
public final class MacStreamSessionModel: ObservableObject, BoBoStreamClientDelegate {
    public let hostName: String
    public let displayLayer = AVSampleBufferDisplayLayer()

    private let client: BoBoStreamClient
    private let media = MacStreamMedia()
    private var quality = MacStreamQuality()
    private var requestedQuality: MacStreamQuality?
    private var reconfigureTimeout: Task<Void, Never>?
    private var lastKeyframeRequest: TimeInterval = -.infinity
    private var finished = false

    @Published public private(set) var connected = false
    @Published public private(set) var rttMs: Double = 0
    @Published public private(set) var remotePlatform = BoBoProtocol.platformWindows
    @Published public private(set) var viewOnly = false
    @Published public private(set) var hostCursorComposited = false
    @Published public private(set) var videoSize = CGSize.zero
    @Published public private(set) var privacyActive = false
    @Published public private(set) var superDisplayActive = false
    @Published public private(set) var muted = false
    @Published public private(set) var monitors: [BoBoMonitorDescriptor] = []
    @Published public private(set) var activeMonitorIndex = 0
    @Published public private(set) var receivedFPS = 0
    @Published public private(set) var reconfiguring = false
    @Published public private(set) var reconfigureNotice: String?
    @Published public private(set) var closedReason: String?
    @Published public var scale = MacVideoScale.fit
    @Published public var mapCommandToControl = true

    var inputEnabled: Bool { connected && !viewOnly && !reconfiguring && videoSize != .zero }
    var supportsPrivacy: Bool { connected && !viewOnly && (remotePlatform == BoBoProtocol.platformWindows || remotePlatform == BoBoProtocol.platformMacOS) }
    var supportsSuperDisplay: Bool { connected && !viewOnly && remotePlatform == BoBoProtocol.platformWindows }

    public init(identity: BoBoIdentityStore, host: String, port: Int, hostName: String, autoPrivacy: Bool,
                expectedHostThumbprint: String) {
        self.hostName = hostName
        client = BoBoStreamClient(identity: identity)
        var options = BoBoStreamOptions(host: host, port: port)
        options.width = quality.width
        options.height = quality.height
        options.fps = quality.fps
        options.bitrateKbps = quality.bitrateKbps
        options.requestPrivacy = autoPrivacy
        options.expectedHostThumbprint = expectedHostThumbprint
        if let screen = NSScreen.main {
            options.clientWidth = Int(screen.frame.width * screen.backingScaleFactor)
            options.clientHeight = Int(screen.frame.height * screen.backingScaleFactor)
        }
        client.connectAndStartStream(options: options, delegate: self)
    }

    func sendInput(_ packet: BoBoInputPacket) {
        if connected && (inputEnabled || packet.kind == BoBoProtocol.inputKindReleaseAll) { client.sendInput(packet) }
    }

    func sendMobileNav(_ action: Int16) { sendInput(BoBoProtocol.buildMobileNav(action)) }
    func sendKeyCombo(_ vks: [UInt8]) { if inputEnabled { client.sendKeyCombo(vks) } }

    func toggleMute() {
        muted.toggle()
        media.audioPlayer.muted = muted
    }

    func setPrivacy(_ enable: Bool) {
        if supportsPrivacy { client.sendSetPrivacy(enable) }
    }

    func setSuperDisplay(_ enable: Bool) {
        guard supportsSuperDisplay else { return }
        let screen = NSScreen.main
        let w = Int((screen?.frame.width ?? 1920) * (screen?.backingScaleFactor ?? 1))
        let h = Int((screen?.frame.height ?? 1080) * (screen?.backingScaleFactor ?? 1))
        client.sendSetSuperDisplay(enable: enable, clientWidth: w, clientHeight: h)
    }

    func switchMonitor(_ index: Int) {
        guard index != activeMonitorIndex else { return }
        reconfigure(monitorIndex: index, quality: quality)
    }

    func switchQuality(width: Int, height: Int, fps: Int, bitrateKbps: Int) {
        reconfigure(monitorIndex: activeMonitorIndex,
                    quality: MacStreamQuality(width: width, height: height, fps: fps, bitrateKbps: bitrateKbps))
    }

    private func reconfigure(monitorIndex: Int, quality: MacStreamQuality) {
        guard connected, !reconfiguring else { return }
        client.sendInput(BoBoProtocol.buildReleaseAll())
        requestedQuality = quality
        reconfiguring = true
        reconfigureNotice = nil
        client.sendReconfigure(monitorIndex: monitorIndex, width: quality.width, height: quality.height,
                               fps: quality.fps, bitrateKbps: quality.bitrateKbps)
        reconfigureTimeout = Task { [weak self] in
            do { try await Task.sleep(nanoseconds: 5_000_000_000) } catch { return }
            guard let self else { return }
            self.requestedQuality = nil
            self.reconfiguring = false
            self.reconfigureNotice = "远端未确认显示设置"
        }
    }

    func syncClipboardToRemote() {
        guard inputEnabled, let text = NSPasteboard.general.string(forType: .string), !text.isEmpty else { return }
        client.sendClipboardText(text, injectUnicode: true)
    }

    func lockHost() { if inputEnabled { client.sendLockWorkstation() } }

    func disconnect() { endSession(reason: nil, sendRelease: true) }

    private func endSession(reason: String?, sendRelease: Bool) {
        guard !finished else { return }
        if sendRelease && connected { client.sendInput(BoBoProtocol.buildReleaseAll()) }
        finished = true
        reconfigureTimeout?.cancel()
        client.disconnect()
        media.finish()
        displayLayer.flushAndRemoveImage()
        videoSize = .zero
        connected = false
        closedReason = reason
    }

    private func requestKeyframe() {
        let now = ProcessInfo.processInfo.systemUptime
        guard connected, now - lastKeyframeRequest >= 0.5 else { return }
        lastKeyframeRequest = now
        client.sendRequestKeyframe()
    }

    private func recoverVideo() {
        media.resetVideo()
        displayLayer.flushAndRemoveImage()
        videoSize = .zero
        requestKeyframe()
    }

    nonisolated public func streamClientDidStart(monitors: [BoBoMonitorDescriptor], width: Int, height: Int, fps: Int,
                                                remotePlatform: String, hostControlMode: String) {
        DispatchQueue.main.async { [weak self] in
            guard let self, !self.finished else { return }
            self.monitors = monitors
            self.remotePlatform = remotePlatform
            self.viewOnly = hostControlMode == BoBoProtocol.controlModeViewOnly
            self.mapCommandToControl = remotePlatform != BoBoProtocol.platformMacOS
            self.receivedFPS = fps
            self.connected = true
            self.requestKeyframe()
        }
    }

    nonisolated public func streamClientDidReceiveVideo(_ payload: BoBoVideoPayload, timestampMs: UInt32) {
        guard let frame = media.convert(payload, timestampMs: timestampMs) else { return }
        DispatchQueue.main.async { [weak self] in
            guard let self, !self.finished else { return }
            switch self.media.consume(frame) {
            case .discard:
                return
            case .recover:
                self.recoverVideo()
            case .display:
                guard self.displayLayer.status != .failed, !self.displayLayer.requiresFlushToResumeDecoding,
                      self.displayLayer.isReadyForMoreMediaData else {
                    self.recoverVideo()
                    return
                }
                self.displayLayer.enqueue(frame.sample)
                let dimensions = CMSampleBufferGetFormatDescription(frame.sample).map {
                    CMVideoFormatDescriptionGetPresentationDimensions($0, usePixelAspectRatio: true, useCleanAperture: true)
                } ?? frame.size
                if dimensions.width > 0, dimensions.height > 0, dimensions != self.videoSize {
                    self.videoSize = dimensions
                }
            }
        }
    }

    nonisolated public func streamClientDidReceiveAudio(_ payload: BoBoAudioPayload) { media.play(payload) }

    nonisolated public func streamClientDidUpdateRtt(_ rttMs: Double) {
        DispatchQueue.main.async { [weak self] in self?.rttMs = rttMs }
    }

    nonisolated public func streamClientDidUpdatePrivacy(active: Bool) {
        DispatchQueue.main.async { [weak self] in self?.privacyActive = active }
    }

    nonisolated public func streamClientDidUpdateCursorMode(hostCursorComposited: Bool) {
        DispatchQueue.main.async { [weak self] in self?.hostCursorComposited = hostCursorComposited }
    }

    nonisolated public func streamClientDidUpdateSuperDisplay(active: Bool, nativeMatch: Bool, width: Int, height: Int) {
        DispatchQueue.main.async { [weak self] in self?.superDisplayActive = active }
    }

    nonisolated public func streamClientDidReconfigure(monitorIndex: Int, width: Int, height: Int, fps: Int) {
        DispatchQueue.main.async { [weak self] in
            guard let self, !self.finished else { return }
            self.reconfigureTimeout?.cancel()
            self.activeMonitorIndex = monitorIndex
            self.receivedFPS = fps
            if let requested = self.requestedQuality { self.quality = requested }
            self.requestedQuality = nil
            self.reconfiguring = false
            self.reconfigureNotice = nil
            self.recoverVideo()
        }
    }

    nonisolated public func streamClientDidFail(code: String, message: String) {
        DispatchQueue.main.async { [weak self] in self?.endSession(reason: message, sendRelease: false) }
    }

    nonisolated public func streamClientDidDisconnect(reason: String) {
        DispatchQueue.main.async { [weak self] in self?.endSession(reason: reason, sendRelease: false) }
    }
}

private final class MacStreamMedia: @unchecked Sendable {
    struct Frame: @unchecked Sendable {
        let sample: CMSampleBuffer
        let size: CGSize
        let generation: UInt
    }

    enum Disposition { case discard, recover, display }

    let audioPlayer = BoBoAudioPlayer()
    private let converter = H264AnnexBToSampleBufferConverter()
    private let lock = NSLock()
    private var generation: UInt = 0
    private var pendingFrames = 0
    private var framesDropped = false
    private var finished = false

    func convert(_ payload: BoBoVideoPayload, timestampMs: UInt32) -> Frame? {
        lock.withLock {
            guard !finished, !framesDropped else { return nil }
            guard pendingFrames < 3 else { framesDropped = true; return nil }
            guard let sample = converter.convert(annexB: payload.h264Data, timestampMs: timestampMs,
                                                 isKeyframeHint: payload.isKeyframe) else { return nil }
            pendingFrames += 1
            return Frame(sample: sample, size: CGSize(width: payload.width, height: payload.height), generation: generation)
        }
    }

    func consume(_ frame: Frame) -> Disposition {
        lock.withLock {
            guard !finished, generation == frame.generation else { return .discard }
            pendingFrames -= 1
            return framesDropped ? .recover : .display
        }
    }

    func play(_ payload: BoBoAudioPayload) {
        lock.withLock {
            guard !finished else { return }
            audioPlayer.playAdpcm(sampleRate: payload.sampleRate, channels: payload.channels,
                                  sampleCount: payload.sampleCount, adpcmData: payload.adpcmData)
        }
    }

    func resetVideo() { lock.withLock { resetVideoLocked() } }

    func finish() {
        lock.withLock {
            finished = true
            resetVideoLocked()
        }
        audioPlayer.stop()
    }

    private func resetVideoLocked() {
        generation &+= 1
        pendingFrames = 0
        framesDropped = false
        converter.reset()
    }
}

@MainActor
public struct MacStreamView: View {
    @ObservedObject var model: MacStreamSessionModel
    let onClose: () -> Void
    @State private var showControls = false

    public var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 10) {
                Text("BoBo远程 · \(model.hostName)").font(.system(size: 13, weight: .bold))
                if model.rttMs > 0 {
                    Text("\(Int(model.rttMs.rounded())) ms").font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                }
                if model.viewOnly { Text("仅查看模式").font(.caption).foregroundStyle(.secondary) }
                Spacer()
                Button(action: model.toggleMute) {
                    Image(systemName: model.muted ? "speaker.slash.fill" : "speaker.wave.2.fill")
                }
                .help(model.muted ? "恢复播放声音" : "静音本机播放")
                Button("控制中心") { showControls.toggle() }
                    .popover(isPresented: $showControls, arrowEdge: .bottom) { controlCenter }
                Button("退出远控", action: close)
                    .help("退出远控（⌃⌥⇧Esc）")
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 7)
            .background(Color(NSColor.windowBackgroundColor))

            if model.remotePlatform == BoBoProtocol.platformAndroid && !model.viewOnly {
                HStack(spacing: 8) {
                    Button("返回") { model.sendMobileNav(BoBoProtocol.mobileNavBack) }
                    Button("主页") { model.sendMobileNav(BoBoProtocol.mobileNavHome) }
                    Button("多任务") { model.sendMobileNav(BoBoProtocol.mobileNavRecents) }
                    Button("通知栏") { model.sendMobileNav(BoBoProtocol.mobileNavNotifications) }
                    Button("音量+") { model.sendMobileNav(BoBoProtocol.mobileNavVolumeUp) }
                    Button("音量-") { model.sendMobileNav(BoBoProtocol.mobileNavVolumeDown) }
                    Button("锁屏") { model.sendMobileNav(BoBoProtocol.mobileNavLock) }
                    Spacer()
                }
                .disabled(!model.inputEnabled)
                .padding(.horizontal, 12).padding(.vertical, 5)
            }

            ZStack {
                MacRemoteVideoView(displayLayer: model.displayLayer, videoSize: model.videoSize, scale: model.scale,
                                   inputEnabled: model.inputEnabled, hostCursorComposited: model.hostCursorComposited,
                                   mapCommandToControl: model.mapCommandToControl, onInput: model.sendInput, onClose: close)
                if !model.connected || model.videoSize == .zero {
                    VStack(spacing: 12) {
                        if model.closedReason == nil { ProgressView() }
                        Text(model.closedReason ?? (model.connected ? "正在等待远端画面…" : "正在建立加密连接…"))
                            .font(.headline)
                            .foregroundStyle(.white)
                        if model.closedReason != nil { Button("返回主界面", action: close) }
                    }
                    .padding(24)
                    .background(.black.opacity(0.78), in: RoundedRectangle(cornerRadius: 12))
                }
            }
        }
        .buttonStyle(.bordered)
        .frame(minWidth: 960, minHeight: 600)
        .onDisappear { model.disconnect() }
    }

    private func close() {
        model.disconnect()
        onClose()
    }

    private var controlCenter: some View {
        VStack(alignment: .leading, spacing: 10) {
            Picker("窗口缩放", selection: $model.scale) {
                Text("等比适配").tag(MacVideoScale.fit)
                Text("铺满窗口").tag(MacVideoScale.fill)
                Text("实际大小 1:1").tag(MacVideoScale.actualSize)
            }
            if model.supportsSuperDisplay {
                Toggle("远端超级屏", isOn: Binding(get: { model.superDisplayActive }, set: model.setSuperDisplay))
            }
            if model.supportsPrivacy {
                Toggle("远端防窥屏", isOn: Binding(get: { model.privacyActive }, set: model.setPrivacy))
            }
            if model.connected && !model.viewOnly {
                Toggle("交换 ⌘ 与 Ctrl", isOn: $model.mapCommandToControl)
            }
            if model.monitors.count > 1 {
                Picker("远端显示器", selection: Binding(get: { model.activeMonitorIndex }, set: model.switchMonitor)) {
                    ForEach(model.monitors) { monitor in Text(monitor.name).tag(monitor.index) }
                }
                .disabled(!model.connected || model.reconfiguring)
            }
            if model.connected {
                Divider()
                if model.videoSize != .zero {
                    Text("\(Int(model.videoSize.width)) × \(Int(model.videoSize.height)) · \(model.receivedFPS) fps")
                        .font(.caption).foregroundStyle(.secondary)
                }
                if !model.viewOnly {
                    HStack {
                        Button("720p") { model.switchQuality(width: 1280, height: 720, fps: 30, bitrateKbps: 5000) }
                        Button("1080p") { model.switchQuality(width: 1920, height: 1080, fps: 60, bitrateKbps: 14_000) }
                        Button("1440p") { model.switchQuality(width: 2560, height: 1440, fps: 60, bitrateKbps: 24_000) }
                    }
                    .disabled(model.reconfiguring)
                }
                if let notice = model.reconfigureNotice { Text(notice).font(.caption).foregroundStyle(.secondary) }
            }
            if model.connected && !model.viewOnly {
                Divider()
                HStack {
                    Button("同步剪贴板", action: model.syncClipboardToRemote)
                    if model.remotePlatform == BoBoProtocol.platformWindows || model.remotePlatform == BoBoProtocol.platformMacOS {
                        Button("锁定远端", action: model.lockHost)
                    }
                    if model.remotePlatform == BoBoProtocol.platformWindows {
                        Button("Ctrl+Alt+Del") { model.sendKeyCombo([0x11, 0x12, 0x2E]) }
                    }
                }
                .disabled(!model.inputEnabled)
            }
        }
        .padding(16)
        .frame(width: 320)
    }
}
