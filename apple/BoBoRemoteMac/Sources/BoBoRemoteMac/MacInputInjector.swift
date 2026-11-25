import ApplicationServices
import BoBoAppleCore
import Foundation

public final class MacInputInjector: @unchecked Sendable {
    private static let injectedEventMarker: Int64 = 0x424F424F5636

    private enum MouseButton: Int16, CaseIterable {
        case left = 1, middle, right

        var quartzButton: CGMouseButton {
            switch self {
            case .left: return .left
            case .middle: return .center
            case .right: return .right
            }
        }

        var dragEvent: CGEventType {
            switch self {
            case .left: return .leftMouseDragged
            case .middle: return .otherMouseDragged
            case .right: return .rightMouseDragged
            }
        }

        func event(down: Bool) -> CGEventType {
            switch self {
            case .left: return down ? .leftMouseDown : .leftMouseUp
            case .middle: return down ? .otherMouseDown : .otherMouseUp
            case .right: return down ? .rightMouseDown : .rightMouseUp
            }
        }
    }

    private struct Click {
        let time: TimeInterval
        let position: CGPoint
        let count: Int64
    }

    private let lock = NSLock()
    private let eventSource = CGEventSource(stateID: .privateState)
    private let doubleClickInterval: TimeInterval
    private var displayID = CGMainDisplayID()
    private var mapControlToCommand = false
    private var mousePosition: CGPoint?
    private var buttonsDown: [MouseButton: Int64] = [:]
    private var recentClicks: [MouseButton: Click] = [:]
    private var keysDown: Set<CGKeyCode> = []
    private var verticalWheelRemainder: Int32 = 0
    private var horizontalWheelRemainder: Int32 = 0

    // The event tap and its callback run on the main run loop.
    private var eventTap: CFMachPort?
    private var tapRunLoopSource: CFRunLoopSource?
    @MainActor public var onPrivacyEscapePressed: (() -> Void)?

    public init(doubleClickInterval: TimeInterval = 0.5) {
        self.doubleClickInterval = doubleClickInterval
    }

    public static func hasAccessibilityPermission(prompt: Bool = false) -> Bool {
        let opts = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: prompt] as CFDictionary
        return AXIsProcessTrustedWithOptions(opts)
    }

    func configure(displayID: CGDirectDisplayID, mapControlToCommand: Bool) {
        lock.withLock {
            releaseAllLocked()
            self.displayID = displayID
            self.mapControlToCommand = mapControlToCommand
            mousePosition = nil
        }
    }

    public func inject(_ packet: BoBoInputPacket) {
        lock.withLock {
            switch packet.kind {
            case BoBoProtocol.inputKindMouseAbs:
                moveAbsolute(packet)
            case BoBoProtocol.inputKindMouseRel:
                let bounds = CGDisplayBounds(displayID)
                guard !bounds.isEmpty else { return }
                let position = currentPosition(in: bounds)
                moveMouse(clamp(CGPoint(x: position.x + CGFloat(packet.a), y: position.y + CGFloat(packet.b)), to: bounds))
            case BoBoProtocol.inputKindMouseBtn:
                guard let button = MouseButton(rawValue: packet.a) else { return }
                setMouseButton(button, down: (packet.flags & 1) != 0)
            case BoBoProtocol.inputKindMouseWheel:
                let horizontal = (packet.flags & 1) != 0
                let delta = Int32(packet.a)
                let total = delta + (horizontal ? horizontalWheelRemainder : verticalWheelRemainder)
                if horizontal { horizontalWheelRemainder = total % 3 }
                else { verticalWheelRemainder = total % 3 }
                let pixels = total / 3
                guard pixels != 0 else { return }
                post(CGEvent(scrollWheelEvent2Source: eventSource, units: .pixel, wheelCount: horizontal ? 2 : 1,
                             wheel1: horizontal ? 0 : pixels, wheel2: horizontal ? pixels : 0, wheel3: 0), flags: modifierFlags)
            case BoBoProtocol.inputKindKey:
                guard let key = WindowsVkMap.winVkToMacKeyCode(UInt8(truncatingIfNeeded: packet.a), mapControlToCommand: mapControlToCommand) else { return }
                setKey(key, down: (packet.flags & 1) != 0)
            case BoBoProtocol.inputKindReleaseAll:
                releaseAllLocked()
            case BoBoProtocol.inputKindTouch:
                guard packet.flags >> 4 == 0 else { return }
                let action = packet.flags & 0x0F
                moveAbsolute(packet)
                if action == BoBoProtocol.touchActionDown { setMouseButton(.left, down: true) }
                if action == BoBoProtocol.touchActionUp || action == BoBoProtocol.touchActionCancel { setMouseButton(.left, down: false) }
            default:
                break
            }
        }
    }

    public func releaseAll() {
        lock.withLock { releaseAllLocked() }
    }

    public func injectUnicodeText(_ text: String) {
        lock.withLock {
            var chunk: [UInt16] = []
            chunk.reserveCapacity(16)
            for scalar in text.unicodeScalars {
                let units = String(scalar).utf16
                if chunk.count + units.count > 16 {
                    postUnicode(chunk)
                    chunk.removeAll(keepingCapacity: true)
                }
                chunk.append(contentsOf: units)
            }
            postUnicode(chunk)
        }
    }

    func lockWorkstation() {
        lock.withLock {
            releaseAllLocked()
            // The macOS Lock Screen shortcut is Control-Command-Q.
            let shortcut: [CGKeyCode] = [0x3B, 0x37, 0x0C]
            for key in shortcut { setKey(key, down: true) }
            for key in shortcut.reversed() { setKey(key, down: false) }
        }
    }

    private func releaseAllLocked() {
        for button in MouseButton.allCases where buttonsDown[button] != nil {
            setMouseButton(button, down: false)
        }
        for key in keysDown.sorted() { setKey(key, down: false) }
        recentClicks.removeAll()
        verticalWheelRemainder = 0
        horizontalWheelRemainder = 0
    }

    private func moveAbsolute(_ packet: BoBoInputPacket) {
        let bounds = CGDisplayBounds(displayID)
        guard !bounds.isEmpty else { return }
        let x = CGFloat(UInt16(bitPattern: packet.a)) / 65_535
        let y = CGFloat(UInt16(bitPattern: packet.b)) / 65_535
        moveMouse(CGPoint(x: bounds.minX + x * (bounds.width - 1), y: bounds.minY + y * (bounds.height - 1)))
    }

    private func currentPosition(in bounds: CGRect) -> CGPoint {
        let center = CGPoint(x: bounds.midX, y: bounds.midY)
        return clamp(mousePosition ?? CGEvent(source: nil)?.location ?? center, to: bounds)
    }

    private func clamp(_ point: CGPoint, to bounds: CGRect) -> CGPoint {
        CGPoint(x: max(bounds.minX, min(bounds.maxX - 1, point.x)),
                y: max(bounds.minY, min(bounds.maxY - 1, point.y)))
    }

    private func moveMouse(_ point: CGPoint) {
        mousePosition = point
        if let button = MouseButton.allCases.first(where: { buttonsDown[$0] != nil }) {
            postMouse(type: button.dragEvent, position: point, button: button, clickCount: buttonsDown[button] ?? 1)
        } else {
            postMouse(type: .mouseMoved, position: point, button: .left)
        }
    }

    private func setMouseButton(_ button: MouseButton, down: Bool) {
        if down {
            let bounds = CGDisplayBounds(displayID)
            guard !bounds.isEmpty, buttonsDown[button] == nil else { return }
            let position = currentPosition(in: bounds)
            let now = ProcessInfo.processInfo.systemUptime
            var count: Int64 = 1
            if let previous = recentClicks[button], now - previous.time <= doubleClickInterval,
               abs(position.x - previous.position.x) <= 4, abs(position.y - previous.position.y) <= 4 {
                count = min(previous.count + 1, 3)
            }
            recentClicks[button] = Click(time: now, position: position, count: count)
            buttonsDown[button] = count
            mousePosition = position
            postMouse(type: button.event(down: true), position: position, button: button, clickCount: count)
        } else if let count = buttonsDown.removeValue(forKey: button), let position = mousePosition {
            postMouse(type: button.event(down: false), position: position, button: button, clickCount: count)
        }
    }

    private func postMouse(type: CGEventType, position: CGPoint, button: MouseButton, clickCount: Int64 = 0) {
        let event = CGEvent(mouseEventSource: eventSource, mouseType: type, mouseCursorPosition: position, mouseButton: button.quartzButton)
        event?.setIntegerValueField(.mouseEventClickState, value: clickCount)
        post(event, flags: modifierFlags)
    }

    private func setKey(_ key: CGKeyCode, down: Bool) {
        if down { keysDown.insert(key) }
        else if keysDown.remove(key) == nil { return }
        let event = CGEvent(keyboardEventSource: eventSource, virtualKey: key, keyDown: down)
        if Self.modifierFlag(for: key) != nil { event?.type = .flagsChanged }
        post(event, flags: modifierFlags)
    }

    private static func modifierFlag(for key: CGKeyCode) -> CGEventFlags? {
        switch key {
        case 0x38, 0x3C: return .maskShift
        case 0x3B, 0x3E: return .maskControl
        case 0x3A, 0x3D: return .maskAlternate
        case 0x37, 0x36: return .maskCommand
        default: return nil
        }
    }

    private var modifierFlags: CGEventFlags {
        keysDown.reduce(into: []) { flags, key in
            if let flag = Self.modifierFlag(for: key) { flags.insert(flag) }
        }
    }

    private func postUnicode(_ chunk: [UInt16]) {
        guard !chunk.isEmpty else { return }
        for keyDown in [true, false] {
            let event = CGEvent(keyboardEventSource: eventSource, virtualKey: 0, keyDown: keyDown)
            event?.keyboardSetUnicodeString(stringLength: chunk.count, unicodeString: chunk)
            post(event, flags: [])
        }
    }

    private func post(_ event: CGEvent?, flags: CGEventFlags) {
        guard let event else { return }
        event.flags = flags
        event.setIntegerValueField(.eventSourceUserData, value: Self.injectedEventMarker)
        event.post(tap: .cghidEventTap)
    }

    @MainActor @discardableResult
    public func setPrivacyInputBlock(_ enabled: Bool) -> Bool {
        if !enabled {
            stopPrivacyTap()
            return false
        }
        guard Self.hasAccessibilityPermission() else { return false }
        if let eventTap { return CGEvent.tapIsEnabled(tap: eventTap) }

        let eventTypes: [CGEventType] = [
            .keyDown, .keyUp, .flagsChanged, .mouseMoved, .scrollWheel,
            .leftMouseDown, .leftMouseUp, .leftMouseDragged,
            .rightMouseDown, .rightMouseUp, .rightMouseDragged,
            .otherMouseDown, .otherMouseUp, .otherMouseDragged
        ]
        let mask = eventTypes.reduce(CGEventMask(0)) { $0 | (CGEventMask(1) << $1.rawValue) }
        guard let tap = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap,
                                         options: .defaultTap, eventsOfInterest: mask,
                                         callback: Self.privacyTapCallback,
                                         userInfo: Unmanaged.passUnretained(self).toOpaque()) else { return false }
        guard let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0) else {
            CFMachPortInvalidate(tap)
            return false
        }
        eventTap = tap
        tapRunLoopSource = source
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        guard CGEvent.tapIsEnabled(tap: tap) else {
            stopPrivacyTap()
            return false
        }
        return true
    }

    private static let privacyTapCallback: CGEventTapCallBack = { _, type, event, userInfo in
        guard let userInfo else { return Unmanaged.passUnretained(event) }
        let injector = Unmanaged<MacInputInjector>.fromOpaque(userInfo).takeUnretainedValue()
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            if let tap = injector.eventTap, MacInputInjector.hasAccessibilityPermission() {
                CGEvent.tapEnable(tap: tap, enable: true)
                if CGEvent.tapIsEnabled(tap: tap) { return nil }
            }
            DispatchQueue.main.async { injector.onPrivacyEscapePressed?() }
            return nil
        }
        if event.getIntegerValueField(.eventSourceUserData) == MacInputInjector.injectedEventMarker {
            return Unmanaged.passUnretained(event)
        }
        if type == .keyDown, event.getIntegerValueField(.keyboardEventKeycode) == 0x23,
           event.flags.contains([.maskCommand, .maskAlternate, .maskShift]) {
            DispatchQueue.main.async { injector.onPrivacyEscapePressed?() }
        }
        return nil
    }

    @MainActor private func stopPrivacyTap() {
        let tap = eventTap
        eventTap = nil
        if let tap {
            CGEvent.tapEnable(tap: tap, enable: false)
            CFMachPortInvalidate(tap)
        }
        if let source = tapRunLoopSource { CFRunLoopRemoveSource(CFRunLoopGetMain(), source, .commonModes) }
        tapRunLoopSource = nil
    }
}
