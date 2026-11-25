import AppKit
import BoBoAppleCore
import SwiftUI

@main
struct BoBoRemoteMacApp: App {
    @StateObject private var runtime = MacAppRuntime()
    @State private var activeStream: MacStreamSessionModel?

    var body: some Scene {
        WindowGroup("BoBo远程 · macOS") {
            if let streamModel = activeStream {
                MacStreamView(model: streamModel) {
                    activeStream = nil
                }
            } else {
                MacMainView(runtime: runtime) { host, port, name, autoPrivacy in
                    activeStream = MacStreamSessionModel(
                        identity: runtime.identity,
                        host: host,
                        port: port,
                        hostName: name,
                        autoPrivacy: autoPrivacy
                    )
                }
            }
        }
        .windowStyle(.titleBar)
    }
}

struct MacMainView: View {
    @ObservedObject var runtime: MacAppRuntime
    let onLaunchStream: (String, Int, String, Bool) -> Void

    @State private var targetInput = ""
    @State private var autoPrivacy = true
    @State private var statusNotice = ""
    @State private var busy = false

    @State private var showPairSheet = false
    @State private var pairTargetHost = ""
    @State private var pairTargetPort = BoBoProtocol.defaultTcpPort
    @State private var pairTargetName = ""
    @State private var pairPinInput = ""
    @State private var pairError = ""

    var body: some View {
        NavigationSplitView {
            VStack(alignment: .leading, spacing: 14) {
                HStack(spacing: 10) {
                    RoundedRectangle(cornerRadius: 9)
                        .fill(LinearGradient(colors: [.blue, .cyan], startPoint: .topLeading, endPoint: .bottomTrailing))
                        .frame(width: 34, height: 34)
                        .overlay(Text("B").font(.title3.bold()).foregroundColor(.white))
                    VStack(alignment: .leading, spacing: 2) {
                        Text("BoBo远程").font(.headline)
                        Text("V6.2 · 四端互通").font(.caption).foregroundColor(.secondary)
                    }
                }
                .padding(.bottom, 6)

                // Local Host Device Code & 4-digit PIN card
                VStack(alignment: .leading, spacing: 8) {
                    Text("本机 Mac 设备码").font(.caption).foregroundColor(.secondary)
                    HStack {
                        Text(runtime.identity.formattedDeviceCode)
                            .font(.system(size: 20, weight: .heavy, design: .monospaced))
                        Spacer()
                        Button(action: {
                            NSPasteboard.general.clearContents()
                            NSPasteboard.general.setString(runtime.identity.deviceCode, forType: .string)
                        }) {
                            Image(systemName: "doc.on.doc")
                        }
                        .buttonStyle(.borderless)
                    }
                    if let sess = runtime.localPairSession, runtime.pairCountdown > 0 {
                        HStack {
                            VStack(alignment: .leading) {
                                Text("4 位临时配对码（\(runtime.pairCountdown)s）").font(.caption2).foregroundColor(.secondary)
                                Text(sess.pin).font(.system(size: 22, weight: .bold, design: .monospaced)).foregroundColor(.blue)
                            }
                            Spacer()
                            Button("刷新") { runtime.generatePairingPin() }.buttonStyle(.bordered)
                        }
                        .padding(8)
                        .background(Color.blue.opacity(0.08))
                        .cornerRadius(8)
                    } else {
                        Button("生成 4 位临时配对码") { runtime.generatePairingPin() }
                            .buttonStyle(.borderedProminent)
                            .frame(maxWidth: .infinity)
                    }
                }
                .padding(12)
                .background(Color(NSColor.controlBackgroundColor))
                .cornerRadius(12)

                // macOS Host Permissions Checklist
                VStack(alignment: .leading, spacing: 8) {
                    Text("macOS 被控系统权限").font(.caption.bold()).foregroundColor(.secondary)
                    HStack {
                        Image(systemName: runtime.screenRecordingGranted ? "checkmark.circle.fill" : "exclamationmark.triangle.fill")
                            .foregroundColor(runtime.screenRecordingGranted ? .green : .orange)
                        Text("屏幕录制与声音").font(.caption)
                        Spacer()
                        if !runtime.screenRecordingGranted {
                            Button("授权") { runtime.requestScreenRecordingPermission() }.font(.caption)
                        }
                    }
                    HStack {
                        Image(systemName: runtime.accessibilityGranted ? "checkmark.circle.fill" : "exclamationmark.triangle.fill")
                            .foregroundColor(runtime.accessibilityGranted ? .green : .orange)
                        Text("辅助功能（键鼠）").font(.caption)
                        Spacer()
                        if !runtime.accessibilityGranted {
                            Button("授权") { runtime.requestAccessibilityPermission() }.font(.caption)
                        }
                    }
                    Toggle("允许本机被远控", isOn: Binding(
                        get: { runtime.allowIncoming },
                        set: { runtime.setAllowIncoming($0) }
                    ))
                    .font(.caption)
                }
                .padding(12)
                .background(Color(NSColor.controlBackgroundColor))
                .cornerRadius(12)

                if let activePeer = runtime.activeIncomingPeer {
                    HStack {
                        Text("正在被「\(activePeer)」控制").font(.caption.bold()).foregroundColor(.red)
                        Spacer()
                        Button("断开") { runtime.hostServer.disconnectAllStreams(reason: "Mac 主动断开") }
                            .font(.caption).tint(.red)
                    }
                    .padding(10)
                    .background(Color.red.opacity(0.1))
                    .cornerRadius(10)
                }

                Spacer()
            }
            .padding(14)
            .frame(minWidth: 260)
        } detail: {
            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    // Quick Connect Card
                    VStack(alignment: .leading, spacing: 10) {
                        Text("连接远端设备（Windows / macOS / Android / iOS）").font(.headline)
                        HStack {
                            TextField("输入局域网 IP、Tailscale IP 或 9 位 BoBo 设备码", text: $targetInput)
                                .textFieldStyle(.roundedBorder)
                            Button(busy ? "探测中..." : "进入远程桌面") {
                                connectTarget(targetInput, port: BoBoProtocol.defaultTcpPort, name: targetInput)
                            }
                            .buttonStyle(.borderedProminent)
                            .disabled(busy || targetInput.trimmingCharacters(in: .whitespaces).isEmpty)
                        }
                        Toggle("连接电脑时自动开启被控端防窥屏", isOn: $autoPrivacy)
                        if !statusNotice.isEmpty {
                            Text(statusNotice).font(.caption).foregroundColor(.red)
                        }
                    }
                    .padding(16)
                    .background(Color(NSColor.controlBackgroundColor))
                    .cornerRadius(14)

                    // LAN Peers
                    HStack {
                        Text("局域网自动发现（UDP 49812 · \(runtime.lanPeers.count) 台）").font(.headline)
                        Spacer()
                        Button("刷新发现") { runtime.discovery.sendProbeNow() }.buttonStyle(.bordered)
                    }

                    if runtime.lanPeers.isEmpty {
                        Text("暂未发现同局域网在线的 BoBo远程 设备。你也可以直接输入 Tailscale 虚拟内网 IP 跨网直连。")
                            .font(.subheadline)
                            .foregroundColor(.secondary)
                            .padding(14)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .background(Color(NSColor.controlBackgroundColor))
                            .cornerRadius(12)
                    } else {
                        ForEach(runtime.lanPeers) { peer in
                            HStack {
                                Image(systemName: platformSymbol(peer.platform))
                                    .font(.title2).foregroundColor(.blue)
                                    .frame(width: 34)
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(peer.deviceName).font(.headline)
                                    Text("\(peer.platform.uppercased()) · \(peer.host) · 码 \(BoBoIdentityStore.formatDeviceCode(peer.deviceCode))")
                                        .font(.caption).foregroundColor(.secondary)
                                }
                                Spacer()
                                Button(peer.hostControlMode == BoBoProtocol.controlModeViewOnly ? "观看投屏" : "连接") {
                                    connectTarget(peer.host, port: peer.port, name: peer.deviceName)
                                }
                                .buttonStyle(.borderedProminent)
                                .disabled(!peer.allowIncoming)
                            }
                            .padding(12)
                            .background(Color(NSColor.controlBackgroundColor))
                            .cornerRadius(12)
                        }
                    }

                    // Authorized Peers
                    if !runtime.authorizedPeers.isEmpty {
                        Text("已授权信任设备（\(runtime.authorizedPeers.count) 台）").font(.headline)
                        ForEach(runtime.authorizedPeers) { peer in
                            HStack {
                                VStack(alignment: .leading) {
                                    Text(peer.deviceName.isEmpty ? peer.lastHost : peer.deviceName).font(.headline)
                                    Text("\(peer.lastHost) · 设备码 \(BoBoIdentityStore.formatDeviceCode(peer.deviceCode))")
                                        .font(.caption).foregroundColor(.secondary)
                                }
                                Spacer()
                                Button("移除") {
                                    runtime.identity.removeAuthorizedPeer(thumbprint: peer.certThumbprint)
                                    runtime.authorizedPeers = runtime.identity.listAuthorizedPeers()
                                }
                                .buttonStyle(.bordered)
                                Button("直连") {
                                    connectTarget(peer.lastHost, port: BoBoProtocol.defaultTcpPort, name: peer.deviceName)
                                }
                                .buttonStyle(.borderedProminent)
                            }
                            .padding(12)
                            .background(Color(NSColor.controlBackgroundColor))
                            .cornerRadius(12)
                        }
                    }
                }
                .padding(20)
            }
        }
        .frame(minWidth: 880, minHeight: 580)
        .sheet(isPresented: $showPairSheet) {
            VStack(spacing: 14) {
                Text("BoBo 双端配对授权").font(.headline)
                Text("请输入「\(pairTargetName)」上生成的 4 位临时配对码：").font(.subheadline).foregroundColor(.secondary)
                TextField("4 位数字配对码", text: $pairPinInput)
                    .textFieldStyle(.roundedBorder)
                    .frame(width: 180)
                if !pairError.isEmpty {
                    Text(pairError).font(.caption).foregroundColor(.red)
                }
                HStack {
                    Button("取消") { showPairSheet = false }
                    Button("配对并连接") { submitPair() }
                        .buttonStyle(.borderedProminent)
                        .disabled(pairPinInput.count != 4)
                }
            }
            .padding(24)
            .frame(width: 340)
        }
    }

    private func connectTarget(_ raw: String, port: Int, name: String) {
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
                pairTargetHost = host
                pairTargetPort = port
                pairTargetName = probe.hostName.isEmpty ? name : probe.hostName
                pairPinInput = ""
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
            let res = await BoBoStreamClient.pairWithPin(
                host: pairTargetHost,
                port: pairTargetPort,
                pin: pairPinInput,
                identity: runtime.identity
            )
            if res.ok {
                showPairSheet = false
                onLaunchStream(pairTargetHost, pairTargetPort, res.hostName.isEmpty ? pairTargetName : res.hostName, autoPrivacy)
            } else {
                pairError = res.error.isEmpty ? "配对失败" : res.error
            }
        }
    }

    private func platformSymbol(_ platform: String) -> String {
        switch platform.lowercased() {
        case BoBoProtocol.platformMacOS: return "laptopcomputer"
        case BoBoProtocol.platformAndroid: return "smartphone"
        case BoBoProtocol.platformIOS: return "iphone"
        default: return "desktopcomputer"
        }
    }
}
