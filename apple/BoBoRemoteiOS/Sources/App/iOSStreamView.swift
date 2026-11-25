import AVFoundation
import BoBoAppleCore
import SwiftUI
import UIKit

@MainActor
public final class iOSStreamSessionModel: ObservableObject, BoBoStreamClientDelegate {
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
    @Published public var touchpadMode = true
    @Published public var superDisplay = true
    @Published public var privacyActive = false
    @Published public var muted = false
    @Published public var closedReason: String?

    public init(identity: BoBoIdentityStore, host: String, port: Int, hostName: String, autoPrivacy: Bool) {
        self.host = host
        self.port = port
        self.hostName = hostName
        self.client = BoBoStreamClient(identity: identity)

        let bounds = UIScreen.main.nativeBounds
        var opts = BoBoStreamOptions(host: host, port: port)
        opts.width = 1920
        opts.height = 1080
        opts.fps = 60
        opts.bitrateKbps = 12000
        opts.requestPrivacy = autoPrivacy
        opts.superDisplay = true
        opts.clientWidth = Int(max(bounds.width, bounds.height))
        opts.clientHeight = Int(min(bounds.width, bounds.height))
        client.connectAndStartStream(options: opts, delegate: self)
    }

    public func sendInput(_ pkt: BoBoInputPacket) { client.sendInput(pkt) }
    public func sendKeyCombo(_ vks: [UInt8]) { client.sendKeyCombo(vks) }
    public func sendText(_ txt: String) { client.sendClipboardText(txt, injectUnicode: true) }
    public func sendMobileNav(_ action: Int16) { client.sendInput(BoBoProtocol.buildMobileNav(action)) }

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
        let b = UIScreen.main.nativeBounds
        client.sendSetSuperDisplay(enable: superDisplay, clientWidth: Int(max(b.width, b.height)), clientHeight: Int(min(b.width, b.height)))
    }

    public func switchQuality(width: Int, height: Int, fps: Int, bitrateKbps: Int) {
        client.sendReconfigure(monitorIndex: 0, width: width, height: height, fps: fps, bitrateKbps: bitrateKbps)
    }

    public func syncClipboardToRemote() {
        if let txt = UIPasteboard.general.string, !txt.isEmpty {
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
            self.remotePlatform = remotePlatform
            self.viewOnly = (hostControlMode == BoBoProtocol.controlModeViewOnly)
            self.touchpadMode = (remotePlatform == BoBoProtocol.platformWindows || remotePlatform == BoBoProtocol.platformMacOS)
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

/// iOS 零拷贝 GPU 视频显示 + 双模式手势交互（触控板鼠标 / 直点触控）
public struct iOSRemoteVideoView: UIViewRepresentable {
    public let displayLayer: AVSampleBufferDisplayLayer
    public let touchpadMode: Bool
    public let superDisplay: Bool
    public let viewOnly: Bool
    public let isAndroidHost: Bool
    public let onInput: (BoBoInputPacket) -> Void

    public func makeUIView(context: Context) -> iOSVideoCanvasUIView {
        let v = iOSVideoCanvasUIView(displayLayer: displayLayer)
        v.update(touchpadMode: touchpadMode, superDisplay: superDisplay, viewOnly: viewOnly, isAndroidHost: isAndroidHost, onInput: onInput)
        return v
    }

    public func updateUIView(_ uiView: iOSVideoCanvasUIView, context: Context) {
        uiView.update(touchpadMode: touchpadMode, superDisplay: superDisplay, viewOnly: viewOnly, isAndroidHost: isAndroidHost, onInput: onInput)
    }
}

public final class iOSVideoCanvasUIView: UIView {
    private let videoLayer: AVSampleBufferDisplayLayer
    private var touchpadMode = true
    private var viewOnly = false
    private var isAndroidHost = false
    private var onInput: ((BoBoInputPacket) -> Void)?

    private var lastTouchPt: CGPoint = .zero
    private var downTouchPt: CGPoint = .zero
    private var downTime: TimeInterval = 0
    private var maxTouchCount = 0
    private var movedBeyondTap = false

    public init(displayLayer: AVSampleBufferDisplayLayer) {
        self.videoLayer = displayLayer
        super.init(frame: .zero)
        backgroundColor = UIColor(red: 0.055, green: 0.082, blue: 0.125, alpha: 1.0)
        isMultipleTouchEnabled = true
        videoLayer.videoGravity = .resizeAspect
        layer.addSublayer(videoLayer)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) not supported") }

    public override func layoutSubviews() {
        super.layoutSubviews()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        videoLayer.frame = bounds
        CATransaction.commit()
    }

    public func update(touchpadMode: Bool, superDisplay: Bool, viewOnly: Bool, isAndroidHost: Bool, onInput: @escaping (BoBoInputPacket) -> Void) {
        self.touchpadMode = touchpadMode
        self.viewOnly = viewOnly
        self.isAndroidHost = isAndroidHost
        self.onInput = onInput
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        videoLayer.videoGravity = (superDisplay && !isAndroidHost) ? .resize : .resizeAspect
        CATransaction.commit()
    }

    public override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
        guard !viewOnly, let first = touches.first, bounds.width > 1, bounds.height > 1 else { return }
        let pt = first.location(in: self)
        let count = event?.allTouches?.count ?? touches.count

        if !touchpadMode {
            let (nx, ny) = normalize(pt)
            if isAndroidHost {
                onInput?(BoBoProtocol.buildTouch(pointerId: 0, action: BoBoProtocol.touchActionDown, nx: nx, ny: ny))
            } else {
                onInput?(BoBoProtocol.buildMouseAbs(nx: nx, ny: ny))
                onInput?(BoBoProtocol.buildMouseButton(button: 1, down: true))
            }
            return
        }

        if count == 1 {
            downTouchPt = pt
            lastTouchPt = pt
            downTime = CACurrentMediaTime()
            maxTouchCount = 1
            movedBeyondTap = false
        } else {
            maxTouchCount = max(maxTouchCount, count)
            lastTouchPt = pt
        }
    }

    public override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) {
        guard !viewOnly, let first = touches.first, bounds.width > 1, bounds.height > 1 else { return }
        let pt = first.location(in: self)
        let count = event?.allTouches?.count ?? touches.count

        if !touchpadMode {
            let (nx, ny) = normalize(pt)
            if isAndroidHost {
                onInput?(BoBoProtocol.buildTouch(pointerId: 0, action: BoBoProtocol.touchActionMove, nx: nx, ny: ny))
            } else {
                onInput?(BoBoProtocol.buildMouseAbs(nx: nx, ny: ny))
            }
            return
        }

        let dx = pt.x - lastTouchPt.x
        let dy = pt.y - lastTouchPt.y
        lastTouchPt = pt
        if abs(pt.x - downTouchPt.x) > 10 || abs(pt.y - downTouchPt.y) > 10 {
            movedBeyondTap = true
        }
        if count >= 2 {
            let scroll = Int((dy * 3.0).rounded())
            if scroll != 0 { onInput?(BoBoProtocol.buildMouseWheel(delta: scroll, horizontal: false)) }
        } else {
            let rdx = Int((dx * 1.8).rounded())
            let rdy = Int((dy * 1.8).rounded())
            if rdx != 0 || rdy != 0 { onInput?(BoBoProtocol.buildMouseRel(dx: rdx, dy: rdy)) }
        }
    }

    public override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) {
        guard !viewOnly, let first = touches.first else { return }
        let pt = first.location(in: self)
        if !touchpadMode {
            let (nx, ny) = normalize(pt)
            if isAndroidHost {
                onInput?(BoBoProtocol.buildTouch(pointerId: 0, action: BoBoProtocol.touchActionUp, nx: nx, ny: ny))
            } else {
                onInput?(BoBoProtocol.buildMouseAbs(nx: nx, ny: ny))
                onInput?(BoBoProtocol.buildMouseButton(button: 1, down: false))
            }
            return
        }

        let elapsed = CACurrentMediaTime() - downTime
        if !movedBeyondTap && elapsed < 0.28 {
            let btn: Int16 = (maxTouchCount >= 2) ? 3 : 1
            onInput?(BoBoProtocol.buildMouseButton(button: btn, down: true))
            onInput?(BoBoProtocol.buildMouseButton(button: btn, down: false))
        }
    }

    private func normalize(_ pt: CGPoint) -> (UInt16, UInt16) {
        let nx = max(0.0, min(1.0, pt.x / max(1.0, bounds.width - 1.0)))
        let ny = max(0.0, min(1.0, pt.y / max(1.0, bounds.height - 1.0)))
        return (UInt16((nx * 65535.0).rounded()), UInt16((ny * 65535.0).rounded()))
    }
}

public struct iOSStreamView: View {
    @ObservedObject var model: iOSStreamSessionModel
    let onClose: () -> Void

    @State private var showControlSheet = false
    @State private var showQuickKeys = false
    @State private var showTextAlert = false
    @State private var textDraft = ""
    @State private var ballPos = CGPoint(x: 44, y: 120)

    public var body: some View {
        GeometryReader { geo in
            ZStack {
                iOSRemoteVideoView(
                    displayLayer: model.displayLayer,
                    touchpadMode: model.touchpadMode,
                    superDisplay: model.superDisplay,
                    viewOnly: model.viewOnly,
                    isAndroidHost: model.remotePlatform == BoBoProtocol.platformAndroid,
                    onInput: { model.sendInput($0) }
                )
                .ignoresSafeArea()

                if !model.connected {
                    VStack(spacing: 12) {
                        ProgressView().tint(.white)
                        Text(model.closedReason ?? model.statusText)
                            .foregroundColor(.white).font(.headline)
                        Button("返回") {
                            model.disconnect()
                            onClose()
                        }
                        .buttonStyle(.borderedProminent)
                    }
                    .padding(24)
                    .background(Color.black.opacity(0.8))
                    .cornerRadius(16)
                }

                // Bottom PC quick hotkey strip
                if model.connected && showQuickKeys && !model.viewOnly {
                    VStack {
                        Spacer()
                        ScrollView(.horizontal, showsIndicators: false) {
                            HStack(spacing: 8) {
                                keyPill("Esc", [0x1B])
                                keyPill("Tab", [0x09])
                                keyPill("Ctrl+C", [0x11, 0x43])
                                keyPill("Ctrl+V", [0x11, 0x56])
                                keyPill("Alt+Tab", [0x12, 0x09])
                                keyPill("Win", [0x5B])
                                keyPill("Win+D", [0x5B, 0x44])
                                Button("输入文字") { showTextAlert = true }
                                    .font(.caption.bold())
                                    .padding(.horizontal, 12).padding(.vertical, 8)
                                    .background(Color.blue).foregroundColor(.white).cornerRadius(8)
                            }
                            .padding(.horizontal, 12).padding(.vertical, 8)
                        }
                        .background(Color.black.opacity(0.82))
                    }
                }

                // Draggable Edge-Snapping Floating Ball (UU-style)
                HStack(spacing: 5) {
                    Image(systemName: "slider.horizontal.3").foregroundColor(.cyan)
                    Text(model.rttMs > 0.1 ? "\(Int(model.rttMs.rounded()))ms" : "BoBo")
                        .font(.caption.bold()).foregroundColor(.white)
                }
                .padding(.horizontal, 10).padding(.vertical, 8)
                .background(Color(red: 0.12, green: 0.16, blue: 0.24).opacity(0.9))
                .cornerRadius(12)
                .shadow(radius: 5)
                .position(ballPos)
                .gesture(
                    DragGesture()
                        .onChanged { v in
                            ballPos = CGPoint(
                                x: max(36, min(geo.size.width - 36, v.location.x)),
                                y: max(44, min(geo.size.height - 44, v.location.y))
                            )
                        }
                        .onEnded { v in
                            let snapX = (v.location.x < geo.size.width / 2) ? 44 : (geo.size.width - 44)
                            withAnimation(.spring()) { ballPos.x = snapX }
                        }
                )
                .onTapGesture { showControlSheet = true }
            }
        }
        .statusBar(hidden: true)
        .sheet(isPresented: $showControlSheet) {
            NavigationStack {
                List {
                    Section("交互模式") {
                        Picker("鼠标模式", selection: $model.touchpadMode) {
                            Text("触控板鼠标").tag(true)
                            Text("直点触控").tag(false)
                        }
                        .pickerStyle(.segmented)
                        Toggle("显示底部快捷键盘栏", isOn: $showQuickKeys)
                        Button("发送文字输入到远端") { showControlSheet = false; showTextAlert = true }
                        Button("同步本机剪贴板到远端") { model.syncClipboardToRemote(); showControlSheet = false }
                    }
                    Section("画面与安全") {
                        Toggle("超级屏自适应", isOn: Binding(get: { model.superDisplay }, set: { _ in model.toggleSuperDisplay() }))
                        Toggle("被控端防窥保护", isOn: Binding(get: { model.privacyActive }, set: { _ in model.togglePrivacy() }))
                        Toggle("静音远端声音", isOn: Binding(get: { model.muted }, set: { _ in model.toggleMute() }))
                        Button("锁定远端电脑") { model.lockHost(); showControlSheet = false }
                    }
                    Section("画质切换") {
                        Button("流畅 720p · 30fps") { model.switchQuality(width: 1280, height: 720, fps: 30, bitrateKbps: 4000); showControlSheet = false }
                        Button("超清 1080p · 60fps") { model.switchQuality(width: 1920, height: 1080, fps: 60, bitrateKbps: 12000); showControlSheet = false }
                        Button("原画 2K · 60fps") { model.switchQuality(width: 2560, height: 1440, fps: 60, bitrateKbps: 22000); showControlSheet = false }
                    }
                    if model.remotePlatform == BoBoProtocol.platformAndroid {
                        Section("Android 导航键") {
                            HStack {
                                Button("返回") { model.sendMobileNav(BoBoProtocol.mobileNavBack) }
                                Spacer()
                                Button("主页") { model.sendMobileNav(BoBoProtocol.mobileNavHome) }
                                Spacer()
                                Button("多任务") { model.sendMobileNav(BoBoProtocol.mobileNavRecents) }
                            }
                        }
                    }
                    Section {
                        Button(role: .destructive) {
                            showControlSheet = false
                            model.disconnect()
                            onClose()
                        } label: {
                            HStack { Spacer(); Text("退出远程控制").bold(); Spacer() }
                        }
                    }
                }
                .navigationTitle("BoBo 控制中心")
                .navigationBarTitleDisplayMode(.inline)
            }
            .presentationDetents([.medium, .large])
        }
        .alert("发送文字到远端", isPresented: $showTextAlert) {
            TextField("输入中英文文本", text: $textDraft)
            Button("发送") {
                if !textDraft.isEmpty { model.sendText(textDraft) }
                textDraft = ""
            }
            Button("取消", role: .cancel) {}
        }
    }

    private func keyPill(_ label: String, _ combo: [UInt8]) -> some View {
        Button(label) { model.sendKeyCombo(combo) }
            .font(.caption)
            .padding(.horizontal, 11).padding(.vertical, 8)
            .background(Color.white.opacity(0.16)).foregroundColor(.white).cornerRadius(8)
    }
}
