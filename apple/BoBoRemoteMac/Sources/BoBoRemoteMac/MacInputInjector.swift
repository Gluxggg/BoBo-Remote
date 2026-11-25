import AppKit
import ApplicationServices
import BoBoAppleCore
import CoreGraphics
import Foundation

/// macOS 远程输入注入器 + 防窥物理键鼠锁（CGEvent + CGEventTap）。
/// - 所有远程注入事件都打上 kBoBoInjectedMagic 标签，防窥模式下的 CGEventTap 100% 吞掉本地物理键鼠但放行远程注入输入！
/// - 紧急退出防窥热键：Cmd + Option + Shift + P
public final class MacInputInjector: @unchecked Sendable {
    public static let kBoBoInjectedMagic: Int64 = 0x424F424F5636

    private let lock = NSLock()
    private var currentMousePos: CGPoint = CGPoint(x: 400, y: 400)
    private var leftButtonDown = false
    private var rightButtonDown = false
    private var activeFlags: CGEventFlags = []

    private var eventTap: CFMachPort?
    private var tapRunLoopSource: CFRunLoopSource?
    public var onPrivacyEscapePressed: (() -> Void)?
    public var mapControlToCommand: Bool = true

    public init() {}

    public static func hasAccessibilityPermission(prompt: Bool = false) -> Bool {
        let opts = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: prompt] as CFDictionary
        return AXIsProcessTrustedWithOptions(opts)
    }

    public func inject(_ pkt: BoBoInputPacket, monitorIndex: Int = 0) {
        let screens = NSScreen.screens
        let targetScreen = (monitorIndex >= 0 && monitorIndex < screens.count) ? screens[monitorIndex] : NSScreen.main
        let bounds = targetScreen?.frame ?? CGRect(x: 0, y: 0, width: 1920, height: 1080)
        let primaryHeight = screens.first?.frame.height ?? bounds.height

        switch pkt.kind {
        case BoBoProtocol.inputKindMouseAbs:
            let nx = Double(UInt16(bitPattern: pkt.a)) / 65535.0
            let ny = Double(UInt16(bitPattern: pkt.b)) / 65535.0
            let cgX = bounds.origin.x + nx * (bounds.width - 1)
            let screenTopCgY = primaryHeight - (bounds.origin.y + bounds.height)
            let cgY = screenTopCgY + ny * (bounds.height - 1)
            let pt = CGPoint(x: cgX, y: cgY)
            lock.lock()
            currentMousePos = pt
            let isLeftDown = leftButtonDown
            let isRightDown = rightButtonDown
            lock.unlock()
            let moveType: CGEventType = isLeftDown ? .leftMouseDragged : (isRightDown ? .rightMouseDragged : .mouseMoved)
            postMouse(type: moveType, point: pt, button: isLeftDown ? .left : (isRightDown ? .right : .left))

        case BoBoProtocol.inputKindMouseRel:
            lock.lock()
            let next = CGPoint(
                x: max(bounds.minX, min(bounds.maxX - 1, currentMousePos.x + CGFloat(pkt.a))),
                y: max(0, min(primaryHeight - 1, currentMousePos.y + CGFloat(pkt.b)))
            )
            currentMousePos = next
            let isLeftDown = leftButtonDown
            lock.unlock()
            postMouse(type: isLeftDown ? .leftMouseDragged : .mouseMoved, point: next, button: .left)

        case BoBoProtocol.inputKindMouseBtn:
            let down = (pkt.flags & 1) != 0
            lock.lock()
            let pt = currentMousePos
            if pkt.a == 1 { leftButtonDown = down }
            if pkt.a == 3 { rightButtonDown = down }
            lock.unlock()
            switch pkt.a {
            case 1: postMouse(type: down ? .leftMouseDown : .leftMouseUp, point: pt, button: .left)
            case 2: postMouse(type: down ? .otherMouseDown : .otherMouseUp, point: pt, button: .center)
            case 3: postMouse(type: down ? .rightMouseDown : .rightMouseUp, point: pt, button: .right)
            default: break
            }

        case BoBoProtocol.inputKindMouseWheel:
            let horiz = (pkt.flags & 1) != 0
            let ticks = Int32(pkt.a) / 24
            if let ev = CGEvent(scrollWheelEvent2Source: nil, units: .pixel, wheelCount: horiz ? 2 : 1, wheel1: horiz ? 0 : ticks * 8, wheel2: horiz ? ticks * 8 : 0, wheel3: 0) {
                ev.setIntegerValueField(.eventSourceUserData, value: Self.kBoBoInjectedMagic)
                ev.post(tap: .cghidEventTap)
            }

        case BoBoProtocol.inputKindKey:
            let vk = UInt8(truncatingIfNeeded: pkt.a)
            let down = (pkt.flags & 1) != 0
            updateModifierFlag(vk: vk, down: down)
            if let cgCode = WindowsVkMap.winVkToMacKeyCode(vk, mapControlToCommand: mapControlToCommand),
               let ev = CGEvent(keyboardEventSource: nil, virtualKey: cgCode, keyDown: down) {
                lock.lock()
                ev.flags = activeFlags
                lock.unlock()
                ev.setIntegerValueField(.eventSourceUserData, value: Self.kBoBoInjectedMagic)
                ev.post(tap: .cghidEventTap)
            }

        case BoBoProtocol.inputKindReleaseAll:
            lock.lock()
            activeFlags = []
            lock.unlock()
            for code: UInt16 in [0x38, 0x3C, 0x3B, 0x3E, 0x3A, 0x3D, 0x37, 0x36] {
                if let ev = CGEvent(keyboardEventSource: nil, virtualKey: code, keyDown: false) {
                    ev.setIntegerValueField(.eventSourceUserData, value: Self.kBoBoInjectedMagic)
                    ev.post(tap: .cghidEventTap)
                }
            }

        case BoBoProtocol.inputKindTouch:
            let pointerId = (pkt.flags >> 4) & 0x0F
            let action = pkt.flags & 0x0F
            guard pointerId == 0 else { return }
            inject(BoBoProtocol.buildMouseAbs(nx: UInt16(bitPattern: pkt.a), ny: UInt16(bitPattern: pkt.b)), monitorIndex: monitorIndex)
            if action == BoBoProtocol.touchActionDown {
                inject(BoBoProtocol.buildMouseButton(button: 1, down: true), monitorIndex: monitorIndex)
            } else if action == BoBoProtocol.touchActionUp || action == BoBoProtocol.touchActionCancel {
                inject(BoBoProtocol.buildMouseButton(button: 1, down: false), monitorIndex: monitorIndex)
            }

        default:
            break
        }
    }

    public func injectUnicodeText(_ text: String) {
        let utf16 = Array(text.utf16)
        guard !utf16.isEmpty else { return }
        if let down = CGEvent(keyboardEventSource: nil, virtualKey: 0, keyDown: true),
           let up = CGEvent(keyboardEventSource: nil, virtualKey: 0, keyDown: false) {
            down.keyboardSetUnicodeString(stringLength: utf16.count, unicodeString: utf16)
            up.keyboardSetUnicodeString(stringLength: utf16.count, unicodeString: utf16)
            down.setIntegerValueField(.eventSourceUserData, value: Self.kBoBoInjectedMagic)
            up.setIntegerValueField(.eventSourceUserData, value: Self.kBoBoInjectedMagic)
            down.post(tap: .cghidEventTap)
            up.post(tap: .cghidEventTap)
        }
    }

    // MARK: - Privacy Physical Input Lock (CGEventTap)

    public func setPrivacyInputBlock(_ enabled: Bool) {
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            if enabled {
                self.startEventTapOnMain()
            } else {
                self.stopEventTapOnMain()
            }
        }
    }

    private func startEventTapOnMain() {
        if eventTap != nil { return }
        let mask: CGEventMask =
            (1 << CGEventType.keyDown.rawValue) |
            (1 << CGEventType.keyUp.rawValue) |
            (1 << CGEventType.flagsChanged.rawValue) |
            (1 << CGEventType.leftMouseDown.rawValue) |
            (1 << CGEventType.leftMouseUp.rawValue) |
            (1 << CGEventType.rightMouseDown.rawValue) |
            (1 << CGEventType.rightMouseUp.rawValue) |
            (1 << CGEventType.mouseMoved.rawValue) |
            (1 << CGEventType.leftMouseDragged.rawValue) |
            (1 << CGEventType.scrollWheel.rawValue)

        let callback: CGEventTapCallBack = { _, type, event, refcon in
            if event.getIntegerValueField(.eventSourceUserData) == MacInputInjector.kBoBoInjectedMagic {
                return Unmanaged.passUnretained(event)
            }
            // Emergency owner escape: Cmd + Option + Shift + P (keyCode 0x23)
            if type == .keyDown {
                let code = event.getIntegerValueField(.keyboardEventKeycode)
                let flags = event.flags
                if code == 0x23 && flags.contains([.maskCommand, .maskAlternate, .maskShift]) {
                    if let refcon {
                        let inj = Unmanaged<MacInputInjector>.fromOpaque(refcon).takeUnretainedValue()
                        DispatchQueue.main.async { inj.onPrivacyEscapePressed?() }
                    }
                }
            }
            return nil // Block physical local input!
        }

        guard let tap = CGEvent.tapCreate(
            tap: .cgSessionEventTap,
            place: .headInsertEventTap,
            options: .defaultTap,
            eventsOfInterest: mask,
            callback: callback,
            userInfo: Unmanaged.passUnretained(self).toOpaque()
        ) else { return }

        let src = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        CFRunLoopAddSource(CFRunLoopGetMain(), src, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        eventTap = tap
        tapRunLoopSource = src
    }

    private func stopEventTapOnMain() {
        if let tap = eventTap {
            CGEvent.tapEnable(tap: tap, enable: false)
        }
        if let src = tapRunLoopSource {
            CFRunLoopRemoveSource(CFRunLoopGetMain(), src, .commonModes)
        }
        eventTap = nil
        tapRunLoopSource = nil
    }

    private func postMouse(type: CGEventType, point: CGPoint, button: CGMouseButton) {
        if let ev = CGEvent(mouseEventSource: nil, mouseType: type, mouseCursorPosition: point, mouseButton: button) {
            ev.setIntegerValueField(.eventSourceUserData, value: Self.kBoBoInjectedMagic)
            ev.post(tap: .cghidEventTap)
        }
    }

    private func updateModifierFlag(vk: UInt8, down: Bool) {
        lock.lock()
        defer { lock.unlock() }
        let flag: CGEventFlags?
        switch vk {
        case 0x10, 0xA0, 0xA1: flag = .maskShift
        case 0x11, 0xA2, 0xA3: flag = mapControlToCommand ? .maskCommand : .maskControl
        case 0x12, 0xA4, 0xA5: flag = .maskAlternate
        case 0x5B, 0x5C: flag = .maskCommand
        default: flag = nil
        }
        if let flag {
            if down { activeFlags.insert(flag) } else { activeFlags.remove(flag) }
        }
    }
}
