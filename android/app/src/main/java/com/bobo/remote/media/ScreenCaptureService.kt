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
import android.os.Handler
import android.os.HandlerThread
import android.os.IBinder
import android.os.SystemClock
import android.util.Log
import android.view.Display
import android.view.Surface
import android.view.WindowManager
import com.bobo.remote.input.BoBoAccessibilityService
import com.bobo.remote.network.BoBoHostServer
import com.bobo.remote.network.BoBoLanDiscovery
import com.bobo.remote.protocol.BoBoAuthorizedPeer
import com.bobo.remote.protocol.BoBoDiscoveredPeer
import com.bobo.remote.protocol.BoBoIdentityStore
import com.bobo.remote.protocol.BoBoProtocol
import com.bobo.remote.ui.MainActivity
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import java.io.File
import java.nio.ByteBuffer
import java.util.concurrent.FutureTask
import java.util.concurrent.TimeUnit

object BoBoAppRuntime {
    private var initialized = false

    lateinit var identity: BoBoIdentityStore
        private set
    lateinit var hostServer: BoBoHostServer
        private set
    lateinit var discovery: BoBoLanDiscovery
        private set

    private val _lanPeers = MutableStateFlow<List<BoBoDiscoveredPeer>>(emptyList())
    val lanPeers: StateFlow<List<BoBoDiscoveredPeer>> = _lanPeers
    private val _authorizedPeers = MutableStateFlow<List<BoBoAuthorizedPeer>>(emptyList())
    val authorizedPeers: StateFlow<List<BoBoAuthorizedPeer>> = _authorizedPeers
    private val _activeSessionPeer = MutableStateFlow<String?>(null)
    val activeSessionPeer: StateFlow<String?> = _activeSessionPeer
    private val _hostReady = MutableStateFlow(false)
    val hostReady: StateFlow<Boolean> = _hostReady
    private val _projectionGranted = MutableStateFlow(false)
    val projectionGranted: StateFlow<Boolean> = _projectionGranted
    private val _deviceCode = MutableStateFlow("")
    val deviceCode: StateFlow<String> = _deviceCode

    @Volatile private var captureService: ScreenCaptureService? = null

    @Synchronized
    fun init(context: Context) {
        if (initialized) return
        val app = context.applicationContext
        val deviceName = "${Build.MANUFACTURER} ${Build.MODEL}".trim().ifBlank { "Android Device" }
        identity = BoBoIdentityStore(File(app.filesDir, "bobo6"), deviceName)
        identity.ensureLoaded()
        _deviceCode.value = identity.formattedDeviceCode
        refreshAuthorizedPeers()

        val downloads = File(app.getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS) ?: app.filesDir, "BoBoRemote")
        hostServer = BoBoHostServer(
            identity = identity,
            downloadDir = downloads,
            requestedPort = BoBoProtocol.DEFAULT_TCP_PORT,
            screenMetricsProvider = { queryScreenMetrics(app) },
            isCaptureReadyProvider = { _projectionGranted.value },
            onStartMediaCapture = { w, h, fps, bps -> captureService?.configureStream(w, h, fps, bps) },
            onReconfigureCapture = { w, h, fps, bps -> captureService?.configureStream(w, h, fps, bps) },
            onStopMediaCapture = { captureService?.stopStream() },
            onRequestKeyframe = { captureService?.requestKeyframe() },
            onInputEvent = { packet ->
                if (captureService?.capturesDefaultDisplay == true) {
                    BoBoAccessibilityService.instance?.handleInputPacket(packet)
                }
            },
            onClipboardText = { text, paste ->
                if (captureService?.capturesDefaultDisplay == true) {
                    BoBoAccessibilityService.instance?.setClipboardText(text, paste)
                }
            },
            onLockWorkstation = {
                if (captureService?.capturesDefaultDisplay == true) {
                    BoBoAccessibilityService.instance?.performMobileNav(BoBoProtocol.MOBILE_NAV_LOCK)
                }
            },
            onSessionChanged = { active, peer ->
                if (!active) BoBoAccessibilityService.instance?.releaseInput()
                _activeSessionPeer.value = if (active) peer else null
            },
            onPeerPaired = { refreshAuthorizedPeers() }
        )
        _hostReady.value = hostServer.start().isSuccess
        discovery = BoBoLanDiscovery(
            app,
            identity,
            tcpPortProvider = { hostServer.boundPort },
            allowIncomingProvider = { _hostReady.value && hostServer.allowIncomingControl && _projectionGranted.value },
            onPeersUpdated = { _lanPeers.value = it }
        )
        discovery.start()
        initialized = true
    }

    @Synchronized
    fun refreshAuthorizedPeers() {
        _deviceCode.value = identity.formattedDeviceCode
        _authorizedPeers.value = identity.listAuthorizedPeers()
    }

    @Synchronized
    fun reloadAfterIdentityImport() {
        if (!initialized) return
        hostServer.stop()
        _hostReady.value = hostServer.start().isSuccess
        refreshAuthorizedPeers()
        discovery.sendProbeNow()
    }

    fun setAllowIncoming(allow: Boolean) {
        hostServer.allowIncomingControl = allow
        discovery.sendProbeNow()
    }

    @Synchronized
    internal fun attachCaptureService(service: ScreenCaptureService) {
        captureService = service
    }

    @Synchronized
    internal fun detachCaptureService(service: ScreenCaptureService): Boolean {
        if (captureService !== service) return false
        val wasGranted = _projectionGranted.value
        setProjectionGranted(service, false)
        captureService = null
        return wasGranted
    }

    @Synchronized
    internal fun setProjectionGranted(service: ScreenCaptureService, granted: Boolean) {
        if (captureService !== service) return
        _projectionGranted.value = granted
        discovery.sendProbeNow()
    }

    @Synchronized
    fun shutdown() {
        if (!initialized) return
        initialized = false
        discovery.stop()
        hostServer.stop()
        _hostReady.value = false
        _activeSessionPeer.value = null
        _projectionGranted.value = false
    }
}

@Suppress("DEPRECATION")
internal fun queryScreenMetrics(context: Context): Triple<Int, Int, Int> {
    val windowManager = context.getSystemService(WindowManager::class.java)
    val display = context.getSystemService(DisplayManager::class.java)?.getDisplay(Display.DEFAULT_DISPLAY)
    val dimensions = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R && windowManager != null) {
        val bounds = windowManager.maximumWindowMetrics.bounds
        bounds.width() to bounds.height()
    } else {
        val size = android.graphics.Point()
        display?.getRealSize(size)
        size.x to size.y
    }
    val rotation = when (display?.rotation) {
        Surface.ROTATION_90 -> 90
        Surface.ROTATION_180 -> 180
        Surface.ROTATION_270 -> 270
        else -> 0
    }
    return Triple(dimensions.first.coerceAtLeast(1), dimensions.second.coerceAtLeast(1), rotation)
}

/** Owns a single virtual display for the lifetime of one granted MediaProjection. */
class ScreenCaptureService : Service() {
    private data class StreamRequest(val maxWidth: Int, val maxHeight: Int, val fps: Int, val bitrateKbps: Int)
    private data class Encoder(
        val codec: MediaCodec,
        val surface: Surface,
        val width: Int,
        val height: Int,
        val parameterSets: AvcNalUnits.ParameterSets = AvcNalUnits.ParameterSets()
    )

    private lateinit var worker: HandlerThread
    private lateinit var captureHandler: Handler
    private lateinit var displayManager: DisplayManager
    @Volatile private var stopping = false
    @Volatile internal var capturesDefaultDisplay = false
        private set

    // Capture objects are owned by captureHandler, including MediaCodec output buffer operations.
    private var projection: MediaProjection? = null
    private var projectionCallback: MediaProjection.Callback? = null
    private var virtualDisplay: VirtualDisplay? = null
    private var encoder: Encoder? = null
    private var audio: PlaybackAudioCapture? = null
    private var stream: StreamRequest? = null
    private var capturedSize = 1 to 1
    private var densityDpi = 0
    private var streamStartMs = 0L
    private var encoderRecoveryUsed = false
    private val drain = Runnable { drainMedia() }
    private val outputInfo = MediaCodec.BufferInfo()

    private val displayListener = object : DisplayManager.DisplayListener {
        override fun onDisplayAdded(displayId: Int) = Unit
        override fun onDisplayRemoved(displayId: Int) = Unit
        override fun onDisplayChanged(displayId: Int) {
            if (displayId != Display.DEFAULT_DISPLAY || projection == null) return
            if (Build.VERSION.SDK_INT < Build.VERSION_CODES.UPSIDE_DOWN_CAKE || capturesDefaultDisplay) {
                val (w, h) = queryScreenMetrics(this@ScreenCaptureService)
                resizeContent(w, h)
            }
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        BoBoAppRuntime.init(this)
        worker = HandlerThread("BoBo6-Android-Capture").apply { start() }
        captureHandler = Handler(worker.looper)
        displayManager = checkNotNull(getSystemService(DisplayManager::class.java))
        displayManager.registerDisplayListener(displayListener, captureHandler)
        BoBoAppRuntime.attachCaptureService(this)
        getSystemService(NotificationManager::class.java)?.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "BoBo远程被控服务", NotificationManager.IMPORTANCE_LOW)
        )
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action != ACTION_START_PROJECTION) {
            if (intent?.action == ACTION_STOP || !BoBoAppRuntime.projectionGranted.value) stopSelf()
            return START_NOT_STICKY
        }
        val resultCode = intent.getIntExtra(EXTRA_RESULT_CODE, Activity.RESULT_CANCELED)
        val data = runCatching {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                intent.getParcelableExtra(EXTRA_RESULT_DATA, Intent::class.java)
            } else {
                @Suppress("DEPRECATION")
                intent.getParcelableExtra<Intent>(EXTRA_RESULT_DATA)
            }
        }.getOrNull()
        if (resultCode != Activity.RESULT_OK || data == null) {
            if (!BoBoAppRuntime.projectionGranted.value) stopSelfResult(startId)
            return START_NOT_STICKY
        }

        try {
            val notification = buildNotification()
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
            } else {
                startForeground(NOTIFICATION_ID, notification)
            }
            captureHandler.post { openProjection(resultCode, data, startId) }
        } catch (error: RuntimeException) {
            Log.w(TAG, "Unable to start the screen capture foreground service", error)
            stopSelfResult(startId)
        }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        stopping = true
        displayManager.unregisterDisplayListener(displayListener)
        capturesDefaultDisplay = false
        if (BoBoAppRuntime.detachCaptureService(this)) {
            BoBoAccessibilityService.instance?.releaseInput()
            disconnectStream("手机已停止屏幕共享")
        }
        captureHandler.post { closeProjection() }
        worker.quitSafely()
        stopForeground(STOP_FOREGROUND_REMOVE)
        super.onDestroy()
    }

    private fun openProjection(resultCode: Int, data: Intent, startId: Int) {
        if (stopping || projection != null) return
        try {
            val manager = checkNotNull(getSystemService(MediaProjectionManager::class.java))
            val granted = checkNotNull(manager.getMediaProjection(resultCode, data))
            projection = granted
            val callback = object : MediaProjection.Callback() {
                override fun onStop() {
                    if (projection !== granted) return
                    signalProjectionEnded("系统已停止 Android 屏幕共享")
                    closeProjection()
                    stopSelf()
                }

                override fun onCapturedContentResize(width: Int, height: Int) {
                    if (projection === granted) resizeContent(width, height)
                }
            }
            projectionCallback = callback
            granted.registerCallback(callback, captureHandler)
            val (width, height) = queryScreenMetrics(this)
            capturedSize = width to height
            densityDpi = resources.configuration.densityDpi.coerceAtLeast(1)
            virtualDisplay = checkNotNull(granted.createVirtualDisplay(
                "BoBoRemote-Capture", width, height, densityDpi,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, null, null, captureHandler
            ))
            if (!stopping) {
                capturesDefaultDisplay = Build.VERSION.SDK_INT < Build.VERSION_CODES.UPSIDE_DOWN_CAKE
                BoBoAppRuntime.setProjectionGranted(this, true)
            }
        } catch (error: RuntimeException) {
            Log.w(TAG, "Android screen capture authorization could not be used", error)
            closeProjection()
            stopSelfResult(startId)
        }
    }

    internal fun configureStream(width: Int, height: Int, fps: Int, bitrateKbps: Int): Pair<Int, Int>? {
        if (stopping) return null
        val request = StreamRequest(width, height, fps, bitrateKbps)
        val job = FutureTask<Pair<Int, Int>?> {
            if (stopping || virtualDisplay == null) return@FutureTask null
            val previous = stream
            if (previous == request) {
                encoder?.let {
                    syncFrame()
                    return@FutureTask it.width to it.height
                }
            }
            if (previous == null) streamStartMs = SystemClock.elapsedRealtime()
            stream = request
            encoderRecoveryUsed = false
            val configured = replaceEncoder(request)
            if (configured == null) {
                stopStreaming()
            } else if (audio == null && Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                projection?.let { audio = PlaybackAudioCapture.start(this, it) }
            }
            configured
        }
        if (!captureHandler.post(job)) return null
        return try {
            job.get(4, TimeUnit.SECONDS)
        } catch (error: Exception) {
            job.cancel(false)
            captureHandler.post { if (stream === request) stopStreaming() }
            if (error is InterruptedException) Thread.currentThread().interrupt()
            Log.w(TAG, "Screen encoder setup did not complete", error)
            null
        }
    }

    internal fun stopStream() {
        if (!stopping) captureHandler.post(::stopStreaming)
    }

    internal fun requestKeyframe() {
        if (!stopping) captureHandler.post(::syncFrame)
    }

    private fun resizeContent(width: Int, height: Int) {
        if (stopping || width <= 0 || height <= 0) return
        val newSize = width to height
        val (displayWidth, displayHeight) = queryScreenMetrics(this)
        val matchesDisplay = width == displayWidth && height == displayHeight
        if (capturesDefaultDisplay && (!matchesDisplay || capturedSize != newSize)) {
            BoBoAccessibilityService.instance?.releaseInput()
        }
        capturesDefaultDisplay = matchesDisplay
        val dpi = resources.configuration.densityDpi.coerceAtLeast(1)
        if (capturedSize == newSize && densityDpi == dpi) return
        capturedSize = newSize
        densityDpi = dpi
        val current = stream ?: return
        val size = replaceEncoder(current)
        if (size == null) {
            failStream("Android 屏幕旋转后无法重新启动编码器")
        } else {
            BoBoAppRuntime.hostServer.notifyResolutionChanged(size.first, size.second, current.fps, current.bitrateKbps)
        }
    }

    private fun replaceEncoder(request: StreamRequest): Pair<Int, Int>? {
        val display = virtualDisplay ?: return null
        releaseEncoder()
        var codec: MediaCodec? = null
        var surface: Surface? = null
        var started = false
        try {
            val created = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
            codec = created
            val capabilities = created.codecInfo.getCapabilitiesForType(MediaFormat.MIMETYPE_VIDEO_AVC)
            val video = checkNotNull(capabilities.videoCapabilities)
            val (sourceW, sourceH) = capturedSize
            val longer = maxOf(request.maxWidth, request.maxHeight).coerceIn(320, 2560)
            val shorter = minOf(request.maxWidth, request.maxHeight).coerceIn(240, 1920)
            val maxW = if (sourceW >= sourceH) longer else shorter
            val maxH = if (sourceW >= sourceH) shorter else longer
            var scale = minOf(1.0, maxW.toDouble() / sourceW, maxH.toDouble() / sourceH)
            var dimensions: Pair<Int, Int>? = null
            while (true) {
                val w = (sourceW * scale).toInt() / video.widthAlignment * video.widthAlignment
                val h = (sourceH * scale).toInt() / video.heightAlignment * video.heightAlignment
                if (w < video.supportedWidths.lower || h < video.supportedHeights.lower) break
                if (video.isSizeSupported(w, h)) {
                    dimensions = w to h
                    break
                }
                scale *= 0.8
            }
            val (width, height) = checkNotNull(dimensions) { "No AVC encoder size for ${sourceW}x$sourceH" }
            val fps = request.fps.coerceAtMost(video.getSupportedFrameRatesFor(width, height).upper.toInt())
            val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height).apply {
                setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
                setInteger(MediaFormat.KEY_BIT_RATE, (request.bitrateKbps * 1000).coerceIn(
                    video.bitrateRange.lower, video.bitrateRange.upper
                ))
                setInteger(MediaFormat.KEY_FRAME_RATE, fps.coerceAtLeast(1))
                setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 2)
                if (capabilities.encoderCapabilities?.isBitrateModeSupported(MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR) == true) {
                    setInteger(MediaFormat.KEY_BITRATE_MODE, MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR)
                }
            }
            created.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
            val input = created.createInputSurface()
            surface = input
            created.start()
            started = true
            display.resize(width, height, densityDpi)
            display.setSurface(input)
            encoder = Encoder(created, input, width, height)
            syncFrame()
            captureHandler.post(drain)
            return width to height
        } catch (error: Exception) {
            Log.w(TAG, "Unable to configure the Android H.264 encoder", error)
            runCatching { display.setSurface(null) }
            if (started) runCatching { codec?.stop() }
            runCatching { codec?.release() }
            runCatching { surface?.release() }
            return null
        }
    }

    private fun drainMedia() {
        if (stopping) return
        val active = encoder ?: return
        try {
            for (i in 0 until 8) {
                val outputIndex = active.codec.dequeueOutputBuffer(outputInfo, 0L)
                if (outputIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                    val format = active.codec.outputFormat
                    cacheConfig(active, format.getByteBuffer("csd-0"))
                    cacheConfig(active, format.getByteBuffer("csd-1"))
                    continue
                }
                if (outputIndex < 0) break
                try {
                    val bytes = active.codec.getOutputBuffer(outputIndex)
                    if (bytes != null && outputInfo.size > 0) {
                        bytes.limit(outputInfo.offset + outputInfo.size)
                        bytes.position(outputInfo.offset)
                        publishVideo(active, ByteArray(outputInfo.size).also { bytes.get(it) }, outputInfo.flags)
                    }
                } finally {
                    active.codec.releaseOutputBuffer(outputIndex, false)
                }
            }
        } catch (error: RuntimeException) {
            Log.w(TAG, "Android H.264 encoder stopped producing output", error)
            recoverEncoder()
            return
        }
        audio?.let { recording ->
            if (!recording.drain { rate, frames, packet ->
                    BoBoAppRuntime.hostServer.broadcastAudioFrame(rate, 2, frames, packet)
                }) {
                recording.release()
                audio = null
            }
        }
        captureHandler.postDelayed(drain, 5L)
    }

    private fun cacheConfig(active: Encoder, buffer: ByteBuffer?) {
        val original = buffer?.duplicate() ?: return
        val bytes = ByteArray(original.remaining()).also { original.get(it) }
        val annexB = AvcNalUnits.toAnnexB(bytes, codecConfig = true) ?: return
        active.parameterSets.update(annexB, AvcNalUnits.inspect(annexB))
    }

    private fun publishVideo(active: Encoder, bytes: ByteArray, flags: Int) {
        val isConfig = flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0
        val annexB = AvcNalUnits.toAnnexB(bytes, isConfig) ?: return
        val units = AvcNalUnits.inspect(annexB)
        active.parameterSets.update(annexB, units)
        if (units.none { it.type in 1..5 }) return
        val isIdr = units.any { it.type == 5 }
        val packet = if (isIdr) active.parameterSets.prependMissing(annexB, units) ?: return else annexB
        BoBoAppRuntime.hostServer.broadcastVideoFrame(
            active.width, active.height, isIdr, packet, SystemClock.elapsedRealtime() - streamStartMs
        )
    }

    private fun syncFrame() {
        val active = encoder ?: return
        runCatching {
            active.codec.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0) })
        }
    }

    private fun recoverEncoder() {
        val request = stream ?: return
        if (!encoderRecoveryUsed) {
            encoderRecoveryUsed = true
            val size = replaceEncoder(request)
            if (size != null) {
                BoBoAppRuntime.hostServer.notifyResolutionChanged(size.first, size.second, request.fps, request.bitrateKbps)
                return
            }
        }
        failStream("Android 视频编码器已停止。请重新连接。")
    }

    private fun failStream(reason: String) {
        stopStreaming()
        disconnectStream(reason)
    }

    private fun signalProjectionEnded(reason: String) {
        capturesDefaultDisplay = false
        BoBoAppRuntime.setProjectionGranted(this, false)
        BoBoAccessibilityService.instance?.releaseInput()
        disconnectStream(reason)
    }

    private fun disconnectStream(reason: String) {
        // The host may still be waiting on a configuration request on the capture worker.
        Thread({ BoBoAppRuntime.hostServer.disconnectActiveStream(reason) }, "BoBo6-Capture-Disconnect").apply {
            isDaemon = true
            start()
        }
    }

    private fun stopStreaming() {
        stream = null
        releaseEncoder()
        audio?.release()
        audio = null
    }

    private fun releaseEncoder() {
        captureHandler.removeCallbacks(drain)
        val current = encoder ?: return
        encoder = null
        runCatching { virtualDisplay?.setSurface(null) }
        runCatching { current.codec.stop() }
        runCatching { current.codec.release() }
        runCatching { current.surface.release() }
    }

    private fun closeProjection() {
        stopStreaming()
        runCatching { virtualDisplay?.release() }
        virtualDisplay = null
        val current = projection ?: return
        projection = null
        projectionCallback?.let { runCatching { current.unregisterCallback(it) } }
        projectionCallback = null
        runCatching { current.stop() }
    }

    private fun buildNotification(): Notification {
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val stop = PendingIntent.getService(this, 0, Intent(this, javaClass).setAction(ACTION_STOP), PendingIntent.FLAG_IMMUTABLE)
        return Notification.Builder(this, CHANNEL_ID)
            .setContentTitle("BoBo远程")
            .setContentText("Android 屏幕共享已开启")
            .setSmallIcon(android.R.drawable.ic_menu_share)
            .setContentIntent(open)
            .addAction(Notification.Action.Builder(0, "停止共享", stop).build())
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
        private const val TAG = "BoBoScreenCapture"
    }
}
