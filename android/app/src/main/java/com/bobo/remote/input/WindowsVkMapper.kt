package com.bobo.remote.input

import android.view.KeyEvent

/**
 * 将 Android 物理/蓝牙键盘 KeyEvent 键码转换为 BoBo6 规范要求的 Windows VK_* 虚拟键码。
 */
object WindowsVkMapper {
    data class MappedVk(val vk: Int, val extended: Boolean = false)

    fun mapAndroidKeyCode(keyCode: Int): MappedVk? = when (keyCode) {
        in KeyEvent.KEYCODE_A..KeyEvent.KEYCODE_Z -> MappedVk(0x41 + (keyCode - KeyEvent.KEYCODE_A))
        in KeyEvent.KEYCODE_0..KeyEvent.KEYCODE_9 -> MappedVk(0x30 + (keyCode - KeyEvent.KEYCODE_0))
        in KeyEvent.KEYCODE_F1..KeyEvent.KEYCODE_F12 -> MappedVk(0x70 + (keyCode - KeyEvent.KEYCODE_F1))
        KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER -> MappedVk(0x0D)
        KeyEvent.KEYCODE_DEL -> MappedVk(0x08) // Backspace
        KeyEvent.KEYCODE_FORWARD_DEL -> MappedVk(0x2E, true) // Delete
        KeyEvent.KEYCODE_TAB -> MappedVk(0x09)
        KeyEvent.KEYCODE_SPACE -> MappedVk(0x20)
        KeyEvent.KEYCODE_ESCAPE -> MappedVk(0x1B)
        KeyEvent.KEYCODE_DPAD_LEFT -> MappedVk(0x25, true)
        KeyEvent.KEYCODE_DPAD_UP -> MappedVk(0x26, true)
        KeyEvent.KEYCODE_DPAD_RIGHT -> MappedVk(0x27, true)
        KeyEvent.KEYCODE_DPAD_DOWN -> MappedVk(0x28, true)
        KeyEvent.KEYCODE_MOVE_HOME -> MappedVk(0x24, true)
        KeyEvent.KEYCODE_MOVE_END -> MappedVk(0x23, true)
        KeyEvent.KEYCODE_PAGE_UP -> MappedVk(0x21, true)
        KeyEvent.KEYCODE_PAGE_DOWN -> MappedVk(0x22, true)
        KeyEvent.KEYCODE_INSERT -> MappedVk(0x2D, true)
        KeyEvent.KEYCODE_SHIFT_LEFT -> MappedVk(0xA0)
        KeyEvent.KEYCODE_SHIFT_RIGHT -> MappedVk(0xA1)
        KeyEvent.KEYCODE_CTRL_LEFT -> MappedVk(0xA2)
        KeyEvent.KEYCODE_CTRL_RIGHT -> MappedVk(0xA3, true)
        KeyEvent.KEYCODE_ALT_LEFT -> MappedVk(0xA4)
        KeyEvent.KEYCODE_ALT_RIGHT -> MappedVk(0xA5, true)
        KeyEvent.KEYCODE_META_LEFT -> MappedVk(0x5B, true)
        KeyEvent.KEYCODE_META_RIGHT -> MappedVk(0x5C, true)
        KeyEvent.KEYCODE_MINUS -> MappedVk(0xBD)
        KeyEvent.KEYCODE_EQUALS -> MappedVk(0xBB)
        KeyEvent.KEYCODE_LEFT_BRACKET -> MappedVk(0xDB)
        KeyEvent.KEYCODE_RIGHT_BRACKET -> MappedVk(0xDD)
        KeyEvent.KEYCODE_BACKSLASH -> MappedVk(0xDC)
        KeyEvent.KEYCODE_SEMICOLON -> MappedVk(0xBA)
        KeyEvent.KEYCODE_APOSTROPHE -> MappedVk(0xDE)
        KeyEvent.KEYCODE_GRAVE -> MappedVk(0xC0)
        KeyEvent.KEYCODE_COMMA -> MappedVk(0xBC)
        KeyEvent.KEYCODE_PERIOD -> MappedVk(0xBE)
        KeyEvent.KEYCODE_SLASH -> MappedVk(0xBF)
        KeyEvent.KEYCODE_CAPS_LOCK -> MappedVk(0x14)
        else -> null
    }
}
