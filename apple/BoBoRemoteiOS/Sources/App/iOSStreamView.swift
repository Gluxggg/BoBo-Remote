import BoBoAppleCore
import SwiftUI

public struct iOSStreamView: View {
    @Environment(\.displayScale) private var displayScale
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
                    displayLayer: model.displayLayer, videoSize: model.videoSize,
                    touchpadMode: model.touchpadMode, viewOnly: !model.connected || model.viewOnly || model.configurationPending,
                    isAndroidHost: model.remotePlatform == BoBoProtocol.platformAndroid,
                    onInput: { model.sendInput($0) }
                )
                .ignoresSafeArea()

                if model.videoSize == .zero {
                    VStack(spacing: 12) {
                        if model.closedReason == nil { ProgressView().tint(.white) }
                        Text(model.closedReason ?? (model.connected ? "正在等待视频关键帧..." : "正在建立加密连接..."))
                            .foregroundColor(.white).font(.headline)
                        Button("返回", action: closeStream).buttonStyle(.borderedProminent)
                    }
                    .padding(24)
                    .background(Color.black.opacity(0.8))
                    .cornerRadius(16)
                }

                if model.connected, showQuickKeys, !model.viewOnly {
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

                if model.connected {
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
                            .onChanged { drag in
                                ballPos = CGPoint(
                                    x: max(36, min(geo.size.width - 36, drag.location.x)),
                                    y: max(44, min(geo.size.height - 44, drag.location.y))
                                )
                            }
                            .onEnded { drag in
                                let snapX = drag.location.x < geo.size.width / 2 ? 44 : geo.size.width - 44
                                withAnimation(.spring()) { ballPos.x = snapX }
                            }
                    )
                    .onTapGesture { showControlSheet = true }
                    .accessibilityAddTraits(.isButton)
                    .accessibilityLabel("BoBo 控制中心")
                }
            }
            .onAppear { model.start(viewSize: geo.size, scale: displayScale) }
            .onChange(of: geo.size) { size in
                model.updateViewport(size: size, scale: displayScale)
                ballPos.x = max(36, min(geo.size.width - 36, ballPos.x))
                ballPos.y = max(44, min(geo.size.height - 44, ballPos.y))
            }
        }
        .statusBar(hidden: true)
        .onChange(of: model.connected) { connected in
            if !connected {
                showControlSheet = false
                showTextAlert = false
                showQuickKeys = false
            }
        }
        .sheet(isPresented: $showControlSheet) {
            NavigationStack {
                List {
                    if !model.viewOnly {
                        Section("交互模式") {
                            Picker("鼠标模式", selection: $model.touchpadMode) {
                                Text("触控板鼠标").tag(true)
                                Text("直点触控").tag(false)
                            }
                            .pickerStyle(.segmented)
                            Toggle("显示底部快捷键盘栏", isOn: $showQuickKeys)
                            Button("发送文字输入到远端") {
                                showControlSheet = false
                                showTextAlert = true
                            }
                            Button("同步本机剪贴板到远端") {
                                model.syncClipboardToRemote()
                                showControlSheet = false
                            }
                        }
                    }
                    Section("画面与安全") {
                        if !model.viewOnly {
                            Toggle("超级屏自适应", isOn: Binding(get: { model.superDisplay }, set: { _ in model.toggleSuperDisplay() }))
                            Toggle("被控端防窥保护", isOn: Binding(get: { model.privacyActive }, set: { _ in model.togglePrivacy() }))
                            Button("锁定远端电脑") {
                                model.lockHost()
                                showControlSheet = false
                            }
                        }
                        Toggle("静音远端声音", isOn: Binding(get: { model.muted }, set: { _ in model.toggleMute() }))
                    }
                    Section("画面设置") {
                        if model.videoSize != .zero, model.negotiatedFps > 0 {
                            let width = Int(model.videoSize.width.rounded())
                            let height = Int(model.videoSize.height.rounded())
                            LabeledContent("当前画面", value: "\(width) × \(height) · \(model.negotiatedFps) fps")
                        }
                        if model.remotePlatform != BoBoProtocol.platformIOS {
                            if model.monitors.count > 1 {
                                Picker("显示器", selection: Binding(get: { model.monitorIndex }, set: model.selectMonitor)) {
                                    ForEach(model.monitors) { monitor in
                                        Text("\(monitor.name) · \(monitor.width) × \(monitor.height)").tag(monitor.index)
                                    }
                                }
                                .disabled(model.configurationPending)
                            }
                            Group {
                                Button("流畅 720p · 30fps") { model.switchQuality(width: 1280, height: 720, fps: 30, bitrateKbps: 4000) }
                                Button("超清 1080p · 60fps") { model.switchQuality(width: 1920, height: 1080, fps: 60, bitrateKbps: 12000) }
                                Button("原画 2K · 60fps") { model.switchQuality(width: 2560, height: 1440, fps: 60, bitrateKbps: 22000) }
                            }
                            .disabled(model.configurationPending)
                            if model.configurationPending { ProgressView("正在切换画面...") }
                            if let notice = model.configurationNotice { Text(notice).foregroundColor(.secondary) }
                        }
                    }
                    if model.remotePlatform == BoBoProtocol.platformAndroid, !model.viewOnly {
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
                        Button(role: .destructive, action: closeStream) {
                            HStack {
                                Spacer()
                                Text("退出远程控制").bold()
                                Spacer()
                            }
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

    private func closeStream() {
        showControlSheet = false
        model.disconnect()
        onClose()
    }

    private func keyPill(_ label: String, _ combo: [UInt8]) -> some View {
        Button(label) { model.sendKeyCombo(combo) }
            .font(.caption)
            .padding(.horizontal, 11).padding(.vertical, 8)
            .background(Color.white.opacity(0.16)).foregroundColor(.white).cornerRadius(8)
    }
}
