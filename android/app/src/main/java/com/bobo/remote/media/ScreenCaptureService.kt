package com.bobo.remote.media

import android.app.Activity
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import android.os.Environment
import android.os.IBinder
import android.util.DisplayMetrics
import android.view.Surface
import android.view.WindowManager
import com.bobo.remote.input.BoBoAccessibilityService
import com.bobo.remote.network.BoBoHostServer
import com.bobo.remote.network.BoBoLanDiscovery
import com.bobo.remote.protocol.BoBoDiscoveredPeer
import com.bobo.remote.protocol.BoBoIdentityStore
import com.bobo.remote.protocol.BoBoProtocol
import com.bobo.remote.ui.MainActivity
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import java.io.ByteArrayOutputStream
import java.io.File
import java.util.concurrent.atomic.AtomicBoolean

/**
 * 进程内单例运行时：统一托管身份证书、局域网发现与被控端服务（无独立后台守护进程）。
 */
object BoBoAppRuntime {
    private val initialized = AtomicBoolean(false)

    lateinit var identity: BoBoIdentityStore
        private set
    lateinit var hostServer: BoBoHostServer
        private set
    lateinit var discovery: BoBoLanDiscovery
        private set

    private val _lanPeers = MutableStateFlow<List<BoBoDiscoveredPeer>>(emptyList())
    val lanPeers: StateFlow<List<BoBoDiscoveredPeer>> = _lanPeers

    private val _activeSessionPeer = MutableStateFlow<String?>(null)
    val activeSessionPeer: StateFlow<String?> = _activeSessionPeer

    private val _hostReady = MutableStateFlow(false)
    val hostReady: StateFlow<Boolean> = _hostReady

    private val _projectionGranted = MutableStateFlow(false)
    val projectionGranted: StateFlow<Boolean> = _projectionGranted

    @Volatile internal var captureStarter: ((Int, Int, Int, Int) -> Unit)? = null
    @Volatile internal var captureStopper: (() -> Unit)? = null
    @Volatile internal var keyframeRequester: (() -> Unit)? = null

    fun init(context: Context) {
        if (!initialized.compareAndSet(false, true)) return
        val appCtx = context.applicationContext
        val deviceName = "${Build.MANUFACTURER} ${Build.MODEL}".trim().ifBlank { "Android Device" }
        identity = BoBoIdentityStore(File(appCtx.filesDir, "bobo6"), deviceName)
        identity.ensureLoaded()

        val dlDir = File(
            appCtx.getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS) ?: appCtx.filesDir,
            "BoBoRemote"
        )
        hostServer = BoBoHostServer(
            identity = identity,
            downloadDir = dlDir,
            requestedPort = BoBoProtocol.DEFAULT_TCP_PORT,
            screenMetricsProvider = { queryScreenMetrics(appCtx) },
            onStartMediaCapture = { w, h, fps, bps -> captureStarter?.invoke(w, h, fps, bps) },
            onStopMediaCapture = { captureStopper?.invoke() },
            onRequestKeyframe = { keyframeRequester?.invoke() },
            onInputEvent = { pkt -> BoBoAccessibilityService.instance?.handleInputPacket(pkt) },
            onClipboardText = { txt -> BoBoAccessibilityService.instance?.setClipboardText(txt) },
            onSessionChanged = { active, peer -> _activeSessionPeer.value = if (active) peer else null }
        )

        val res = hostServer.start()
        _hostReady.value = res.isSuccess

        discovery = BoBoLanDiscovery(
            identity = identity,
            tcpPortProvider = { hostServer.boundPort },
            allowIncomingProvider = { hostServer.allowIncomingControl && _projectionGranted.value },
            onPeersUpdated = { list -> _lanPeers.value = list }
        )
        discovery.start()
    }

    internal fun setProjectionGranted(granted: Boolean) {
        _projectionGranted.value = granted
        if (initialized.get()) discovery.sendProbeNow()
    }

    fun shutdown() {
        if (!initialized.compareAndSet(true, false)) return
        try { discovery.stop() } catch (_: Exception) {}
        try { hostServer.stop() } catch (_: Exception) {}
        _hostReady.value = false
        _activeSessionPeer.value = null
        _projectionGranted.value = false
    }

    private fun queryScreenMetrics(ctx: Context): Triple<Int, Int, Int> {
        val wm = ctx.getSystemService(Context.WINDOW_SERVICE) as WindowManager
        val (w, h) = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            val b = wm.maximumWindowMetrics.bounds
            Pair(b.width().coerceAtLeast(360), b.height().coerceAtLeast(640))
        } else {
            @Suppress("DEPRECATION")
            val dm = DisplayMetrics().also { wm.defaultDisplay.getRealMetrics(it) }
            Pair(dm.widthPixels.coerceAtLeast(360), dm.heightPixels.coerceAtLeast(640))
        }
        return Triple(w, h, 0)
    }
}

/**
 * Android MediaProjection 录屏推流前台服务（硬编 H.264 Annex-B，每个 IDR 自动附带 SPS+PPS）。
 */
class ScreenCaptureService : Service() {

    private var mediaProjection: MediaProjection? = null
    private var virtualDisplay: VirtualDisplay? = null
    private var encoder: MediaCodec? = null
    private var inputSurface: Surface? = null
    private val encoding = AtomicBoolean(false)
    @Volatile private var cachedConfigNal: ByteArray? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        BoBoAppRuntime.init(this)
        createNotificationChannel()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP -> {
                stopEncoding()
                stopProjection()
                stopForeground(STOP_FOREGROUND_REMOVE)
                stopSelf()
                return START_NOT_STICKY
            }
            ACTION_START_PROJECTION -> {
                val notif = buildNotification("BoBo远程 被控就绪 · 等待已授权设备连接")
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                    startForeground(NOTIFICATION_ID, notif, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
                } else {
                    startForeground(NOTIFICATION_ID, notif)
                }
                val resultCode = intent.getIntExtra(EXTRA_RESULT_CODE, Activity.RESULT_CANCELED)
                val resultData: Intent? = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                    intent.getParcelableExtra(EXTRA_RESULT_DATA, Intent::class.java)
                } else {
                    @Suppress("DEPRECATION")
                    intent.getParcelableExtra(EXTRA_RESULT_DATA)
                }
                if (resultCode == Activity.RESULT_OK && resultData != null) {
                    val mpm = getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
                    stopProjection()
                    val proj = mpm.getMediaProjection(resultCode, resultData)
                    proj.registerCallback(object : MediaProjection.Callback() {
                        override fun onStop() {
                            stopEncoding()
                            BoBoAppRuntime.setProjectionGranted(false)
                        }
                    }, null)
                    mediaProjection = proj
                    BoBoAppRuntime.captureStarter = { w, h, fps, bps -> startEncoding(w, h, fps, bps) }
                    BoBoAppRuntime.captureStopper = { stopEncoding() }
                    BoBoAppRuntime.keyframeRequester = { requestSyncFrame() }
                    BoBoAppRuntime.setProjectionGranted(true)
                }
            }
        }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        BoBoAppRuntime.captureStarter = null
        BoBoAppRuntime.captureStopper = null
        BoBoAppRuntime.keyframeRequester = null
        stopEncoding()
        stopProjection()
        super.onDestroy()
    }

    private fun startEncoding(width: Int, height: Int, fps: Int, bitrateKbps: Int) {
        stopEncoding()
        val proj = mediaProjection ?: return
        try {
            val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height).apply {
                setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
                setInteger(MediaFormat.KEY_BIT_RATE, bitrateKbps * 1000)
                setInteger(MediaFormat.KEY_FRAME_RATE, fps)
                setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 2)
                setInteger(MediaFormat.KEY_BITRATE_MODE, MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR)
            }
            val mc = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
            mc.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
            val surf = mc.createInputSurface()
            mc.start()

            val dpi = resources.displayMetrics.densityDpi
            val vd = proj.createVirtualDisplay(
                "BoBoRemote-Capture",
                width,
                height,
                dpi,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
                surf,
                null,
                null
            )

            encoder = mc
            inputSurface = surf
            virtualDisplay = vd
            cachedConfigNal = null
            encoding.set(true)

            Thread({ drainLoop(mc, width, height) }, "BoBo6-Android-Enc").apply { isDaemon = true; start() }
        } catch (_: Exception) {
            stopEncoding()
        }
    }

    private fun drainLoop(mc: MediaCodec, width: Int, height: Int) {
        val info = MediaCodec.BufferInfo()
        val startMs = System.currentTimeMillis()
        while (encoding.get()) {
            try {
                val outIdx = mc.dequeueOutputBuffer(info, 15_000L)
                if (outIdx >= 0) {
                    val buf = mc.getOutputBuffer(outIdx)
                    if (buf != null && info.size > 0) {
                        buf.position(info.offset)
                        buf.limit(info.offset + info.size)
                        val bytes = ByteArray(info.size)
                        buf.get(bytes)

                        val isConfig = (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0
                        val isKey = (info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0
                        if (isConfig) {
                            cachedConfigNal = bytes
                        } else {
                            val frameBytes = if (isKey && cachedConfigNal != null) {
                                ByteArrayOutputStream(cachedConfigNal!!.size + bytes.size).apply {
                                    write(cachedConfigNal!!)
                                    write(bytes)
                                }.toByteArray()
                            } else {
                                bytes
                            }
                            val tsMs = (System.currentTimeMillis() - startMs).coerceAtLeast(0L)
                            BoBoAppRuntime.hostServer.broadcastVideoFrame(width, height, isKey, frameBytes, tsMs)
                        }
                    }
                    mc.releaseOutputBuffer(outIdx, false)
                }
            } catch (_: Exception) {
                break
            }
        }
    }

    private fun requestSyncFrame() {
        try {
            val params = Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0) }
            encoder?.setParameters(params)
        } catch (_: Exception) {
        }
    }

    private fun stopEncoding() {
        encoding.set(false)
        try { virtualDisplay?.release() } catch (_: Exception) {}
        virtualDisplay = null
        try { encoder?.stop() } catch (_: Exception) {}
        try { encoder?.release() } catch (_: Exception) {}
        encoder = null
        try { inputSurface?.release() } catch (_: Exception) {}
        inputSurface = null
    }

    private fun stopProjection() {
        try { mediaProjection?.stop() } catch (_: Exception) {}
        mediaProjection = null
        BoBoAppRuntime.setProjectionGranted(false)
    }

    private fun createNotificationChannel() {
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        val ch = NotificationChannel(CHANNEL_ID, "BoBo远程被控服务", NotificationManager.IMPORTANCE_LOW)
        nm.createNotificationChannel(ch)
    }

    private fun buildNotification(content: String): Notification {
        val pi = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        return Notification.Builder(this, CHANNEL_ID)
            .setContentTitle("BoBo远程")
            .setContentText(content)
            .setSmallIcon(android.R.drawable.ic_menu_share)
            .setContentIntent(pi)
            .setOngoing(true)
            .build()
    }

    companion object {
        const val ACTION_START_PROJECTION = "com.bobo.remote.action.START_PROJECTION"
        const val ACTION_STOP = "com.bobo.remote.action.STOP"
        const val EXTRA_RESULT_CODE = "result_code"
        const val EXTRA_RESULT_DATA = "result_data"
        private const val CHANNEL_ID = "bobo_remote_host"
        private const val NOTIFICATION_ID = 49810
    }
}
