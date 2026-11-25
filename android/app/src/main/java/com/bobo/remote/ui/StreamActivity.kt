package com.bobo.remote.ui

import android.content.ClipboardManager
import android.content.Context
import android.content.pm.ActivityInfo
import android.os.Bundle
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.WindowManager
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
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
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Keyboard
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.Mouse
import androidx.compose.material.icons.filled.ScreenRotation
import androidx.compose.material.icons.filled.Shield
import androidx.compose.material.icons.filled.TouchApp
import androidx.compose.material.icons.filled.Tune
import androidx.compose.material.icons.filled.VolumeOff
import androidx.compose.material.icons.filled.VolumeUp
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
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableDoubleStateOf
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
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
import kotlin.math.abs
import kotlin.math.roundToInt

/**
 * 全屏低延迟远程桌面操控页（对齐 UU远程 移动端体验）：
 * - SurfaceView + MediaCodec H.264 硬件零拷贝解码 + 48kHz IMA-ADPCM 立体声音频
 * - 触控板模式（单指移动光标、单指点击左键、双指点击右键、双指滚轮）与直点触控模式自由切换
 * - 可拖拽贴边方形悬浮球 + UU 风格控制中心底部抽屉 + PC 快捷组合键栏 + 文本直输入
 */
class StreamActivity : ComponentActivity() {

    private lateinit var streamClient: BoBoStreamClient
    private val videoDecoder = H264HardwareDecoder()
    private val audioPlayer = AudioPlayer()

    private val streamConnectedState = mutableStateOf(false)
    private val statusTextState = mutableStateOf("正在建立 BoBo V6.2 加密连接...")
    private val rttMsState = mutableDoubleStateOf(0.0)
    private val remoteWidthState = mutableIntStateOf(1920)
    private val remoteHeightState = mutableIntStateOf(1080)
    private val remotePlatformState = mutableStateOf(BoBoProtocol.PLATFORM_WINDOWS)
    private val viewOnlyState = mutableStateOf(false)
    private val privacyActiveState = mutableStateOf(false)
    private val touchpadModeState = mutableStateOf(true)
    private val mutedState = mutableStateOf(false)
    private val superDisplayState = mutableStateOf(true)

    private var targetHost = ""
    private var targetPort = BoBoProtocol.DEFAULT_TCP_PORT
    private var targetName = ""

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        BoBoAppRuntime.init(this)

        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        WindowCompat.setDecorFitsSystemWindows(window, false)
        WindowInsetsControllerCompat(window, window.decorView).apply {
            hide(WindowInsetsCompat.Type.systemBars())
            systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }

        targetHost = intent.getStringExtra(EXTRA_HOST) ?: ""
        targetPort = intent.getIntExtra(EXTRA_PORT, BoBoProtocol.DEFAULT_TCP_PORT)
        targetName = intent.getStringExtra(EXTRA_NAME) ?: targetHost
        val autoPrivacy = intent.getBooleanExtra(EXTRA_PRIVACY, true)

        if (targetHost.isBlank()) {
            finish()
            return
        }

        streamClient = BoBoStreamClient(BoBoAppRuntime.identity)
        startStreamSession(autoPrivacy)

        setContent {
            BoBoTheme {
                StreamScreen(
                    hostName = targetName,
                    connected = streamConnectedState.value,
                    statusText = statusTextState.value,
                    rttMs = rttMsState.doubleValue,
                    remotePlatform = remotePlatformState.value,
                    viewOnly = viewOnlyState.value,
                    privacyActive = privacyActiveState.value,
                    touchpadMode = touchpadModeState.value,
                    muted = mutedState.value,
                    superDisplay = superDisplayState.value,
                    onSurfaceCreated = { holder -> videoDecoder.attachSurface(holder.surface) },
                    onSurfaceDestroyed = { videoDecoder.attachSurface(null) },
                    onTouchEvent = { viewW, viewH, ev -> handleRemoteTouch(viewW, viewH, ev) },
                    onToggleTouchpad = { touchpadModeState.value = it },
                    onToggleMute = {
                        mutedState.value = !mutedState.value
                        audioPlayer.setMutedState(mutedState.value)
                    },
                    onTogglePrivacy = {
                        val next = !privacyActiveState.value
                        privacyActiveState.value = next
                        streamClient.sendSetPrivacy(next)
                    },
                    onToggleSuperDisplay = {
                        val next = !superDisplayState.value
                        superDisplayState.value = next
                        val dm = resources.displayMetrics
                        streamClient.sendSetSuperDisplay(next, dm.widthPixels, dm.heightPixels)
                    },
                    onSwitchQuality = { w, h, fps, bps ->
                        streamClient.sendReconfigure(0, w, h, fps, bps)
                    },
                    onSendKeyCombo = { vks -> streamClient.sendKeyCombo(vks) },
                    onSendText = { txt -> streamClient.sendClipboardText(txt, true) },
                    onSyncClipboard = { syncPhoneClipboardToRemote() },
                    onSendMobileNav = { act -> streamClient.sendInput(BoBoProtocol.buildMobileNav(act)) },
                    onLockHost = { streamClient.sendLockWorkstation() },
                    onToggleOrientation = {
                        requestedOrientation = if (requestedOrientation == ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE) {
                            ActivityInfo.SCREEN_ORIENTATION_SENSOR_PORTRAIT
                        } else {
                            ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
                        }
                    },
                    onDisconnect = {
                        streamClient.disconnect()
                        finish()
                    }
                )
            }
        }
    }

    private fun startStreamSession(autoPrivacy: Boolean) {
        val dm = resources.displayMetrics
        val opts = BoBoStreamOptions(
            host = targetHost,
            port = targetPort,
            width = 1920,
            height = 1080,
            fps = 60,
            bitrateKbps = 12000,
            requestPrivacy = autoPrivacy,
            superDisplay = true,
            clientWidth = maxOf(dm.widthPixels, dm.heightPixels),
            clientHeight = minOf(dm.widthPixels, dm.heightPixels)
        )
        streamClient.connectAndStartStream(opts, object : BoBoStreamClient.Listener {
            override fun onStreamStarted(
                monitors: List<BoBoMonitorDescriptor>,
                width: Int,
                height: Int,
                fps: Int,
                remotePlatform: String,
                hostControlMode: String
            ) {
                runOnUiThread {
                    remoteWidthState.intValue = width
                    remoteHeightState.intValue = height
                    remotePlatformState.value = remotePlatform
                    viewOnlyState.value = (hostControlMode == BoBoProtocol.CONTROL_MODE_VIEW_ONLY)
                    touchpadModeState.value = (remotePlatform == BoBoProtocol.PLATFORM_WINDOWS || remotePlatform == BoBoProtocol.PLATFORM_MACOS)
                    streamConnectedState.value = true
                    statusTextState.value = ""
                    if (remotePlatform == BoBoProtocol.PLATFORM_WINDOWS || remotePlatform == BoBoProtocol.PLATFORM_MACOS) {
                        requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
                    }
                    if (autoPrivacy && remotePlatform != BoBoProtocol.PLATFORM_ANDROID && remotePlatform != BoBoProtocol.PLATFORM_IOS) {
                        privacyActiveState.value = true
                    }
                }
            }

            override fun onVideoFrame(frame: BoBoVideoPayload, timestampMs: Long) {
                videoDecoder.decode(frame.h264Data, frame.width, frame.height, frame.isKeyframe, timestampMs)
            }

            override fun onAudioFrame(frame: BoBoAudioPayload) {
                audioPlayer.playAdpcm(frame.sampleRate, frame.channels, frame.sampleCount, frame.adpcmData)
            }

            override fun onRttUpdated(rttMs: Double) {
                rttMsState.doubleValue = rttMs
            }

            override fun onPrivacyStateChanged(active: Boolean) {
                privacyActiveState.value = active
            }

            override fun onError(code: String, message: String) {
                runOnUiThread {
                    Toast.makeText(this@StreamActivity, message, Toast.LENGTH_LONG).show()
                    finish()
                }
            }

            override fun onDisconnected(reason: String) {
                runOnUiThread {
                    Toast.makeText(this@StreamActivity, "远控已结束：$reason", Toast.LENGTH_SHORT).show()
                    finish()
                }
            }
        })
    }

    // Touch gesture tracker
    private var lastTouchX = 0f
    private var lastTouchY = 0f
    private var downTouchX = 0f
    private var downTouchY = 0f
    private var downTimeMs = 0L
    private var maxPointers = 0
    private var movedBeyondTap = false

    private fun handleRemoteTouch(viewW: Int, viewH: Int, ev: MotionEvent): Boolean {
        if (viewOnlyState.value || !streamClient.isConnected || viewW <= 0 || viewH <= 0) return true
        val touchpad = touchpadModeState.value
        val isAndroidHost = remotePlatformState.value == BoBoProtocol.PLATFORM_ANDROID

        if (!touchpad) {
            // Direct touch mode: absolute position or kind=7 touch
            val nx = ((ev.x / viewW.toFloat()).coerceIn(0f, 1f) * 65535f).roundToInt()
            val ny = ((ev.y / viewH.toFloat()).coerceIn(0f, 1f) * 65535f).roundToInt()
            when (ev.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    if (isAndroidHost) {
                        streamClient.sendInput(BoBoProtocol.buildTouch(0, BoBoProtocol.TOUCH_ACTION_DOWN, nx, ny))
                    } else {
                        streamClient.sendInput(BoBoProtocol.buildMouseAbs(nx, ny))
                        streamClient.sendInput(BoBoProtocol.buildMouseButton(1, true))
                    }
                }
                MotionEvent.ACTION_MOVE -> {
                    if (isAndroidHost) {
                        streamClient.sendInput(BoBoProtocol.buildTouch(0, BoBoProtocol.TOUCH_ACTION_MOVE, nx, ny))
                    } else {
                        streamClient.sendInput(BoBoProtocol.buildMouseAbs(nx, ny))
                    }
                }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    if (isAndroidHost) {
                        streamClient.sendInput(BoBoProtocol.buildTouch(0, BoBoProtocol.TOUCH_ACTION_UP, nx, ny))
                    } else {
                        streamClient.sendInput(BoBoProtocol.buildMouseAbs(nx, ny))
                        streamClient.sendInput(BoBoProtocol.buildMouseButton(1, false))
                    }
                }
            }
            return true
        }

        // Touchpad mode (smooth relative trackpad for controlling Windows / macOS)
        when (ev.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                downTouchX = ev.x
                downTouchY = ev.y
                lastTouchX = ev.x
                lastTouchY = ev.y
                downTimeMs = System.currentTimeMillis()
                maxPointers = 1
                movedBeyondTap = false
            }
            MotionEvent.ACTION_POINTER_DOWN -> {
                maxPointers = maxOf(maxPointers, ev.pointerCount)
                lastTouchX = ev.x
                lastTouchY = ev.y
            }
            MotionEvent.ACTION_MOVE -> {
                val dx = ev.x - lastTouchX
                val dy = ev.y - lastTouchY
                lastTouchX = ev.x
                lastTouchY = ev.y
                if (abs(ev.x - downTouchX) > 12f || abs(ev.y - downTouchY) > 12f) {
                    movedBeyondTap = true
                }
                if (ev.pointerCount >= 2) {
                    val scroll = (dy * 2.4f).roundToInt()
                    if (scroll != 0) streamClient.sendInput(BoBoProtocol.buildMouseWheel(scroll, false))
                } else {
                    val rdx = (dx * 1.45f).roundToInt()
                    val rdy = (dy * 1.45f).roundToInt()
                    if (rdx != 0 || rdy != 0) {
                        streamClient.sendInput(BoBoProtocol.buildMouseRel(rdx, rdy))
                    }
                }
            }
            MotionEvent.ACTION_UP -> {
                val elapsed = System.currentTimeMillis() - downTimeMs
                if (!movedBeyondTap && elapsed < 260L) {
                    val btn = if (maxPointers >= 2) 3 else 1
                    streamClient.sendInput(BoBoProtocol.buildMouseButton(btn, true))
                    streamClient.sendInput(BoBoProtocol.buildMouseButton(btn, false))
                }
            }
        }
        return true
    }

    private fun syncPhoneClipboardToRemote() {
        val cm = getSystemService(Context.CLIPBOARD_SERVICE) as? ClipboardManager
        val txt = cm?.primaryClip?.getItemAt(0)?.coerceToText(this)?.toString().orEmpty()
        if (txt.isNotEmpty()) {
            streamClient.sendClipboardText(txt, true)
            Toast.makeText(this, "已发送剪贴板文本（${txt.length} 字）", Toast.LENGTH_SHORT).show()
        } else {
            Toast.makeText(this, "手机剪贴板为空", Toast.LENGTH_SHORT).show()
        }
    }

    override fun onKeyDown(keyCode: Int, event: KeyEvent?): Boolean {
        if (!viewOnlyState.value && streamClient.isConnected) {
            WindowsVkMapper.mapAndroidKeyCode(keyCode)?.let { mapped ->
                streamClient.sendInput(BoBoProtocol.buildKey(mapped.vk, true, mapped.extended))
                return true
            }
        }
        return super.onKeyDown(keyCode, event)
    }

    override fun onKeyUp(keyCode: Int, event: KeyEvent?): Boolean {
        if (!viewOnlyState.value && streamClient.isConnected) {
            WindowsVkMapper.mapAndroidKeyCode(keyCode)?.let { mapped ->
                streamClient.sendInput(BoBoProtocol.buildKey(mapped.vk, false, mapped.extended))
                return true
            }
        }
        return super.onKeyUp(keyCode, event)
    }

    override fun onDestroy() {
        streamClient.disconnect()
        videoDecoder.release()
        audioPlayer.release()
        super.onDestroy()
    }

    companion object {
        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"
        const val EXTRA_NAME = "name"
        const val EXTRA_PRIVACY = "privacy"
    }
}

@OptIn(ExperimentalMaterial3Api::class)
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
    onSurfaceCreated: (SurfaceHolder) -> Unit,
    onSurfaceDestroyed: () -> Unit,
    onTouchEvent: (Int, Int, MotionEvent) -> Boolean,
    onToggleTouchpad: (Boolean) -> Unit,
    onToggleMute: () -> Unit,
    onTogglePrivacy: () -> Unit,
    onToggleSuperDisplay: () -> Unit,
    onSwitchQuality: (Int, Int, Int, Int) -> Unit,
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
        AndroidView(
            factory = { ctx ->
                SurfaceView(ctx).apply {
                    holder.addCallback(object : SurfaceHolder.Callback {
                        override fun surfaceCreated(holder: SurfaceHolder) = onSurfaceCreated(holder)
                        override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) = Unit
                        override fun surfaceDestroyed(holder: SurfaceHolder) = onSurfaceDestroyed()
                    })
                    setOnTouchListener { v, ev -> onTouchEvent(v.width, v.height, ev) }
                }
            },
            modifier = Modifier.fillMaxSize()
        )

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
                Icon(Icons.Default.Tune, contentDescription = "控制中心", tint = Color(0xFF60A5FA), modifier = Modifier.size(18.dp))
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
                        Icon(Icons.Default.Close, contentDescription = null, modifier = Modifier.size(16.dp))
                        Spacer(Modifier.width(4.dp))
                        Text("退出远控")
                    }
                }

                // Interaction Mode Row
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    FilterChip(
                        selected = touchpadMode,
                        onClick = { onToggleTouchpad(true) },
                        label = { Text("触控板鼠标") },
                        leadingIcon = { Icon(Icons.Default.Mouse, contentDescription = null, modifier = Modifier.size(16.dp)) }
                    )
                    FilterChip(
                        selected = !touchpadMode,
                        onClick = { onToggleTouchpad(false) },
                        label = { Text("直点触控") },
                        leadingIcon = { Icon(Icons.Default.TouchApp, contentDescription = null, modifier = Modifier.size(16.dp)) }
                    )
                    FilterChip(
                        selected = showQuickKeys,
                        onClick = { showQuickKeys = !showQuickKeys; showSheet = false },
                        label = { Text("快捷键盘栏") },
                        leadingIcon = { Icon(Icons.Default.Keyboard, contentDescription = null, modifier = Modifier.size(16.dp)) }
                    )
                }

                // Quick actions grid
                Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedButton(onClick = { showSheet = false; showTextInput = true }, modifier = Modifier.weight(1f)) {
                        Text("输入文字")
                    }
                    OutlinedButton(onClick = { onSyncClipboard(); showSheet = false }, modifier = Modifier.weight(1f)) {
                        Text("同步剪贴板")
                    }
                    OutlinedButton(onClick = onToggleOrientation, modifier = Modifier.weight(1f)) {
                        Icon(Icons.Default.ScreenRotation, contentDescription = null, modifier = Modifier.size(16.dp))
                        Spacer(Modifier.width(4.dp))
                        Text("旋屏")
                    }
                }

                Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    FilterChip(
                        selected = privacyActive,
                        onClick = onTogglePrivacy,
                        label = { Text(if (privacyActive) "防窥已开" else "防窥屏") },
                        leadingIcon = { Icon(Icons.Default.Shield, contentDescription = null, modifier = Modifier.size(16.dp)) }
                    )
                    FilterChip(
                        selected = superDisplay,
                        onClick = onToggleSuperDisplay,
                        label = { Text("超级屏") }
                    )
                    FilterChip(
                        selected = muted,
                        onClick = onToggleMute,
                        label = { Text(if (muted) "已静音" else "声音开") },
                        leadingIcon = {
                            Icon(
                                if (muted) Icons.Default.VolumeOff else Icons.Default.VolumeUp,
                                contentDescription = null,
                                modifier = Modifier.size(16.dp)
                            )
                        }
                    )
                    OutlinedButton(onClick = { onLockHost(); showSheet = false }) {
                        Icon(Icons.Default.Lock, contentDescription = null, modifier = Modifier.size(15.dp))
                        Spacer(Modifier.width(3.dp))
                        Text("锁屏")
                    }
                }

                // Quality switcher
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

                if (remotePlatform == BoBoProtocol.PLATFORM_ANDROID) {
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
        AlertDialog(
            onDismissRequest = { showTextInput = false },
            title = { Text("发送文本到远端输入") },
            text = {
                OutlinedTextField(
                    value = textDraft,
                    onValueChange = { textDraft = it },
                    label = { Text("支持中英文直接键入远端光标处") },
                    modifier = Modifier.fillMaxWidth()
                )
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
