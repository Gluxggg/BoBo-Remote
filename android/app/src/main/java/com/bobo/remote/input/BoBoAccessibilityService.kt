package com.bobo.remote.input

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.graphics.Path
import android.graphics.PointF
import android.media.AudioManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo
import com.bobo.remote.media.queryScreenMetrics
import com.bobo.remote.protocol.BoBoInputPacket
import com.bobo.remote.protocol.BoBoProtocol
import kotlin.math.abs

/** Injects input through the accessibility service explicitly enabled on the host. */
class BoBoAccessibilityService : AccessibilityService() {
    private class Contact(val source: Int, start: PointF) {
        var point = start
        var end = start
        var held = true
        var lastEventMs = SystemClock.uptimeMillis()
        var stroke: GestureDescription.StrokeDescription? = null
    }

    private val handler = Handler(Looper.getMainLooper())
    private var cursorX = 0.5f
    private var cursorY = 0.5f
    private val contacts = ArrayDeque<Contact>()
    private var incoming: Contact? = null
    private var busy = false
    private var wheelX = 0
    private var wheelY = 0
    private var nextWheelMs = 0L
    private val pump = Runnable { dispatchNext() }

    override fun onServiceConnected() {
        super.onServiceConnected()
        instance = this
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) = Unit
    override fun onInterrupt() = releaseInput()

    override fun onUnbind(intent: Intent?): Boolean {
        releaseNow()
        if (instance === this) instance = null
        return super.onUnbind(intent)
    }

    override fun onDestroy() {
        releaseNow()
        if (instance === this) instance = null
        super.onDestroy()
    }

    fun handleInputPacket(packet: BoBoInputPacket) = onMain { applyInput(packet) }
    fun performMobileNav(action: Int) = onMain { applyNavigation(action) }
    fun releaseInput() = onMain { releaseNow() }

    fun setClipboardText(text: String, paste: Boolean) = onMain {
        val clipboard = getSystemService(ClipboardManager::class.java) ?: return@onMain
        runCatching {
            clipboard.setPrimaryClip(ClipData.newPlainText("BoBoRemote", text))
            if (paste) pasteToFocusedInput()
        }
    }

    @Suppress("DEPRECATION")
    private fun pasteToFocusedInput() {
        val node = findFocus(AccessibilityNodeInfo.FOCUS_INPUT) ?: return
        try {
            if (node.isEditable && node.isVisibleToUser && node.isEnabled) {
                node.performAction(AccessibilityNodeInfo.ACTION_PASTE)
            }
        } finally {
            node.recycle()
        }
    }

    private fun onMain(action: () -> Unit) {
        // Always enqueue so releases are ordered against packets already received on the socket.
        handler.post { if (instance === this) action() }
    }

    private fun applyInput(packet: BoBoInputPacket) {
        val kind = packet.kind
        when (kind) {
            BoBoProtocol.INPUT_KIND_MOUSE_ABS -> {
                updateCursor(packet.a.toInt() and 0xffff, packet.b.toInt() and 0xffff)
                moveContact(BoBoProtocol.INPUT_KIND_MOUSE_BTN)
            }
            BoBoProtocol.INPUT_KIND_MOUSE_REL -> {
                val (w, h) = queryScreenMetrics(this)
                cursorX = (cursorX + packet.a / w.toFloat()).coerceIn(0f, 1f)
                cursorY = (cursorY + packet.b / h.toFloat()).coerceIn(0f, 1f)
                moveContact(BoBoProtocol.INPUT_KIND_MOUSE_BTN)
            }
            BoBoProtocol.INPUT_KIND_MOUSE_BTN -> when (packet.a.toInt()) {
                1 -> if (packet.flags and 1 != 0) beginContact(kind) else endContact(kind, false)
                3 -> if (packet.flags and 1 != 0) applyNavigation(BoBoProtocol.MOBILE_NAV_BACK)
            }
            BoBoProtocol.INPUT_KIND_TOUCH -> {
                if (packet.flags ushr 4 and 0x0f != 0) return
                updateCursor(packet.a.toInt() and 0xffff, packet.b.toInt() and 0xffff)
                when (packet.flags and 0x0f) {
                    BoBoProtocol.TOUCH_ACTION_DOWN -> beginContact(kind)
                    BoBoProtocol.TOUCH_ACTION_MOVE -> moveContact(kind)
                    BoBoProtocol.TOUCH_ACTION_UP -> endContact(kind, false)
                    BoBoProtocol.TOUCH_ACTION_CANCEL -> endContact(kind, true)
                }
            }
            BoBoProtocol.INPUT_KIND_MOUSE_WHEEL -> {
                if (incoming != null) return
                val delta = packet.a.toInt()
                if (packet.flags and 1 != 0) wheelX = (wheelX + delta).coerceIn(-480, 480)
                else wheelY = (wheelY + delta).coerceIn(-480, 480)
                dispatchNext()
            }
            BoBoProtocol.INPUT_KIND_MOBILE_NAV -> applyNavigation(packet.a.toInt())
            BoBoProtocol.INPUT_KIND_KEY -> if (packet.flags and 1 != 0) {
                when (packet.a.toInt() and 0xff) {
                    0x1b -> applyNavigation(BoBoProtocol.MOBILE_NAV_BACK)
                    0x24 -> applyNavigation(BoBoProtocol.MOBILE_NAV_HOME)
                }
            }
        }
    }

    private fun updateCursor(x: Int, y: Int) {
        cursorX = x / 65535f
        cursorY = y / 65535f
    }

    private fun cursorPoint(): PointF {
        val (w, h) = queryScreenMetrics(this)
        return PointF(cursorX * (w - 1), cursorY * (h - 1))
    }

    private fun beginContact(source: Int) {
        if (incoming != null || contacts.size >= 8) return
        val contact = Contact(source, cursorPoint())
        contacts.addLast(contact)
        incoming = contact
        dispatchNext()
    }

    private fun moveContact(source: Int) {
        val contact = incoming?.takeIf { it.source == source } ?: return
        contact.end = cursorPoint()
        contact.lastEventMs = SystemClock.uptimeMillis()
    }

    private fun endContact(source: Int, cancelled: Boolean) {
        val contact = incoming?.takeIf { it.source == source } ?: return
        incoming = null
        if (cancelled && contact.stroke == null) contacts.remove(contact)
        contact.held = false
        contact.end = if (cancelled) contact.point else cursorPoint()
        dispatchNext()
    }

    private fun releaseNow() {
        incoming = null
        val active = contacts.firstOrNull()?.takeIf { it.stroke != null }
        contacts.clear()
        if (active != null) {
            active.held = false
            active.end = active.point
            contacts.addLast(active)
        }
        wheelX = 0
        wheelY = 0
        handler.removeCallbacks(pump)
        dispatchNext()
    }

    private fun dispatchNext() {
        if (busy) return
        val contact = contacts.firstOrNull()
        if (contact != null) {
            if (SystemClock.uptimeMillis() - contact.lastEventMs > 15_000L) {
                contact.held = false
                if (incoming === contact) incoming = null
            }
            val target = contact.end
            val path = pathBetween(contact.point, target)
            val keepDown = contact.held
            val stroke = contact.stroke?.continueStroke(path, 0L, 24L, keepDown)
                ?: GestureDescription.StrokeDescription(path, 0L, 24L, keepDown)
            contact.point = target
            contact.stroke = stroke
            dispatch(stroke) { completed ->
                if (!keepDown || !completed) {
                    contacts.remove(contact)
                    if (incoming === contact) incoming = null
                }
            }
            return
        }
        if (wheelX == 0 && wheelY == 0) return
        val now = SystemClock.uptimeMillis()
        if (now < nextWheelMs) {
            handler.removeCallbacks(pump)
            handler.postAtTime(pump, nextWheelMs)
            return
        }
        val (width, height) = queryScreenMetrics(this)
        val start = cursorPoint()
        val horizontal = abs(wheelX) > abs(wheelY)
        val delta = if (horizontal) wheelX else wheelY
        if (horizontal) wheelX = 0 else wheelY = 0
        val distance = (minOf(width, height) * delta / 360f)
            .coerceIn(-minOf(width, height) * 0.35f, minOf(width, height) * 0.35f)
        val end = if (horizontal) PointF((start.x + distance).coerceIn(0f, width - 1f), start.y)
        else PointF(start.x, (start.y + distance).coerceIn(0f, height - 1f))
        nextWheelMs = now + 125L
        if (start != end) dispatch(GestureDescription.StrokeDescription(pathBetween(start, end), 0L, 110L)) { }
        else if (wheelX != 0 || wheelY != 0) handler.postAtTime(pump, nextWheelMs)
    }

    private fun dispatch(stroke: GestureDescription.StrokeDescription, onComplete: (Boolean) -> Unit) {
        busy = true
        fun finish(completed: Boolean) {
            busy = false
            onComplete(completed)
            handler.post(pump)
        }
        val sent = runCatching {
            dispatchGesture(GestureDescription.Builder().addStroke(stroke).build(), object : GestureResultCallback() {
                override fun onCompleted(gestureDescription: GestureDescription) = finish(true)
                override fun onCancelled(gestureDescription: GestureDescription) = finish(false)
            }, handler)
        }.getOrDefault(false)
        if (!sent) finish(false)
    }

    private fun pathBetween(start: PointF, end: PointF) = Path().apply {
        moveTo(start.x, start.y)
        if (start != end) lineTo(end.x, end.y)
    }

    private fun applyNavigation(action: Int) {
        when (action) {
            BoBoProtocol.MOBILE_NAV_VOLUME_UP -> adjustVolume(AudioManager.ADJUST_RAISE)
            BoBoProtocol.MOBILE_NAV_VOLUME_DOWN -> adjustVolume(AudioManager.ADJUST_LOWER)
            else -> {
                val global = when (action) {
                    BoBoProtocol.MOBILE_NAV_BACK -> GLOBAL_ACTION_BACK
                    BoBoProtocol.MOBILE_NAV_HOME -> GLOBAL_ACTION_HOME
                    BoBoProtocol.MOBILE_NAV_RECENTS -> GLOBAL_ACTION_RECENTS
                    BoBoProtocol.MOBILE_NAV_NOTIFICATIONS -> GLOBAL_ACTION_NOTIFICATIONS
                    BoBoProtocol.MOBILE_NAV_POWER_DIALOG -> GLOBAL_ACTION_POWER_DIALOG
                    BoBoProtocol.MOBILE_NAV_LOCK -> if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) GLOBAL_ACTION_LOCK_SCREEN else return
                    else -> return
                }
                releaseNow()
                performGlobalAction(global)
            }
        }
    }

    private fun adjustVolume(direction: Int) {
        runCatching {
            val audio = getSystemService(Context.AUDIO_SERVICE) as AudioManager
            audio.adjustStreamVolume(AudioManager.STREAM_MUSIC, direction, AudioManager.FLAG_SHOW_UI)
        }
    }

    companion object {
        @Volatile var instance: BoBoAccessibilityService? = null
            private set
        val isEnabled: Boolean get() = instance != null
    }
}
