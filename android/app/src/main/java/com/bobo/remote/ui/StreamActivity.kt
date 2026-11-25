package com.bobo.remote.ui

import android.content.ClipboardManager
import android.content.Context
import android.content.pm.ActivityInfo
import android.os.Bundle
import android.view.InputDevice
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.PointerIcon
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.ViewConfiguration
import android.view.WindowManager
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableDoubleStateOf
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.ExperimentalComposeUiApi
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import com.bobo.remote.R
import com.bobo.remote.input.WindowsVkMapper
import com.bobo.remote.media.AudioPlayer
import com.bobo.remote.media.BoBoAppRuntime
import com.bobo.remote.media.H264HardwareDecoder
import com.bobo.remote.network.BoBoStreamClient
import com.bobo.remote.network.BoBoStreamOptions
import com.bobo.remote.protocol.BoBoAudioPayload
import com.bobo.remote.protocol.BoBoMonitorDescriptor
import com.bobo.remote.protocol.BoBoProtocol
import com.bobo.remote.protocol.BoBoVideoPayload
import kotlin.math.roundToInt

class StreamActivity : ComponentActivity() {
    private data class Quality(val width: Int, val height: Int, val fps: Int, val bitrate: Int)

    private lateinit var streamClient: BoBoStreamClient
    private val videoDecoder = H264HardwareDecoder(
        onNeedKeyframe = { if (::streamClient.isInitialized) streamClient.sendRequestKeyframe() },
        onFrameRendered = { width, height -> onActiveUi {
            if (surfaceAvailable) {
                updateDimensions(width, height)
                frameReady.value = true
            }
        } }
    )
    private val audioPlayer = AudioPlayer()
    private var sessionStarted = false
    private var closing = false
    private var surfaceAvailable = false

    private val connected = mutableStateOf(false)
    private val frameReady = mutableStateOf(false)
    private val rttMs = mutableDoubleStateOf(0.0)
    private val remoteWidth = mutableIntStateOf(1920)
    private val remoteHeight = mutableIntStateOf(1080)
    private val remotePlatform = mutableStateOf(BoBoProtocol.PLATFORM_WINDOWS)
    private val viewOnly = mutableStateOf(false)
    private val privacy = mutableStateOf(false)
    private val touchpad = mutableStateOf(true)
    private val muted = mutableStateOf(false)
    private val superDisplay = mutableStateOf(true)
    private val cursorComposited = mutableStateOf(false)
    private val cursor = mutableStateOf(Offset(0.5f, 0.5f))
    private val hardwareMouse = mutableStateOf(false)
    private val monitors = mutableStateOf<List<BoBoMonitorDescriptor>>(emptyList())
    private val monitorIndex = mutableIntStateOf(0)
    private var quality = Quality(1920, 1080, 60, 12000)

    private val pressedKeys = LinkedHashMap<Int, WindowsVkMapper.MappedVk>()
    private val mouseButtons = LinkedHashSet<Int>()
    private var directPointer: Int? = null
    private var directUsesTouch = false
    private var directPoint = Offset.Zero
    private var trackpadStart = Offset.Zero
    private var trackpadPoint = Offset.Zero
    private var trackpadStartMs = 0L
    private var trackpadPointers = 0
    private var trackpadMoved = false
    private var trackpadActive = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val host = intent.getStringExtra(EXTRA_HOST).orEmpty()
        if (host.isBlank()) {
            finish()
            return
        }
        BoBoAppRuntime.init(this)
        streamClient = BoBoStreamClient(BoBoAppRuntime.identity)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        WindowCompat.setDecorFitsSystemWindows(window, false)
        WindowInsetsControllerCompat(window, window.decorView).apply {
            hide(WindowInsetsCompat.Type.systemBars())
            systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }

        val port = intent.getIntExtra(EXTRA_PORT, BoBoProtocol.DEFAULT_TCP_PORT)
        val name = intent.getStringExtra(EXTRA_NAME) ?: host
        val autoPrivacy = intent.getBooleanExtra(EXTRA_PRIVACY, true)
        val nodeId = intent.getStringExtra(EXTRA_NODE_ID).orEmpty()
        val thumbprint = intent.getStringExtra(EXTRA_THUMBPRINT).orEmpty()
        setContent {
            BoBoTheme {
                val readyForInput = connected.value && frameReady.value && !viewOnly.value
                StreamScreen(
                    hostName = name,
                    connected = connected.value,
                    statusText = "正在建立 BoBo 加密连接…",
                    rttMs = rttMs.doubleValue,
                    remotePlatform = remotePlatform.value,
                    viewOnly = viewOnly.value,
                    privacyActive = privacy.value,
                    touchpadMode = touchpad.value,
                    muted = muted.value,
                    superDisplay = superDisplay.value,
                    videoAspect = remoteWidth.intValue.toFloat() / remoteHeight.intValue,
                    cursor = cursor.value.takeIf { readyForInput && touchpad.value && !cursorComposited.value && !hardwareMouse.value },
                    hidePointer = readyForInput && cursorComposited.value,
                    monitors = monitors.value,
                    selectedMonitor = monitorIndex.intValue,
                    onSurfaceCreated = { holder ->
                        surfaceAvailable = true
                        videoDecoder.attachSurface(holder.surface)
                        if (!sessionStarted && !closing) {
                            sessionStarted = true
                            startStreamSession(host, port, autoPrivacy, nodeId, thumbprint)
                        }
                    },
                    onSurfaceDestroyed = {
                        releaseRemoteInput()
                        surfaceAvailable = false
                        frameReady.value = false
                        videoDecoder.attachSurface(null)
                    },
                    onTouchEvent = ::handleRemoteTouch,
                    onPointerEvent = ::handlePointerMotion,
                    onToggleTouchpad = { releaseRemoteInput(); touchpad.value = it },
                    onToggleMute = {
                        muted.value = !muted.value
                        audioPlayer.setMutedState(muted.value)
                    },
                    onTogglePrivacy = { if (canControl()) streamClient.sendSetPrivacy(!privacy.value) },
                    onToggleSuperDisplay = {
                        val dm = resources.displayMetrics
                        streamClient.sendSetSuperDisplay(!superDisplay.value, dm.widthPixels, dm.heightPixels)
                    },
                    onSwitchQuality = { w, h, fps, bps ->
                        quality = Quality(w, h, fps, bps)
                        reconfigure(monitorIndex.intValue)
                    },
                    onSwitchMonitor = ::reconfigure,
                    onSendKeyCombo = { if (canControl()) streamClient.sendKeyCombo(it) },
                    onSendText = { if (canControl()) streamClient.sendClipboardText(it, true) },
                    onSyncClipboard = ::syncPhoneClipboardToRemote,
                    onSendMobileNav = { if (canControl()) streamClient.sendInput(BoBoProtocol.buildMobileNav(it)) },
                    onLockHost = { if (canControl()) streamClient.sendLockWorkstation() },
                    onToggleOrientation = {
                        releaseRemoteInput()
                        requestedOrientation = if (resources.configuration.orientation == android.content.res.Configuration.ORIENTATION_LANDSCAPE) {
                            ActivityInfo.SCREEN_ORIENTATION_SENSOR_PORTRAIT
                        } else {
                            ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
                        }
                    },
                    onDisconnect = ::closeSession
                )
            }
        }
    }

    private fun startStreamSession(host: String, port: Int, autoPrivacy: Boolean, nodeId: String, thumbprint: String) {
        val dm = resources.displayMetrics
        val options = BoBoStreamOptions(
            host = host,
            port = port,
            width = quality.width,
            height = quality.height,
            fps = quality.fps,
            bitrateKbps = quality.bitrate,
            requestPrivacy = autoPrivacy,
            superDisplay = true,
            clientWidth = maxOf(dm.widthPixels, dm.heightPixels),
            clientHeight = minOf(dm.widthPixels, dm.heightPixels),
            expectedHostThumbprint = thumbprint,
            expectedHostNodeId = nodeId
        )
        streamClient.connectAndStartStream(options, object : BoBoStreamClient.Listener {
            override fun onStreamStarted(
                monitors: List<BoBoMonitorDescriptor>, width: Int, height: Int, fps: Int,
                remotePlatform: String, hostControlMode: String
            ) {
                BoBoAppRuntime.refreshAuthorizedPeers()
                onActiveUi {
                    if (!frameReady.value) updateDimensions(width, height)
                    this@StreamActivity.remotePlatform.value = remotePlatform
                    viewOnly.value = hostControlMode == BoBoProtocol.CONTROL_MODE_VIEW_ONLY
                    this@StreamActivity.monitors.value = monitors
                    monitorIndex.intValue = monitors.firstOrNull { it.index == options.monitorIndex }?.index
                        ?: monitors.firstOrNull { it.isPrimary }?.index ?: options.monitorIndex
                    connected.value = true
                    if (remotePlatform == BoBoProtocol.PLATFORM_ANDROID || remotePlatform == BoBoProtocol.PLATFORM_IOS) {
                        touchpad.value = false
                        superDisplay.value = false
                    } else {
                        requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
                    }
                }
            }

            override fun onVideoFrame(frame: BoBoVideoPayload, timestampMs: Long) {
                videoDecoder.decode(frame.h264Data, frame.width, frame.height, timestampMs)
            }

            override fun onAudioFrame(frame: BoBoAudioPayload) {
                audioPlayer.playAdpcm(frame.sampleRate, frame.channels, frame.sampleCount, frame.adpcmData)
            }

            override fun onRttUpdated(rttMs: Double) = onActiveUi { this@StreamActivity.rttMs.doubleValue = rttMs }
            override fun onPrivacyStateChanged(active: Boolean) = onActiveUi { privacy.value = active }
            override fun onHostCursorCompositedChanged(composited: Boolean) = onActiveUi { cursorComposited.value = composited }
            override fun onSuperDisplayStateChanged(active: Boolean, nativeMatch: Boolean, width: Int, height: Int) = onActiveUi {
                superDisplay.value = active
                updateDimensions(width, height)
            }
            override fun onReconfigured(width: Int, height: Int, fps: Int) = onActiveUi {
                monitors.value = streamClient.remoteMonitors
                updateDimensions(width, height)
            }
            override fun onError(code: String, message: String) = onActiveUi {
                Toast.makeText(this@StreamActivity, message, Toast.LENGTH_LONG).show()
                if (!connected.value || code == "CAPTURE_FAILED") closeSession()
            }
            override fun onDisconnected(reason: String) = onActiveUi {
                Toast.makeText(this@StreamActivity, "远控已结束：$reason", Toast.LENGTH_SHORT).show()
                closeSession()
            }
        })
    }

    private fun onActiveUi(action: () -> Unit) {
        runOnUiThread { if (!closing && !isFinishing && !isDestroyed) action() }
    }

    private fun updateDimensions(width: Int, height: Int) {
        if (width !in 1..8192 || height !in 1..8192 || (remoteWidth.intValue == width && remoteHeight.intValue == height)) return
        releaseRemoteInput()
        remoteWidth.intValue = width
        remoteHeight.intValue = height
    }

    private fun reconfigure(selected: Int) {
        if (!connected.value) return
        releaseRemoteInput()
        monitorIndex.intValue = selected
        streamClient.sendReconfigure(selected, quality.width, quality.height, quality.fps, quality.bitrate)
    }

    private fun canControl(): Boolean = ::streamClient.isInitialized && connected.value && frameReady.value &&
        !viewOnly.value && streamClient.isConnected

    private fun normalized(event: MotionEvent, width: Int, height: Int, index: Int = 0) = Offset(
        (event.getX(index) / width).coerceIn(0f, 1f), (event.getY(index) / height).coerceIn(0f, 1f)
    )

    private fun handleRemoteTouch(width: Int, height: Int, event: MotionEvent): Boolean {
        if (!canControl() || width <= 0 || height <= 0) return true
        if (event.actionMasked == MotionEvent.ACTION_DOWN) {
            hardwareMouse.value = event.isFromSource(InputDevice.SOURCE_MOUSE)
            if (hardwareMouse.value && event.buttonState and MotionEvent.BUTTON_PRIMARY == 0) return false
        }
        if (touchpad.value && !hardwareMouse.value) doTrackpad(event, width, height)
        else doDirectTouch(event, width, height)
        return true
    }

    private fun doDirectTouch(event: MotionEvent, width: Int, height: Int) {
        val action = event.actionMasked
        if (action == MotionEvent.ACTION_DOWN) {
            directPointer = event.getPointerId(0)
            directUsesTouch = remotePlatform.value == BoBoProtocol.PLATFORM_ANDROID && !hardwareMouse.value
            directPoint = normalized(event, width, height)
            sendDirect(BoBoProtocol.TOUCH_ACTION_DOWN)
            return
        }
        val pointer = directPointer ?: return
        if (action == MotionEvent.ACTION_CANCEL) {
            sendDirect(BoBoProtocol.TOUCH_ACTION_CANCEL)
            directPointer = null
            return
        }
        val index = event.findPointerIndex(pointer)
        if (index < 0) return
        directPoint = normalized(event, width, height, index)
        if (action == MotionEvent.ACTION_MOVE) sendDirect(BoBoProtocol.TOUCH_ACTION_MOVE)
        if (action == MotionEvent.ACTION_UP || (action == MotionEvent.ACTION_POINTER_UP && event.actionIndex == index)) {
            sendDirect(BoBoProtocol.TOUCH_ACTION_UP)
            directPointer = null
        }
    }

    private fun sendDirect(action: Int) {
        val x = (directPoint.x * 65535).roundToInt()
        val y = (directPoint.y * 65535).roundToInt()
        if (directUsesTouch) {
            streamClient.sendInput(BoBoProtocol.buildTouch(0, action, x, y))
        } else {
            if (action != BoBoProtocol.TOUCH_ACTION_CANCEL) sendAbsolute(directPoint)
            if (action == BoBoProtocol.TOUCH_ACTION_DOWN) sendMouseButton(1, true)
            if (action == BoBoProtocol.TOUCH_ACTION_UP || action == BoBoProtocol.TOUCH_ACTION_CANCEL) sendMouseButton(1, false)
        }
    }

    private fun sendAbsolute(point: Offset) {
        cursor.value = point
        streamClient.sendInput(BoBoProtocol.buildMouseAbs((point.x * 65535).roundToInt(), (point.y * 65535).roundToInt()))
    }

    private fun sendMouseButton(button: Int, pressed: Boolean) {
        val changed = if (pressed) mouseButtons.add(button) else mouseButtons.remove(button)
        if (changed) streamClient.sendInput(BoBoProtocol.buildMouseButton(button, pressed))
    }

    private fun handlePointerMotion(width: Int, height: Int, event: MotionEvent): Boolean {
        if (!canControl() || width <= 0 || height <= 0 || !event.isFromSource(InputDevice.SOURCE_MOUSE)) return false
        hardwareMouse.value = true
        when (event.actionMasked) {
            MotionEvent.ACTION_HOVER_MOVE -> sendAbsolute(normalized(event, width, height))
            MotionEvent.ACTION_SCROLL -> {
                sendAbsolute(normalized(event, width, height))
                val vertical = (event.getAxisValue(MotionEvent.AXIS_VSCROLL) * 120).roundToInt()
                val horizontal = (event.getAxisValue(MotionEvent.AXIS_HSCROLL) * 120).roundToInt()
                if (vertical != 0) streamClient.sendInput(BoBoProtocol.buildMouseWheel(vertical, false))
                if (horizontal != 0) streamClient.sendInput(BoBoProtocol.buildMouseWheel(horizontal, true))
            }
            MotionEvent.ACTION_BUTTON_PRESS, MotionEvent.ACTION_BUTTON_RELEASE -> {
                val button = when (event.actionButton) {
                    MotionEvent.BUTTON_SECONDARY -> 3
                    MotionEvent.BUTTON_TERTIARY -> 2
                    else -> return false
                }
                sendAbsolute(normalized(event, width, height))
                sendMouseButton(button, event.actionMasked == MotionEvent.ACTION_BUTTON_PRESS)
            }
            else -> return false
        }
        return true
    }

    private fun doTrackpad(event: MotionEvent, width: Int, height: Int) {
        val point = Offset(event.x, event.y)
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                trackpadActive = true
                trackpadStart = point
                trackpadPoint = point
                trackpadStartMs = event.eventTime
                trackpadPointers = 1
                trackpadMoved = false
            }
            MotionEvent.ACTION_POINTER_DOWN -> {
                trackpadPointers = maxOf(trackpadPointers, event.pointerCount)
                trackpadPoint = point
            }
            MotionEvent.ACTION_POINTER_UP -> {
                val remaining = if (event.actionIndex == 0) 1 else 0
                trackpadPoint = Offset(event.getX(remaining), event.getY(remaining))
            }
            MotionEvent.ACTION_MOVE -> if (trackpadActive) {
                val movement = point - trackpadPoint
                trackpadPoint = point
                if ((point - trackpadStart).getDistance() > ViewConfiguration.get(this).scaledTouchSlop) trackpadMoved = true
                if (event.pointerCount >= 2) {
                    val scroll = (movement.y * 2.4f).roundToInt()
                    if (scroll != 0) streamClient.sendInput(BoBoProtocol.buildMouseWheel(scroll, false))
                } else if (cursorComposited.value) {
                    val x = (movement.x * 1.45f).roundToInt()
                    val y = (movement.y * 1.45f).roundToInt()
                    if (x != 0 || y != 0) streamClient.sendInput(BoBoProtocol.buildMouseRel(x, y))
                } else if (movement != Offset.Zero) {
                    val last = cursor.value
                    sendAbsolute(Offset((last.x + movement.x / width).coerceIn(0f, 1f), (last.y + movement.y / height).coerceIn(0f, 1f)))
                }
            }
            MotionEvent.ACTION_UP -> {
                if (trackpadActive && !trackpadMoved && event.eventTime - trackpadStartMs < 260L) {
                    if (!cursorComposited.value) sendAbsolute(cursor.value)
                    val button = if (trackpadPointers >= 2) 3 else 1
                    sendMouseButton(button, true)
                    sendMouseButton(button, false)
                }
                trackpadActive = false
            }
            MotionEvent.ACTION_CANCEL -> trackpadActive = false
        }
    }

    private fun syncPhoneClipboardToRemote() {
        if (!canControl()) return
        val clipboard = getSystemService(ClipboardManager::class.java)
        val text = clipboard?.primaryClip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.coerceToText(this)?.toString().orEmpty()
        if (text.isNotEmpty()) {
            streamClient.sendClipboardText(text, false)
            Toast.makeText(this, "已发送剪贴板文本（${text.length} 字）", Toast.LENGTH_SHORT).show()
        } else {
            Toast.makeText(this, "手机剪贴板为空", Toast.LENGTH_SHORT).show()
        }
    }

    override fun onKeyDown(keyCode: Int, event: KeyEvent?): Boolean {
        if (canControl()) WindowsVkMapper.mapAndroidKeyCode(keyCode)?.let { mapped ->
            pressedKeys[keyCode] = mapped
            streamClient.sendInput(BoBoProtocol.buildKey(mapped.vk, true, mapped.extended))
            return true
        }
        return super.onKeyDown(keyCode, event)
    }

    override fun onKeyUp(keyCode: Int, event: KeyEvent?): Boolean {
        val mapped = pressedKeys.remove(keyCode) ?: return super.onKeyUp(keyCode, event)
        if (streamClient.isConnected) streamClient.sendInput(BoBoProtocol.buildKey(mapped.vk, false, mapped.extended))
        return true
    }

    private fun releaseRemoteInput() {
        if (::streamClient.isInitialized && streamClient.isConnected) {
            if (directPointer != null) sendDirect(BoBoProtocol.TOUCH_ACTION_CANCEL)
            for (button in mouseButtons) streamClient.sendInput(BoBoProtocol.buildMouseButton(button, false))
            for (mapped in pressedKeys.values.toList().asReversed()) {
                streamClient.sendInput(BoBoProtocol.buildKey(mapped.vk, false, mapped.extended))
            }
        }
        directPointer = null
        mouseButtons.clear()
        pressedKeys.clear()
        trackpadActive = false
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (!hasFocus) releaseRemoteInput()
    }

    override fun onPause() {
        releaseRemoteInput()
        super.onPause()
    }

    private fun closeSession() {
        releaseRemoteInput()
        closing = true
        if (::streamClient.isInitialized) streamClient.disconnect()
        finish()
    }

    override fun onDestroy() {
        releaseRemoteInput()
        if (!closing && ::streamClient.isInitialized) streamClient.disconnect()
        closing = true
        videoDecoder.release()
        audioPlayer.release()
        super.onDestroy()
    }

    companion object {
        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"
        const val EXTRA_NAME = "name"
        const val EXTRA_PRIVACY = "privacy"
        const val EXTRA_NODE_ID = "node_id"
        const val EXTRA_THUMBPRINT = "thumbprint"
    }
}

@OptIn(ExperimentalMaterial3Api::class, ExperimentalComposeUiApi::class)
@Composable
private fun StreamScreen(
    hostName: String,
    connected: Boolean,
    statusText: String,
    rttMs: Double,
    remotePlatform: String,
    viewOnly: Boolean,
    privacyActive: Boolean,
    touchpadMode: Boolean,
    muted: Boolean,
    superDisplay: Boolean,
    videoAspect: Float,
    cursor: Offset?,
    hidePointer: Boolean,
    monitors: List<BoBoMonitorDescriptor>,
    selectedMonitor: Int,
    onSurfaceCreated: (SurfaceHolder) -> Unit,
    onSurfaceDestroyed: () -> Unit,
    onTouchEvent: (Int, Int, MotionEvent) -> Boolean,
    onPointerEvent: (Int, Int, MotionEvent) -> Boolean,
    onToggleTouchpad: (Boolean) -> Unit,
    onToggleMute: () -> Unit,
    onTogglePrivacy: () -> Unit,
    onToggleSuperDisplay: () -> Unit,
    onSwitchQuality: (Int, Int, Int, Int) -> Unit,
    onSwitchMonitor: (Int) -> Unit,
    onSendKeyCombo: (IntArray) -> Unit,
    onSendText: (String) -> Unit,
    onSyncClipboard: () -> Unit,
    onSendMobileNav: (Int) -> Unit,
    onLockHost: () -> Unit,
    onToggleOrientation: () -> Unit,
    onDisconnect: () -> Unit
) {
    var showSheet by remember { mutableStateOf(false) }
    var showTextInput by remember { mutableStateOf(false) }
    var showQuickKeys by remember { mutableStateOf(false) }
    var textDraft by remember { mutableStateOf("") }

    val density = LocalDensity.current
    val cfg = LocalConfiguration.current
    val screenWpx = with(density) { cfg.screenWidthDp.dp.toPx() }
    val screenHpx = with(density) { cfg.screenHeightDp.dp.toPx() }
    var ballX by remember { mutableFloatStateOf(16f) }
    var ballY by remember { mutableFloatStateOf(160f) }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Color(0xFF0E1520))
    ) {
        BoxWithConstraints(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            val videoWidth = minOf(maxWidth.value, maxHeight.value * videoAspect).dp
            val videoHeight = videoWidth / videoAspect
            Box(Modifier.size(videoWidth, videoHeight)) {
                AndroidView(
                    factory = { context ->
                        SurfaceView(context).apply {
                            holder.addCallback(object : SurfaceHolder.Callback {
                                override fun surfaceCreated(holder: SurfaceHolder) = onSurfaceCreated(holder)
                                override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) = Unit
                                override fun surfaceDestroyed(holder: SurfaceHolder) = onSurfaceDestroyed()
                            })
                            setOnTouchListener { view, event -> onTouchEvent(view.width, view.height, event) }
                            setOnHoverListener { view, event -> onPointerEvent(view.width, view.height, event) }
                            setOnGenericMotionListener { view, event -> onPointerEvent(view.width, view.height, event) }
                        }
                    },
                    modifier = Modifier.fillMaxSize(),
                    update = { view ->
                        view.pointerIcon = if (hidePointer) PointerIcon.getSystemIcon(view.context, PointerIcon.TYPE_NULL) else null
                    }
                )
                if (cursor != null) {
                    Canvas(Modifier.matchParentSize()) {
                        val center = Offset(cursor.x * size.width, cursor.y * size.height)
                        drawCircle(Color.Black, 6.dp.toPx(), center)
                        drawCircle(Color.White, 4.dp.toPx(), center)
                    }
                }
            }
        }

        if (!connected) {
            Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Surface(
                    shape = RoundedCornerShape(18.dp),
                    color = Color(0xE6172235),
                    modifier = Modifier.padding(24.dp)
                ) {
                    Column(
                        modifier = Modifier.padding(24.dp),
                        horizontalAlignment = Alignment.CenterHorizontally
                    ) {
                        Text("正在连接 $hostName", color = Color.White, fontWeight = FontWeight.Bold, fontSize = 16.sp)
                        Spacer(Modifier.height(8.dp))
                        Text(statusText, color = Color(0xFFCBD5E1), fontSize = 13.sp)
                        Spacer(Modifier.height(14.dp))
                        OutlinedButton(onClick = onDisconnect) {
                            Text("取消连接", color = Color.White)
                        }
                    }
                }
            }
        }

        // Optional PC quick shortcut strip at bottom
        if (connected && showQuickKeys && !viewOnly) {
            Row(
                modifier = Modifier
                    .align(Alignment.BottomCenter)
                    .fillMaxWidth()
                    .background(Color(0xDD111827))
                    .horizontalScroll(rememberScrollState())
                    .padding(horizontal = 10.dp, vertical = 8.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                val keys: List<Pair<String, IntArray>> = listOf(
                    "Esc" to intArrayOf(0x1B),
                    "Tab" to intArrayOf(0x09),
                    "Ctrl+C" to intArrayOf(0x11, 0x43),
                    "Ctrl+V" to intArrayOf(0x11, 0x56),
                    "Ctrl+Z" to intArrayOf(0x11, 0x5A),
                    "Alt+Tab" to intArrayOf(0x12, 0x09),
                    "Win" to intArrayOf(0x5B),
                    "Win+D" to intArrayOf(0x5B, 0x44),
                    "Alt+F4" to intArrayOf(0x12, 0x73),
                    "任务管理" to intArrayOf(0x11, 0x10, 0x1B)
                )
                for ((label, combo) in keys) {
                    Surface(
                        shape = RoundedCornerShape(8.dp),
                        color = Color(0xFF1F2937),
                        modifier = Modifier.clickable { onSendKeyCombo(combo) }
                    ) {
                        Text(label, color = Color.White, fontSize = 12.sp, modifier = Modifier.padding(horizontal = 12.dp, vertical = 8.dp))
                    }
                }
                Surface(
                    shape = RoundedCornerShape(8.dp),
                    color = BoBoBlue,
                    modifier = Modifier.clickable { showTextInput = true }
                ) {
                    Text("输入文字", color = Color.White, fontSize = 12.sp, fontWeight = FontWeight.Bold, modifier = Modifier.padding(horizontal = 12.dp, vertical = 8.dp))
                }
            }
        }

        // Draggable Edge-Snapping Square Floating Ball (UU-style)
        Surface(
            shape = RoundedCornerShape(14.dp),
            color = Color(0xE01E293B),
            shadowElevation = 6.dp,
            modifier = Modifier
                .offset { IntOffset(ballX.roundToInt(), ballY.roundToInt()) }
                .pointerInput(screenWpx, screenHpx) {
                    detectDragGestures(
                        onDragEnd = {
                            ballX = if (ballX + 26f < screenWpx / 2f) 10f else (screenWpx - 140f).coerceAtLeast(10f)
                        }
                    ) { change, dragAmount ->
                        change.consume()
                        ballX = (ballX + dragAmount.x).coerceIn(0f, (screenWpx - 120f).coerceAtLeast(0f))
                        ballY = (ballY + dragAmount.y).coerceIn(24f, (screenHpx - 140f).coerceAtLeast(24f))
                    }
                }
                .clickable { showSheet = true }
        ) {
            Row(
                modifier = Modifier.padding(horizontal = 10.dp, vertical = 8.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Icon(painterResource(R.drawable.ic_bobo_tune), contentDescription = "控制中心", tint = Color(0xFF60A5FA), modifier = Modifier.size(18.dp))
                Spacer(Modifier.width(5.dp))
                Text(
                    if (rttMs > 0.1) "${rttMs.roundToInt()}ms" else "BoBo",
                    color = Color.White,
                    fontSize = 11.sp,
                    fontWeight = FontWeight.Bold
                )
            }
        }
    }

    if (showSheet) {
        ModalBottomSheet(onDismissRequest = { showSheet = false }, containerColor = BoBoCardWhite) {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .verticalScroll(rememberScrollState())
                    .padding(horizontal = 18.dp, vertical = 6.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column {
                        Text("BoBo 控制中心 · $hostName", fontWeight = FontWeight.Bold, fontSize = 16.sp)
                        Text(
                            if (viewOnly) "仅查看协助模式" else "延迟 ${rttMs.roundToInt()} ms · ${if (touchpadMode) "触控板模式" else "直点触控模式"}",
                            fontSize = 12.sp,
                            color = BoBoInkMuted
                        )
                    }
                    Button(
                        onClick = { showSheet = false; onDisconnect() },
                        colors = ButtonDefaults.buttonColors(containerColor = BoBoDanger)
                    ) {
                        Icon(painterResource(R.drawable.ic_bobo_close), contentDescription = null, modifier = Modifier.size(16.dp))
                        Spacer(Modifier.width(4.dp))
                        Text("退出远控")
                    }
                }

                Row(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.horizontalScroll(rememberScrollState())) {
                    FilterChip(
                        selected = touchpadMode,
                        enabled = connected && !viewOnly,
                        onClick = { onToggleTouchpad(true) },
                        label = { Text("触控板鼠标") },
                        leadingIcon = { Icon(painterResource(R.drawable.ic_bobo_mouse), contentDescription = null, modifier = Modifier.size(16.dp)) }
                    )
                    FilterChip(
                        selected = !touchpadMode,
                        enabled = connected && !viewOnly,
                        onClick = { onToggleTouchpad(false) },
                        label = { Text("直点触控") },
                        leadingIcon = { Icon(painterResource(R.drawable.ic_bobo_touch), contentDescription = null, modifier = Modifier.size(16.dp)) }
                    )
                    FilterChip(
                        selected = showQuickKeys,
                        enabled = connected && !viewOnly,
                        onClick = { showQuickKeys = !showQuickKeys; showSheet = false },
                        label = { Text("快捷键盘栏") },
                        leadingIcon = { Icon(painterResource(R.drawable.ic_bobo_keyboard), contentDescription = null, modifier = Modifier.size(16.dp)) }
                    )
                }

                // Quick actions grid
                Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedButton(onClick = { showSheet = false; showTextInput = true }, enabled = connected && !viewOnly, modifier = Modifier.weight(1f)) {
                        Text("输入文字")
                    }
                    OutlinedButton(onClick = { onSyncClipboard(); showSheet = false }, enabled = connected && !viewOnly, modifier = Modifier.weight(1f)) {
                        Text("同步剪贴板")
                    }
                    OutlinedButton(onClick = onToggleOrientation, modifier = Modifier.weight(1f)) {
                        Icon(painterResource(R.drawable.ic_bobo_rotate), contentDescription = null, modifier = Modifier.size(16.dp))
                        Spacer(Modifier.width(4.dp))
                        Text("旋屏")
                    }
                }

                Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    FilterChip(
                        selected = privacyActive,
                        enabled = connected && !viewOnly && remotePlatform != BoBoProtocol.PLATFORM_ANDROID,
                        onClick = onTogglePrivacy,
                        label = { Text(if (privacyActive) "防窥已开" else "防窥屏") },
                        leadingIcon = { Icon(painterResource(R.drawable.ic_bobo_shield), contentDescription = null, modifier = Modifier.size(16.dp)) }
                    )
                    FilterChip(
                        selected = superDisplay,
                        enabled = connected && remotePlatform != BoBoProtocol.PLATFORM_ANDROID && remotePlatform != BoBoProtocol.PLATFORM_IOS,
                        onClick = onToggleSuperDisplay,
                        label = { Text("超级屏") }
                    )
                    FilterChip(
                        selected = muted,
                        onClick = onToggleMute,
                        label = { Text(if (muted) "已静音" else "声音开") },
                        leadingIcon = {
                            Icon(
                                painterResource(if (muted) R.drawable.ic_bobo_volume_off else R.drawable.ic_bobo_volume_up),
                                contentDescription = null,
                                modifier = Modifier.size(16.dp)
                            )
                        }
                    )
                    OutlinedButton(onClick = { onLockHost(); showSheet = false }, enabled = connected && !viewOnly) {
                        Icon(painterResource(R.drawable.ic_bobo_lock), contentDescription = null, modifier = Modifier.size(15.dp))
                        Spacer(Modifier.width(3.dp))
                        Text("锁屏")
                    }
                }

                if (monitors.size > 1) {
                    Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        for (monitor in monitors) {
                            FilterChip(
                                selected = selectedMonitor == monitor.index,
                                enabled = connected,
                                onClick = { onSwitchMonitor(monitor.index); showSheet = false },
                                label = { Text(monitor.name) }
                            )
                        }
                    }
                }
                Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedButton(onClick = { onSwitchQuality(1280, 720, 30, 4000); showSheet = false }, modifier = Modifier.weight(1f)) {
                        Text("流畅 720p")
                    }
                    OutlinedButton(onClick = { onSwitchQuality(1920, 1080, 60, 12000); showSheet = false }, modifier = Modifier.weight(1f)) {
                        Text("超清 1080p")
                    }
                    OutlinedButton(onClick = { onSwitchQuality(2560, 1440, 60, 22000); showSheet = false }, modifier = Modifier.weight(1f)) {
                        Text("原画 2K")
                    }
                }

                if (connected && !viewOnly && remotePlatform == BoBoProtocol.PLATFORM_ANDROID) {
                    Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        OutlinedButton(onClick = { onSendMobileNav(BoBoProtocol.MOBILE_NAV_BACK) }, modifier = Modifier.weight(1f)) { Text("返回") }
                        OutlinedButton(onClick = { onSendMobileNav(BoBoProtocol.MOBILE_NAV_HOME) }, modifier = Modifier.weight(1f)) { Text("主页") }
                        OutlinedButton(onClick = { onSendMobileNav(BoBoProtocol.MOBILE_NAV_RECENTS) }, modifier = Modifier.weight(1f)) { Text("多任务") }
                    }
                }
                Spacer(Modifier.height(14.dp))
            }
        }
    }

    if (showTextInput) {
        val focusRequester = remember { FocusRequester() }
        AlertDialog(
            onDismissRequest = { showTextInput = false },
            title = { Text("发送文本到远端输入") },
            text = {
                val keyboard = LocalSoftwareKeyboardController.current
                OutlinedTextField(
                    value = textDraft,
                    onValueChange = { textDraft = it },
                    label = { Text("支持中英文直接键入远端光标处") },
                    modifier = Modifier.fillMaxWidth().focusRequester(focusRequester)
                )
                LaunchedEffect(Unit) {
                    focusRequester.requestFocus()
                    keyboard?.show()
                }
            },
            confirmButton = {
                Button(onClick = {
                    if (textDraft.isNotEmpty()) onSendText(textDraft)
                    textDraft = ""
                    showTextInput = false
                }) {
                    Text("发送")
                }
            },
            dismissButton = {
                TextButton(onClick = { showTextInput = false }) { Text("取消") }
            }
        )
    }
}
