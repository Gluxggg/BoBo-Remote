package com.bobo.remote.input

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.graphics.Path
import android.media.AudioManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.util.DisplayMetrics
import android.view.WindowManager
import android.view.accessibility.AccessibilityEvent
import com.bobo.remote.protocol.BoBoInputPacket
import com.bobo.remote.protocol.BoBoProtocol
import kotlin.math.hypot

/**
 * Android 免 Root 远控输入执行器（基于系统无障碍服务 AccessibilityService）。
 * 支持：
 * - 单指/鼠标绝对坐标点击、长按、拖拽（支持 Windows/macOS/iOS 主控鼠标与 Android 触控 kind=7）
 * - 滚轮滑动（映射为垂直/水平平滑滑动手势）
 * - 系统导航动作 kind=8（返回、主页、多任务、通知栏、锁屏、音量+/-、电源菜单）
 * - 远端剪贴板同步到本地系统剪贴板
 */
class BoBoAccessibilityService : AccessibilityService() {

    private val mainHandler = Handler(Looper.getMainLooper())
    private var cursorNormX: Float = 0.5f
    private var cursorNormY: Float = 0.5f
    private var dragActive: Boolean = false
    private var dragStartX: Float = 0f
    private var dragStartY: Float = 0f
    private var dragStartTimeMs: Long = 0L

    override fun onServiceConnected() {
        super.onServiceConnected()
        instance = this
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) = Unit

    override fun onInterrupt() = Unit

    override fun onDestroy() {
        if (instance === this) instance = null
        super.onDestroy()
    }

    fun handleInputPacket(pkt: BoBoInputPacket) {
        when (pkt.kind) {
            BoBoProtocol.INPUT_KIND_MOUSE_ABS -> {
                cursorNormX = ((pkt.a.toInt() and 0xFFFF) / 65535f).coerceIn(0f, 1f)
                cursorNormY = ((pkt.b.toInt() and 0xFFFF) / 65535f).coerceIn(0f, 1f)
            }
            BoBoProtocol.INPUT_KIND_MOUSE_REL -> {
                val (w, h) = screenSize()
                cursorNormX = (cursorNormX + pkt.a / w.toFloat()).coerceIn(0f, 1f)
                cursorNormY = (cursorNormY + pkt.b / h.toFloat()).coerceIn(0f, 1f)
            }
            BoBoProtocol.INPUT_KIND_MOUSE_BTN -> {
                val down = (pkt.flags and 1) != 0
                val button = pkt.a.toInt()
                val (px, py) = normToPixel(cursorNormX, cursorNormY)
                when (button) {
                    1 -> handlePrimaryTouch(down, px, py)
                    3 -> if (down) performGlobalAction(GLOBAL_ACTION_BACK) // Right-click -> Back
                    else -> Unit
                }
            }
            BoBoProtocol.INPUT_KIND_MOUSE_WHEEL -> {
                val horizontal = (pkt.flags and 1) != 0
                val delta = pkt.a.toInt()
                val (cx, cy) = normToPixel(cursorNormX, cursorNormY)
                val (w, h) = screenSize()
                val dist = (minOf(w, h) * 0.18f).coerceAtLeast(120f)
                val sign = if (delta >= 0) 1f else -1f
                if (horizontal) {
                    dispatchSwipe(cx, cy, (cx + sign * dist).coerceIn(10f, w - 10f), cy, 120L)
                } else {
                    dispatchSwipe(cx, cy, cx, (cy + sign * dist).coerceIn(10f, h - 10f), 120L)
                }
            }
            BoBoProtocol.INPUT_KIND_TOUCH -> {
                val pointerId = (pkt.flags ushr 4) and 0x0F
                val action = pkt.flags and 0x0F
                if (pointerId != 0) return
                val nx = ((pkt.a.toInt() and 0xFFFF) / 65535f).coerceIn(0f, 1f)
                val ny = ((pkt.b.toInt() and 0xFFFF) / 65535f).coerceIn(0f, 1f)
                cursorNormX = nx
                cursorNormY = ny
                val (px, py) = normToPixel(nx, ny)
                when (action) {
                    BoBoProtocol.TOUCH_ACTION_DOWN -> handlePrimaryTouch(true, px, py)
                    BoBoProtocol.TOUCH_ACTION_UP, BoBoProtocol.TOUCH_ACTION_CANCEL -> handlePrimaryTouch(false, px, py)
                    else -> Unit
                }
            }
            BoBoProtocol.INPUT_KIND_MOBILE_NAV -> {
                performMobileNav(pkt.a.toInt())
            }
            BoBoProtocol.INPUT_KIND_KEY -> {
                val down = (pkt.flags and 1) != 0
                if (!down) return
                when (pkt.a.toInt() and 0xFF) {
                    0x1B -> performGlobalAction(GLOBAL_ACTION_BACK) // ESC -> Back
                    0x24 -> performGlobalAction(GLOBAL_ACTION_HOME) // HOME -> Home
                }
            }
        }
    }

    fun performMobileNav(action: Int) {
        mainHandler.post {
            when (action) {
                BoBoProtocol.MOBILE_NAV_BACK -> performGlobalAction(GLOBAL_ACTION_BACK)
                BoBoProtocol.MOBILE_NAV_HOME -> performGlobalAction(GLOBAL_ACTION_HOME)
                BoBoProtocol.MOBILE_NAV_RECENTS -> performGlobalAction(GLOBAL_ACTION_RECENTS)
                BoBoProtocol.MOBILE_NAV_NOTIFICATIONS -> performGlobalAction(GLOBAL_ACTION_NOTIFICATIONS)
                BoBoProtocol.MOBILE_NAV_POWER_DIALOG -> performGlobalAction(GLOBAL_ACTION_POWER_DIALOG)
                BoBoProtocol.MOBILE_NAV_LOCK -> {
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                        performGlobalAction(GLOBAL_ACTION_LOCK_SCREEN)
                    }
                }
                BoBoProtocol.MOBILE_NAV_VOLUME_UP -> adjustVolume(AudioManager.ADJUST_RAISE)
                BoBoProtocol.MOBILE_NAV_VOLUME_DOWN -> adjustVolume(AudioManager.ADJUST_LOWER)
            }
        }
    }

    fun setClipboardText(text: String) {
        mainHandler.post {
            try {
                val cm = getSystemService(Context.CLIPBOARD_SERVICE) as? ClipboardManager
                cm?.setPrimaryClip(ClipData.newPlainText("BoBoRemote", text))
            } catch (_: Exception) {
            }
        }
    }

    private fun handlePrimaryTouch(down: Boolean, px: Float, py: Float) {
        if (down) {
            dragActive = true
            dragStartX = px
            dragStartY = py
            dragStartTimeMs = System.currentTimeMillis()
        } else if (dragActive) {
            dragActive = false
            val elapsed = (System.currentTimeMillis() - dragStartTimeMs).coerceIn(45L, 800L)
            val dist = hypot((px - dragStartX).toDouble(), (py - dragStartY).toDouble())
            if (dist < 18.0) {
                dispatchTap(px, py, elapsed.coerceIn(45L, 550L))
            } else {
                dispatchSwipe(dragStartX, dragStartY, px, py, elapsed.coerceIn(80L, 450L))
            }
        }
    }

    private fun dispatchTap(x: Float, y: Float, durationMs: Long) {
        mainHandler.post {
            val path = Path().apply { moveTo(x, y) }
            val stroke = GestureDescription.StrokeDescription(path, 0L, durationMs)
            dispatchGesture(GestureDescription.Builder().addStroke(stroke).build(), null, null)
        }
    }

    private fun dispatchSwipe(x1: Float, y1: Float, x2: Float, y2: Float, durationMs: Long) {
        mainHandler.post {
            val path = Path().apply {
                moveTo(x1, y1)
                lineTo(x2, y2)
            }
            val stroke = GestureDescription.StrokeDescription(path, 0L, durationMs)
            dispatchGesture(GestureDescription.Builder().addStroke(stroke).build(), null, null)
        }
    }

    private fun adjustVolume(direction: Int) {
        try {
            val am = getSystemService(Context.AUDIO_SERVICE) as? AudioManager
            am?.adjustStreamVolume(AudioManager.STREAM_MUSIC, direction, AudioManager.FLAG_SHOW_UI)
        } catch (_: Exception) {
        }
    }

    private fun normToPixel(nx: Float, ny: Float): Pair<Float, Float> {
        val (w, h) = screenSize()
        return Pair(
            (nx * (w - 1)).coerceIn(1f, (w - 1).toFloat()),
            (ny * (h - 1)).coerceIn(1f, (h - 1).toFloat())
        )
    }

    private fun screenSize(): Pair<Int, Int> {
        val wm = getSystemService(Context.WINDOW_SERVICE) as WindowManager
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            val b = wm.maximumWindowMetrics.bounds
            Pair(b.width().coerceAtLeast(320), b.height().coerceAtLeast(320))
        } else {
            @Suppress("DEPRECATION")
            val dm = DisplayMetrics().also { wm.defaultDisplay.getRealMetrics(it) }
            Pair(dm.widthPixels.coerceAtLeast(320), dm.heightPixels.coerceAtLeast(320))
        }
    }

    companion object {
        @Volatile
        var instance: BoBoAccessibilityService? = null
            private set

        val isEnabled: Boolean get() = instance != null
    }
}
