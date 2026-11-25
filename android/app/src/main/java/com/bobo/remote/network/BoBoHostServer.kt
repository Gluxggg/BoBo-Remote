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
import java.io.FileOutputStream
import java.io.OutputStream
import java.security.MessageDigest
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import javax.net.ssl.SSLServerSocket
import javax.net.ssl.SSLSocket

/**
 * Android 原生 BoBo V6.2 被控端服务（仅随 BoBo远程 App 运行，关闭即停服断口）。
 * - 监听 TCP 49810（TLS 1.2）
 * - 支持 4 位临时配对码 HMAC 认证与已授权设备管理
 * - 实时分发 H.264 录屏帧与 IMA-ADPCM 音频帧
 * - 接收鼠标/多指触控(kind=7)/移动导航(kind=8)/键盘/剪贴板并交由 BoBoAccessibilityService 免 Root 执行
 * - 支持 TLS SHA-256 文件接收
 */
class BoBoHostServer(
    private val identity: BoBoIdentityStore,
    private val downloadDir: File,
    private val requestedPort: Int = BoBoProtocol.DEFAULT_TCP_PORT,
    private val screenMetricsProvider: () -> Triple<Int, Int, Int> = { Triple(1080, 2400, 0) }, // w, h, rotation
    private val onStartMediaCapture: (width: Int, height: Int, fps: Int, bitrateKbps: Int) -> Unit = { _, _, _, _ -> },
    private val onStopMediaCapture: () -> Unit = {},
    private val onRequestKeyframe: () -> Unit = {},
    private val onInputEvent: (BoBoInputPacket) -> Unit = {},
    private val onClipboardText: (String) -> Unit = {},
    private val onSessionChanged: (active: Boolean, peerName: String) -> Unit = { _, _ -> },
    private val onPeerPaired: (BoBoAuthorizedPeer) -> Unit = {},
    private val onFileReceived: (File) -> Unit = {}
) {
    private val running = AtomicBoolean(false)
    private val pairLock = Any()
    private val connections = CopyOnWriteArrayList<ClientConn>()

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
        onStopMediaCapture()
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
        onStopMediaCapture()
        onSessionChanged(false, "")
    }

    fun broadcastVideoFrame(width: Int, height: Int, isKeyframe: Boolean, h264: ByteArray, timestampMs: Long) {
        if (connections.isEmpty()) return
        val payload = BoBoProtocol.buildVideoPayload(width, height, isKeyframe, h264)
        for (c in connections) {
            if (c.streaming.get()) {
                c.enqueueVideo(payload, isKeyframe, timestampMs)
            }
        }
    }

    fun broadcastAudioFrame(sampleRate: Int, channels: Int, sampleCount: Int, adpcm: ByteArray) {
        if (connections.isEmpty()) return
        val payload = BoBoProtocol.buildAudioPayload(sampleRate, channels, sampleCount, adpcm)
        for (c in connections) {
            if (c.streaming.get()) {
                c.enqueueAudio(payload)
            }
        }
    }

    private fun acceptLoop(srv: SSLServerSocket) {
        while (running.get()) {
            try {
                val sock = srv.accept() as SSLSocket
                sock.tcpNoDelay = true
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
        var activeFileStream: FileOutputStream? = null
        var activeFileId = ""
        var activeFileName = ""
        var activeFileSha = ""
        var activeFileTmp: File? = null
        var clientName = remoteIp
        var streamStarted = false

        try {
            conn.socket.startHandshake()
            val inp = conn.socket.inputStream
            val sessionNonce = BoBoIdentityStore.generateNonceHex(16)

            val firstFrame = BoBoProtocol.readFrame(inp) ?: return
            if (firstFrame.type != BoBoPacketType.CONTROL_JSON) return
            val hello = BoBoProtocol.parseJson(firstFrame.payload) ?: return
            if (hello.optString("type") != "HELLO" || hello.optString("protocol") != BoBoProtocol.PROTOCOL_MAGIC) return

            val clientNodeId = hello.optString("nodeId", "")
            val clientCode = hello.optString("deviceCode", "")
            clientName = hello.optString("deviceName", remoteIp)
            val clientThumb = hello.optString("certThumbprint", "")

            var authorized = identity.isPeerAuthorized(clientNodeId, clientThumb)
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
                put("allowIncoming", allowIncomingControl)
                put("authorized", authorized)
                put("nonce", sessionNonce)
                put("monitors", BoBoProtocol.serializeMonitors(monitors))
            })

            while (running.get() && !conn.closed.get()) {
                val frame = BoBoProtocol.readFrame(inp) ?: break
                when (frame.type) {
                    BoBoPacketType.INPUT_EVENT -> {
                        if (authorized && conn.streaming.get()) {
                            BoBoProtocol.parseInputPayload(frame.payload)?.let { onInputEvent(it) }
                        }
                    }
                    BoBoPacketType.FILE_CHUNK -> {
                        if (authorized && activeFileStream != null) {
                            val chunk = BoBoProtocol.parseFileChunkPayload(frame.payload)
                            if (chunk != null && chunk.transferId == activeFileId && chunk.data.isNotEmpty()) {
                                activeFileStream.write(chunk.data)
                            }
                        }
                    }
                    BoBoPacketType.CONTROL_JSON -> {
                        val msg = BoBoProtocol.parseJson(frame.payload) ?: continue
                        when (msg.optString("type")) {
                            "PING" -> {
                                conn.sendJsonSync(JSONObject().apply {
                                    put("type", "PONG")
                                    put("ts", msg.optLong("ts", 0L))
                                })
                            }
                            "PAIR_REQUEST" -> {
                                val resp = JSONObject().put("type", "PAIR_RESULT")
                                if (!allowIncomingControl) {
                                    resp.put("ok", false).put("error", "手机端已关闭「允许本机被控」")
                                    conn.sendJsonSync(resp)
                                    continue
                                }
                                val proof = msg.optString("proof", "")
                                var matched = false
                                synchronized(pairLock) {
                                    val cur = activePairSession
                                    if (cur != null && !cur.isExpired) {
                                        val expected = BoBoIdentityStore.computePinProof(
                                            cur.pin, sessionNonce, clientThumb, identity.certThumbprint
                                        )
                                        if (BoBoIdentityStore.fixedTimeEqualsHex(proof, expected)) {
                                            matched = true
                                            activePairSession = null
                                        }
                                    }
                                }
                                if (!matched) {
                                    resp.put("ok", false).put("error", "4 位配对码错误或已过期")
                                    conn.sendJsonSync(resp)
                                    continue
                                }
                                val peer = BoBoAuthorizedPeer(clientNodeId, clientCode, clientName, clientThumb, remoteIp)
                                identity.authorizeOrUpdatePeer(peer)
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
                                    sendError(conn, "PAIR_REQUIRED", "尚未完成 BoBo 双端配对授权")
                                    continue
                                }
                                val reqFps = msg.optInt("fps", 60).coerceIn(15, 60)
                                val reqBps = msg.optInt("bitrateKbps", 8000).coerceIn(1000, 30000)
                                val (curW, curH, _) = screenMetricsProvider()
                                val encW = align16(minOf(curW, 1280))
                                val encH = align16((encW.toDouble() * curH / curW.coerceAtLeast(1)).toInt())

                                conn.streaming.set(true)
                                streamStarted = true
                                onSessionChanged(true, clientName)
                                onStartMediaCapture(encW, encH, reqFps, reqBps)

                                val curMons = listOf(BoBoMonitorDescriptor(0, "Android Screen (${encW}x${encH})", encW, encH, true))
                                conn.sendJsonSync(JSONObject().apply {
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
                                    put("monitors", BoBoProtocol.serializeMonitors(curMons))
                                })
                            }
                            "REQUEST_KEYFRAME" -> if (authorized) onRequestKeyframe()
                            "CLIPBOARD_TEXT" -> {
                                if (authorized) {
                                    val txt = msg.optString("text", "")
                                    if (txt.isNotEmpty()) onClipboardText(txt)
                                }
                            }
                            "SET_PRIVACY" -> {
                                if (authorized) {
                                    conn.sendJsonSync(JSONObject().apply {
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
                                activeFileStream?.close()
                                activeFileId = msg.optString("transferId", "")
                                activeFileName = File(msg.optString("fileName", "received.bin")).name
                                activeFileSha = msg.optString("sha256", "")
                                if (!downloadDir.exists()) downloadDir.mkdirs()
                                val tmpFile = File(downloadDir, ".bobo-${System.nanoTime()}.part")
                                activeFileTmp = tmpFile
                                activeFileStream = FileOutputStream(tmpFile)
                                conn.sendJsonSync(JSONObject().apply {
                                    put("type", "FILE_ACCEPT")
                                    put("transferId", activeFileId)
                                })
                            }
                            "FILE_DONE" -> {
                                val tid = msg.optString("transferId", "")
                                activeFileStream?.flush()
                                activeFileStream?.close()
                                activeFileStream = null
                                val tmp = activeFileTmp
                                activeFileTmp = null
                                if (tmp != null && tmp.exists() && tid == activeFileId) {
                                    val sha = sha256File(tmp)
                                    if (activeFileSha.isBlank() || sha.equals(activeFileSha, ignoreCase = true)) {
                                        val dest = allocateUniqueFile(downloadDir, activeFileName)
                                        tmp.renameTo(dest)
                                        conn.sendJsonSync(JSONObject().apply {
                                            put("type", "FILE_ACK")
                                            put("transferId", tid)
                                            put("ok", true)
                                            put("savedPath", dest.absolutePath)
                                        })
                                        onFileReceived(dest)
                                    } else {
                                        tmp.delete()
                                        conn.sendJsonSync(JSONObject().apply {
                                            put("type", "FILE_ACK")
                                            put("transferId", tid)
                                            put("ok", false)
                                            put("error", "SHA-256 校验失败")
                                        })
                                    }
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
            try { activeFileStream?.close() } catch (_: Exception) {}
            try { activeFileTmp?.delete() } catch (_: Exception) {}
            conn.forceClose("session ended")
            connections.remove(conn)
            if (streamStarted && connections.none { it.streaming.get() }) {
                onStopMediaCapture()
                onSessionChanged(false, "")
            }
        }
    }

    private fun sendError(conn: ClientConn, code: String, message: String) {
        conn.sendJsonSync(JSONObject().apply {
            put("type", "ERROR")
            put("code", code)
            put("message", message)
        })
    }

    private fun align16(v: Int): Int = ((v.coerceAtLeast(320) + 15) / 16) * 16

    private fun sha256File(file: File): String {
        val md = MessageDigest.getInstance("SHA-256")
        file.inputStream().use { ins ->
            val buf = ByteArray(65536)
            var n: Int
            while (ins.read(buf).also { n = it } > 0) md.update(buf, 0, n)
        }
        return BoBoProtocol.bytesToHex(md.digest())
    }

    private fun allocateUniqueFile(dir: File, name: String): File {
        val first = File(dir, name)
        if (!first.exists()) return first
        val dot = name.lastIndexOf('.')
        val stem = if (dot > 0) name.substring(0, dot) else name
        val ext = if (dot > 0) name.substring(dot) else ""
        for (i in 1..999) {
            val cand = File(dir, "$stem ($i)$ext")
            if (!cand.exists()) return cand
        }
        return File(dir, "${stem}_${System.currentTimeMillis()}$ext")
    }

    private class ClientConn(val socket: SSLSocket) {
        val closed = AtomicBoolean(false)
        val streaming = AtomicBoolean(false)
        private val writeLock = Any()
        private val videoQueueDepth = AtomicInteger(0)
        private val tx = Executors.newSingleThreadExecutor { r ->
            Thread(r, "BoBo6-Host-Tx").apply { isDaemon = true }
        }
        private val out: OutputStream get() = socket.outputStream

        fun sendJsonSync(obj: JSONObject) {
            if (closed.get()) return
            synchronized(writeLock) {
                BoBoProtocol.writeJson(out, obj)
            }
        }

        fun enqueueVideo(payload: ByteArray, isKey: Boolean, tsMs: Long) {
            if (closed.get()) return
            val depth = videoQueueDepth.incrementAndGet()
            if (!isKey && depth > 2) {
                videoQueueDepth.decrementAndGet()
                return
            }
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
        }

        fun enqueueAudio(payload: ByteArray) {
            if (closed.get()) return
            tx.execute {
                try {
                    if (!closed.get()) {
                        synchronized(writeLock) {
                            BoBoProtocol.writeFrame(out, BoBoPacketType.AUDIO_ADPCM, 0L, payload)
                        }
                    }
                } catch (_: Exception) {
                    forceClose("audio write failed")
                }
            }
        }

        fun forceClose(reason: String) {
            if (!closed.compareAndSet(false, true)) return
            streaming.set(false)
            try { socket.close() } catch (_: Exception) {}
            tx.shutdownNow()
        }
    }
}
