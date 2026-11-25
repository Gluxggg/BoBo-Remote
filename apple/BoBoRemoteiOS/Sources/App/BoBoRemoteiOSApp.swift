import BoBoAppleCore
import SwiftUI
import UIKit

@main
@MainActor
struct BoBoRemoteiOSApp: App {
    @Environment(\.scenePhase) private var scenePhase
    @StateObject private var runtime = iOSAppRuntime()
    @State private var activeStream: iOSStreamSessionModel?

    var body: some Scene {
        WindowGroup {
            Group {
                if let streamModel = activeStream {
                    iOSStreamView(model: streamModel) { activeStream = nil }
                } else {
                    iOSMainTabView(runtime: runtime) { host, port, name, autoPrivacy, thumbprint in
                        activeStream = iOSStreamSessionModel(
                            identity: runtime.identity, host: host, port: port, hostName: name,
                            autoPrivacy: autoPrivacy, expectedHostThumbprint: thumbprint
                        )
                    }
                }
            }
            .onAppear { runtime.updateScenePhase(scenePhase) }
            .onChange(of: scenePhase) { phase in
                runtime.updateScenePhase(phase)
                activeStream?.updateScenePhase(phase)
            }
        }
    }
}

struct iOSMainTabView: View {
    private struct Address: Equatable {
        let host: String
        let port: Int

        var display: String {
            if port == BoBoProtocol.defaultTcpPort { return host }
            let bracketedHost = host.contains(":") ? "[\(host)]" : host
            return "\(bracketedHost):\(port)"
        }
    }

    @ObservedObject var runtime: iOSAppRuntime
    let onLaunchStream: (String, Int, String, Bool, String) -> Void

    @State private var targetInput = ""
    @State private var autoPrivacy = true
    @State private var statusNotice = ""
    @State private var busy = false

    @State private var showPairSheet = false
    @State private var pairAddress: Address?
    @State private var pairThumbprint = ""
    @State private var pairName = ""
    @State private var pairPin = ""
    @State private var pairError = ""
    @State private var pairing = false

    var body: some View {
        TabView {
            NavigationStack {
                List {
                    Section("快速连接（局域网 / Tailscale / 9位设备码）") {
                        TextField("输入 IP、主机:端口或 9 位设备码", text: $targetInput)
                            .autocorrectionDisabled()
                            .textInputAutocapitalization(.never)
                        Toggle("连接电脑时立即开启防窥屏", isOn: $autoPrivacy)
                        Button(action: connectManual) {
                            HStack {
                                Spacer()
                                if busy { ProgressView().padding(.trailing, 6) }
                                Text(busy ? "正在探测..." : "进入远程桌面").bold()
                                Spacer()
                            }
                        }
                        .disabled(busy || targetInput.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                        if !statusNotice.isEmpty {
                            Text(statusNotice).font(.caption).foregroundColor(busy ? .secondary : .red)
                        }
                    }

                    Section("局域网自动发现（\(runtime.lanPeers.count) 台）") {
                        if runtime.lanPeers.isEmpty {
                            Text("暂未发现局域网在线 BoBo 设备，可点击右上角刷新或输入 Tailscale 100.x IP。").font(.caption).foregroundColor(.secondary)
                        } else {
                            ForEach(runtime.lanPeers) { peer in
                                Button { connectDiscovered(peer) } label: {
                                    HStack {
                                        Image(systemName: iconName(peer.platform)).foregroundColor(.blue)
                                        VStack(alignment: .leading) {
                                            Text(peer.deviceName).foregroundColor(.primary).bold()
                                            Text("\(peer.platform.uppercased()) · \(Address(host: peer.host, port: peer.port).display) · 码 \(BoBoIdentityStore.formatDeviceCode(peer.deviceCode))")
                                                .font(.caption).foregroundColor(.secondary)
                                        }
                                        Spacer()
                                        Text(peer.hostControlMode == BoBoProtocol.controlModeViewOnly ? "观看" : "连接")
                                            .font(.caption.bold()).foregroundColor(.blue)
                                    }
                                }
                                .disabled(busy || !peer.allowIncoming)
                            }
                        }
                    }

                    if !runtime.authorizedPeers.isEmpty {
                        Section("已授权常用设备") {
                            ForEach(runtime.authorizedPeers) { peer in
                                Button { connectSaved(peer) } label: {
                                    VStack(alignment: .leading) {
                                        Text(peer.deviceName.isEmpty ? peer.lastHost : peer.deviceName).foregroundColor(.primary).bold()
                                        Text("\(savedAddress(peer)?.display ?? peer.lastHost) · 码 \(BoBoIdentityStore.formatDeviceCode(peer.deviceCode))")
                                            .font(.caption).foregroundColor(.secondary)
                                    }
                                }
                                .disabled(busy)
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

            NavigationStack {
                List {
                    Section("本机 iOS 设备码") {
                        HStack {
                            Text(runtime.identity.formattedDeviceCode)
                                .font(.title2.monospaced().bold())
                            Spacer()
                            Button("复制") { UIPasteboard.general.string = runtime.identity.deviceCode }
                        }
                        if let session = runtime.localPairSession, runtime.pairCountdown > 0 {
                            HStack {
                                VStack(alignment: .leading) {
                                    Text("4 位临时配对码（剩余 \(runtime.pairCountdown)s）").font(.caption).foregroundColor(.secondary)
                                    Text(session.pin).font(.largeTitle.monospaced().bold()).foregroundColor(.blue)
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
                                Text(runtime.broadcastActive ? "本机屏幕已就绪，可连接观看" : "点击右侧图标启动系统录屏投屏")
                                    .font(.subheadline.bold())
                                Text("共享期间请让 BoBo远程保持在前台。已配对设备可以观看本机屏幕。")
                                    .font(.caption).foregroundColor(.secondary)
                            }
                            Spacer()
                            ReplayKitPickerView().frame(width: 48, height: 48)
                        }
                        if let viewer = runtime.activeViewerName {
                            HStack {
                                Text("「\(viewer)」正在观看屏幕").foregroundColor(.orange).bold()
                                Spacer()
                                Button("断开") { runtime.hostServer.disconnectStreamingSessions(reason: "iOS 主动断开") }
                                    .foregroundColor(.red)
                            }
                        }
                    }
                }
                .navigationTitle("屏幕共享")
            }
            .tabItem { Label("投屏被看", systemImage: "rectangle.inset.filled.and.person.filled") }

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
                                Text("\(savedAddress(peer)?.display ?? peer.lastHost) · \(BoBoIdentityStore.formatDeviceCode(peer.deviceCode))")
                                    .font(.caption).foregroundColor(.secondary)
                            }
                        }
                        .onDelete { indexes in
                            for index in indexes {
                                let thumbprint = runtime.authorizedPeers[index].certThumbprint
                                runtime.identity.removeAuthorizedPeer(thumbprint: thumbprint)
                                UserDefaults.standard.removeObject(forKey: portKey(for: thumbprint))
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
                            .disabled(pairing)
                        if !pairError.isEmpty {
                            Text(pairError).font(.caption).foregroundColor(.red)
                        }
                    }
                    Button(pairing ? "正在配对..." : "配对并连接") { submitPair() }
                        .disabled(pairing || pairPin.utf8.count != 4 || !pairPin.utf8.allSatisfy { (48...57).contains($0) })
                }
                .navigationTitle("BoBo 配对授权")
                .toolbar {
                    ToolbarItem(placement: .cancellationAction) {
                        Button("取消") { showPairSheet = false }.disabled(pairing)
                    }
                }
            }
            .interactiveDismissDisabled(pairing)
        }
    }

    private func connectManual() {
        let input = targetInput.trimmingCharacters(in: .whitespacesAndNewlines)
        let code = BoBoIdentityStore.normalizeDeviceCode(input)
        if code.count == 9, input.allSatisfy({ "0123456789-".contains($0) || $0.isWhitespace }) {
            if let saved = runtime.identity.findPeerByCodeOrHost(code) {
                connectSaved(saved)
            } else if let live = runtime.discovery.getPeers().first(where: { BoBoIdentityStore.normalizeDeviceCode($0.deviceCode) == code }) {
                connectDiscovered(live)
            } else {
                statusNotice = "未找到此设备码，请刷新局域网设备或输入对方地址"
            }
            return
        }
        guard let target = parseAddress(input, defaultPort: BoBoProtocol.defaultTcpPort) else {
            statusNotice = "请输入有效的地址，端口须为 1–65535"
            return
        }
        connect([target], name: target.display)
    }

    private func connectDiscovered(_ peer: BoBoDiscoveredPeer) {
        guard let address = parseAddress(peer.host, defaultPort: peer.port) else {
            statusNotice = "发现的设备地址无效"
            return
        }
        if let saved = runtime.identity.findPeerByNodeId(peer.nodeId) {
            connectSaved(saved)
        } else {
            connect([address], name: peer.deviceName)
        }
    }

    private func connectSaved(_ peer: BoBoAuthorizedPeer) {
        let code = BoBoIdentityStore.normalizeDeviceCode(peer.deviceCode)
        let live = runtime.discovery.getPeers().first { candidate in
            if !peer.nodeId.isEmpty { return candidate.nodeId.caseInsensitiveCompare(peer.nodeId) == .orderedSame }
            return code.count == 9 && BoBoIdentityStore.normalizeDeviceCode(candidate.deviceCode) == code
        }
        var addresses: [Address] = []
        if let live, let address = parseAddress(live.host, defaultPort: live.port) { addresses.append(address) }
        if let address = savedAddress(peer), !addresses.contains(address) { addresses.append(address) }
        connect(addresses, name: peer.deviceName, expectedPeer: peer)
    }

    private func connect(_ addresses: [Address], name: String, expectedPeer: BoBoAuthorizedPeer? = nil) {
        guard !busy else { return }
        guard let first = addresses.first else {
            statusNotice = "未找到该设备的可用地址"
            return
        }
        busy = true
        statusNotice = "正在探测 \(first.display)..."
        Task {
            defer { busy = false }
            var error = "未检测到对方开启 BoBo远程"
            for address in addresses {
                let probe = await BoBoStreamClient.probeHost(host: address.host, port: address.port, identity: runtime.identity)
                guard probe.reachable, probe.isBoBoHost else {
                    if !probe.error.isEmpty { error = probe.error }
                    continue
                }
                if let expectedPeer, expectedPeer.certThumbprint.caseInsensitiveCompare(probe.hostThumbprint) != .orderedSame {
                    error = "远端证书与已授权设备不一致"
                    continue
                }
                guard probe.allowIncoming else {
                    statusNotice = "对方已关闭「允许本机被控」"
                    return
                }

                let displayName = probe.hostName.isEmpty ? (name.isEmpty ? address.display : name) : probe.hostName
                statusNotice = ""
                if probe.authorized, runtime.identity.isPeerAuthorized(peerNodeId: probe.hostNodeId, peerThumbprint: probe.hostThumbprint) {
                    rememberPort(address, thumbprint: probe.hostThumbprint)
                    onLaunchStream(address.host, address.port, displayName, autoPrivacy, probe.hostThumbprint)
                } else {
                    pairAddress = address
                    pairThumbprint = probe.hostThumbprint
                    pairName = displayName
                    pairPin = ""
                    pairError = ""
                    showPairSheet = true
                }
                return
            }
            statusNotice = error
        }
    }

    private func submitPair() {
        guard !pairing, let address = pairAddress else { return }
        pairing = true
        pairError = ""
        let thumbprint = pairThumbprint
        Task {
            let result = await BoBoStreamClient.pairWithPin(
                host: address.host, port: address.port, pin: pairPin, identity: runtime.identity,
                expectedHostThumbprint: thumbprint
            )
            pairing = false
            if result.ok {
                rememberPort(address, thumbprint: thumbprint)
                showPairSheet = false
                onLaunchStream(address.host, address.port, result.hostName.isEmpty ? pairName : result.hostName, autoPrivacy, thumbprint)
            } else {
                pairError = result.error.isEmpty ? "配对失败" : result.error
            }
        }
    }

    private func parseAddress(_ raw: String, defaultPort: Int) -> Address? {
        guard let address = BoBoProtocol.parseAddress(raw, defaultPort: defaultPort) else { return nil }
        return Address(host: address.host, port: address.port)
    }

    private func savedAddress(_ peer: BoBoAuthorizedPeer) -> Address? {
        let rememberedPort = UserDefaults.standard.integer(forKey: portKey(for: peer.certThumbprint))
        let port = (1...65535).contains(rememberedPort) ? rememberedPort : BoBoProtocol.defaultTcpPort
        return parseAddress(peer.lastHost, defaultPort: port)
    }

    private func rememberPort(_ address: Address, thumbprint: String) {
        guard !thumbprint.isEmpty else { return }
        UserDefaults.standard.set(address.port, forKey: portKey(for: thumbprint))
    }

    private func portKey(for thumbprint: String) -> String { "BoBoRemote.LastPort.\(thumbprint.lowercased())" }

    private func iconName(_ platform: String) -> String {
        switch platform.lowercased() {
        case BoBoProtocol.platformMacOS: return "laptopcomputer"
        case BoBoProtocol.platformAndroid: return "smartphone"
        case BoBoProtocol.platformIOS: return "iphone"
        default: return "desktopcomputer"
        }
    }
}
