import AppKit
import AVFoundation
import BoBoAppleCore
import SwiftUI

@MainActor
public final class MacStreamSessionModel: ObservableObject, BoBoStreamClientDelegate {
    public let host: String
    public let port: Int
    public let hostName: String
    public let displayLayer = AVSampleBufferDisplayLayer()

    private let client: BoBoStreamClient
    private let converter = H264AnnexBToSampleBufferConverter()
    private let audioPlayer = BoBoAudioPlayer()

    @Published public var connected = false
    @Published public var statusText = "正在建立 BoBo V6.2 加密连接..."
    @Published public var rttMs: Double = 0
    @Published public var remotePlatform = BoBoProtocol.platformWindows
    @Published public var viewOnly = false
    @Published public var scaleMode = 2 // 2=超级屏全屏自适应无黑边
    @Published public var superDisplay = true
    @Published public var privacyActive = false
    @Published public var muted = false
    @Published public var mapCommandToControl = true
    @Published public var monitors: [BoBoMonitorDescriptor] = []
    @Published public var activeMonitorIndex = 0
    @Published public var closedReason: String?

    public init(identity: BoBoIdentityStore, host: String, port: Int, hostName: String, autoPrivacy: Bool) {
        self.host = host
        self.port = port
        self.hostName = hostName
        self.client = BoBoStreamClient(identity: identity)

        var opts = BoBoStreamOptions(host: host, port: port)
        opts.width = 2560
        opts.height = 1440
        opts.fps = 60
        opts.bitrateKbps = 18000
        opts.requestPrivacy = autoPrivacy
        opts.superDisplay = true
        if let scr = NSScreen.main {
            opts.clientWidth = Int(scr.frame.width * scr.backingScaleFactor)
            opts.clientHeight = Int(scr.frame.height * scr.backingScaleFactor)
        }
        client.connectAndStartStream(options: opts, delegate: self)
    }

    public func sendInput(_ pkt: BoBoInputPacket) { client.sendInput(pkt) }
    public func sendMobileNav(_ action: Int16) { client.sendInput(BoBoProtocol.buildMobileNav(action)) }
    public func sendKeyCombo(_ vks: [UInt8]) { client.sendKeyCombo(vks) }

    public func toggleMute() {
        muted.toggle()
        audioPlayer.muted = muted
    }

    public func togglePrivacy() {
        privacyActive.toggle()
        client.sendSetPrivacy(privacyActive)
    }

    public func toggleSuperDisplay() {
        superDisplay.toggle()
        scaleMode = superDisplay ? 2 : 1
        let scr = NSScreen.main
        let w = Int((scr?.frame.width ?? 1920) * (scr?.backingScaleFactor ?? 1))
        let h = Int((scr?.frame.height ?? 1080) * (scr?.backingScaleFactor ?? 1))
        client.sendSetSuperDisplay(enable: superDisplay, clientWidth: w, clientHeight: h)
    }

    public func switchMonitor(_ idx: Int) {
        activeMonitorIndex = idx
        client.sendReconfigure(monitorIndex: idx, width: 2560, height: 1440, fps: 60, bitrateKbps: 18000)
    }

    public func switchQuality(width: Int, height: Int, fps: Int, bitrateKbps: Int) {
        client.sendReconfigure(monitorIndex: activeMonitorIndex, width: width, height: height, fps: fps, bitrateKbps: bitrateKbps)
    }

    public func syncClipboardToRemote() {
        if let txt = NSPasteboard.general.string(forType: .string), !txt.isEmpty {
            client.sendClipboardText(txt, injectUnicode: true)
        }
    }

    public func lockHost() { client.sendLockWorkstation() }

    public func disconnect() {
        client.disconnect()
        audioPlayer.stop()
        converter.reset()
        connected = false
    }

    // MARK: - BoBoStreamClientDelegate

    nonisolated public func streamClientDidStart(
        monitors: [BoBoMonitorDescriptor],
        width: Int,
        height: Int,
        fps: Int,
        remotePlatform: String,
        hostControlMode: String
    ) {
        Task { @MainActor in
            self.monitors = monitors
            self.remotePlatform = remotePlatform
            self.viewOnly = (hostControlMode == BoBoProtocol.controlModeViewOnly)
            if remotePlatform == BoBoProtocol.platformAndroid || remotePlatform == BoBoProtocol.platformIOS || height > width {
                self.superDisplay = false
                self.scaleMode = 1
            }
            self.connected = true
            self.statusText = ""
        }
    }

    nonisolated public func streamClientDidReceiveVideo(_ payload: BoBoVideoPayload, timestampMs: UInt32) {
        guard let sb = converter.convert(annexB: payload.h264Data, timestampMs: timestampMs, isKeyframeHint: payload.isKeyframe) else {
            return
        }
        DispatchQueue.main.async { [weak self] in
            guard let layer = self?.displayLayer else { return }
            if layer.status == .failed { layer.flush() }
            layer.enqueue(sb)
        }
    }

    nonisolated public func streamClientDidReceiveAudio(_ payload: BoBoAudioPayload) {
        audioPlayer.playAdpcm(sampleRate: payload.sampleRate, channels: payload.channels, sampleCount: payload.sampleCount, adpcmData: payload.adpcmData)
    }

    nonisolated public func streamClientDidUpdateRtt(_ rttMs: Double) {
        Task { @MainActor in self.rttMs = rttMs }
    }

    nonisolated public func streamClientDidUpdatePrivacy(active: Bool) {
        Task { @MainActor in self.privacyActive = active }
    }

    nonisolated public func streamClientDidFail(code: String, message: String) {
        Task { @MainActor in self.closedReason = message }
    }

    nonisolated public func streamClientDidDisconnect(reason: String) {
        Task { @MainActor in self.closedReason = reason }
    }
}

public struct MacStreamView: View {
    @ObservedObject var model: MacStreamSessionModel
    let onClose: () -> Void
    @State private var showControlCenter = false

    public var body: some View {
        VStack(spacing: 0) {
            // UU-style light compact top bar
            HStack(spacing: 10) {
                Text("BoBo远程 · \(model.hostName)")
                    .font(.system(size: 13, weight: .bold))
                if model.rttMs > 0 {
                    Text("\(Int(model.rttMs.rounded())) ms")
                        .font(.system(size: 11, weight: .semibold))
                        .padding(.horizontal, 7).padding(.vertical, 2)
                        .background(Color.green.opacity(0.15))
                        .foregroundColor(.green)
                        .cornerRadius(6)
                }
                if model.viewOnly {
                    Text("仅查看模式（iOS 投屏）")
                        .font(.system(size: 11, weight: .semibold))
                        .padding(.horizontal, 7).padding(.vertical, 2)
                        .background(Color.orange.opacity(0.18))
                        .foregroundColor(.orange)
                        .cornerRadius(6)
                }
                Spacer()
                Button(action: { model.toggleMute() }) {
                    Image(systemName: model.muted ? "speaker.slash.fill" : "speaker.wave.2.fill")
                }
                .buttonStyle(.bordered)

                Button("控制中心") { showControlCenter.toggle() }
                    .buttonStyle(.bordered)
                    .popover(isPresented: $showControlCenter, arrowEdge: .bottom) {
                        controlCenterPopover
                    }

                Button("退出远控") {
                    model.disconnect()
                    onClose()
                }
                .buttonStyle(.borderedProminent)
                .tint(.red)
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 7)
            .background(Color(NSColor.windowBackgroundColor))

            // Android Mobile Nav Bar when controlling Android phone
            if model.remotePlatform == BoBoProtocol.platformAndroid && !model.viewOnly {
                HStack(spacing: 8) {
                    Button("◀ 返回") { model.sendMobileNav(BoBoProtocol.mobileNavBack) }
                    Button("● 主页") { model.sendMobileNav(BoBoProtocol.mobileNavHome) }
                    Button("▢ 多任务") { model.sendMobileNav(BoBoProtocol.mobileNavRecents) }
                    Button("🔔 通知栏") { model.sendMobileNav(BoBoProtocol.mobileNavNotifications) }
                    Button("🔊 音量+") { model.sendMobileNav(BoBoProtocol.mobileNavVolumeUp) }
                    Button("🔉 音量-") { model.sendMobileNav(BoBoProtocol.mobileNavVolumeDown) }
                    Button("🔒 锁屏") { model.sendMobileNav(BoBoProtocol.mobileNavLock) }
                    Spacer()
                }
                .buttonStyle(.bordered)
                .padding(.horizontal, 12).padding(.vertical, 5)
                .background(Color(red: 0.09, green: 0.13, blue: 0.20))
            }

            ZStack {
                MacRemoteVideoView(
                    displayLayer: model.displayLayer,
                    scaleMode: model.scaleMode,
                    viewOnly: model.viewOnly,
                    mapCommandToControl: model.mapCommandToControl,
                    onInput: { model.sendInput($0) }
                )

                if !model.connected {
                    VStack(spacing: 12) {
                        ProgressView().scaleEffect(1.1)
                        Text(model.closedReason ?? model.statusText)
                            .font(.headline)
                            .foregroundColor(.white)
                        Button("返回主界面") {
                            model.disconnect()
                            onClose()
                        }
                        .buttonStyle(.borderedProminent)
                    }
                    .padding(28)
                    .background(Color.black.opacity(0.78))
                    .cornerRadius(16)
                }
            }
        }
        .frame(minWidth: 960, minHeight: 600)
        .onChange(of: model.closedReason) { newVal in
            if newVal != nil { model.connected = false }
        }
    }

    private var controlCenterPopover: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("控制中心 · \(model.hostName)").font(.headline)
            Divider()
            Toggle("超级屏自适应（消除黑边）", isOn: Binding(
                get: { model.superDisplay },
                set: { _ in model.toggleSuperDisplay() }
            ))
            Toggle("被控端防窥黑屏保护", isOn: Binding(
                get: { model.privacyActive },
                set: { _ in model.togglePrivacy() }
            ))
            Toggle("智能键位映射（⌘ → Ctrl）", isOn: $model.mapCommandToControl)

            if model.monitors.count > 1 {
                Divider()
                Text("切换远端显示器").font(.subheadline).foregroundColor(.secondary)
                HStack {
                    ForEach(model.monitors) { mon in
                        Button("屏 \(mon.index + 1)") { model.switchMonitor(mon.index) }
                            .buttonStyle(.bordered)
                    }
                }
            }

            Divider()
            Text("画质与帧率").font(.subheadline).foregroundColor(.secondary)
            HStack {
                Button("流畅 720p") { model.switchQuality(width: 1280, height: 720, fps: 30, bitrateKbps: 5000) }
                Button("超清 1080p") { model.switchQuality(width: 1920, height: 1080, fps: 60, bitrateKbps: 14000) }
                Button("原画 2K") { model.switchQuality(width: 2560, height: 1440, fps: 60, bitrateKbps: 24000) }
            }
            .buttonStyle(.bordered)

            Divider()
            HStack {
                Button("同步剪贴板") { model.syncClipboardToRemote() }
                Button("锁定远端") { model.lockHost() }
                Button("Ctrl+Alt+Del") { model.sendKeyCombo([0x11, 0x12, 0x2E]) }
            }
            .buttonStyle(.bordered)
        }
        .padding(16)
        .frame(width: 310)
    }
}
