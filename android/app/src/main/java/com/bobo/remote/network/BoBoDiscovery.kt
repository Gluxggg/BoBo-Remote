package com.bobo.remote.network

import com.bobo.remote.protocol.BoBoDiscoveredPeer
import com.bobo.remote.protocol.BoBoIdentityStore
import com.bobo.remote.protocol.BoBoProtocol
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.nio.charset.StandardCharsets
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean

/**
 * 局域网 UDP 49812 发现服务（四端互通：Windows / macOS / Android / iOS）。
 */
class BoBoLanDiscovery(
    private val identity: BoBoIdentityStore,
    private val tcpPortProvider: () -> Int = { BoBoProtocol.DEFAULT_TCP_PORT },
    private val allowIncomingProvider: () -> Boolean = { true },
    private val onPeersUpdated: ((List<BoBoDiscoveredPeer>) -> Unit)? = null
) {
    private val peers = ConcurrentHashMap<String, BoBoDiscoveredPeer>()
    private val running = AtomicBoolean(false)
    @Volatile private var socket: DatagramSocket? = null
    private var rxThread: Thread? = null
    private var txThread: Thread? = null

    fun start() {
        if (!running.compareAndSet(false, true)) return
        identity.ensureLoaded()
        try {
            val sock = DatagramSocket(null).apply {
                reuseAddress = true
                broadcast = true
                soTimeout = 1000
                bind(InetSocketAddress(BoBoProtocol.DISCOVERY_UDP_PORT))
            }
            socket = sock

            rxThread = Thread({ receiveLoop(sock) }, "BoBo6-UDP-Rx").apply { isDaemon = true; start() }
            txThread = Thread({ beaconLoop() }, "BoBo6-UDP-Tx").apply { isDaemon = true; start() }
        } catch (_: Exception) {
            running.set(false)
        }
    }

    fun stop() {
        if (!running.compareAndSet(true, false)) return
        try {
            broadcastPacket("BOBO6_BYE")
        } catch (_: Exception) {
        }
        try {
            socket?.close()
        } catch (_: Exception) {
        }
        socket = null
    }

    fun sendProbeNow() {
        if (!running.get()) return
        Thread({
            try { broadcastPacket("BOBO6_PROBE") } catch (_: Exception) {}
        }, "BoBo6-UDP-Probe").start()
    }

    fun getPeers(): List<BoBoDiscoveredPeer> {
        val cutoff = System.currentTimeMillis() - 18_000L
        val alive = ArrayList<BoBoDiscoveredPeer>()
        val iter = peers.entries.iterator()
        while (iter.hasNext()) {
            val e = iter.next()
            if (e.value.lastSeenMs >= cutoff) alive.add(e.value) else iter.remove()
        }
        return alive.sortedBy { it.deviceName.lowercase() }
    }

    fun resolveHostByDeviceCode(input: String): String {
        val norm = BoBoIdentityStore.normalizeDeviceCode(input)
        if (norm.length == 9) {
            getPeers().firstOrNull {
                BoBoIdentityStore.normalizeDeviceCode(it.deviceCode) == norm
            }?.let { return it.host }
            identity.findPeerByCodeOrHost(norm)?.lastHost?.takeIf { it.isNotBlank() }?.let { return it }
        }
        return input.trim()
    }

    private fun beaconLoop() {
        try {
            broadcastPacket("BOBO6_PROBE")
            while (running.get()) {
                broadcastPacket("BOBO6_HELLO")
                Thread.sleep(3500L)
            }
        } catch (_: Exception) {
        }
    }

    private fun receiveLoop(sock: DatagramSocket) {
        val buf = ByteArray(4096)
        while (running.get()) {
            try {
                val pkt = DatagramPacket(buf, buf.size)
                sock.receive(pkt)
                val text = String(pkt.data, pkt.offset, pkt.length, StandardCharsets.UTF_8)
                val obj = JSONObject(text)
                val kind = obj.optString("kind", "")
                val peerNodeId = obj.optString("nodeId", "")
                if (peerNodeId.isBlank() || peerNodeId.equals(identity.nodeId, ignoreCase = true)) continue
                var senderIp = pkt.address?.hostAddress ?: continue
                if (senderIp.startsWith("::ffff:", ignoreCase = true)) senderIp = senderIp.substring(7)

                if (kind == "BOBO6_BYE") {
                    peers.remove(peerNodeId)
                    onPeersUpdated?.invoke(getPeers())
                    continue
                }
                if (kind == "BOBO6_HELLO" || kind == "BOBO6_PROBE") {
                    peers[peerNodeId] = BoBoDiscoveredPeer(
                        nodeId = peerNodeId,
                        deviceCode = obj.optString("deviceCode", ""),
                        deviceName = obj.optString("deviceName", senderIp),
                        host = senderIp,
                        port = obj.optInt("port", BoBoProtocol.DEFAULT_TCP_PORT),
                        version = obj.optString("version", "6.2.1"),
                        platform = obj.optString("platform", BoBoProtocol.PLATFORM_WINDOWS),
                        hostControlMode = obj.optString("hostControlMode", BoBoProtocol.CONTROL_MODE_FULL),
                        allowIncoming = obj.optBoolean("allowIncoming", true),
                        lastSeenMs = System.currentTimeMillis()
                    )
                    if (kind == "BOBO6_PROBE") {
                        sendPacketTo("BOBO6_HELLO", pkt.address, BoBoProtocol.DISCOVERY_UDP_PORT)
                    }
                    onPeersUpdated?.invoke(getPeers())
                }
            } catch (_: java.net.SocketTimeoutException) {
                // Periodic wakeup
            } catch (_: Exception) {
                if (!running.get()) break
            }
        }
    }

    private fun broadcastPacket(kind: String) {
        sendPacketTo(kind, InetAddress.getByName("255.255.255.255"), BoBoProtocol.DISCOVERY_UDP_PORT)
    }

    private fun sendPacketTo(kind: String, address: InetAddress, port: Int) {
        val sock = socket ?: return
        try {
            val payload = JSONObject().apply {
                put("kind", kind)
                put("protocol", BoBoProtocol.PROTOCOL_MAGIC)
                put("protocolVersion", BoBoProtocol.PROTOCOL_VERSION)
                put("platform", BoBoProtocol.PLATFORM_ANDROID)
                put("hostControlMode", BoBoProtocol.CONTROL_MODE_FULL)
                put("nodeId", identity.nodeId)
                put("deviceCode", identity.deviceCode)
                put("deviceName", identity.deviceName)
                put("port", tcpPortProvider())
                put("version", BoBoProtocol.APP_VERSION)
                put("allowIncoming", allowIncomingProvider())
            }.toString().toByteArray(StandardCharsets.UTF_8)
            sock.send(DatagramPacket(payload, payload.size, address, port))
        } catch (_: Exception) {
        }
    }
}
