import BoBoAppleCore
import SwiftUI
import UIKit

@main
struct BoBoRemoteiOSApp: App {
    @StateObject private var runtime = iOSAppRuntime()
    @State private var activeStream: iOSStreamSessionModel?

    var body: some Scene {
        WindowGroup {
            if let streamModel = activeStream {
                iOSStreamView(model: streamModel) {
                    activeStream = nil
                }
            } else {
                iOSMainTabView(runtime: runtime) { host, port, name, autoPrivacy in
                    activeStream = iOSStreamSessionModel(
                        identity: runtime.identity,
                        host: host,
                        port: port,
                        hostName: name,
                        autoPrivacy: autoPrivacy
                    )
                }
            }
        }
    }
}

struct iOSMainTabView: View {
    @ObservedObject var runtime: iOSAppRuntime
    let onLaunchStream: (String, Int, String, Bool) -> Void

    @State private var targetInput = ""
    @State private var autoPrivacy = true
    @State private var statusNotice = ""
    @State private var busy = false

    @State private var showPairSheet = false
    @State private var pairHost = ""
    @State private var pairPort = BoBoProtocol.defaultTcpPort
    @State private var pairName = ""
    @State private var pairPin = ""
    @State private var pairError = ""

    var body: some View {
        TabView {
            // Tab 1: Remote Control
            NavigationStack {
                List {
                    Section("快速连接（局域网 / Tailscale / 9位设备码）") {
                        TextField("输入 IP 或 9 位设备码", text: $targetInput)
                            .autocorrectionDisabled()
                            .textInputAutocapitalization(.never)
                        Toggle("连接电脑时立即开启防窥屏", isOn: $autoPrivacy)
                        Button(action: { connect(targetInput, port: BoBoProtocol.defaultTcpPort, name: targetInput) }) {
                            HStack {
                                Spacer()
                                if busy { ProgressView().padding(.trailing, 6) }
                                Text(busy ? "正在探测..." : "进入远程桌面").bold()
                                Spacer()
                            }
                        }
                        .disabled(busy || targetInput.trimmingCharacters(in: .whitespaces).isEmpty)
                        if !statusNotice.isEmpty {
                            Text(statusNotice).font(.caption).foregroundColor(.red)
                        }
                    }

                    Section("局域网自动发现（\(runtime.lanPeers.count) 台）") {
                        if runtime.lanPeers.isEmpty {
                            Text("暂未发现局域网在线 BoBo 设备，可点击右上角刷新或输入 Tailscale 100.x IP。").font(.caption).foregroundColor(.secondary)
                        } else {
                            ForEach(runtime.lanPeers) { peer in
                                Button(action: { connect(peer.host, port: peer.port, name: peer.deviceName) }) {
                                    HStack {
                                        Image(systemName: iconName(peer.platform)).foregroundColor(.blue)
                                        VStack(alignment: .leading) {
                                            Text(peer.deviceName).foregroundColor(.primary).bold()
                                            Text("\(peer.platform.uppercased()) · \(peer.host) · 码 \(BoBoIdentityStore.formatDeviceCode(peer.deviceCode))")
                                                .font(.caption).foregroundColor(.secondary)
                                        }
                                        Spacer()
                                        Text(peer.hostControlMode == BoBoProtocol.controlModeViewOnly ? "观看" : "连接")
                                            .font(.caption.bold()).foregroundColor(.blue)
                                    }
                                }
                                .disabled(!peer.allowIncoming)
                            }
                        }
                    }

                    if !runtime.authorizedPeers.isEmpty {
                        Section("已授权常用设备") {
                            ForEach(runtime.authorizedPeers) { peer in
                                Button(action: { connect(peer.lastHost, port: BoBoProtocol.defaultTcpPort, name: peer.deviceName) }) {
                                    VStack(alignment: .leading) {
                                        Text(peer.deviceName.isEmpty ? peer.lastHost : peer.deviceName).foregroundColor(.primary).bold()
                                        Text("\(peer.lastHost) · 码 \(BoBoIdentityStore.formatDeviceCode(peer.deviceCode))")
                                            .font(.caption).foregroundColor(.secondary)
                                    }
                                }
                            }
                        }
                    }
                }
                .navigationTitle("BoBo远程")
                .toolbar {
                    Button(action: { runtime.discovery.sendProbeNow() }) {
                        Image(systemName: "arrow.clockwise")
                    }
                }
            }
            .tabItem { Label("远程控制", systemImage: "desktopcomputer") }

            // Tab 2: iOS ReplayKit Screen Share Host
            NavigationStack {
                List {
                    Section("本机 iOS 设备码") {
                        HStack {
                            Text(runtime.identity.formattedDeviceCode)
                                .font(.title2.monospaced().bold())
                            Spacer()
                            Button("复制") { UIPasteboard.general.string = runtime.identity.deviceCode }
                        }
                        if let sess = runtime.localPairSession, runtime.pairCountdown > 0 {
                            HStack {
                                VStack(alignment: .leading) {
                                    Text("4 位临时配对码（剩余 \(runtime.pairCountdown)s）").font(.caption).foregroundColor(.secondary)
                                    Text(sess.pin).font(.largeTitle.monospaced().bold()).foregroundColor(.blue)
                                }
                                Spacer()
                                Button("刷新") { runtime.generatePairingPin() }
                            }
                        } else {
                            Button("生成 4 位临时配对码（120秒有效）") { runtime.generatePairingPin() }
                        }
                    }

                    Section("屏幕共享协助（ReplayKit）") {
                        HStack {
                            VStack(alignment: .leading, spacing: 4) {
                                Text(runtime.broadcastActive ? "正在实时共享本机屏幕" : "点击右侧图标启动系统录屏投屏")
                                    .font(.subheadline.bold())
                                Text("受 Apple iOS 沙盒限制，iOS 被控为「投屏观看协助」模式；使用 iOS 远控 Windows / Mac / Android 支持 100% 全功能操控。")
                                    .font(.caption).foregroundColor(.secondary)
                            }
                            Spacer()
                            ReplayKitPickerView().frame(width: 48, height: 48)
                        }
                        if let viewer = runtime.activeViewerName {
                            HStack {
                                Text("「\(viewer)」正在观看屏幕").foregroundColor(.orange).bold()
                                Spacer()
                                Button("断开") { runtime.hostServer.disconnectAllStreams(reason: "iOS 主动断开") }
                                    .foregroundColor(.red)
                            }
                        }
                    }
                }
                .navigationTitle("屏幕共享")
            }
            .tabItem { Label("投屏被看", systemImage: "rectangle.inset.filled.and.person.filled") }

            // Tab 3: Settings
            NavigationStack {
                List {
                    Section("安全与网络") {
                        Toggle("允许其他已配对设备接入", isOn: Binding(
                            get: { runtime.allowIncoming },
                            set: { runtime.setAllowIncoming($0) }
                        ))
                        LabeledContent("协议版本", value: "BoBo6 v6.2")
                        LabeledContent("监听端口", value: "TLS :49810 / UDP :49812")
                        LabeledContent("证书指纹", value: String(runtime.identity.certThumbprint.prefix(16)) + "...")
                    }
                    Section("已信任设备（左滑移除）") {
                        ForEach(runtime.authorizedPeers) { peer in
                            VStack(alignment: .leading) {
                                Text(peer.deviceName.isEmpty ? peer.lastHost : peer.deviceName).bold()
                                Text("\(peer.lastHost) · \(BoBoIdentityStore.formatDeviceCode(peer.deviceCode))").font(.caption).foregroundColor(.secondary)
                            }
                        }
                        .onDelete { idxs in
                            for i in idxs {
                                runtime.identity.removeAuthorizedPeer(thumbprint: runtime.authorizedPeers[i].certThumbprint)
                            }
                            runtime.authorizedPeers = runtime.identity.listAuthorizedPeers()
                        }
                    }
                }
                .navigationTitle("设置")
            }
            .tabItem { Label("设置", systemImage: "gearshape") }
        }
        .sheet(isPresented: $showPairSheet) {
            NavigationStack {
                Form {
                    Section("首次连接「\(pairName)」") {
                        TextField("输入对方 4 位临时配对码", text: $pairPin)
                            .keyboardType(.numberPad)
                        if !pairError.isEmpty {
                            Text(pairError).font(.caption).foregroundColor(.red)
                        }
                    }
                    Button("配对并连接") { submitPair() }
                        .disabled(pairPin.count != 4)
                }
                .navigationTitle("BoBo 配对授权")
                .toolbar {
                    ToolbarItem(placement: .cancellationAction) {
                        Button("取消") { showPairSheet = false }
                    }
                }
            }
        }
    }

    private func connect(_ raw: String, port: Int, name: String) {
        let host = runtime.discovery.resolveHostByDeviceCode(raw)
        guard !host.isEmpty else { return }
        busy = true
        statusNotice = "正在探测 \(host)..."
        Task {
            let probe = await BoBoStreamClient.probeHost(host: host, port: port, identity: runtime.identity)
            busy = false
            if !probe.reachable || !probe.isBoBoHost {
                statusNotice = probe.error.isEmpty ? "未检测到对方开启 BoBo远程" : probe.error
            } else if !probe.allowIncoming {
                statusNotice = "对方已关闭「允许本机被控」"
            } else if !probe.authorized {
                pairHost = host
                pairPort = port
                pairName = probe.hostName.isEmpty ? name : probe.hostName
                pairPin = ""
                pairError = ""
                statusNotice = ""
                showPairSheet = true
            } else {
                statusNotice = ""
                onLaunchStream(host, port, probe.hostName.isEmpty ? name : probe.hostName, autoPrivacy)
            }
        }
    }

    private func submitPair() {
        pairError = ""
        Task {
            let res = await BoBoStreamClient.pairWithPin(host: pairHost, port: pairPort, pin: pairPin, identity: runtime.identity)
            if res.ok {
                showPairSheet = false
                onLaunchStream(pairHost, pairPort, res.hostName.isEmpty ? pairName : res.hostName, autoPrivacy)
            } else {
                pairError = res.error.isEmpty ? "配对失败" : res.error
            }
        }
    }

    private func iconName(_ platform: String) -> String {
        switch platform.lowercased() {
        case BoBoProtocol.platformMacOS: return "laptopcomputer"
        case BoBoProtocol.platformAndroid: return "smartphone"
        case BoBoProtocol.platformIOS: return "iphone"
        default: return "desktopcomputer"
        }
    }
}
