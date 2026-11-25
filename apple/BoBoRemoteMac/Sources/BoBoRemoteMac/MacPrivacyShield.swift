import AppKit
import CoreGraphics
import Foundation

/// macOS 原生防窥屏保护：
/// 1. 在所有物理显示器铺设纯黑防窥遮罩窗（ScreenCaptureKit 自动排除这些窗口，远端画面 100% 清晰可见）；
/// 2. 联动 CGEventTap 物理键鼠锁，杜绝机前偷点与偷看；
/// 3. 机主可随时按 Cmd + Option + Shift + P 一键紧急退出防窥。
@MainActor
public final class MacPrivacyShield {
    private var windows: [NSWindow] = []
    public private(set) var isActive: Bool = false

    public var excludedWindowIDs: Set<CGWindowID> {
        Set(windows.map { CGWindowID($0.windowNumber) })
    }

    public init() {}

    public func activate(inputInjector: MacInputInjector) -> Set<CGWindowID> {
        if isActive { return excludedWindowIDs }
        var created: [NSWindow] = []
        for scr in NSScreen.screens {
            let win = NSWindow(
                contentRect: scr.frame,
                styleMask: [.borderless],
                backing: .buffered,
                defer: false
            )
            win.setFrame(scr.frame, display: true)
            win.backgroundColor = .black
            win.isOpaque = true
            win.level = .screenSaver
            win.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
            win.ignoresMouseEvents = false

            let label = NSTextField(labelWithString: "BoBo远程 · 防窥保护已开启（机主紧急退出：⌘ + ⌥ + ⇧ + P）")
            label.textColor = NSColor(white: 0.22, alpha: 1.0)
            label.font = NSFont.systemFont(ofSize: 15, weight: .medium)
            label.alignment = .center
            label.translatesAutoresizingMaskIntoConstraints = false

            let container = NSView(frame: scr.frame)
            container.wantsLayer = true
            container.layer?.backgroundColor = NSColor.black.cgColor
            container.addSubview(label)
            NSLayoutConstraint.activate([
                label.centerXAnchor.constraint(equalTo: container.centerXAnchor),
                label.centerYAnchor.constraint(equalTo: container.centerYAnchor)
            ])
            win.contentView = container
            win.orderFrontRegardless()
            created.append(win)
        }
        windows = created
        isActive = true
        inputInjector.setPrivacyInputBlock(true)
        return excludedWindowIDs
    }

    public func deactivate(inputInjector: MacInputInjector) {
        inputInjector.setPrivacyInputBlock(false)
        for win in windows {
            win.orderOut(nil)
            win.close()
        }
        windows.removeAll()
        isActive = false
    }
}
