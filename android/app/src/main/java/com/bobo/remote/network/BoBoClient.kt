package com.bobo.remote.network

import com.bobo.remote.protocol.BoBoAudioPayload
import com.bobo.remote.protocol.BoBoAuthorizedPeer
import com.bobo.remote.protocol.BoBoIdentityStore
import com.bobo.remote.protocol.BoBoInputPacket
import com.bobo.remote.protocol.BoBoMonitorDescriptor
import com.bobo.remote.protocol.BoBoPacketType
import com.bobo.remote.protocol.BoBoProtocol
import com.bobo.remote.protocol.BoBoVideoPayload
import org.json.JSONObject
import java.io.File
import java.io.FileInputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.security.MessageDigest
import java.util.UUID
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
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
    val clientHeight: Int = 0
)

/**
 * BoBo V6.2 Android 原生主控客户端（TLS 1.2 + 二进制流 + 配对 + 控制 + 文件发送）。
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
        fun onError(code: String, message: String)
        fun onDisconnected(reason: String)
    }

    private val writeLock = Any()
    private val sendExecutor = Executors.newSingleThreadExecutor { r ->
        Thread(r, "BoBo6-Client-Tx").apply { isDaemon = true }
    }
    private val connected = AtomicBoolean(false)
    @Volatile private var socket: SSLSocket? = null
    @Volatile private var outStream: OutputStream? = null

    @Volatile var remotePlatform: String = BoBoProtocol.PLATFORM_WINDOWS
        private set
    @Volatile var remoteHostControlMode: String = BoBoProtocol.CONTROL_MODE_FULL
        private set
    @Volatile var remoteMonitors: List<BoBoMonitorDescriptor> = emptyList()
        private set

    val isConnected: Boolean get() = connected.get()

    fun connectAndStartStream(options: BoBoStreamOptions, listener: Listener) {
        disconnect()
        identity.ensureLoaded()
        Thread({
            var disconnectReason = "连接已断开"
            try {
                var serverThumb = ""
                val sslSock = openTlsSocket(options.host, options.port, 8000) { serverThumb = it }
                socket = sslSock
                val out = sslSock.outputStream
                val inp = sslSock.inputStream
                outStream = out

                sendHello(out, identity)
                val ackFrame = BoBoProtocol.readFrame(inp)
                if (ackFrame == null || ackFrame.type != BoBoPacketType.CONTROL_JSON) {
                    listener.onError("HANDSHAKE_FAILED", "目标主机未返回有效的 BoBo 握手响应。")
                    disconnect()
                    return@Thread
                }
                val ack = BoBoProtocol.parseJson(ackFrame.payload)
                if (ack == null || ack.optString("protocol") != BoBoProtocol.PROTOCOL_MAGIC) {
                    listener.onError("NOT_BOBO", "目标主机未运行 BoBo远程 V6。")
                    disconnect()
                    return@Thread
                }
                if (!ack.optBoolean("allowIncoming", true)) {
                    listener.onError("HOST_DISABLED", "对方已关闭「允许本机被控」。")
                    disconnect()
                    return@Thread
                }
                if (!ack.optBoolean("authorized", false)) {
                    listener.onError("PAIR_REQUIRED", "首次连接需先输入对方 4 位临时配对码。")
                    disconnect()
                    return@Thread
                }

                remotePlatform = ack.optString("platform", BoBoProtocol.PLATFORM_WINDOWS)
                remoteHostControlMode = ack.optString("hostControlMode", BoBoProtocol.CONTROL_MODE_FULL)
                remoteMonitors = BoBoProtocol.parseMonitors(ack)

                val hostNodeId = ack.optString("nodeId", "")
                val hostCode = ack.optString("deviceCode", "")
                val hostName = ack.optString("deviceName", options.host)
                val thumb = serverThumb.ifBlank { ack.optString("certThumbprint", "") }
                if (hostNodeId.isNotBlank() && thumb.isNotBlank()) {
                    identity.authorizeOrUpdatePeer(
                        BoBoAuthorizedPeer(hostNodeId, hostCode, hostName, thumb, options.host)
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
                }
                synchronized(writeLock) {
                    BoBoProtocol.writeJson(out, startReq)
                }

                connected.set(true)
                Thread({ pingLoop() }, "BoBo6-Client-Ping").apply { isDaemon = true; start() }

                while (connected.get()) {
                    val frame = BoBoProtocol.readFrame(inp) ?: break
                    when (frame.type) {
                        BoBoPacketType.VIDEO_H264 -> {
                            BoBoProtocol.parseVideoPayload(frame.payload)?.let {
                                listener.onVideoFrame(it, frame.flagsOrTimestamp)
                            }
                        }
                        BoBoPacketType.AUDIO_ADPCM -> {
                            BoBoProtocol.parseAudioPayload(frame.payload)?.let {
                                listener.onAudioFrame(it)
                            }
                        }
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
                                    listener.onStreamStarted(remoteMonitors, w, h, fps, remotePlatform, remoteHostControlMode)
                                    listener.onSuperDisplayStateChanged(
                                        msg.optBoolean("superDisplayActive", false),
                                        msg.optBoolean("superDisplayNativeMatch", false),
                                        w, h
                                    )
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
                                    disconnectReason = emsg
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
                if (connected.get()) {
                    disconnectReason = ex.message ?: "网络连接中断"
                } else {
                    listener.onError("CONNECT_FAILED", "连接被控端失败：${ex.message}")
                    return@Thread
                }
            } finally {
                val was = connected.getAndSet(false)
                closeSocketQuietly()
                if (was) listener.onDisconnected(disconnectReason)
            }
        }, "BoBo6-Client-Rx").apply { isDaemon = true; start() }
    }

    private fun pingLoop() {
        while (connected.get()) {
            try {
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
        val payload = BoBoProtocol.buildInputPayload(pkt)
        sendExecutor.execute {
            val out = outStream ?: return@execute
            if (!connected.get()) return@execute
            try {
                synchronized(writeLock) {
                    BoBoProtocol.writeFrame(out, BoBoPacketType.INPUT_EVENT, 0L, payload)
                }
            } catch (_: Exception) {
            }
        }
    }

    fun sendKeyCombo(vks: IntArray) {
        for (vk in vks) sendInput(BoBoProtocol.buildKey(vk, true))
        for (i in vks.indices.reversed()) sendInput(BoBoProtocol.buildKey(vks[i], false))
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
        sendExecutor.execute {
            val out = outStream ?: return@execute
            if (!connected.get()) return@execute
            try {
                synchronized(writeLock) {
                    BoBoProtocol.writeJson(out, obj)
                }
            } catch (_: Exception) {
            }
        }
    }

    fun disconnect() {
        val was = connected.getAndSet(false)
        if (was) {
            try {
                outStream?.let { out ->
                    synchronized(writeLock) {
                        BoBoProtocol.writeJson(out, JSONObject().put("type", "DISCONNECT"))
                    }
                }
            } catch (_: Exception) {
            }
        }
        closeSocketQuietly()
    }

    private fun closeSocketQuietly() {
        try { socket?.close() } catch (_: Exception) {}
        socket = null
        outStream = null
    }

    companion object {
        fun probeHost(host: String, port: Int, identity: BoBoIdentityStore, timeoutMs: Int = 5000): BoBoProbeResult {
            identity.ensureLoaded()
            val targetPort = if (port > 0) port else BoBoProtocol.DEFAULT_TCP_PORT
            var sock: SSLSocket? = null
            return try {
                var serverThumb = ""
                sock = openTlsSocket(host, targetPort, timeoutMs) { serverThumb = it }
                sock.soTimeout = timeoutMs
                sendHello(sock.outputStream, identity)
                val reply = BoBoProtocol.readFrame(sock.inputStream)
                if (reply == null || reply.type != BoBoPacketType.CONTROL_JSON) {
                    return BoBoProbeResult(reachable = true, host = host, port = targetPort, error = "未返回有效 BoBo 握手")
                }
                val ack = BoBoProtocol.parseJson(reply.payload)
                if (ack == null || ack.optString("protocol") != BoBoProtocol.PROTOCOL_MAGIC) {
                    return BoBoProbeResult(reachable = true, host = host, port = targetPort, error = "目标非 BoBo远程 V6")
                }
                BoBoProbeResult(
                    reachable = true,
                    isBoBoHost = true,
                    allowIncoming = ack.optBoolean("allowIncoming", true),
                    authorized = ack.optBoolean("authorized", false),
                    host = host,
                    port = targetPort,
                    hostNodeId = ack.optString("nodeId", ""),
                    hostDeviceCode = ack.optString("deviceCode", ""),
                    hostName = ack.optString("deviceName", host),
                    hostThumbprint = serverThumb.ifBlank { ack.optString("certThumbprint", "") },
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
            timeoutMs: Int = 10000
        ): Triple<Boolean, String, String> {
            identity.ensureLoaded()
            val targetPort = if (port > 0) port else BoBoProtocol.DEFAULT_TCP_PORT
            var sock: SSLSocket? = null
            return try {
                var serverThumb = ""
                sock = openTlsSocket(host, targetPort, timeoutMs) { serverThumb = it }
                sock.soTimeout = timeoutMs
                sendHello(sock.outputStream, identity)
                val ackFrame = BoBoProtocol.readFrame(sock.inputStream)
                    ?: return Triple(false, "", "被控端未返回握手信息")
                val ack = BoBoProtocol.parseJson(ackFrame.payload)
                    ?: return Triple(false, "", "握手数据无效")
                if (ack.optString("protocol") != BoBoProtocol.PROTOCOL_MAGIC) {
                    return Triple(false, "", "目标不是 BoBo远程 V6 设备")
                }
                if (!ack.optBoolean("allowIncoming", true)) {
                    return Triple(false, "", "对方已关闭「允许本机被控」")
                }
                val hostNodeId = ack.optString("nodeId", "")
                val hostCode = ack.optString("deviceCode", "")
                val hostName = ack.optString("deviceName", host)
                val hostThumb = serverThumb.ifBlank { ack.optString("certThumbprint", "") }
                val nonce = ack.optString("nonce", "")

                val proof = BoBoIdentityStore.computePinProof(pin.trim(), nonce, identity.certThumbprint, hostThumb)
                BoBoProtocol.writeJson(sock.outputStream, JSONObject().apply {
                    put("type", "PAIR_REQUEST")
                    put("proof", proof)
                })

                val resFrame = BoBoProtocol.readFrame(sock.inputStream)
                    ?: return Triple(false, hostName, "被控端未返回配对结果")
                val res = BoBoProtocol.parseJson(resFrame.payload)
                    ?: return Triple(false, hostName, "配对结果格式错误")
                if (!res.optBoolean("ok", false)) {
                    return Triple(false, hostName, res.optString("error", "4 位配对码校验失败"))
                }

                identity.authorizeOrUpdatePeer(
                    BoBoAuthorizedPeer(hostNodeId, hostCode, hostName, hostThumb, host)
                )
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

                sock = openTlsSocket(host, targetPort, 15000) {}
                val out = sock.outputStream
                val inp = sock.inputStream
                sendHello(out, identity)
                val ackFrame = BoBoProtocol.readFrame(inp) ?: return Pair(false, "握手失败")
                val ack = BoBoProtocol.parseJson(ackFrame.payload)
                if (ack == null || !ack.optBoolean("authorized", false)) {
                    return Pair(false, "请先与目标设备完成 4 位配对授权")
                }

                BoBoProtocol.writeJson(out, JSONObject().apply {
                    put("type", "FILE_OFFER")
                    put("transferId", tid)
                    put("fileName", file.name)
                    put("totalBytes", file.length())
                    put("sha256", shaHex)
                })
                val accFrame = BoBoProtocol.readFrame(inp) ?: return Pair(false, "对方未接受文件")
                val acc = BoBoProtocol.parseJson(accFrame.payload)
                if (acc?.optString("type") != "FILE_ACCEPT") {
                    return Pair(false, acc?.optString("message", "对方拒绝了文件传输") ?: "拒绝接收")
                }

                val buf = ByteArray(65536)
                var offset = 0L
                val total = file.length()
                FileInputStream(file).use { fis ->
                    var n: Int
                    while (fis.read(buf).also { n = it } > 0) {
                        val payload = BoBoProtocol.buildFileChunkPayload(tid, offset, buf, n)
                        BoBoProtocol.writeFrame(out, BoBoPacketType.FILE_CHUNK, 0L, payload)
                        offset += n
                        onProgress?.invoke(offset, total)
                    }
                }
                BoBoProtocol.writeJson(out, JSONObject().apply {
                    put("type", "FILE_DONE")
                    put("transferId", tid)
                })
                val doneFrame = BoBoProtocol.readFrame(inp) ?: return Pair(false, "未收到完成确认")
                val done = BoBoProtocol.parseJson(doneFrame.payload)
                if (done?.optBoolean("ok", false) == true) {
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
            sock.tcpNoDelay = true
            sock.connect(InetSocketAddress(host, port), timeoutMs)
            sock.enabledProtocols = sock.supportedProtocols.filter { it == "TLSv1.2" || it == "TLSv1.3" }.toTypedArray()
            val params = sock.sslParameters
            params.serverNames = listOf(SNIHostName(BoBoProtocol.TLS_SNI_HOST))
            sock.sslParameters = params
            sock.startHandshake()
            return sock
        }
    }
}
