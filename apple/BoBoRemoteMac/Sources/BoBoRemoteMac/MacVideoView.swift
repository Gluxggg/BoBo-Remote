import AppKit
import AVFoundation
import BoBoAppleCore
import CoreMedia
import SwiftUI

/// macOS 零拷贝 GPU 视频画面承载视图（AVSampleBufferDisplayLayer + 键鼠捕获 + 框内隐藏本机大鼠标）。
public struct MacRemoteVideoView: NSViewRepresentable {
    public let displayLayer: AVSampleBufferDisplayLayer
    public let scaleMode: Int // 2=SuperFill无黑边, 1=等比居中, 0=1:1
    public let viewOnly: Bool
    public let mapCommandToControl: Bool
    public let onInput: (BoBoInputPacket) -> Void

    public func makeNSView(context: Context) -> VideoCanvasNSView {
        let view = VideoCanvasNSView(displayLayer: displayLayer)
        view.onInput = onInput
        view.viewOnly = viewOnly
        view.mapCommandToControl = mapCommandToControl
        view.applyScaleMode(scaleMode)
        return view
    }

    public func updateNSView(_ nsView: VideoCanvasNSView, context: Context) {
        nsView.onInput = onInput
        nsView.viewOnly = viewOnly
        nsView.mapCommandToControl = mapCommandToControl
        nsView.applyScaleMode(scaleMode)
    }
}

public final class VideoCanvasNSView: NSView {
    private let videoLayer: AVSampleBufferDisplayLayer
    private var trackingAreaRef: NSTrackingArea?
    private var cursorHiddenInside = false

    public var onInput: ((BoBoInputPacket) -> Void)?
    public var viewOnly: Bool = false
    public var mapCommandToControl: Bool = true

    public init(displayLayer: AVSampleBufferDisplayLayer) {
        self.videoLayer = displayLayer
        super.init(frame: .zero)
        wantsLayer = true
        layer?.backgroundColor = NSColor(red: 0.055, green: 0.082, blue: 0.125, alpha: 1.0).cgColor
        videoLayer.videoGravity = .resize
        layer?.addSublayer(videoLayer)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) not supported") }

    public override var acceptsFirstResponder: Bool { true }

    public override func layout() {
        super.layout()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        videoLayer.frame = bounds
        CATransaction.commit()
    }

    public func applyScaleMode(_ mode: Int) {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        videoLayer.videoGravity = (mode == 2) ? .resize : .resizeAspect
        CATransaction.commit()
    }

    public override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let old = trackingAreaRef { removeTrackingArea(old) }
        let area = NSTrackingArea(
            rect: bounds,
            options: [.mouseEnteredAndExited, .mouseMoved, .activeInKeyWindow, .inVisibleRect],
            owner: self,
            userInfo: nil
        )
        addTrackingArea(area)
        trackingAreaRef = area
    }

    public override func mouseEntered(with event: NSEvent) {
        if !viewOnly && !cursorHiddenInside {
            NSCursor.hide()
            cursorHiddenInside = true
        }
        window?.makeFirstResponder(self)
    }

    public override func mouseExited(with event: NSEvent) {
        if cursorHiddenInside {
            NSCursor.unhide()
            cursorHiddenInside = false
        }
    }

    deinit {
        if cursorHiddenInside { NSCursor.unhide() }
    }

    public override func mouseMoved(with event: NSEvent) { sendAbsMouse(event) }
    public override func mouseDragged(with event: NSEvent) { sendAbsMouse(event) }
    public override func rightMouseDragged(with event: NSEvent) { sendAbsMouse(event) }
    public override func otherMouseDragged(with event: NSEvent) { sendAbsMouse(event) }

    public override func mouseDown(with event: NSEvent) {
        window?.makeFirstResponder(self)
        sendAbsMouse(event)
        guard !viewOnly else { return }
        onInput?(BoBoProtocol.buildMouseButton(button: 1, down: true))
    }

    public override func mouseUp(with event: NSEvent) {
        sendAbsMouse(event)
        guard !viewOnly else { return }
        onInput?(BoBoProtocol.buildMouseButton(button: 1, down: false))
    }

    public override func rightMouseDown(with event: NSEvent) {
        sendAbsMouse(event)
        guard !viewOnly else { return }
        onInput?(BoBoProtocol.buildMouseButton(button: 3, down: true))
    }

    public override func rightMouseUp(with event: NSEvent) {
        sendAbsMouse(event)
        guard !viewOnly else { return }
        onInput?(BoBoProtocol.buildMouseButton(button: 3, down: false))
    }

    public override func otherMouseDown(with event: NSEvent) {
        sendAbsMouse(event)
        guard !viewOnly else { return }
        onInput?(BoBoProtocol.buildMouseButton(button: 2, down: true))
    }

    public override func otherMouseUp(with event: NSEvent) {
        sendAbsMouse(event)
        guard !viewOnly else { return }
        onInput?(BoBoProtocol.buildMouseButton(button: 2, down: false))
    }

    public override func scrollWheel(with event: NSEvent) {
        guard !viewOnly else { return }
        let dy = Int((event.scrollingDeltaY * (event.hasPreciseScrollingDeltas ? 2.5 : 40.0)).rounded())
        if dy != 0 {
            onInput?(BoBoProtocol.buildMouseWheel(delta: dy, horizontal: false))
        }
        let dx = Int((event.scrollingDeltaX * (event.hasPreciseScrollingDeltas ? 2.5 : 40.0)).rounded())
        if dx != 0 {
            onInput?(BoBoProtocol.buildMouseWheel(delta: dx, horizontal: true))
        }
    }

    public override func keyDown(with event: NSEvent) {
        guard !viewOnly else { return }
        if let mapped = WindowsVkMap.macKeyCodeToWinVk(event.keyCode, mapCommandToControl: mapCommandToControl) {
            onInput?(BoBoProtocol.buildKey(vk: mapped.vk, down: true, extended: mapped.extended))
        }
    }

    public override func keyUp(with event: NSEvent) {
        guard !viewOnly else { return }
        if let mapped = WindowsVkMap.macKeyCodeToWinVk(event.keyCode, mapCommandToControl: mapCommandToControl) {
            onInput?(BoBoProtocol.buildKey(vk: mapped.vk, down: false, extended: mapped.extended))
        }
    }

    public override func flagsChanged(with event: NSEvent) {
        guard !viewOnly else { return }
        if let mapped = WindowsVkMap.macKeyCodeToWinVk(event.keyCode, mapCommandToControl: mapCommandToControl) {
            let down: Bool
            switch event.keyCode {
            case 0x38, 0x3C: down = event.modifierFlags.contains(.shift)
            case 0x3B, 0x3E: down = event.modifierFlags.contains(.control)
            case 0x3A, 0x3D: down = event.modifierFlags.contains(.option)
            case 0x37, 0x36: down = event.modifierFlags.contains(.command)
            default: down = false
            }
            onInput?(BoBoProtocol.buildKey(vk: mapped.vk, down: down, extended: mapped.extended))
        }
    }

    private func sendAbsMouse(_ event: NSEvent) {
        guard !viewOnly, bounds.width > 1, bounds.height > 1 else { return }
        let pt = convert(event.locationInWindow, from: nil)
        let nx = max(0.0, min(1.0, pt.x / (bounds.width - 1)))
        // Flip AppKit bottom-left Y to top-left remote Y
        let ny = max(0.0, min(1.0, 1.0 - (pt.y / (bounds.height - 1))))
        onInput?(BoBoProtocol.buildMouseAbs(nx: UInt16((nx * 65535.0).rounded()), ny: UInt16((ny * 65535.0).rounded())))
    }
}
