import AppKit
import CoreGraphics

@MainActor
public final class MacPrivacyShield {
    private var windows: [NSWindow] = []
    public private(set) var isActive = false

    public var excludedWindowIDs: Set<CGWindowID> {
        Set(windows.compactMap { CGWindowID(exactly: $0.windowNumber) }.filter { $0 != 0 })
    }

    public init() {}

    // The capture filter must exclude these windows before they are shown.
    func prepare() -> Set<CGWindowID> {
        if windows.isEmpty {
            windows = NSScreen.screens.map { screen in
                let window = NSWindow(contentRect: screen.frame, styleMask: .borderless, backing: .buffered, defer: false)
                window.setFrame(screen.frame, display: false)
                window.backgroundColor = .black
                window.isOpaque = true
                window.isReleasedWhenClosed = false
                window.canHide = false
                window.level = .screenSaver
                window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
                window.ignoresMouseEvents = true
                window.sharingType = .none

                let container = NSView(frame: CGRect(origin: .zero, size: screen.frame.size))
                let label = NSTextField(labelWithString: "BoBo远程 · 防窥保护已开启（本机退出：⌘⌥⇧P）")
                label.textColor = NSColor(white: 0.55, alpha: 1)
                label.font = .systemFont(ofSize: 15, weight: .medium)
                label.alignment = .center
                label.translatesAutoresizingMaskIntoConstraints = false
                container.addSubview(label)
                NSLayoutConstraint.activate([
                    label.centerXAnchor.constraint(equalTo: container.centerXAnchor),
                    label.centerYAnchor.constraint(equalTo: container.centerYAnchor)
                ])
                window.contentView = container
                return window
            }
        }
        let ids = excludedWindowIDs
        return ids.count == windows.count ? ids : []
    }

    func activate(inputInjector: MacInputInjector) -> Bool {
        if isActive { return true }
        guard !windows.isEmpty, inputInjector.setPrivacyInputBlock(true) else { return false }
        for window in windows { window.orderFrontRegardless() }
        isActive = true
        return true
    }

    public func deactivate(inputInjector: MacInputInjector) {
        inputInjector.setPrivacyInputBlock(false)
        for window in windows {
            window.orderOut(nil)
            window.close()
        }
        windows.removeAll()
        isActive = false
    }
}
