package com.bobo.remote.network

import com.bobo.remote.protocol.BoBoAudioPayload
import com.bobo.remote.protocol.BoBoAuthorizedPeer
import com.bobo.remote.protocol.BoBoIdentityStore
import com.bobo.remote.protocol.BoBoInputPacket
import com.bobo.remote.protocol.BoBoMonitorDescriptor
import com.bobo.remote.protocol.BoBoPacketType
import com.bobo.remote.protocol.BoBoProtocol
import com.bobo.remote.protocol.BoBoVideoPayload
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileInputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.security.MessageDigest
import java.util.ArrayDeque
import java.util.UUID
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong
import javax.net.ssl.SNIHostName
import javax.net.ssl.SSLSocket

data class BoBoProbeResult(
    val reachable: Boolean = false,
    val isBoBoHost: Boolean = false,
    val allowIncoming: Boolean = false,
    val authorized: Boolean = false,
    val host: String = "",
    val port: Int = BoBoProtocol.DEFAULT_TCP_PORT,
    val hostNodeId: String = "",
    val hostDeviceCode: String = "",
    val hostName: String = "",
    val hostThumbprint: String = "",
    val platform: String = BoBoProtocol.PLATFORM_WINDOWS,
    val hostControlMode: String = BoBoProtocol.CONTROL_MODE_FULL,
    val screenOrientation: Int = 0,
    val nonce: String = "",
    val monitors: List<BoBoMonitorDescriptor> = emptyList(),
    val error: String = ""
)

data class BoBoStreamOptions(
    val host: String,
    val port: Int = BoBoProtocol.DEFAULT_TCP_PORT,
    val monitorIndex: Int = 0,
    val width: Int = 1920,
    val height: Int = 1080,
    val fps: Int = 60,
    val bitrateKbps: Int = 10000,
    val muteHostAudio: Boolean = false,
    val requestPrivacy: Boolean = false,
    val superDisplay: Boolean = true,
    val clientWidth: Int = 0,
    val clientHeight: Int = 0,
    val expectedHostThumbprint: String = "",
    val expectedHostNodeId: String = ""
)

/**
 * BoBo V6.3 Android 原生主控客户端（TLS 1.2+ + 严格 TOFU 证书钉扎 + RSA 双向身份证明 + 合并防积压发送队列）。
 */
class BoBoStreamClient(private val identity: BoBoIdentityStore) {
    interface Listener {
        fun onStreamStarted(
            monitors: List<BoBoMonitorDescriptor>,
            width: Int,
            height: Int,
            fps: Int,
            remotePlatform: String,
            hostControlMode: String
        )
        fun onVideoFrame(frame: BoBoVideoPayload, timestampMs: Long)
        fun onAudioFrame(frame: BoBoAudioPayload)
        fun onRttUpdated(rttMs: Double) {}
        fun onPrivacyStateChanged(active: Boolean) {}
        fun onSuperDisplayStateChanged(active: Boolean, nativeMatch: Boolean, width: Int, height: Int) {}
        fun onReconfigured(width: Int, height: Int, fps: Int) {}
        fun onHostCursorCompositedChanged(composited: Boolean) {}
        fun onError(code: String, message: String)
        fun onDisconnected(reason: String)
    }

    private data class QueuedFrame(val type: BoBoPacketType, val flags: Long, val payload: ByteArray)

    private val sendLock = Object()
    private val sendQueue = ArrayDeque<QueuedFrame>()
    private var pendingAbsMove: BoBoInputPacket? = null
    private var pendingRelDx: Int = 0
    private var pendingRelDy: Int = 0
    private val pendingTouchMoves = LinkedHashMap<Int, BoBoInputPacket>()

    private val connected = AtomicBoolean(false)
    private val lastRxNs = AtomicLong(0L)
    @Volatile private var socket: SSLSocket? = null
    @Volatile private var udpChannel: BoBoUdpChannel? = null
    private val generation = AtomicLong(0L)

    @Volatile var remotePlatform: String = BoBoProtocol.PLATFORM_WINDOWS
        private set
    @Volatile var remoteHostControlMode: String = BoBoProtocol.CONTROL_MODE_FULL
        private set
    @Volatile var remoteMonitors: List<BoBoMonitorDescriptor> = emptyList()
        private set
    @Volatile var hostCursorComposited: Boolean = false
        private set

    val isConnected: Boolean get() = connected.get()

    fun connectAndStartStream(options: BoBoStreamOptions, listener: Listener) {
        val request = resetConnection()
        identity.ensureLoaded()
        Thread({
            var disconnectReason = "连接已断开"
            var failed = false
            var currentSocket: SSLSocket? = null
            try {
                var serverThumb = ""
                if (generation.get() != request) return@Thread
                val sslSock = openTlsSocket(options.host, options.port, 10_000) { serverThumb = it }
                currentSocket = sslSock
                synchronized(sendLock) {
                    if (generation.get() != request) return@Thread
                    socket = sslSock
                }
                val out = sslSock.outputStream
                val inp = sslSock.inputStream

                sendHello(out, identity)
                val ackFrame = BoBoProtocol.readFrame(inp, BoBoProtocol.MAX_PRE_AUTH_PAYLOAD_BYTES)
                if (ackFrame == null || ackFrame.type != BoBoPacketType.CONTROL_JSON) {
                    listener.onError("HANDSHAKE_FAILED", "目标主机未返回有效的 BoBo 握手响应。")
                    return@Thread
                }
                val ack = BoBoProtocol.parseJson(ackFrame.payload)
                if (ack == null || !isHelloAck(ack)) {
                    listener.onError("NOT_BOBO", "目标主机未运行 BoBo远程 V6。")
                    return@Thread
                }
                if (!ack.optBoolean("allowIncoming", true)) {
                    listener.onError("HOST_DISABLED", "对方已关闭「允许本机被控」或尚未开启录屏权限。")
                    return@Thread
                }
                // Strict Client TOFU Certificate Pinning (never fall back to JSON certThumbprint!)
                if (serverThumb.isBlank()) {
                    listener.onError("CERT_MISSING", "未能从 TLS 握手提取对方证书指纹，已拒绝连接。")
                    return@Thread
                }
                val hostNodeId = ack.optString("nodeId", "")
                val hostCode = ack.optString("deviceCode", "")
                val hostName = ack.optString("deviceName", options.host)
                if (hasPinMismatch(identity, hostNodeId, serverThumb, options.expectedHostThumbprint, options.expectedHostNodeId)) {
                    listener.onError(
                        "CERT_MISMATCH",
                        "安全警告：目标设备 TLS 证书指纹与本地已配对记录不一致，已拒绝连接。请先删除旧授权后再重新配对。"
                    )
                    return@Thread
                }

                if (!ack.optBoolean("authorized", false) || !identity.isPeerAuthorized(hostNodeId, serverThumb)) {
                    listener.onError("PAIR_REQUIRED", "请先输入对方 4 位临时配对码。")
                    return@Thread
                }

                // Prove private-key possession over host's fresh sessionNonce before issuing START_STREAM
                if (ack.optBoolean("authChallenge", false)) {
                    val nonce = ack.optString("nonce", "")
                    val sig = identity.signAuthChallenge(nonce, serverThumb)
                    BoBoProtocol.writeJson(out, JSONObject().apply {
                        put("type", "AUTH_PROOF")
                        put("signature", sig)
                        put("clientCertDer", identity.certificateDerBase64)
                    })
                    val authFrame = BoBoProtocol.readFrame(inp, BoBoProtocol.MAX_PRE_AUTH_PAYLOAD_BYTES)
                    val authRes = authFrame?.takeIf { it.type == BoBoPacketType.CONTROL_JSON }?.let { BoBoProtocol.parseJson(it.payload) }
                    if (authRes?.optString("type") != "AUTH_RESULT" || authRes.optBoolean("ok", false) != true) {
                        listener.onError("AUTH_FAILED", authRes?.optString("error", "客户端身份签名校验失败") ?: "双向身份校验失败")
                            return@Thread
                    }
                }

                remotePlatform = ack.optString("platform", BoBoProtocol.PLATFORM_WINDOWS)
                remoteHostControlMode = ack.optString("hostControlMode", BoBoProtocol.CONTROL_MODE_FULL)
                remoteMonitors = BoBoProtocol.parseMonitors(ack)

                if (hostNodeId.isNotBlank()) {
                    identity.authorizeOrUpdatePeer(
                        BoBoAuthorizedPeer(hostNodeId, hostCode, hostName, serverThumb, options.host)
                    )
                }

                val startReq = JSONObject().apply {
                    put("type", "START_STREAM")
                    put("monitorIndex", options.monitorIndex)
                    put("width", options.width)
                    put("height", options.height)
                    put("fps", options.fps)
                    put("bitrateKbps", options.bitrateKbps)
                    put("muteHost", options.muteHostAudio)
                    put("requestPrivacy", options.requestPrivacy)
                    put("superDisplay", options.superDisplay)
                    put("clientWidth", options.clientWidth)
                    put("clientHeight", options.clientHeight)
                    put("udpTransport", true)
                    put("udpCipher", "aes-128-gcm")
                    put("udpCiphers", JSONArray(listOf("aes-128-gcm")))
                    put("udpInterleavedFec", true)
                    put("udpNack", true)
                    put("audioCodecs", JSONArray(listOf("adpcm")))
                }
                BoBoProtocol.writeJson(out, startReq)

                sslSock.soTimeout = STREAM_READ_TIMEOUT_MS
                synchronized(sendLock) {
                    if (generation.get() != request) return@Thread
                    lastRxNs.set(System.nanoTime())
                    connected.set(true)
                    startTxThread(sslSock, request)
                }
                Thread({ pingLoop(sslSock, request) }, "BoBo6-Client-Ping").apply { isDaemon = true; start() }

                while (generation.get() == request && connected.get()) {
                    val frame = BoBoProtocol.readFrame(inp) ?: break
                    if (generation.get() != request) break
                    lastRxNs.set(System.nanoTime())
                    when (frame.type) {
                        BoBoPacketType.VIDEO_H264 -> {
                            if (udpChannel?.isActive == true) continue
                            BoBoProtocol.parseVideoPayload(frame.payload)?.let {
                                listener.onVideoFrame(it, frame.flagsOrTimestamp)
                            }
                        }
                        BoBoPacketType.AUDIO_ADPCM -> {
                            if (udpChannel?.isActive == true) continue
                            BoBoProtocol.parseAudioPayload(frame.payload)?.let {
                                listener.onAudioFrame(it)
                            }
                        }
                        BoBoPacketType.CURSOR_SHAPE -> Unit
                        BoBoPacketType.CONTROL_JSON -> {
                            val msg = BoBoProtocol.parseJson(frame.payload) ?: continue
                            when (msg.optString("type")) {
                                "STREAM_STARTED" -> {
                                    val mons = BoBoProtocol.parseMonitors(msg)
                                    if (mons.isNotEmpty()) remoteMonitors = mons
                                    val sp = msg.optString("platform", "")
                                    if (sp.isNotBlank()) remotePlatform = sp
                                    val sm = msg.optString("hostControlMode", "")
                                    if (sm.isNotBlank()) remoteHostControlMode = sm
                                    val w = msg.optInt("width", options.width)
                                    val h = msg.optInt("height", options.height)
                                    val fps = msg.optInt("fps", options.fps)
                                    val defaultComposited = remotePlatform == BoBoProtocol.PLATFORM_WINDOWS ||
                                        remotePlatform == BoBoProtocol.PLATFORM_MACOS
                                    val comp = msg.optBoolean("hostCursorComposited", defaultComposited)
                                    hostCursorComposited = comp
                                    listener.onHostCursorCompositedChanged(comp)
                                    val udpPort = msg.optInt("udpPort", 0)
                                    val udpSid = msg.optLong("udpSessionId", 0L)
                                    val udpKey = msg.optString("udpKey", "")
                                    val udpCipher = msg.optString("udpCipher", "")
                                    val udpNack = msg.optBoolean("udpNack", false)
                                    if (udpPort > 0 && udpSid != 0L && udpKey.isNotBlank() &&
                                        udpCipher.equals("aes-128-gcm", ignoreCase = true)) {
                                        startUdpChannel(options.host, udpPort, udpSid, udpKey, udpNack, request, listener)
                                    }
                                    listener.onStreamStarted(remoteMonitors, w, h, fps, remotePlatform, remoteHostControlMode)
                                    if (msg.has("privacyActive")) listener.onPrivacyStateChanged(msg.optBoolean("privacyActive"))
                                    listener.onSuperDisplayStateChanged(
                                        msg.optBoolean("superDisplayActive", false),
                                        msg.optBoolean("superDisplayNativeMatch", false),
                                        w, h
                                    )
                                }
                                "CURSOR_MODE_STATE" -> {
                                    val comp = msg.optBoolean("hostCursorComposited", true)
                                    hostCursorComposited = comp
                                    listener.onHostCursorCompositedChanged(comp)
                                }
                                "RECONFIGURED" -> {
                                    val mons = BoBoProtocol.parseMonitors(msg)
                                    if (mons.isNotEmpty()) remoteMonitors = mons
                                    val w = msg.optInt("width", 0)
                                    val h = msg.optInt("height", 0)
                                    val fps = msg.optInt("fps", 60)
                                    if (w > 0 && h > 0) listener.onReconfigured(w, h, fps)
                                }
                                "SUPER_DISPLAY_STATE" -> {
                                    listener.onSuperDisplayStateChanged(
                                        msg.optBoolean("active", false),
                                        msg.optBoolean("nativeMatch", false),
                                        msg.optInt("width", 0),
                                        msg.optInt("height", 0)
                                    )
                                }
                                "PRIVACY_STATE" -> {
                                    listener.onPrivacyStateChanged(msg.optBoolean("active", false))
                                }
                                "PONG" -> {
                                    val sent = msg.optLong("ts", 0L)
                                    if (sent > 0L) {
                                        val rtt = (System.nanoTime() - sent) / 1_000_000.0
                                        if (rtt in 0.0..10_000.0) listener.onRttUpdated(rtt)
                                    }
                                }
                                "ERROR" -> {
                                    val code = msg.optString("code", "ERROR")
                                    val emsg = msg.optString("message", "远端返回错误")
                                    failed = true
                                    listener.onError(code, emsg)
                                    break
                                }
                                "DISCONNECT" -> {
                                    disconnectReason = "对方已断开远控"
                                    break
                                }
                            }
                        }
                        else -> Unit
                    }
                }
            } catch (ex: Exception) {
                if (generation.get() != request) return@Thread
                if (connected.get()) {
                    disconnectReason = ex.message ?: "网络连接中断"
                } else {
                    listener.onError("CONNECT_FAILED", "连接被控端失败：${ex.message}")
                }
            } finally {
                val notify = synchronized(sendLock) {
                    if (generation.get() != request) false else {
                        val wasConnected = connected.get()
                        clearConnectionLocked()
                        wasConnected && !failed
                    }
                }
                try { currentSocket?.close() } catch (_: Exception) {}
                if (notify) listener.onDisconnected(disconnectReason)
            }
        }, "BoBo6-Client-Rx").apply { isDaemon = true; start() }
    }

    private fun startTxThread(connection: SSLSocket, request: Long) {
        Thread({ txLoop(connection, request) }, "BoBo6-Client-Tx").apply { isDaemon = true; start() }
    }

    private fun txLoop(connection: SSLSocket, request: Long) {
        try {
            val out = connection.outputStream
            while (true) {
                val item = synchronized(sendLock) {
                    while (generation.get() == request && connected.get() && sendQueue.isEmpty() &&
                        pendingAbsMove == null && pendingRelDx == 0 && pendingRelDy == 0 && pendingTouchMoves.isEmpty()) {
                        sendLock.wait()
                    }
                    if (generation.get() != request || !connected.get()) return
                    if (sendQueue.isNotEmpty()) sendQueue.removeFirst() else takeNextCoalescedMoveLocked() ?: return
                }
                BoBoProtocol.writeFrame(out, item.type, item.flags, item.payload)
            }
        } catch (_: Exception) {
            try { connection.close() } catch (_: Exception) {}
        }
    }

    private fun flushCoalescedMovesLocked() {
        while (true) {
            val m = takeNextCoalescedMoveLocked() ?: break
            sendQueue.addLast(m)
        }
    }

    private fun takeNextCoalescedMoveLocked(): QueuedFrame? {
        pendingAbsMove?.let { abs ->
            pendingAbsMove = null
            return QueuedFrame(BoBoPacketType.INPUT_EVENT, 0L, BoBoProtocol.buildInputPayload(abs))
        }
        if (pendingRelDx != 0 || pendingRelDy != 0) {
            val dx = pendingRelDx.coerceIn(-32767, 32767)
            val dy = pendingRelDy.coerceIn(-32767, 32767)
            pendingRelDx -= dx
            pendingRelDy -= dy
            return QueuedFrame(BoBoPacketType.INPUT_EVENT, 0L, BoBoProtocol.buildInputPayload(BoBoProtocol.buildMouseRel(dx, dy)))
        }
        if (pendingTouchMoves.isNotEmpty()) {
            val it = pendingTouchMoves.entries.iterator()
            val first = it.next().value
            it.remove()
            return QueuedFrame(BoBoPacketType.INPUT_EVENT, 0L, BoBoProtocol.buildInputPayload(first))
        }
        return null
    }

    private fun pingLoop(connection: SSLSocket, request: Long) {
        while (generation.get() == request && connected.get()) {
            try {
                val silentMs = (System.nanoTime() - lastRxNs.get()) / 1_000_000L
                if (silentMs >= STREAM_READ_TIMEOUT_MS.toLong()) {
                    connection.close()
                    break
                }
                sendJson(JSONObject().apply {
                    put("type", "PING")
                    put("ts", System.nanoTime())
                })
                Thread.sleep(1500L)
            } catch (_: Exception) {
                break
            }
        }
    }

    fun sendInput(pkt: BoBoInputPacket) {
        if (!connected.get()) return
        synchronized(sendLock) {
            if (!connected.get()) return
            when {
                pkt.kind == BoBoProtocol.INPUT_KIND_MOUSE_ABS -> {
                    pendingAbsMove = pkt
                }
                pkt.kind == BoBoProtocol.INPUT_KIND_MOUSE_REL -> {
                    if (pkt.a.toInt() == 0 && pkt.b.toInt() == 0) return
                    pendingRelDx = (pendingRelDx + pkt.a.toInt()).coerceIn(-32768, 32767)
                    pendingRelDy = (pendingRelDy + pkt.b.toInt()).coerceIn(-32768, 32767)
                }
                pkt.kind == BoBoProtocol.INPUT_KIND_TOUCH && (pkt.flags and 0x0F) == BoBoProtocol.TOUCH_ACTION_MOVE -> {
                    val pointerId = (pkt.flags ushr 4) and 0x0F
                    pendingTouchMoves[pointerId] = pkt
                }
                else -> {
                    flushCoalescedMovesLocked()
                    if (sendQueue.size >= 256 && pkt.kind == BoBoProtocol.INPUT_KIND_RELEASE_ALL) sendQueue.clear()
                    if (sendQueue.size < 256) {
                        sendQueue.addLast(QueuedFrame(BoBoPacketType.INPUT_EVENT, 0L, BoBoProtocol.buildInputPayload(pkt)))
                    }
                }
            }
            sendLock.notifyAll()
        }
    }

    fun sendKeyCombo(vks: IntArray) {
        for (vk in vks) sendInput(BoBoProtocol.buildKey(vk, true, isExtendedVk(vk)))
        for (i in vks.indices.reversed()) sendInput(BoBoProtocol.buildKey(vks[i], false, isExtendedVk(vks[i])))
    }

    fun sendReconfigure(monitorIndex: Int, width: Int, height: Int, fps: Int, bitrateKbps: Int) {
        sendJson(JSONObject().apply {
            put("type", "RECONFIGURE")
            put("monitorIndex", monitorIndex)
            put("width", width)
            put("height", height)
            put("fps", fps)
            put("bitrateKbps", bitrateKbps)
        })
    }

    fun sendRequestKeyframe() = sendJson(JSONObject().put("type", "REQUEST_KEYFRAME"))
    fun sendSetMuteHost(mute: Boolean) = sendJson(JSONObject().put("type", "SET_MUTE_HOST").put("mute", mute))
    fun sendSetPrivacy(enable: Boolean) = sendJson(JSONObject().put("type", "SET_PRIVACY").put("enable", enable))
    fun sendLockWorkstation() = sendJson(JSONObject().put("type", "LOCK_WORKSTATION"))
    fun sendSetSuperDisplay(enable: Boolean, clientWidth: Int, clientHeight: Int) = sendJson(
        JSONObject().apply {
            put("type", "SET_SUPER_DISPLAY")
            put("enable", enable)
            put("clientWidth", clientWidth)
            put("clientHeight", clientHeight)
        }
    )

    fun sendClipboardText(text: String, injectUnicode: Boolean = true) {
        if (text.isEmpty()) return
        sendJson(JSONObject().apply {
            put("type", "CLIPBOARD_TEXT")
            put("text", text)
            put("injectUnicode", injectUnicode)
        })
    }

    private fun sendJson(obj: JSONObject) {
        if (!connected.get()) return
        val bytes = obj.toString().toByteArray(Charsets.UTF_8)
        synchronized(sendLock) {
            if (!connected.get()) return
            flushCoalescedMovesLocked()
            if (sendQueue.size < 256) {
                sendQueue.addLast(QueuedFrame(BoBoPacketType.CONTROL_JSON, 0L, bytes))
                sendLock.notifyAll()
            }
        }
    }

    fun disconnect() {
        resetConnection()
    }

    private fun resetConnection(): Long {
        val (request, oldSocket) = synchronized(sendLock) {
            val previous = socket
            val next = generation.incrementAndGet()
            clearConnectionLocked()
            next to previous
        }
        if (oldSocket != null) {
            Thread({
                try { oldSocket.close() } catch (_: Exception) {}
            }, "BoBo6-Client-Close").apply { isDaemon = true; start() }
        }
        return request
    }

    private fun startUdpChannel(
        host: String,
        udpPort: Int,
        udpSid: Long,
        udpKey: String,
        udpNack: Boolean,
        request: Long,
        listener: Listener
    ) {
        try {
            val ch = BoBoUdpChannel(
                host = host,
                port = udpPort,
                sessionId = udpSid,
                keyBase64 = udpKey,
                nackEnabled = udpNack,
                onVideo = { vp, tsMs ->
                    if (generation.get() == request && connected.get()) {
                        lastRxNs.set(System.nanoTime())
                        listener.onVideoFrame(vp, tsMs)
                    }
                },
                onAudio = { ap ->
                    if (generation.get() == request && connected.get()) {
                        lastRxNs.set(System.nanoTime())
                        listener.onAudioFrame(ap)
                    }
                },
                onRtt = { rtt ->
                    if (generation.get() == request && connected.get()) {
                        listener.onRttUpdated(rtt)
                    }
                },
                onFallback = {
                    if (generation.get() == request && connected.get()) {
                        sendJson(JSONObject().put("type", "TRANSPORT_FALLBACK").put("reason", "udp_timeout"))
                    }
                }
            )
            synchronized(sendLock) {
                if (generation.get() != request || !connected.get()) {
                    ch.close()
                    return
                }
                udpChannel?.close()
                udpChannel = ch
            }
            ch.start()
        } catch (_: Exception) {
        }
    }

    private fun clearConnectionLocked() {
        connected.set(false)
        socket = null
        try { udpChannel?.close() } catch (_: Exception) {}
        udpChannel = null
        sendQueue.clear()
        pendingAbsMove = null
        pendingRelDx = 0
        pendingRelDy = 0
        pendingTouchMoves.clear()
        sendLock.notifyAll()
    }

    companion object {
        private const val STREAM_READ_TIMEOUT_MS = 12_000
        private const val PIN_MISMATCH_MESSAGE = "目标设备证书与配对记录不一致（CERT_MISMATCH）"

        private fun isHelloAck(ack: JSONObject) =
            ack.optString("type") == "HELLO_ACK" && ack.optString("protocol") == BoBoProtocol.PROTOCOL_MAGIC

        private fun hasPinMismatch(identity: BoBoIdentityStore, node: String, thumb: String,
                                   expectedThumb: String = "", expectedNode: String = ""): Boolean {
            if (expectedNode.isNotBlank() && !expectedNode.equals(node, ignoreCase = true)) return true
            if (expectedThumb.isNotBlank() && !BoBoIdentityStore.fixedTimeEqualsHex(expectedThumb, thumb)) return true
            val pinned = identity.findPeerByNodeId(node) ?: return false
            return !BoBoIdentityStore.fixedTimeEqualsHex(pinned.certThumbprint, thumb)
        }

        private fun isExtendedVk(vk: Int): Boolean {
            val v = vk and 0xFF
            return v == 0x5B || v == 0x5C || v == 0xA3 || v == 0xA5 || (v in 0x21..0x2E)
        }

        fun probeHost(host: String, port: Int, identity: BoBoIdentityStore, timeoutMs: Int = 5000): BoBoProbeResult {
            identity.ensureLoaded()
            val targetPort = if (port > 0) port else BoBoProtocol.DEFAULT_TCP_PORT
            var sock: SSLSocket? = null
            return try {
                var serverThumb = ""
                sock = openTlsSocket(host, targetPort, timeoutMs) { serverThumb = it }
                sendHello(sock.outputStream, identity)
                val reply = BoBoProtocol.readFrame(sock.inputStream, BoBoProtocol.MAX_PRE_AUTH_PAYLOAD_BYTES)
                if (reply == null || reply.type != BoBoPacketType.CONTROL_JSON) {
                    return BoBoProbeResult(reachable = true, host = host, port = targetPort, error = "未返回有效 BoBo 握手")
                }
                val ack = BoBoProtocol.parseJson(reply.payload)
                if (ack == null || !isHelloAck(ack)) {
                    return BoBoProbeResult(reachable = true, host = host, port = targetPort, error = "目标非 BoBo远程 V6")
                }
                if (serverThumb.isBlank()) {
                    return BoBoProbeResult(reachable = true, host = host, port = targetPort, error = "缺少有效 TLS 证书")
                }
                val hostNodeId = ack.optString("nodeId", "")
                if (hasPinMismatch(identity, hostNodeId, serverThumb)) {
                    return BoBoProbeResult(reachable = true, host = host, port = targetPort, error = PIN_MISMATCH_MESSAGE)
                }
                BoBoProbeResult(
                    reachable = true,
                    isBoBoHost = true,
                    allowIncoming = ack.optBoolean("allowIncoming", true),
                    authorized = ack.optBoolean("authorized", false) && identity.isPeerAuthorized(hostNodeId, serverThumb),
                    host = host,
                    port = targetPort,
                    hostNodeId = ack.optString("nodeId", ""),
                    hostDeviceCode = ack.optString("deviceCode", ""),
                    hostName = ack.optString("deviceName", host),
                    hostThumbprint = serverThumb,
                    platform = ack.optString("platform", BoBoProtocol.PLATFORM_WINDOWS),
                    hostControlMode = ack.optString("hostControlMode", BoBoProtocol.CONTROL_MODE_FULL),
                    screenOrientation = ack.optInt("screenOrientation", 0),
                    nonce = ack.optString("nonce", ""),
                    monitors = BoBoProtocol.parseMonitors(ack)
                )
            } catch (ex: Exception) {
                BoBoProbeResult(
                    reachable = false,
                    host = host,
                    port = targetPort,
                    error = "未检测到对方打开 BoBo远程：${ex.message}"
                )
            } finally {
                try { sock?.close() } catch (_: Exception) {}
            }
        }

        fun pairWithPin(
            host: String,
            port: Int,
            pin: String,
            identity: BoBoIdentityStore,
            timeoutMs: Int = 10000,
            expectedHostThumbprint: String = "",
            expectedHostNodeId: String = ""
        ): Triple<Boolean, String, String> {
            identity.ensureLoaded()
            val targetPort = if (port > 0) port else BoBoProtocol.DEFAULT_TCP_PORT
            var sock: SSLSocket? = null
            return try {
                var serverThumb = ""
                sock = openTlsSocket(host, targetPort, timeoutMs) { serverThumb = it }
                sendHello(sock.outputStream, identity)
                val ackFrame = BoBoProtocol.readFrame(sock.inputStream, BoBoProtocol.MAX_PRE_AUTH_PAYLOAD_BYTES)
                    ?: return Triple(false, "", "被控端未返回握手信息")
                val ack = BoBoProtocol.parseJson(ackFrame.payload)
                    ?: return Triple(false, "", "握手数据无效")
                if (ackFrame.type != BoBoPacketType.CONTROL_JSON || !isHelloAck(ack)) {
                    return Triple(false, "", "目标不是 BoBo远程 V6 设备")
                }
                if (!ack.optBoolean("allowIncoming", true)) {
                    return Triple(false, "", "对方已关闭「允许本机被控」")
                }
                val hostNodeId = ack.optString("nodeId", "")
                val hostCode = ack.optString("deviceCode", "")
                val hostName = ack.optString("deviceName", host)
                if (serverThumb.isBlank()) {
                    return Triple(false, hostName, "未能从 TLS 握手提取对方证书指纹")
                }
                if (hasPinMismatch(identity, hostNodeId, serverThumb, expectedHostThumbprint, expectedHostNodeId)) {
                    return Triple(false, hostName, PIN_MISMATCH_MESSAGE)
                }
                val nonce = ack.optString("nonce", "")
                if (nonce.isBlank() || hostNodeId.isBlank()) return Triple(false, hostName, "无效的配对身份")

                val proof = BoBoIdentityStore.computePinProof(pin.trim(), nonce, identity.certThumbprint, serverThumb)
                val authSig = identity.signAuthChallenge(nonce, serverThumb)
                BoBoProtocol.writeJson(sock.outputStream, JSONObject().apply {
                    put("type", "PAIR_REQUEST")
                    put("proof", proof)
                    put("authSig", authSig)
                    put("clientCertDer", identity.certificateDerBase64)
                })

                val resFrame = BoBoProtocol.readFrame(sock.inputStream, BoBoProtocol.MAX_PRE_AUTH_PAYLOAD_BYTES)
                    ?: return Triple(false, hostName, "被控端未返回配对结果")
                val res = BoBoProtocol.parseJson(resFrame.payload)
                    ?: return Triple(false, hostName, "配对结果格式错误")
                if (resFrame.type != BoBoPacketType.CONTROL_JSON || res.optString("type") != "PAIR_RESULT" || !res.optBoolean("ok", false)) {
                    return Triple(false, hostName, res.optString("error", "4 位配对码校验失败"))
                }

                if (!identity.authorizeOrUpdatePeer(BoBoAuthorizedPeer(hostNodeId, hostCode, hostName, serverThumb, host))) {
                    return Triple(false, hostName, PIN_MISMATCH_MESSAGE)
                }
                Triple(true, hostName, "")
            } catch (ex: Exception) {
                Triple(false, "", "配对失败：${ex.message}")
            } finally {
                try { sock?.close() } catch (_: Exception) {}
            }
        }

        fun sendFile(
            host: String,
            port: Int,
            file: File,
            identity: BoBoIdentityStore,
            onProgress: ((Long, Long) -> Unit)? = null
        ): Pair<Boolean, String> {
            if (!file.exists() || !file.isFile) return Pair(false, "文件不存在")
            val total = file.length()
            if (total > BoBoProtocol.MAX_FILE_TRANSFER_BYTES) return Pair(false, "单文件大小超出 2 GiB 上限")
            identity.ensureLoaded()
            val targetPort = if (port > 0) port else BoBoProtocol.DEFAULT_TCP_PORT
            var sock: SSLSocket? = null
            return try {
                val digest = MessageDigest.getInstance("SHA-256")
                FileInputStream(file).use { fis ->
                    val b = ByteArray(65536)
                    var n: Int
                    while (fis.read(b).also { n = it } > 0) digest.update(b, 0, n)
                }
                val shaHex = BoBoProtocol.bytesToHex(digest.digest())
                val tid = UUID.randomUUID().toString().replace("-", "").substring(0, 16)

                var serverThumb = ""
                sock = openTlsSocket(host, targetPort, 15_000) { serverThumb = it }
                val out = sock.outputStream
                val inp = sock.inputStream
                sendHello(out, identity)
                val ackFrame = BoBoProtocol.readFrame(inp, BoBoProtocol.MAX_PRE_AUTH_PAYLOAD_BYTES)
                    ?: return Pair(false, "握手失败")
                val ack = BoBoProtocol.parseJson(ackFrame.payload)
                if (ackFrame.type != BoBoPacketType.CONTROL_JSON || ack == null || !isHelloAck(ack) || !ack.optBoolean("authorized", false)) {
                    return Pair(false, "请先与目标设备完成 4 位配对授权")
                }
                val hostNodeId = ack.optString("nodeId", "")
                if (serverThumb.isBlank() || !identity.isPeerAuthorized(hostNodeId, serverThumb)) {
                    return Pair(false, "目标主机 TLS 证书指纹未通过本地信任钉扎校验，已中止发送")
                }
                if (ack.optBoolean("authChallenge", false)) {
                    val nonce = ack.optString("nonce", "")
                    BoBoProtocol.writeJson(out, JSONObject().apply {
                        put("type", "AUTH_PROOF")
                        put("signature", identity.signAuthChallenge(nonce, serverThumb))
                        put("clientCertDer", identity.certificateDerBase64)
                    })
                    val authFrame = BoBoProtocol.readFrame(inp, BoBoProtocol.MAX_PRE_AUTH_PAYLOAD_BYTES)
                    val authRes = authFrame?.takeIf { it.type == BoBoPacketType.CONTROL_JSON }?.let { BoBoProtocol.parseJson(it.payload) }
                    if (authRes?.optString("type") != "AUTH_RESULT" || authRes.optBoolean("ok", false) != true) {
                        return Pair(false, authRes?.optString("error", "双向密钥身份校验失败") ?: "身份校验失败")
                    }
                }

                BoBoProtocol.writeJson(out, JSONObject().apply {
                    put("type", "FILE_OFFER")
                    put("transferId", tid)
                    put("fileName", file.name)
                    put("totalBytes", total)
                    put("sha256", shaHex)
                })
                val accFrame = BoBoProtocol.readFrame(inp) ?: return Pair(false, "对方未接受文件")
                val acc = BoBoProtocol.parseJson(accFrame.payload)
                if (accFrame.type != BoBoPacketType.CONTROL_JSON ||
                    acc?.optString("type") != "FILE_ACCEPT" || acc.optString("transferId") != tid) {
                    return Pair(false, acc?.optString("message", "对方拒绝了文件传输") ?: "拒绝接收")
                }

                val buf = ByteArray(65536)
                var offset = 0L
                FileInputStream(file).use { fis ->
                    var n: Int
                    while (fis.read(buf).also { n = it } > 0) {
                        val payload = BoBoProtocol.buildFileChunkPayload(tid, offset, buf, n)
                        BoBoProtocol.writeFrame(out, BoBoPacketType.FILE_CHUNK, 0L, payload)
                        offset += n
                        onProgress?.invoke(offset, total)
                    }
                }
                sock.soTimeout = 30_000
                BoBoProtocol.writeJson(out, JSONObject().apply {
                    put("type", "FILE_DONE")
                    put("transferId", tid)
                })
                val doneFrame = BoBoProtocol.readFrame(inp) ?: return Pair(false, "未收到完成确认")
                val done = BoBoProtocol.parseJson(doneFrame.payload)
                if (doneFrame.type == BoBoPacketType.CONTROL_JSON && done?.optString("type") == "FILE_ACK" &&
                    done.optString("transferId") == tid && done.optBoolean("ok", false)) {
                    Pair(true, done.optString("savedPath", file.name))
                } else {
                    Pair(false, done?.optString("error", "对方保存文件失败") ?: "传输校验失败")
                }
            } catch (ex: Exception) {
                Pair(false, "文件发送失败：${ex.message}")
            } finally {
                try { sock?.close() } catch (_: Exception) {}
            }
        }

        private fun sendHello(out: OutputStream, identity: BoBoIdentityStore) {
            BoBoProtocol.writeJson(out, JSONObject().apply {
                put("type", "HELLO")
                put("protocol", BoBoProtocol.PROTOCOL_MAGIC)
                put("protocolVersion", BoBoProtocol.PROTOCOL_VERSION)
                put("platform", BoBoProtocol.PLATFORM_ANDROID)
                put("nodeId", identity.nodeId)
                put("deviceCode", identity.deviceCode)
                put("deviceName", identity.deviceName)
                put("certThumbprint", identity.certThumbprint)
                put("clientCertDer", identity.certificateDerBase64)
                put("version", BoBoProtocol.APP_VERSION)
            })
        }

        private fun openTlsSocket(
            host: String,
            port: Int,
            timeoutMs: Int,
            onServerThumb: (String) -> Unit
        ): SSLSocket {
            val ctx = BoBoIdentityStore.createCapturingClientSslContext(onServerThumb)
            val sock = ctx.socketFactory.createSocket() as SSLSocket
            try {
                sock.tcpNoDelay = true
                sock.keepAlive = true
                sock.connect(InetSocketAddress(host, port), timeoutMs)
                sock.soTimeout = timeoutMs
                sock.enabledProtocols = sock.supportedProtocols.filter { it == "TLSv1.2" || it == "TLSv1.3" }.toTypedArray()
                val params = sock.sslParameters
                params.serverNames = listOf(SNIHostName(BoBoProtocol.TLS_SNI_HOST))
                sock.sslParameters = params
                sock.startHandshake()
                return sock
            } catch (e: Exception) {
                try { sock.close() } catch (_: Exception) {}
                throw e
            }
        }
    }
}
