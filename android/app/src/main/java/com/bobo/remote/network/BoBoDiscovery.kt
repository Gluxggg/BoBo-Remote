package com.bobo.remote.network

import android.content.Context
import android.net.wifi.WifiManager
import android.os.SystemClock
import android.util.Log
import com.bobo.remote.BuildConfig
import com.bobo.remote.protocol.BoBoDiscoveredPeer
import com.bobo.remote.protocol.BoBoIdentityStore
import com.bobo.remote.protocol.BoBoProtocol
import org.json.JSONException
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.SocketTimeoutException
import java.util.concurrent.atomic.AtomicBoolean

/** Owns the discovery socket and Wi-Fi multicast lock on one background thread. */
class BoBoLanDiscovery(
    context: Context,
    private val identity: BoBoIdentityStore,
    private val tcpPortProvider: () -> Int = { BoBoProtocol.DEFAULT_TCP_PORT },
    private val allowIncomingProvider: () -> Boolean = { true },
    private val onPeersUpdated: ((List<BoBoDiscoveredPeer>) -> Unit)? = null
) {
    private class Session {
        val stopping = AtomicBoolean(false)
        val probePending = AtomicBoolean(true)
    }

    private data class SeenPeer(val peer: BoBoDiscoveredPeer, val elapsedMs: Long)

    private val wifi = context.applicationContext.getSystemService(WifiManager::class.java)
    private val lock = Any()
    private var session: Session? = null
    private var started = false
    @Volatile private var snapshot = emptyList<BoBoDiscoveredPeer>()

    fun start() = synchronized(lock) {
        started = true
        startWorker()
    }

    fun stop() {
        synchronized(lock) {
            started = false
            session?.stopping?.set(true)
        }
    }

    fun sendProbeNow() {
        synchronized(lock) {
            if (!started) return
            startWorker()
            session?.probePending?.set(true)
        }
    }

    fun getPeers(): List<BoBoDiscoveredPeer> = snapshot

    private fun startWorker() {
        if (session != null) return
        val current = Session()
        session = current
        Thread({ runDiscovery(current) }, "BoBo6-UDP-Discovery").apply { isDaemon = true; start() }
    }

    private fun runDiscovery(current: Session) {
        var multicast: WifiManager.MulticastLock? = null
        try {
            if (current.stopping.get()) return
            identity.ensureLoaded()
            multicast = runCatching {
                wifi?.createMulticastLock("BoBoRemote:discovery")?.apply {
                    setReferenceCounted(false)
                    acquire()
                }
            }.getOrNull()
            DatagramSocket(null).use { socket ->
                socket.reuseAddress = true
                socket.broadcast = true
                socket.soTimeout = 250
                socket.bind(InetSocketAddress(BoBoProtocol.DISCOVERY_UDP_PORT))
                receiveAndAdvertise(current, socket)
                if (current.stopping.get()) broadcast(socket, "BOBO6_BYE")
            }
        } catch (error: Exception) {
            Log.w(TAG, "LAN discovery socket stopped", error)
        } finally {
            runCatching { multicast?.let { if (it.isHeld) it.release() } }
            publish(emptyList())
            synchronized(lock) {
                session = null
                if (started && current.stopping.get()) startWorker()
            }
        }
    }

    private fun receiveAndAdvertise(current: Session, socket: DatagramSocket) {
        val peers = LinkedHashMap<String, SeenPeer>()
        val bytes = ByteArray(4096)
        val packet = DatagramPacket(bytes, bytes.size)
        var nextBeacon = 0L
        while (!current.stopping.get()) {
            val now = SystemClock.elapsedRealtime()
            if (current.probePending.getAndSet(false)) {
                broadcast(socket, "BOBO6_PROBE")
                nextBeacon = now + BEACON_INTERVAL_MS
            } else if (now >= nextBeacon) {
                broadcast(socket, "BOBO6_HELLO")
                nextBeacon = now + BEACON_INTERVAL_MS
            }
            val expired = peers.entries.removeAll { now - it.value.elapsedMs > PEER_TIMEOUT_MS }
            val changed = try {
                packet.length = bytes.size
                socket.receive(packet)
                receivePeer(socket, packet, peers)
            } catch (_: SocketTimeoutException) {
                false
            } catch (_: JSONException) {
                false
            }
            if (expired || changed) publish(peers.values.map { it.peer }.sortedBy { it.deviceName.lowercase() })
        }
    }

    private fun receivePeer(
        socket: DatagramSocket,
        packet: DatagramPacket,
        peers: LinkedHashMap<String, SeenPeer>
    ): Boolean {
        val message = JSONObject(String(packet.data, packet.offset, packet.length, Charsets.UTF_8))
        if (message.optString("protocol") != BoBoProtocol.PROTOCOL_MAGIC ||
            message.optString("protocolVersion") != BoBoProtocol.PROTOCOL_VERSION
        ) return false
        val kind = message.optString("kind")
        val nodeId = message.optString("nodeId").trim()
        if (nodeId.isEmpty() || nodeId.length > 128 || nodeId.equals(identity.nodeId, ignoreCase = true)) return false
        val key = nodeId.lowercase()
        val host = packet.address.hostAddress?.removePrefix("::ffff:") ?: return false
        if (kind == "BOBO6_BYE") {
            return peers[key]?.peer?.host == host && peers.remove(key) != null
        }
        if (kind != "BOBO6_HELLO" && kind != "BOBO6_PROBE") return false
        val port = message.optInt("port", BoBoProtocol.DEFAULT_TCP_PORT)
        if (port !in 1..65535) return false
        val previous = peers.remove(key)
        val peer = BoBoDiscoveredPeer(
            nodeId = nodeId,
            deviceCode = message.optString("deviceCode").take(32),
            deviceName = message.optString("deviceName", host).take(128),
            host = host,
            port = port,
            version = message.optString("version").take(32),
            platform = message.optString("platform", BoBoProtocol.PLATFORM_WINDOWS).take(32),
            hostControlMode = message.optString("hostControlMode", BoBoProtocol.CONTROL_MODE_FULL).take(32),
            allowIncoming = message.optBoolean("allowIncoming", true),
            lastSeenMs = System.currentTimeMillis()
        )
        if (peers.size >= MAX_PEERS) peers.remove(peers.keys.first())
        val existing = previous?.peer?.takeIf { it.copy(lastSeenMs = peer.lastSeenMs) == peer }
        peers[key] = SeenPeer(existing ?: peer, SystemClock.elapsedRealtime())
        if (kind == "BOBO6_PROBE") send(socket, payload("BOBO6_HELLO"), packet.address)
        return existing == null
    }

    private fun publish(peers: List<BoBoDiscoveredPeer>) {
        if (snapshot == peers) return
        snapshot = peers
        onPeersUpdated?.invoke(peers)
    }

    private fun broadcast(socket: DatagramSocket, kind: String) {
        val destinations = linkedSetOf<InetAddress>()
        destinations.add(InetAddress.getByAddress(byteArrayOf(-1, -1, -1, -1)))
        runCatching {
            val interfaces = NetworkInterface.getNetworkInterfaces() ?: return@runCatching
            for (network in interfaces) {
                if (!network.isUp || network.isLoopback) continue
                for (address in network.interfaceAddresses) {
                    val directed = address.broadcast as? Inet4Address ?: continue
                    destinations.add(directed)
                }
            }
        }
        val data = payload(kind)
        for (address in destinations) send(socket, data, address)
    }

    private fun send(socket: DatagramSocket, bytes: ByteArray, address: InetAddress) {
        runCatching {
            socket.send(DatagramPacket(bytes, bytes.size, address, BoBoProtocol.DISCOVERY_UDP_PORT))
        }
    }

    private fun payload(kind: String): ByteArray = JSONObject().apply {
        put("kind", kind)
        put("protocol", BoBoProtocol.PROTOCOL_MAGIC)
        put("protocolVersion", BoBoProtocol.PROTOCOL_VERSION)
        put("platform", BoBoProtocol.PLATFORM_ANDROID)
        put("hostControlMode", BoBoProtocol.CONTROL_MODE_FULL)
        put("nodeId", identity.nodeId)
        put("deviceCode", identity.deviceCode)
        put("deviceName", identity.deviceName)
        put("port", tcpPortProvider())
        put("version", BuildConfig.VERSION_NAME)
        put("allowIncoming", allowIncomingProvider())
    }.toString().toByteArray(Charsets.UTF_8)

    private companion object {
        const val TAG = "BoBoLanDiscovery"
        const val MAX_PEERS = 128
        const val BEACON_INTERVAL_MS = 3500L
        const val PEER_TIMEOUT_MS = 18_000L
    }
}
