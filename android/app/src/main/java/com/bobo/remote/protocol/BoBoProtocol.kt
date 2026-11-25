package com.bobo.remote.protocol

import org.json.JSONArray
import org.json.JSONObject
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.nio.charset.StandardCharsets

enum class BoBoPacketType(val code: Byte) {
    CONTROL_JSON(0x01),
    VIDEO_H264(0x02),
    AUDIO_ADPCM(0x03),
    INPUT_EVENT(0x04),
    FILE_CHUNK(0x05);

    companion object {
        fun fromCode(code: Byte): BoBoPacketType? = values().firstOrNull { it.code == code }
    }
}

data class BoBoMonitorDescriptor(
    val index: Int = 0,
    val name: String = "DISPLAY1",
    val width: Int = 1920,
    val height: Int = 1080,
    val isPrimary: Boolean = true
)

data class BoBoDiscoveredPeer(
    val nodeId: String,
    val deviceCode: String,
    val deviceName: String,
    val host: String,
    val port: Int = BoBoProtocol.DEFAULT_TCP_PORT,
    val version: String = "6.2.3",
    val platform: String = BoBoProtocol.PLATFORM_WINDOWS,
    val hostControlMode: String = BoBoProtocol.CONTROL_MODE_FULL,
    val allowIncoming: Boolean = true,
    val lastSeenMs: Long = System.currentTimeMillis()
)

data class BoBoFrame(
    val type: BoBoPacketType,
    val flagsOrTimestamp: Long, // uint32 stored in Long
    val payload: ByteArray
)

data class BoBoVideoPayload(
    val width: Int,
    val height: Int,
    val isKeyframe: Boolean,
    val h264Data: ByteArray
)

data class BoBoAudioPayload(
    val sampleRate: Int,
    val channels: Int,
    val sampleCount: Int,
    val adpcmData: ByteArray
)

data class BoBoInputPacket(
    val kind: Int,
    val flags: Int,
    val a: Short,
    val b: Short
)

data class BoBoFileChunk(
    val transferId: String,
    val offset: Long,
    val data: ByteArray
)

/**
 * BoBo V6.2 权威二进制帧协议（TCP+TLS 1.2 :49810 / LAN UDP :49812）。
 * 严格遵循 protocol/BOBO6-SPEC.md，所有多字节整数均为 Little-Endian。
 */
object BoBoProtocol {
    const val PROTOCOL_MAGIC = "BOBO6"
    const val PROTOCOL_VERSION = "6.2"
    const val APP_VERSION = "6.2.3"
    const val TLS_SNI_HOST = "BoBoRemote-V6-Node"

    const val PLATFORM_WINDOWS = "windows"
    const val PLATFORM_MACOS = "macos"
    const val PLATFORM_ANDROID = "android"
    const val PLATFORM_IOS = "ios"

    const val CONTROL_MODE_FULL = "full"
    const val CONTROL_MODE_VIEW_ONLY = "view_only"

    const val DEFAULT_TCP_PORT = 49810
    const val DISCOVERY_UDP_PORT = 49812
    const val HEADER_BYTES = 9
    const val MAX_CONTROL_PAYLOAD_BYTES = 2 * 1024 * 1024
    const val MAX_VIDEO_PAYLOAD_BYTES = 16 * 1024 * 1024

    const val INPUT_KIND_MOUSE_ABS = 1
    const val INPUT_KIND_MOUSE_REL = 2
    const val INPUT_KIND_MOUSE_BTN = 3
    const val INPUT_KIND_MOUSE_WHEEL = 4
    const val INPUT_KIND_KEY = 5
    const val INPUT_KIND_RELEASE_ALL = 6
    const val INPUT_KIND_TOUCH = 7
    const val INPUT_KIND_MOBILE_NAV = 8

    const val TOUCH_ACTION_DOWN = 0
    const val TOUCH_ACTION_MOVE = 1
    const val TOUCH_ACTION_UP = 2
    const val TOUCH_ACTION_CANCEL = 3

    const val MOBILE_NAV_BACK = 1
    const val MOBILE_NAV_HOME = 2
    const val MOBILE_NAV_RECENTS = 3
    const val MOBILE_NAV_NOTIFICATIONS = 4
    const val MOBILE_NAV_LOCK = 5
    const val MOBILE_NAV_VOLUME_UP = 6
    const val MOBILE_NAV_VOLUME_DOWN = 7
    const val MOBILE_NAV_POWER_DIALOG = 8

    fun encodeFrame(type: BoBoPacketType, flagsOrTs: Long, payload: ByteArray): ByteArray {
        val out = ByteArray(HEADER_BYTES + payload.size)
        out[0] = type.code
        writeInt32LE(out, 1, payload.size)
        writeUInt32LE(out, 5, flagsOrTs)
        if (payload.isNotEmpty()) {
            System.arraycopy(payload, 0, out, HEADER_BYTES, payload.size)
        }
        return out
    }

    @Throws(IOException::class)
    fun writeFrame(stream: OutputStream, type: BoBoPacketType, flagsOrTs: Long, payload: ByteArray) {
        val header = ByteArray(HEADER_BYTES)
        header[0] = type.code
        writeInt32LE(header, 1, payload.size)
        writeUInt32LE(header, 5, flagsOrTs)
        stream.write(header)
        if (payload.isNotEmpty()) {
            stream.write(payload)
        }
        stream.flush()
    }

    @Throws(IOException::class)
    fun writeJson(stream: OutputStream, json: JSONObject) {
        val bytes = json.toString().toByteArray(StandardCharsets.UTF_8)
        writeFrame(stream, BoBoPacketType.CONTROL_JSON, 0L, bytes)
    }

    @Throws(IOException::class)
    fun readFrame(stream: InputStream): BoBoFrame? {
        val header = ByteArray(HEADER_BYTES)
        if (!readExact(stream, header, HEADER_BYTES)) return null
        val type = BoBoPacketType.fromCode(header[0])
            ?: throw IOException("Unknown BoBo6 frame type: ${header[0].toInt() and 0xFF}")
        val length = readInt32LE(header, 1)
        val flagsOrTs = readUInt32LE(header, 5)
        val maxLen = if (type == BoBoPacketType.VIDEO_H264) MAX_VIDEO_PAYLOAD_BYTES else MAX_CONTROL_PAYLOAD_BYTES
        if (length < 0 || length > maxLen) {
            throw IOException("BoBo6 frame payload out of bounds: $length")
        }
        val payload = ByteArray(length)
        if (length > 0 && !readExact(stream, payload, length)) {
            throw EOFException("Unexpected EOF reading BoBo6 payload ($length bytes)")
        }
        return BoBoFrame(type, flagsOrTs, payload)
    }

    fun parseJson(payload: ByteArray): JSONObject? = try {
        JSONObject(String(payload, StandardCharsets.UTF_8))
    } catch (_: Exception) {
        null
    }

    fun buildVideoPayload(width: Int, height: Int, isKeyframe: Boolean, h264Data: ByteArray): ByteArray {
        val buf = ByteArray(5 + h264Data.size)
        writeUInt16LE(buf, 0, width and 0xFFFF)
        writeUInt16LE(buf, 2, height and 0xFFFF)
        buf[4] = if (isKeyframe) 1 else 0
        if (h264Data.isNotEmpty()) {
            System.arraycopy(h264Data, 0, buf, 5, h264Data.size)
        }
        return buf
    }

    fun parseVideoPayload(payload: ByteArray): BoBoVideoPayload? {
        if (payload.size <= 5) return null
        val w = readUInt16LE(payload, 0)
        val h = readUInt16LE(payload, 2)
        val isKey = (payload[4].toInt() and 1) != 0
        val data = payload.copyOfRange(5, payload.size)
        return BoBoVideoPayload(w, h, isKey, data)
    }

    fun buildAudioPayload(sampleRate: Int, channels: Int, sampleCount: Int, adpcmData: ByteArray): ByteArray {
        val buf = ByteArray(7 + adpcmData.size)
        writeInt32LE(buf, 0, sampleRate)
        buf[4] = (channels and 0xFF).toByte()
        writeUInt16LE(buf, 5, sampleCount and 0xFFFF)
        if (adpcmData.isNotEmpty()) {
            System.arraycopy(adpcmData, 0, buf, 7, adpcmData.size)
        }
        return buf
    }

    fun parseAudioPayload(payload: ByteArray): BoBoAudioPayload? {
        if (payload.size <= 7) return null
        val sr = readInt32LE(payload, 0)
        val ch = payload[4].toInt() and 0xFF
        val sc = readUInt16LE(payload, 5)
        val data = payload.copyOfRange(7, payload.size)
        return BoBoAudioPayload(sr, ch, sc, data)
    }

    fun buildInputPayload(pkt: BoBoInputPacket): ByteArray {
        val buf = ByteArray(6)
        buf[0] = (pkt.kind and 0xFF).toByte()
        buf[1] = (pkt.flags and 0xFF).toByte()
        writeInt16LE(buf, 2, pkt.a)
        writeInt16LE(buf, 4, pkt.b)
        return buf
    }

    fun parseInputPayload(payload: ByteArray): BoBoInputPacket? {
        if (payload.size < 6) return null
        return BoBoInputPacket(
            kind = payload[0].toInt() and 0xFF,
            flags = payload[1].toInt() and 0xFF,
            a = readInt16LE(payload, 2),
            b = readInt16LE(payload, 4)
        )
    }

    fun buildMouseAbs(nx: Int, ny: Int) = BoBoInputPacket(
        INPUT_KIND_MOUSE_ABS, 0, (nx and 0xFFFF).toShort(), (ny and 0xFFFF).toShort()
    )

    fun buildMouseRel(dx: Int, dy: Int) = BoBoInputPacket(
        INPUT_KIND_MOUSE_REL, 0, dx.coerceIn(-32768, 32767).toShort(), dy.coerceIn(-32768, 32767).toShort()
    )

    fun buildMouseButton(button: Int, down: Boolean) = BoBoInputPacket(
        INPUT_KIND_MOUSE_BTN, if (down) 1 else 0, button.toShort(), 0
    )

    fun buildMouseWheel(delta: Int, horizontal: Boolean = false) = BoBoInputPacket(
        INPUT_KIND_MOUSE_WHEEL, if (horizontal) 1 else 0, delta.coerceIn(-32768, 32767).toShort(), 0
    )

    fun buildKey(vk: Int, down: Boolean, extended: Boolean = false) = BoBoInputPacket(
        INPUT_KIND_KEY, (if (down) 1 else 0) or (if (extended) 2 else 0), (vk and 0xFF).toShort(), 0
    )

    fun buildReleaseAll() = BoBoInputPacket(INPUT_KIND_RELEASE_ALL, 0, 0, 0)

    fun buildTouch(pointerId: Int, action: Int, nx: Int, ny: Int): BoBoInputPacket {
        val flags = ((pointerId and 0x0F) shl 4) or (action and 0x0F)
        return BoBoInputPacket(
            INPUT_KIND_TOUCH,
            flags,
            (nx and 0xFFFF).toShort(),
            (ny and 0xFFFF).toShort()
        )
    }

    fun buildMobileNav(navAction: Int) = BoBoInputPacket(INPUT_KIND_MOBILE_NAV, 0, navAction.toShort(), 0)

    fun buildFileChunkPayload(transferId16: String, offset: Long, data: ByteArray, count: Int = data.size): ByteArray {
        val idRaw = transferId16.toByteArray(StandardCharsets.US_ASCII)
        val buf = ByteArray(24 + count)
        System.arraycopy(idRaw, 0, buf, 0, minOf(16, idRaw.size))
        writeInt64LE(buf, 16, offset)
        if (count > 0) {
            System.arraycopy(data, 0, buf, 24, count)
        }
        return buf
    }

    fun parseFileChunkPayload(payload: ByteArray): BoBoFileChunk? {
        if (payload.size < 24) return null
        var len = 0
        while (len < 16 && payload[len].toInt() != 0) len++
        val tid = String(payload, 0, len, StandardCharsets.US_ASCII)
        val offset = readInt64LE(payload, 16)
        val chunk = payload.copyOfRange(24, payload.size)
        return BoBoFileChunk(tid, offset, chunk)
    }

    fun serializeMonitors(monitors: List<BoBoMonitorDescriptor>): JSONArray {
        val arr = JSONArray()
        for (m in monitors) {
            arr.put(JSONObject().apply {
                put("index", m.index)
                put("name", m.name)
                put("width", m.width)
                put("height", m.height)
                put("primary", m.isPrimary)
            })
        }
        return arr
    }

    fun parseMonitors(obj: JSONObject, key: String = "monitors"): List<BoBoMonitorDescriptor> {
        val arr = obj.optJSONArray(key) ?: return emptyList()
        val list = ArrayList<BoBoMonitorDescriptor>(arr.length())
        for (i in 0 until arr.length()) {
            val item = arr.optJSONObject(i) ?: continue
            list.add(
                BoBoMonitorDescriptor(
                    index = item.optInt("index", i),
                    name = item.optString("name", "DISPLAY${i + 1}"),
                    width = item.optInt("width", 1920),
                    height = item.optInt("height", 1080),
                    isPrimary = item.optBoolean("primary", i == 0)
                )
            )
        }
        return list
    }

    private fun readExact(stream: InputStream, buf: ByteArray, count: Int): Boolean {
        var offset = 0
        while (offset < count) {
            val n = stream.read(buf, offset, count - offset)
            if (n <= 0) return false
            offset += n
        }
        return true
    }

    fun writeInt16LE(b: ByteArray, o: Int, v: Short) {
        val iv = v.toInt()
        b[o] = (iv and 0xFF).toByte()
        b[o + 1] = ((iv ushr 8) and 0xFF).toByte()
    }

    fun readInt16LE(b: ByteArray, o: Int): Short {
        return ((b[o].toInt() and 0xFF) or ((b[o + 1].toInt() and 0xFF) shl 8)).toShort()
    }

    fun writeUInt16LE(b: ByteArray, o: Int, v: Int) {
        b[o] = (v and 0xFF).toByte()
        b[o + 1] = ((v ushr 8) and 0xFF).toByte()
    }

    fun readUInt16LE(b: ByteArray, o: Int): Int {
        return (b[o].toInt() and 0xFF) or ((b[o + 1].toInt() and 0xFF) shl 8)
    }

    fun writeInt32LE(b: ByteArray, o: Int, v: Int) {
        b[o] = (v and 0xFF).toByte()
        b[o + 1] = ((v ushr 8) and 0xFF).toByte()
        b[o + 2] = ((v ushr 16) and 0xFF).toByte()
        b[o + 3] = ((v ushr 24) and 0xFF).toByte()
    }

    fun readInt32LE(b: ByteArray, o: Int): Int {
        return (b[o].toInt() and 0xFF) or
            ((b[o + 1].toInt() and 0xFF) shl 8) or
            ((b[o + 2].toInt() and 0xFF) shl 16) or
            ((b[o + 3].toInt() and 0xFF) shl 24)
    }

    fun writeUInt32LE(b: ByteArray, o: Int, v: Long) {
        b[o] = (v and 0xFFL).toByte()
        b[o + 1] = ((v ushr 8) and 0xFFL).toByte()
        b[o + 2] = ((v ushr 16) and 0xFFL).toByte()
        b[o + 3] = ((v ushr 24) and 0xFFL).toByte()
    }

    fun readUInt32LE(b: ByteArray, o: Int): Long {
        return (b[o].toLong() and 0xFFL) or
            ((b[o + 1].toLong() and 0xFFL) shl 8) or
            ((b[o + 2].toLong() and 0xFFL) shl 16) or
            ((b[o + 3].toLong() and 0xFFL) shl 24)
    }

    fun writeInt64LE(b: ByteArray, o: Int, v: Long) {
        for (i in 0 until 8) {
            b[o + i] = ((v ushr (i * 8)) and 0xFFL).toByte()
        }
    }

    fun readInt64LE(b: ByteArray, o: Int): Long {
        var v = 0L
        for (i in 0 until 8) {
            v = v or ((b[o + i].toLong() and 0xFFL) shl (i * 8))
        }
        return v
    }

    fun bytesToHex(data: ByteArray): String {
        val sb = StringBuilder(data.size * 2)
        for (byte in data) {
            val v = byte.toInt() and 0xFF
            sb.append(HEX_CHARS[v ushr 4])
            sb.append(HEX_CHARS[v and 0x0F])
        }
        return sb.toString()
    }

    fun hexToBytes(hex: String): ByteArray {
        val clean = hex.trim()
        val out = ByteArray(clean.length / 2)
        for (i in out.indices) {
            out[i] = clean.substring(i * 2, i * 2 + 2).toInt(16).toByte()
        }
        return out
    }

    private const val HEX_CHARS = "0123456789abcdef"
}
