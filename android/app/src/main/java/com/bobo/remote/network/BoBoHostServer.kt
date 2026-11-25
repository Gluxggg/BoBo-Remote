package com.bobo.remote.network

import com.bobo.remote.protocol.BoBoAuthorizedPeer
import com.bobo.remote.protocol.BoBoIdentityStore
import com.bobo.remote.protocol.BoBoInputPacket
import com.bobo.remote.protocol.BoBoLocalPairSession
import com.bobo.remote.protocol.BoBoMonitorDescriptor
import com.bobo.remote.protocol.BoBoPacketType
import com.bobo.remote.protocol.BoBoProtocol
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.io.OutputStream
import java.io.RandomAccessFile
import java.nio.file.Files
import java.security.MessageDigest
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong
import javax.net.ssl.SSLServerSocket
import javax.net.ssl.SSLSocket

/**
 * Android 原生 BoBo V6.3 被控端服务（仅随 BoBo远程 App 运行，关闭即停服断口）。
 * - 监听 TCP 49810（TLS 1.2+），强制 64 KiB 预认证帧限流与连接超时；
 * - 支持 4 位临时配对码 HMAC + RSA-SHA256 双向密钥身份证明（连续错 5 次自动作废防爆破）；
 * - 实时分发 H.264 录屏帧与 IMA-ADPCM 音频帧（丢 P 帧自动防抖请求恢复 IDR）；
 * - 接收鼠标/多指触控(kind=7)/移动导航(kind=8)/键盘/锁屏/剪贴板并交由 BoBoAccessibilityService 免 Root 执行；
 * - 支持 TLS SHA-256 随机偏移安全文件接收（路径消毒 + 2 GiB 上限 + 长度与摘要强制校验）。
 */
class BoBoHostServer(
    private val identity: BoBoIdentityStore,
    private val downloadDir: File,
    private val requestedPort: Int = BoBoProtocol.DEFAULT_TCP_PORT,
    private val screenMetricsProvider: () -> Triple<Int, Int, Int> = { Triple(1080, 2400, 0) }, // w, h, rotation
    private val isCaptureReadyProvider: () -> Boolean = { true },
    private val onStartMediaCapture: (width: Int, height: Int, fps: Int, bitrateKbps: Int) -> Pair<Int, Int>? = { _, _, _, _ -> null },
    private val onReconfigureCapture: (width: Int, height: Int, fps: Int, bitrateKbps: Int) -> Pair<Int, Int>? = { _, _, _, _ -> null },
    private val onStopMediaCapture: () -> Unit = {},
    private val onRequestKeyframe: () -> Unit = {},
    private val onInputEvent: (BoBoInputPacket) -> Unit = {},
    private val onClipboardText: (String, Boolean) -> Unit = { _, _ -> },
    private val onLockWorkstation: () -> Unit = {},
    private val onSetMuteHost: (Boolean) -> Unit = {},
    private val onSessionChanged: (active: Boolean, peerName: String) -> Unit = { _, _ -> },
    private val onPeerPaired: (BoBoAuthorizedPeer) -> Unit = {},
    private val onFileReceived: (File) -> Unit = {}
) {
    private val running = AtomicBoolean(false)
    private val pairLock = Any()
    private val streamLock = Any()
    private val fileSaveLock = Any()
    @Volatile private var activeStream: ClientConn? = null
    private val connections = CopyOnWriteArrayList<ClientConn>()
    private val lastRecoveryKeyMs = AtomicLong(0L)

    @Volatile var allowIncomingControl: Boolean = true
        set(value) {
            field = value
            if (!value) disconnectAllStreams("手机端已关闭「允许本机被控」")
        }

    @Volatile var boundPort: Int = requestedPort
        private set
    @Volatile private var serverSocket: SSLServerSocket? = null
    @Volatile private var activePairSession: BoBoLocalPairSession? = null

    val isRunning: Boolean get() = running.get()

    fun start(): Result<Int> {
        if (!running.compareAndSet(false, true)) return Result.success(boundPort)
        return try {
            identity.ensureLoaded()
            val ctx = identity.createServerSslContext()
            val srv = ctx.serverSocketFactory.createServerSocket(requestedPort) as SSLServerSocket
            srv.reuseAddress = true
            srv.enabledProtocols = srv.supportedProtocols.filter { it == "TLSv1.2" || it == "TLSv1.3" }.toTypedArray()
            serverSocket = srv
            boundPort = srv.localPort
            Thread({ acceptLoop(srv) }, "BoBo6-Host-Accept").apply { isDaemon = true; start() }
            Result.success(boundPort)
        } catch (ex: Exception) {
            running.set(false)
            Result.failure(ex)
        }
    }

    fun stop() {
        if (!running.compareAndSet(true, false)) return
        cancelPairing()
        try { serverSocket?.close() } catch (_: Exception) {}
        serverSocket = null
        disconnectAllStreams("被控端已退出 BoBo远程")
    }

    fun beginPairing(ttlSeconds: Int = 120): BoBoLocalPairSession {
        identity.ensureLoaded()
        val now = System.currentTimeMillis()
        val session = BoBoLocalPairSession(
            pin = BoBoIdentityStore.generateFourDigitPin(),
            createdMs = now,
            expiresMs = now + ttlSeconds.coerceIn(30, 600) * 1000L
        )
        synchronized(pairLock) { activePairSession = session }
        return session
    }

    fun getActivePairing(): BoBoLocalPairSession? = synchronized(pairLock) {
        val cur = activePairSession
        if (cur != null && cur.isExpired) {
            activePairSession = null
            null
        } else {
            cur
        }
    }

    fun cancelPairing() = synchronized(pairLock) { activePairSession = null }

    fun disconnectAllStreams(reason: String) {
        for (c in connections) {
            c.forceClose(reason)
        }
        connections.clear()
        disconnectActiveStream(reason)
    }

    fun disconnectActiveStream(reason: String) {
        synchronized(streamLock) {
            val conn = activeStream ?: return
            conn.forceClose(reason)
            releaseStreamLocked(conn)
        }
    }

    private fun releaseStreamLocked(conn: ClientConn) {
        if (activeStream !== conn) return
        conn.streaming.set(false)
        activeStream = null
        onStopMediaCapture()
        onSessionChanged(false, "")
    }

    private fun withActiveStream(conn: ClientConn, action: () -> Unit) {
        synchronized(streamLock) {
            if (activeStream === conn && conn.streaming.get() && !conn.closed.get() && allowIncomingControl) {
                action()
            }
        }
    }

    fun broadcastVideoFrame(width: Int, height: Int, isKeyframe: Boolean, h264: ByteArray, timestampMs: Long) {
        val conn = activeStream?.takeIf { it.streaming.get() } ?: return
        val payload = BoBoProtocol.buildVideoPayload(width, height, isKeyframe, h264)
        conn.enqueueVideo(payload, isKeyframe, timestampMs) { requestDebouncedRecoveryKeyframe() }
    }

    fun broadcastAudioFrame(sampleRate: Int, channels: Int, sampleCount: Int, adpcm: ByteArray) {
        val conn = activeStream?.takeIf { it.streaming.get() } ?: return
        conn.enqueueAudio(BoBoProtocol.buildAudioPayload(sampleRate, channels, sampleCount, adpcm))
    }

    fun notifyResolutionChanged(width: Int, height: Int, fps: Int, bitrateKbps: Int) {
        val conn = activeStream?.takeIf { it.streaming.get() } ?: return
        val curMons = listOf(BoBoMonitorDescriptor(0, "Android Screen (${width}x${height})", width, height, true))
        val msg = JSONObject().apply {
            put("type", "RECONFIGURED")
            put("monitorIndex", 0)
            put("width", width)
            put("height", height)
            put("fps", fps)
            put("bitrateKbps", bitrateKbps)
            put("monitors", BoBoProtocol.serializeMonitors(curMons))
        }
        conn.enqueueJson(msg)
    }

    private fun requestDebouncedRecoveryKeyframe() {
        val now = System.currentTimeMillis()
        val prev = lastRecoveryKeyMs.get()
        if (now - prev >= 350L && lastRecoveryKeyMs.compareAndSet(prev, now)) {
            try { onRequestKeyframe() } catch (_: Exception) {}
        }
    }

    private fun acceptLoop(srv: SSLServerSocket) {
        while (running.get()) {
            try {
                val sock = srv.accept() as SSLSocket
                if (!running.get() || connections.size >= MAX_CONCURRENT_CONNECTIONS) {
                    try { sock.close() } catch (_: Exception) {}
                    continue
                }
                sock.tcpNoDelay = true
                sock.keepAlive = true
                sock.soTimeout = HANDSHAKE_TIMEOUT_MS
                val conn = ClientConn(sock)
                connections.add(conn)
                Thread({ handleClient(conn) }, "BoBo6-Host-Conn").apply { isDaemon = true; start() }
            } catch (_: Exception) {
                if (!running.get()) break
            }
        }
    }

    private fun handleClient(conn: ClientConn) {
        val remoteIp = conn.socket.inetAddress?.hostAddress?.removePrefix("::ffff:") ?: ""
        var activeFileRaf: RandomAccessFile? = null
        var activeFileId = ""
        var activeFileName = ""
        var activeFileSha = ""
        var activeFileTotalBytes = 0L
        var activeFileTmp: File? = null
        var clientName = remoteIp

        fun cleanupActiveFile() {
            try { activeFileRaf?.close() } catch (_: Exception) {}
            activeFileRaf = null
            try { activeFileTmp?.delete() } catch (_: Exception) {}
            activeFileTmp = null
            activeFileId = ""
            activeFileName = ""
            activeFileSha = ""
            activeFileTotalBytes = 0L
        }

        try {
            conn.socket.startHandshake()
            val inp = conn.socket.inputStream
            val sessionNonce = BoBoIdentityStore.generateNonceHex(16)

            // 1. Expect HELLO within HANDSHAKE_TIMEOUT_MS and strict 64 KiB pre-auth frame cap
            val firstFrame = BoBoProtocol.readFrame(inp, BoBoProtocol.MAX_PRE_AUTH_PAYLOAD_BYTES) ?: return
            if (firstFrame.type != BoBoPacketType.CONTROL_JSON) return
            val hello = BoBoProtocol.parseJson(firstFrame.payload) ?: return
            if (hello.optString("type") != "HELLO" || hello.optString("protocol") != BoBoProtocol.PROTOCOL_MAGIC) return

            val clientNodeId = hello.optString("nodeId", "")
            val clientCode = hello.optString("deviceCode", "")
            clientName = hello.optString("deviceName", remoteIp)
            val clientThumb = hello.optString("certThumbprint", "")
            val helloCertDer = hello.optString("clientCertDer", "")

            var peerPaired = identity.isPeerAuthorized(clientNodeId, clientThumb)
            // Require RSA proof-of-possession (AUTH_PROOF or signed PAIR_REQUEST) before any privileged command!
            var authorized = false
            val (scrW, scrH, scrRot) = screenMetricsProvider()
            val monitors = listOf(BoBoMonitorDescriptor(0, "Android Screen (${scrW}x${scrH})", scrW, scrH, true))

            conn.sendJsonSync(JSONObject().apply {
                put("type", "HELLO_ACK")
                put("protocol", BoBoProtocol.PROTOCOL_MAGIC)
                put("protocolVersion", BoBoProtocol.PROTOCOL_VERSION)
                put("platform", BoBoProtocol.PLATFORM_ANDROID)
                put("hostControlMode", BoBoProtocol.CONTROL_MODE_FULL)
                put("screenOrientation", scrRot)
                put("nodeId", identity.nodeId)
                put("deviceCode", identity.deviceCode)
                put("deviceName", identity.deviceName)
                put("certThumbprint", identity.certThumbprint)
                put("version", BoBoProtocol.APP_VERSION)
                put("allowIncoming", allowIncomingControl && isCaptureReadyProvider())
                put("authorized", peerPaired)
                put("authChallenge", true)
                put("nonce", sessionNonce)
                put("monitors", BoBoProtocol.serializeMonitors(monitors))
            })

            conn.socket.soTimeout = IDLE_TIMEOUT_MS
            var activeEncW = minOf(scrW, 1280)
            var activeEncH = (activeEncW.toDouble() * scrH / scrW.coerceAtLeast(1)).toInt()
            var activeFps = 60
            var activeBps = 8000

            while (running.get() && !conn.closed.get()) {
                val maxLimit = if (authorized) 0 else BoBoProtocol.MAX_PRE_AUTH_PAYLOAD_BYTES
                val frame = BoBoProtocol.readFrame(inp, maxLimit) ?: break
                when (frame.type) {
                    BoBoPacketType.INPUT_EVENT -> {
                        if (authorized) withActiveStream(conn) {
                            BoBoProtocol.parseInputPayload(frame.payload)?.let(onInputEvent)
                        }
                    }
                    BoBoPacketType.FILE_CHUNK -> {
                        if (authorized && activeFileRaf != null) {
                            val chunk = BoBoProtocol.parseFileChunkPayload(frame.payload)
                            if (chunk != null &&
                                chunk.transferId == activeFileId &&
                                chunk.data.isNotEmpty() &&
                                chunk.data.size <= BoBoProtocol.MAX_FILE_CHUNK_PAYLOAD_BYTES &&
                                chunk.offset >= 0L &&
                                chunk.offset <= activeFileTotalBytes &&
                                chunk.data.size.toLong() <= (activeFileTotalBytes - chunk.offset)
                            ) {
                                activeFileRaf!!.seek(chunk.offset)
                                activeFileRaf!!.write(chunk.data)
                            }
                        }
                    }
                    BoBoPacketType.CONTROL_JSON -> {
                        val msg = BoBoProtocol.parseJson(frame.payload) ?: continue
                        when (msg.optString("type")) {
                            "PING" -> {
                                conn.enqueueJson(JSONObject().apply {
                                    put("type", "PONG")
                                    put("ts", msg.optLong("ts", 0L))
                                })
                            }
                            "AUTH_PROOF" -> {
                                val sigB64 = msg.optString("signature", "")
                                val certDer = msg.optString("clientCertDer", helloCertDer)
                                val ok = peerPaired && identity.isPeerAuthorized(clientNodeId, clientThumb) && BoBoIdentityStore.verifyAuthChallenge(
                                    certDer, clientThumb, sessionNonce, identity.certThumbprint, sigB64
                                )
                                val resp = JSONObject().put("type", "AUTH_RESULT").put("ok", ok)
                                if (!ok) {
                                    resp.put("error", "客户端身份签名校验失败，已拒绝连接")
                                    conn.sendJsonSync(resp)
                                    break
                                }
                                authorized = true
                                conn.sendJsonSync(resp)
                            }
                            "PAIR_REQUEST" -> {
                                val resp = JSONObject().put("type", "PAIR_RESULT")
                                if (!allowIncomingControl) {
                                    resp.put("ok", false).put("error", "手机端已关闭「允许本机被控」")
                                    conn.sendJsonSync(resp)
                                    break
                                }
                                val proof = msg.optString("proof", "")
                                val authSig = msg.optString("authSig", "")
                                val pairCertDer = msg.optString("clientCertDer", helloCertDer)
                                val sigOk = clientNodeId.isNotBlank() && BoBoIdentityStore.verifyAuthChallenge(
                                    pairCertDer, clientThumb, sessionNonce, identity.certThumbprint, authSig
                                )
                                var matched = false
                                var pinInvalidated = false
                                synchronized(pairLock) {
                                    val cur = activePairSession
                                    if (cur != null && !cur.isExpired) {
                                        val expected = BoBoIdentityStore.computePinProof(
                                            cur.pin, sessionNonce, clientThumb, identity.certThumbprint
                                        )
                                        if (sigOk && BoBoIdentityStore.fixedTimeEqualsHex(proof, expected)) {
                                            matched = true
                                            activePairSession = null
                                        } else {
                                            cur.failedAttempts++
                                            if (cur.failedAttempts >= BoBoLocalPairSession.MAX_FAILED_ATTEMPTS) {
                                                activePairSession = null
                                                pinInvalidated = true
                                            }
                                        }
                                    }
                                }
                                if (!matched) {
                                    try { Thread.sleep(500L) } catch (_: InterruptedException) {}
                                    resp.put("ok", false).put(
                                        "error",
                                        when {
                                            pinInvalidated -> "配对码连续输错次数过多，当前 4 位配对码已作废，请在手机端重新生成"
                                            !sigOk -> "客户端密钥签名校验失败，请升级双端 BoBo远程"
                                            else -> "4 位配对码错误或已过期"
                                        }
                                    )
                                    conn.sendJsonSync(resp)
                                    break
                                }
                                val peer = BoBoAuthorizedPeer(clientNodeId, clientCode, clientName, clientThumb, remoteIp)
                                if (!identity.authorizeOrUpdatePeer(peer)) {
                                    conn.sendJsonSync(resp.put("ok", false).put("error", "客户端证书与已配对记录不一致"))
                                    break
                                }
                                peerPaired = true
                                authorized = true
                                resp.put("ok", true)
                                    .put("hostName", identity.deviceName)
                                    .put("hostDeviceCode", identity.deviceCode)
                                conn.sendJsonSync(resp)
                                onPeerPaired(peer)
                            }
                            "START_STREAM" -> {
                                if (!allowIncomingControl) {
                                    sendError(conn, "HOST_DISABLED", "手机端已关闭「允许本机被控」")
                                    continue
                                }
                                if (!authorized) {
                                    sendError(conn, "PAIR_REQUIRED", "尚未完成 BoBo 双端配对与身份验证")
                                    continue
                                }
                                if (!isCaptureReadyProvider()) {
                                    sendError(conn, "CAPTURE_NOT_READY", "手机端尚未授予录屏权限，请先在手机 BoBo远程 中点击「开启被控」并允许录屏。")
                                    continue
                                }
                                val reqFps = msg.optInt("fps", 60).coerceIn(15, 60)
                                val reqBps = msg.optInt("bitrateKbps", 8000).coerceIn(1000, 30000)
                                val reqMaxW = msg.optInt("width", 1280).coerceIn(320, 3840)
                                val reqMaxH = msg.optInt("height", 720).coerceIn(320, 3840)
                                val errorCode = synchronized(streamLock) {
                                    when {
                                        activeStream != null -> "HOST_BUSY"
                                        conn.closed.get() || !allowIncomingControl || !isCaptureReadyProvider() -> "CAPTURE_NOT_READY"
                                        else -> {
                                            val size = onStartMediaCapture(reqMaxW, reqMaxH, reqFps, reqBps)
                                            if (size == null || conn.closed.get() || !allowIncomingControl || !isCaptureReadyProvider()) {
                                                onStopMediaCapture()
                                                "CAPTURE_FAILED"
                                            } else {
                                                val (encW, encH) = size
                                                activeEncW = encW
                                                activeEncH = encH
                                                activeFps = reqFps
                                                activeBps = reqBps
                                                conn.enqueueJson(JSONObject().apply {
                                                    put("type", "STREAM_STARTED")
                                                    put("width", encW)
                                                    put("height", encH)
                                                    put("fps", reqFps)
                                                    put("bitrateKbps", reqBps)
                                                    put("monitorIndex", 0)
                                                    put("superDisplayActive", false)
                                                    put("superDisplayNativeMatch", false)
                                                    put("platform", BoBoProtocol.PLATFORM_ANDROID)
                                                    put("hostControlMode", BoBoProtocol.CONTROL_MODE_FULL)
                                                    put("hostCursorComposited", false)
                                                    put("monitors", BoBoProtocol.serializeMonitors(monitors))
                                                })
                                                activeStream = conn
                                                conn.streaming.set(true)
                                                onSessionChanged(true, clientName)
                                                onRequestKeyframe()
                                                null
                                            }
                                        }
                                    }
                                }
                                if (errorCode != null) {
                                    val message = if (errorCode == "HOST_BUSY") "手机已有一个远控会话。" else "手机无法启动屏幕采集，请重新开启录屏权限。"
                                    sendError(conn, errorCode, message)
                                }
                            }
                            "RECONFIGURE" -> {
                                if (!authorized) continue
                                val reqFps = msg.optInt("fps", activeFps).coerceIn(15, 60)
                                val reqBps = msg.optInt("bitrateKbps", activeBps).coerceIn(1000, 30000)
                                val reqMaxW = msg.optInt("width", activeEncW).coerceIn(320, 3840)
                                val reqMaxH = msg.optInt("height", activeEncH).coerceIn(320, 3840)
                                var reconfigureFailed = false
                                withActiveStream(conn) {
                                    val size = onReconfigureCapture(reqMaxW, reqMaxH, reqFps, reqBps)
                                    if (size == null) {
                                        reconfigureFailed = true
                                        releaseStreamLocked(conn)
                                    } else {
                                        activeEncW = size.first
                                        activeEncH = size.second
                                        activeFps = reqFps
                                        activeBps = reqBps
                                        notifyResolutionChanged(size.first, size.second, reqFps, reqBps)
                                        onRequestKeyframe()
                                    }
                                }
                                if (reconfigureFailed) sendError(conn, "CAPTURE_FAILED", "手机未能切换画质，请重新连接。")
                            }
                            "SET_SUPER_DISPLAY" -> {
                                if (authorized) withActiveStream(conn) {
                                    conn.enqueueJson(JSONObject().apply {
                                        put("type", "SUPER_DISPLAY_STATE")
                                        put("active", false)
                                        put("nativeMatch", false)
                                        put("width", activeEncW)
                                        put("height", activeEncH)
                                    })
                                }
                            }
                            "REQUEST_KEYFRAME" -> if (authorized) withActiveStream(conn) { requestDebouncedRecoveryKeyframe() }
                            "SET_MUTE_HOST" -> if (authorized) withActiveStream(conn) { onSetMuteHost(msg.optBoolean("mute", false)) }
                            "LOCK_WORKSTATION" -> if (authorized) withActiveStream(conn, onLockWorkstation)
                            "CLIPBOARD_TEXT" -> {
                                if (authorized) withActiveStream(conn) {
                                    val txt = msg.optString("text", "")
                                    if (txt.isNotEmpty()) onClipboardText(txt, msg.optBoolean("injectUnicode", true))
                                }
                            }
                            "SET_PRIVACY" -> {
                                if (authorized) {
                                    conn.enqueueJson(JSONObject().apply {
                                        put("type", "PRIVACY_STATE")
                                        put("active", false)
                                    })
                                }
                            }
                            "FILE_OFFER" -> {
                                if (!authorized) {
                                    sendError(conn, "PAIR_REQUIRED", "未授权设备禁止传文件")
                                    continue
                                }
                                cleanupActiveFile()
                                val tid = msg.optString("transferId", "").trim()
                                val totalBytes = msg.optLong("totalBytes", -1L)
                                val offerSha = msg.optString("sha256", "").trim()
                                if (tid.isEmpty() || tid.length > 16 || tid.any { it !in '!'..'~' } ||
                                    totalBytes < 0L || totalBytes > BoBoProtocol.MAX_FILE_TRANSFER_BYTES ||
                                    !BoBoProtocol.isValidHexSha256(offerSha)
                                ) {
                                    sendError(conn, "FILE_REJECTED", "文件参数无效、超过 2 GiB 上限或缺少有效的 SHA-256 摘要")
                                    continue
                                }
                                activeFileId = tid
                                activeFileName = BoBoProtocol.sanitizeFileName(msg.optString("fileName", "received.bin"))
                                activeFileTotalBytes = totalBytes
                                activeFileSha = offerSha
                                try {
                                    if (!downloadDir.isDirectory && !downloadDir.mkdirs() && !downloadDir.isDirectory) {
                                        throw IOException("无法建立下载目录")
                                    }
                                    val tmpFile = File.createTempFile(".bobo-", ".part", downloadDir)
                                    activeFileTmp = tmpFile
                                    activeFileRaf = RandomAccessFile(tmpFile, "rw")
                                } catch (_: IOException) {
                                    cleanupActiveFile()
                                    sendError(conn, "FILE_REJECTED", "无法创建接收文件")
                                    continue
                                }
                                conn.sendJsonSync(JSONObject().apply {
                                    put("type", "FILE_ACCEPT")
                                    put("transferId", activeFileId)
                                })
                            }
                            "FILE_DONE" -> {
                                val tid = msg.optString("transferId", "")
                                if (!authorized) {
                                    conn.sendJsonSync(JSONObject().apply {
                                        put("type", "FILE_ACK")
                                        put("transferId", tid)
                                        put("ok", false)
                                        put("error", "未授权会话")
                                    })
                                    continue
                                }
                                try { activeFileRaf?.fd?.sync() } catch (_: Exception) {}
                                try { activeFileRaf?.close() } catch (_: Exception) {}
                                activeFileRaf = null
                                val tmp = activeFileTmp
                                val expectedLen = activeFileTotalBytes
                                val expectedSha = activeFileSha
                                val expectedTid = activeFileId
                                val savedName = activeFileName
                                activeFileTmp = null
                                activeFileId = ""
                                activeFileName = ""
                                activeFileSha = ""
                                activeFileTotalBytes = 0L

                                if (tmp != null && tmp.exists() && tid == expectedTid) {
                                    val actualLen = tmp.length()
                                    val sha = if (actualLen == expectedLen) sha256File(tmp) else ""
                                    val valid = actualLen == expectedLen &&
                                        BoBoProtocol.isValidHexSha256(expectedSha) &&
                                        BoBoIdentityStore.fixedTimeEqualsHex(sha, expectedSha)
                                    if (valid) {
                                        val dest = try {
                                            synchronized(fileSaveLock) {
                                                val target = allocateUniqueFile(downloadDir, savedName)
                                                Files.move(tmp.toPath(), target.toPath())
                                                target
                                            }
                                        } catch (_: IOException) { null }
                                        if (dest != null) {
                                            conn.sendJsonSync(JSONObject().apply {
                                                put("type", "FILE_ACK")
                                                put("transferId", tid)
                                                put("ok", true)
                                                put("savedPath", dest.absolutePath)
                                                put("sha256", sha)
                                            })
                                            onFileReceived(dest)
                                        } else {
                                            try { tmp.delete() } catch (_: Exception) {}
                                            conn.sendJsonSync(JSONObject().apply {
                                                put("type", "FILE_ACK")
                                                put("transferId", tid)
                                                put("ok", false)
                                                put("error", "保存目标文件失败")
                                            })
                                        }
                                    } else {
                                        try { tmp.delete() } catch (_: Exception) {}
                                        conn.sendJsonSync(JSONObject().apply {
                                            put("type", "FILE_ACK")
                                            put("transferId", tid)
                                            put("ok", false)
                                            put(
                                                "error",
                                                if (actualLen != expectedLen) "文件字节数不匹配" else "SHA-256 校验失败"
                                            )
                                        })
                                    }
                                } else {
                                    try { tmp?.delete() } catch (_: Exception) {}
                                    conn.sendJsonSync(JSONObject().apply {
                                        put("type", "FILE_ACK")
                                        put("transferId", tid)
                                        put("ok", false)
                                        put("error", "未找到对应的文件接收上下文")
                                    })
                                }
                            }
                            "DISCONNECT" -> break
                        }
                    }
                    else -> Unit
                }
            }
        } catch (_: Exception) {
        } finally {
            cleanupActiveFile()
            conn.forceClose("session ended")
            connections.remove(conn)
            synchronized(streamLock) { releaseStreamLocked(conn) }
        }
    }

    private fun sendError(conn: ClientConn, code: String, message: String) {
        conn.sendJsonSync(JSONObject().apply {
            put("type", "ERROR")
            put("code", code)
            put("message", message)
        })
    }

    private fun sha256File(file: File): String {
        val md = MessageDigest.getInstance("SHA-256")
        file.inputStream().use { ins ->
            val buf = ByteArray(65536)
            var n: Int
            while (ins.read(buf).also { n = it } > 0) md.update(buf, 0, n)
        }
        return BoBoProtocol.bytesToHex(md.digest())
    }

    private fun allocateUniqueFile(dir: File, safeName: String): File {
        val canonicalDir = dir.canonicalFile
        val dot = safeName.lastIndexOf('.')
        val stem = if (dot > 0) safeName.substring(0, dot) else safeName
        val ext = if (dot > 0) safeName.substring(dot) else ""
        for (i in 0..999) {
            val name = if (i == 0) safeName else "$stem ($i)$ext"
            val candidate = File(canonicalDir, name)
            if (candidate.canonicalFile.parentFile != canonicalDir) continue
            if (!candidate.exists()) return candidate
        }
        throw IOException("找不到可用的文件名")
    }

    private class ClientConn(val socket: SSLSocket) {
        val closed = AtomicBoolean(false)
        val streaming = AtomicBoolean(false)
        private val writeLock = Any()
        private val videoQueueDepth = AtomicInteger(0)
        private val audioQueueDepth = AtomicInteger(0)
        private val tx = ThreadPoolExecutor(
            1, 1, 0L, TimeUnit.MILLISECONDS, LinkedBlockingQueue(64),
            { r -> Thread(r, "BoBo6-Host-Tx").apply { isDaemon = true } }
        )
        private val out: OutputStream get() = socket.outputStream

        fun sendJsonSync(obj: JSONObject) {
            if (closed.get()) return
            synchronized(writeLock) {
                BoBoProtocol.writeJson(out, obj)
            }
        }

        fun enqueueJson(obj: JSONObject) {
            if (closed.get()) return
            try {
                tx.execute {
                    try {
                        if (!closed.get()) {
                            synchronized(writeLock) {
                                BoBoProtocol.writeJson(out, obj)
                            }
                        }
                    } catch (_: Exception) {
                        forceClose("json write failed")
                    }
                }
            } catch (_: java.util.concurrent.RejectedExecutionException) {
                forceClose("control send queue full")
            }
        }

        fun enqueueVideo(payload: ByteArray, isKey: Boolean, tsMs: Long, onDroppedPFrame: () -> Unit) {
            if (closed.get()) return
            val depth = videoQueueDepth.incrementAndGet()
            if (!isKey && depth > 2) {
                videoQueueDepth.decrementAndGet()
                onDroppedPFrame()
                return
            }
            if (isKey && depth > 4) {
                videoQueueDepth.decrementAndGet()
                return
            }
            try {
                tx.execute {
                    try {
                        if (!closed.get()) {
                            synchronized(writeLock) {
                                BoBoProtocol.writeFrame(out, BoBoPacketType.VIDEO_H264, tsMs, payload)
                            }
                        }
                    } catch (_: Exception) {
                        forceClose("video write failed")
                    } finally {
                        videoQueueDepth.decrementAndGet()
                    }
                }
            } catch (_: Exception) {
                videoQueueDepth.decrementAndGet()
            }
        }

        fun enqueueAudio(payload: ByteArray) {
            if (closed.get()) return
            val depth = audioQueueDepth.incrementAndGet()
            if (depth > 6) {
                audioQueueDepth.decrementAndGet()
                return
            }
            try {
                tx.execute {
                    try {
                        if (!closed.get()) {
                            synchronized(writeLock) {
                                BoBoProtocol.writeFrame(out, BoBoPacketType.AUDIO_ADPCM, 0L, payload)
                            }
                        }
                    } catch (_: Exception) {
                        forceClose("audio write failed")
                    } finally {
                        audioQueueDepth.decrementAndGet()
                    }
                }
            } catch (_: Exception) {
                audioQueueDepth.decrementAndGet()
            }
        }

        fun forceClose(reason: String) {
            if (!closed.compareAndSet(false, true)) return
            streaming.set(false)
            try { socket.close() } catch (_: Exception) {}
            tx.shutdownNow()
        }
    }

    companion object {
        private const val HANDSHAKE_TIMEOUT_MS = 8_000
        private const val IDLE_TIMEOUT_MS = 25_000
        private const val MAX_CONCURRENT_CONNECTIONS = 8
    }
}
