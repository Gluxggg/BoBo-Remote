import AppKit
import AVFoundation
import BoBoAppleCore
import SwiftUI

public enum MacVideoScale: CaseIterable, Hashable {
    case fit, fill, actualSize
}

@MainActor
public struct MacRemoteVideoView: NSViewRepresentable {
    public let displayLayer: AVSampleBufferDisplayLayer
    public let videoSize: CGSize
    public let scale: MacVideoScale
    public let inputEnabled: Bool
    public let hostCursorComposited: Bool
    public let mapCommandToControl: Bool
    public let onInput: (BoBoInputPacket) -> Void
    public let onClose: () -> Void

    public func makeNSView(context: Context) -> VideoCanvasNSView {
        let view = VideoCanvasNSView(displayLayer: displayLayer)
        updateNSView(view, context: context)
        return view
    }

    public func updateNSView(_ view: VideoCanvasNSView, context: Context) {
        view.onInput = onInput
        view.onClose = onClose
        view.configure(videoSize: videoSize, scale: scale, inputEnabled: inputEnabled,
                       hostCursorComposited: hostCursorComposited, mapCommandToControl: mapCommandToControl)
    }

    public static func dismantleNSView(_ view: VideoCanvasNSView, coordinator: Void) {
        view.releaseInput()
    }
}

public final class VideoCanvasNSView: NSView {
    private static let transparentCursor = NSCursor(image: NSImage(size: NSSize(width: 1, height: 1), flipped: false) { _ in true }, hotSpot: .zero)
    private static let modifierKeys: [(flag: NSEvent.ModifierFlags, left: UInt16, right: UInt16)] = [
        (.shift, 0x38, 0x3C), (.control, 0x3B, 0x3E), (.option, 0x3A, 0x3D), (.command, 0x37, 0x36)
    ]

    private let videoLayer: AVSampleBufferDisplayLayer
    private var tracking: NSTrackingArea?
    private var originalMouseMovedSetting: Bool?
    private var videoSize = CGSize.zero
    private var scale = MacVideoScale.fit
    private var inputEnabled = false
    private var hostCursorComposited = false
    private var mapCommandToControl = false
    private var keysDown: [UInt16: (vk: UInt8, extended: Bool)] = [:]
    private var mouseButtonsDown: Set<Int> = []
    private var scrollX: CGFloat = 0
    private var scrollY: CGFloat = 0

    var onInput: ((BoBoInputPacket) -> Void)?
    var onClose: (() -> Void)?

    public init(displayLayer: AVSampleBufferDisplayLayer) {
        videoLayer = displayLayer
        super.init(frame: .zero)
        wantsLayer = true
        layer?.backgroundColor = NSColor.black.cgColor
        layer?.masksToBounds = true
        videoLayer.videoGravity = .resize
        layer?.addSublayer(videoLayer)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) not supported") }

    public override var acceptsFirstResponder: Bool { true }

    func configure(videoSize: CGSize, scale: MacVideoScale, inputEnabled: Bool,
                   hostCursorComposited: Bool, mapCommandToControl: Bool) {
        if self.inputEnabled && (!inputEnabled || self.mapCommandToControl != mapCommandToControl) { releaseInput() }
        let geometryChanged = self.videoSize != videoSize || self.scale != scale
        let cursorChanged = self.hostCursorComposited != hostCursorComposited || self.inputEnabled != inputEnabled
        self.videoSize = videoSize
        self.scale = scale
        self.inputEnabled = inputEnabled
        self.hostCursorComposited = hostCursorComposited
        self.mapCommandToControl = mapCommandToControl
        if geometryChanged { needsLayout = true }
        if geometryChanged || cursorChanged { window?.invalidateCursorRects(for: self) }
    }

    private var videoRect: CGRect {
        guard videoSize.width > 0, videoSize.height > 0, !bounds.isEmpty else { return .zero }
        if scale == .fill { return bounds }
        let ratio: CGFloat
        if scale == .actualSize { ratio = 1 / (window?.backingScaleFactor ?? 1) }
        else { ratio = min(bounds.width / videoSize.width, bounds.height / videoSize.height) }
        let width = videoSize.width * ratio
        let height = videoSize.height * ratio
        return CGRect(x: bounds.midX - width / 2, y: bounds.midY - height / 2, width: width, height: height)
    }

    public override func layout() {
        super.layout()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        videoLayer.frame = videoSize == .zero ? bounds : videoRect
        CATransaction.commit()
        window?.invalidateCursorRects(for: self)
    }

    public override func viewDidChangeBackingProperties() {
        super.viewDidChangeBackingProperties()
        needsLayout = true
        window?.invalidateCursorRects(for: self)
    }

    public override func resetCursorRects() {
        super.resetCursorRects()
        let visibleVideo = videoRect.intersection(bounds)
        if inputEnabled && hostCursorComposited && !visibleVideo.isEmpty {
            addCursorRect(visibleVideo, cursor: Self.transparentCursor)
        }
    }

    public override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let tracking { removeTrackingArea(tracking) }
        let area = NSTrackingArea(rect: bounds, options: [.mouseMoved, .activeInKeyWindow, .inVisibleRect], owner: self, userInfo: nil)
        addTrackingArea(area)
        tracking = area
    }

    public override func viewWillMove(toWindow newWindow: NSWindow?) {
        if let window, window !== newWindow {
            releaseInput()
            NotificationCenter.default.removeObserver(self, name: NSWindow.didResignKeyNotification, object: window)
            if let originalMouseMovedSetting { window.acceptsMouseMovedEvents = originalMouseMovedSetting }
            originalMouseMovedSetting = nil
        }
        super.viewWillMove(toWindow: newWindow)
    }

    public override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        guard let window, originalMouseMovedSetting == nil else { return }
        originalMouseMovedSetting = window.acceptsMouseMovedEvents
        window.acceptsMouseMovedEvents = true
        NotificationCenter.default.addObserver(self, selector: #selector(windowResignedKey(_:)),
                                               name: NSWindow.didResignKeyNotification, object: window)
    }

    @objc private func windowResignedKey(_ notification: Notification) { releaseInput() }

    public override func resignFirstResponder() -> Bool {
        releaseInput()
        return super.resignFirstResponder()
    }

    func releaseInput() {
        if !keysDown.isEmpty || !mouseButtonsDown.isEmpty { onInput?(BoBoProtocol.buildReleaseAll()) }
        keysDown.removeAll()
        mouseButtonsDown.removeAll()
        scrollX = 0
        scrollY = 0
    }

    public override func mouseMoved(with event: NSEvent) { sendPosition(event) }
    public override func mouseDragged(with event: NSEvent) { drag(event, button: 1) }
    public override func rightMouseDragged(with event: NSEvent) { drag(event, button: 3) }
    public override func otherMouseDragged(with event: NSEvent) { drag(event, button: 2) }
    public override func mouseDown(with event: NSEvent) { mouseButton(event, button: 1, down: true) }
    public override func mouseUp(with event: NSEvent) { mouseButton(event, button: 1, down: false) }
    public override func rightMouseDown(with event: NSEvent) { mouseButton(event, button: 3, down: true) }
    public override func rightMouseUp(with event: NSEvent) { mouseButton(event, button: 3, down: false) }
    public override func otherMouseDown(with event: NSEvent) {
        if event.buttonNumber == 2 { mouseButton(event, button: 2, down: true) }
    }
    public override func otherMouseUp(with event: NSEvent) {
        if event.buttonNumber == 2 { mouseButton(event, button: 2, down: false) }
    }

    private func drag(_ event: NSEvent, button: Int) {
        guard mouseButtonsDown.contains(button) else { return }
        sendPosition(event, clampOutside: true)
    }

    private func mouseButton(_ event: NSEvent, button: Int, down: Bool) {
        if down {
            guard sendPosition(event) else { return }
            window?.makeFirstResponder(self)
            synchronizeModifiers(event.modifierFlags)
            mouseButtonsDown.insert(button)
        } else {
            guard mouseButtonsDown.remove(button) != nil else { return }
            sendPosition(event, clampOutside: true)
        }
        onInput?(BoBoProtocol.buildMouseButton(button: Int16(button), down: down))
    }

    public override func scrollWheel(with event: NSEvent) {
        guard sendPosition(event) else { return }
        let multiplier: CGFloat = event.hasPreciseScrollingDeltas ? 2.5 : 40
        scrollX += event.scrollingDeltaX * multiplier
        scrollY += event.scrollingDeltaY * multiplier
        let dx = Int(scrollX.rounded(.towardZero))
        let dy = Int(scrollY.rounded(.towardZero))
        scrollX -= CGFloat(dx)
        scrollY -= CGFloat(dy)
        if dx != 0 { onInput?(BoBoProtocol.buildMouseWheel(delta: dx, horizontal: true)) }
        if dy != 0 { onInput?(BoBoProtocol.buildMouseWheel(delta: dy, horizontal: false)) }
    }

    private var canCaptureKeyboard: Bool {
        inputEnabled && window?.isKeyWindow == true && window?.firstResponder === self
    }

    public override func performKeyEquivalent(with event: NSEvent) -> Bool {
        guard canCaptureKeyboard else { return super.performKeyEquivalent(with: event) }
        if handleCloseShortcut(event) { return true }
        guard forwardKeyDown(event) else { return super.performKeyEquivalent(with: event) }
        return true
    }

    public override func keyDown(with event: NSEvent) {
        guard canCaptureKeyboard else { super.keyDown(with: event); return }
        if handleCloseShortcut(event) { return }
        _ = forwardKeyDown(event)
    }

    public override func keyUp(with event: NSEvent) {
        if !sendKey(event.keyCode, down: false), !canCaptureKeyboard { super.keyUp(with: event) }
    }

    public override func flagsChanged(with event: NSEvent) {
        guard canCaptureKeyboard else { super.flagsChanged(with: event); return }
        if let group = Self.modifierKeys.first(where: { $0.left == event.keyCode || $0.right == event.keyCode }) {
            let down = event.modifierFlags.contains(group.flag) && keysDown[event.keyCode] == nil
            _ = sendKey(event.keyCode, down: down)
        } else if event.keyCode == 0x39 {
            _ = sendKey(event.keyCode, down: true)
            _ = sendKey(event.keyCode, down: false)
        }
    }

    private func handleCloseShortcut(_ event: NSEvent) -> Bool {
        let modifiers: NSEvent.ModifierFlags = [.control, .option, .shift]
        let relevant = event.modifierFlags.intersection([.control, .option, .shift, .command])
        guard event.keyCode == 0x35, relevant == modifiers else { return false }
        if !event.isARepeat {
            releaseInput()
            onClose?()
        }
        return true
    }

    private func forwardKeyDown(_ event: NSEvent) -> Bool {
        synchronizeModifiers(event.modifierFlags)
        return sendKey(event.keyCode, down: true)
    }

    private func synchronizeModifiers(_ flags: NSEvent.ModifierFlags) {
        for group in Self.modifierKeys {
            if flags.contains(group.flag) {
                if keysDown[group.left] == nil, keysDown[group.right] == nil {
                    let key = CGEventSource.keyState(.combinedSessionState, key: group.right) ? group.right : group.left
                    _ = sendKey(key, down: true)
                }
            } else {
                _ = sendKey(group.left, down: false)
                _ = sendKey(group.right, down: false)
            }
        }
    }

    @discardableResult private func sendKey(_ key: UInt16, down: Bool) -> Bool {
        if down {
            guard let mapped = WindowsVkMap.macKeyCodeToWinVk(key, mapCommandToControl: mapCommandToControl) else { return false }
            keysDown[key] = mapped
            onInput?(BoBoProtocol.buildKey(vk: mapped.vk, down: true, extended: mapped.extended))
        } else {
            guard let mapped = keysDown.removeValue(forKey: key) else { return false }
            onInput?(BoBoProtocol.buildKey(vk: mapped.vk, down: false, extended: mapped.extended))
        }
        return true
    }

    @discardableResult private func sendPosition(_ event: NSEvent, clampOutside: Bool = false) -> Bool {
        let rect = videoRect
        guard inputEnabled, !rect.isEmpty else { return false }
        let point = convert(event.locationInWindow, from: nil)
        guard clampOutside || rect.contains(point) else { return false }
        let x = min(1, max(0, (point.x - rect.minX) / rect.width))
        let y = min(1, max(0, (rect.maxY - point.y) / rect.height))
        onInput?(BoBoProtocol.buildMouseAbs(nx: UInt16((x * 65_535).rounded()), ny: UInt16((y * 65_535).rounded())))
        return true
    }
}
