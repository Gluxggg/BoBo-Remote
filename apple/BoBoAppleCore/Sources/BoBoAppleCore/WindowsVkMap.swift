import Foundation

/// Apple macOS 键码 (Carbon/CGKeyCode) 与 BoBo6 Windows VK_* 双向映射表。
/// 支持「跨平台智能映射」：macOS 控 Windows 时可选 Cmd -> Ctrl；Windows 控 macOS 时可选 Ctrl -> Cmd。
public enum WindowsVkMap {
    /// macOS CGKeyCode -> (Windows VK, extended)
    public static func macKeyCodeToWinVk(_ keyCode: UInt16, mapCommandToControl: Bool = true) -> (vk: UInt8, extended: Bool)? {
        switch keyCode {
        // Letters (ANSI layout)
        case 0x00: return (0x41, false) // A
        case 0x0B: return (0x42, false) // B
        case 0x08: return (0x43, false) // C
        case 0x02: return (0x44, false) // D
        case 0x0E: return (0x45, false) // E
        case 0x03: return (0x46, false) // F
        case 0x05: return (0x47, false) // G
        case 0x04: return (0x48, false) // H
        case 0x22: return (0x49, false) // I
        case 0x26: return (0x4A, false) // J
        case 0x28: return (0x4B, false) // K
        case 0x25: return (0x4C, false) // L
        case 0x2E: return (0x4D, false) // M
        case 0x2D: return (0x4E, false) // N
        case 0x1F: return (0x4F, false) // O
        case 0x23: return (0x50, false) // P
        case 0x0C: return (0x51, false) // Q
        case 0x0F: return (0x52, false) // R
        case 0x01: return (0x53, false) // S
        case 0x11: return (0x54, false) // T
        case 0x20: return (0x55, false) // U
        case 0x09: return (0x56, false) // V
        case 0x0D: return (0x57, false) // W
        case 0x07: return (0x58, false) // X
        case 0x10: return (0x59, false) // Y
        case 0x06: return (0x5A, false) // Z
        // Digits
        case 0x1D: return (0x30, false) // 0
        case 0x12: return (0x31, false) // 1
        case 0x13: return (0x32, false) // 2
        case 0x14: return (0x33, false) // 3
        case 0x15: return (0x34, false) // 4
        case 0x17: return (0x35, false) // 5
        case 0x16: return (0x36, false) // 6
        case 0x1A: return (0x37, false) // 7
        case 0x1C: return (0x38, false) // 8
        case 0x19: return (0x39, false) // 9
        // Control & editing
        case 0x24: return (0x0D, false) // Return
        case 0x30: return (0x09, false) // Tab
        case 0x31: return (0x20, false) // Space
        case 0x33: return (0x08, false) // Delete (Backspace)
        case 0x75: return (0x2E, true)  // Forward Delete
        case 0x35: return (0x1B, false) // Escape
        case 0x39: return (0x14, false) // CapsLock
        // Arrows & nav
        case 0x7B: return (0x25, true)  // Left
        case 0x7E: return (0x26, true)  // Up
        case 0x7C: return (0x27, true)  // Right
        case 0x7D: return (0x28, true)  // Down
        case 0x73: return (0x24, true)  // Home
        case 0x77: return (0x23, true)  // End
        case 0x74: return (0x21, true)  // PageUp
        case 0x79: return (0x22, true)  // PageDown
        // Modifiers
        case 0x38: return (0xA0, false) // LShift
        case 0x3C: return (0xA1, false) // RShift
        case 0x3B: return (mapCommandToControl ? 0x5B : 0xA2, mapCommandToControl) // LCtrl
        case 0x3E: return (0xA3, true)  // RCtrl
        case 0x3A: return (0xA4, false) // LOption -> LAlt
        case 0x3D: return (0xA5, true)  // ROption -> RAlt
        case 0x37: return (mapCommandToControl ? 0xA2 : 0x5B, !mapCommandToControl) // LCommand
        case 0x36: return (mapCommandToControl ? 0xA3 : 0x5C, true) // RCommand
        // Function keys
        case 0x7A: return (0x70, false) // F1
        case 0x78: return (0x71, false) // F2
        case 0x63: return (0x72, false) // F3
        case 0x76: return (0x73, false) // F4
        case 0x60: return (0x74, false) // F5
        case 0x61: return (0x75, false) // F6
        case 0x62: return (0x76, false) // F7
        case 0x64: return (0x77, false) // F8
        case 0x65: return (0x78, false) // F9
        case 0x6D: return (0x79, false) // F10
        case 0x67: return (0x7A, false) // F11
        case 0x6F: return (0x7B, false) // F12
        // Punctuation
        case 0x1B: return (0xBD, false) // -
        case 0x18: return (0xBB, false) // =
        case 0x21: return (0xDB, false) // [
        case 0x1E: return (0xDD, false) // ]
        case 0x2A: return (0xDC, false) // \
        case 0x29: return (0xBA, false) // ;
        case 0x27: return (0xDE, false) // '
        case 0x32: return (0xC0, false) // `
        case 0x2B: return (0xBC, false) // ,
        case 0x2F: return (0xBE, false) // .
        case 0x2C: return (0xBF, false) // /
        default: return nil
        }
    }

    /// Windows VK_* -> macOS CGKeyCode (for macOS Host receiving remote Windows/Android/iOS keyboard)
    public static func winVkToMacKeyCode(_ vk: UInt8, mapControlToCommand: Bool = false) -> UInt16? {
        switch vk {
        case 0x41: return 0x00 // A
        case 0x42: return 0x0B // B
        case 0x43: return 0x08 // C
        case 0x44: return 0x02 // D
        case 0x45: return 0x0E // E
        case 0x46: return 0x03 // F
        case 0x47: return 0x05 // G
        case 0x48: return 0x04 // H
        case 0x49: return 0x22 // I
        case 0x4A: return 0x26 // J
        case 0x4B: return 0x28 // K
        case 0x4C: return 0x25 // L
        case 0x4D: return 0x2E // M
        case 0x4E: return 0x2D // N
        case 0x4F: return 0x1F // O
        case 0x50: return 0x23 // P
        case 0x51: return 0x0C // Q
        case 0x52: return 0x0F // R
        case 0x53: return 0x01 // S
        case 0x54: return 0x11 // T
        case 0x55: return 0x20 // U
        case 0x56: return 0x09 // V
        case 0x57: return 0x0D // W
        case 0x58: return 0x07 // X
        case 0x59: return 0x10 // Y
        case 0x5A: return 0x06 // Z
        case 0x30: return 0x1D // 0
        case 0x31: return 0x12 // 1
        case 0x32: return 0x13 // 2
        case 0x33: return 0x14 // 3
        case 0x34: return 0x15 // 4
        case 0x35: return 0x17 // 5
        case 0x36: return 0x16 // 6
        case 0x37: return 0x1A // 7
        case 0x38: return 0x1C // 8
        case 0x39: return 0x19 // 9
        case 0x0D: return 0x24 // Return
        case 0x09: return 0x30 // Tab
        case 0x20: return 0x31 // Space
        case 0x08: return 0x33 // Backspace
        case 0x2E: return 0x75 // Forward Delete
        case 0x1B: return 0x35 // Esc
        case 0x14: return 0x39 // CapsLock
        case 0x25: return 0x7B // Left
        case 0x26: return 0x7E // Up
        case 0x27: return 0x7C // Right
        case 0x28: return 0x7D // Down
        case 0x24: return 0x73 // Home
        case 0x23: return 0x77 // End
        case 0x21: return 0x74 // PageUp
        case 0x22: return 0x79 // PageDown
        case 0x10, 0xA0: return 0x38 // Shift
        case 0xA1: return 0x3C
        case 0x11, 0xA2: return mapControlToCommand ? 0x37 : 0x3B
        case 0xA3: return mapControlToCommand ? 0x36 : 0x3E
        case 0x12, 0xA4: return 0x3A // Alt -> Option
        case 0xA5: return 0x3D
        case 0x5B: return 0x37 // LWin -> LCommand
        case 0x5C: return 0x36 // RWin -> RCommand
        case 0x70: return 0x7A // F1
        case 0x71: return 0x78 // F2
        case 0x72: return 0x63 // F3
        case 0x73: return 0x76 // F4
        case 0x74: return 0x60 // F5
        case 0x75: return 0x61 // F6
        case 0x76: return 0x62 // F7
        case 0x77: return 0x64 // F8
        case 0x78: return 0x65 // F9
        case 0x79: return 0x6D // F10
        case 0x7A: return 0x67 // F11
        case 0x7B: return 0x6F // F12
        case 0xBD: return 0x1B
        case 0xBB: return 0x18
        case 0xDB: return 0x21
        case 0xDD: return 0x1E
        case 0xDC: return 0x2A
        case 0xBA: return 0x29
        case 0xDE: return 0x27
        case 0xC0: return 0x32
        case 0xBC: return 0x2B
        case 0xBE: return 0x2F
        case 0xBF: return 0x2C
        default: return nil
        }
    }
}
