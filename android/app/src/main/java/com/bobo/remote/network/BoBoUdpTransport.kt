package com.bobo.remote.network

import com.bobo.remote.protocol.BoBoAudioPayload
import com.bobo.remote.protocol.BoBoProtocol
import com.bobo.remote.protocol.BoBoVideoPayload
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketTimeoutException
import java.nio.charset.StandardCharsets
import java.util.Arrays
import java.util.Base64
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

object BoBoUdpPacketType {
    const val HELLO = 1
    const val HELLO_ACK = 2
    const val VIDEO_SHARD = 3
    const val AUDIO = 4
    const val INPUT = 5
    const val PING = 6
    const val PONG = 7
    const val CURSOR_SHAPE = 8
    const val FEEDBACK = 9
    const val NACK = 10
    const val AUDIO_AAC = 11
}

data class OpenedUdpPacket(
    val packetType: Int,
    val seq: Long,
    val plaintext: ByteArray
)

data class BoBoUdpNackRequest(
    val frameId: Long,
    val shardIdx: Int
)

data class BoBoReassembledVideoFrame(
    val frameId: Long,
    val timestampMs: Long,
    val width: Int,
    val height: Int,
    val isKeyframe: Boolean,
    val h264Data: ByteArray
)

/**
 * Standard AEAD AES-128-GCM (128-bit tag) UDP transport crypto matching Windows C# CNG and Apple CryptoKit.
 * - Direction-separated 128-bit keys derived via HMAC-SHA256("BoBo7.3-UDP-GCM-H2C"|"BoBo7.3-UDP-GCM-C2H" || LE32(sessionId))
 * - 9B AAD header: sessionId(4B LE) || seq(4B LE) || pktType(1B)
 * - 12B Nonce: header(9B) || dir(1B: 1=H2C, 0=C2H) || 0x00 || 0x00
 * - Verify-before-replay-mutation 512-packet anti-replay window + fail-closed sequence cap (0xFFFFFF00).
 */
class BoBoUdpAesGcmCrypto(
    sessionId: Long,
    masterKey32: ByteArray,
    isHost: Boolean
) : AutoCloseable {
    companion object {
        const val HEADER_BYTES = 9
        const val GCM_TAG_BYTES = 16
        const val GCM_OVERHEAD_BYTES = HEADER_BYTES + GCM_TAG_BYTES
        const val MAX_SAFE_SEQUENCE = 0xFFFFFF00L

        fun deriveDirectionKey(masterKey32: ByteArray, sessionId: Long, label: String): ByteArray {
            val labelBytes = label.toByteArray(StandardCharsets.US_ASCII)
            val msg = ByteArray(labelBytes.size + 4)
            System.arraycopy(labelBytes, 0, msg, 0, labelBytes.size)
            writeUInt32LE(msg, labelBytes.size, sessionId)
            val mac = Mac.getInstance("HmacSHA256")
            mac.init(SecretKeySpec(masterKey32, "HmacSHA256"))
            val full = mac.doFinal(msg)
            val out = full.copyOfRange(0, 16)
            Arrays.fill(full, 0.toByte())
            return out
        }

        fun writeUInt16LE(b: ByteArray, o: Int, v: Int) {
            b[o] = (v and 0xFF).toByte()
            b[o + 1] = ((v ushr 8) and 0xFF).toByte()
        }

        fun readUInt16LE(b: ByteArray, o: Int): Int =
            (b[o].toInt() and 0xFF) or ((b[o + 1].toInt() and 0xFF) shl 8)

        fun writeUInt32LE(b: ByteArray, o: Int, v: Long) {
            b[o] = (v and 0xFFL).toByte()
            b[o + 1] = ((v ushr 8) and 0xFFL).toByte()
            b[o + 2] = ((v ushr 16) and 0xFFL).toByte()
            b[o + 3] = ((v ushr 24) and 0xFFL).toByte()
        }

        fun readUInt32LE(b: ByteArray, o: Int): Long =
            (b[o].toLong() and 0xFFL) or
                ((b[o + 1].toLong() and 0xFFL) shl 8) or
                ((b[o + 2].toLong() and 0xFFL) shl 16) or
                ((b[o + 3].toLong() and 0xFFL) shl 24)

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
    }

    val sessionId: Long = sessionId and 0xFFFFFFFFL
    private val sendDir: Byte = if (isHost) 1 else 0
    private val recvDir: Byte = if (isHost) 0 else 1
    private val encKeyBytes: ByteArray
    private val decKeyBytes: ByteArray
    private val encKeySpec: SecretKeySpec
    private val decKeySpec: SecretKeySpec

    private val encLock = Any()
    private val decLock = Any()
    private var sendSeq: Long = 0L
    private var hasRecvSeq = false
    private var maxRecvSeq: Long = 0L
    private val replayMask = LongArray(8)
    @Volatile private var closed = false

    init {
        require(masterKey32.size >= 32) { "UDP session key must be at least 32 bytes." }
        val h2c = deriveDirectionKey(masterKey32, this.sessionId, "BoBo7.3-UDP-GCM-H2C")
        val c2h = deriveDirectionKey(masterKey32, this.sessionId, "BoBo7.3-UDP-GCM-C2H")
        encKeyBytes = if (isHost) h2c else c2h
        decKeyBytes = if (isHost) c2h else h2c
        encKeySpec = SecretKeySpec(encKeyBytes, "AES")
        decKeySpec = SecretKeySpec(decKeyBytes, "AES")
    }

    internal fun forceSendSeqForTest(nextBeforeIncrement: Long) {
        synchronized(encLock) {
            sendSeq = nextBeforeIncrement and 0xFFFFFFFFL
        }
    }

    fun seal(packetType: Int, plaintext: ByteArray, offset: Int = 0, count: Int = plaintext.size): ByteArray? {
        if (closed) return null
        val bodyLen = if (count > 0) count else 0
        synchronized(encLock) {
            if (closed) return null
            if (sendSeq >= MAX_SAFE_SEQUENCE) {
                throw IllegalStateException("UDP_NONCE_EXHAUSTED")
            }
            sendSeq += 1L
            val seq = sendSeq
            val header = ByteArray(HEADER_BYTES)
            writeUInt32LE(header, 0, sessionId)
            writeUInt32LE(header, 4, seq)
            header[8] = (packetType and 0xFF).toByte()

            val nonce = ByteArray(12)
            System.arraycopy(header, 0, nonce, 0, HEADER_BYTES)
            nonce[9] = sendDir
            nonce[10] = 0
            nonce[11] = 0

            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.ENCRYPT_MODE, encKeySpec, GCMParameterSpec(128, nonce))
            cipher.updateAAD(header)
            val ctWithTag = if (bodyLen > 0) cipher.doFinal(plaintext, offset, bodyLen) else cipher.doFinal()
            val out = ByteArray(HEADER_BYTES + ctWithTag.size)
            System.arraycopy(header, 0, out, 0, HEADER_BYTES)
            System.arraycopy(ctWithTag, 0, out, HEADER_BYTES, ctWithTag.size)
            return out
        }
    }

    fun open(datagram: ByteArray, length: Int = datagram.size): OpenedUdpPacket? {
        if (closed || length < GCM_OVERHEAD_BYTES || length > datagram.size) return null
        if (readUInt32LE(datagram, 0) != sessionId) return null
        val seq = readUInt32LE(datagram, 4)
        val pktType = datagram[8].toInt() and 0xFF

        synchronized(decLock) {
            if (closed) return null
            // Read-only replay pre-check: forged packets never mutate replay state
            if (!checkReplayAcceptableLocked(seq)) return null

            val header = datagram.copyOfRange(0, HEADER_BYTES)
            val nonce = ByteArray(12)
            System.arraycopy(header, 0, nonce, 0, HEADER_BYTES)
            nonce[9] = recvDir
            nonce[10] = 0
            nonce[11] = 0

            val plain = try {
                val cipher = Cipher.getInstance("AES/GCM/NoPadding")
                cipher.init(Cipher.DECRYPT_MODE, decKeySpec, GCMParameterSpec(128, nonce))
                cipher.updateAAD(header)
                cipher.doFinal(datagram, HEADER_BYTES, length - HEADER_BYTES)
            } catch (_: Exception) {
                return null
            }

            if (!commitReplayLocked(seq)) return null
            return OpenedUdpPacket(pktType, seq, plain)
        }
    }

    private fun checkReplayAcceptableLocked(seq: Long): Boolean {
        if (!hasRecvSeq) return true
        val delta = seq.toInt() - maxRecvSeq.toInt()
        if (delta == 0) return false
        if (delta > 0) return true
        val back = -delta
        if (back >= 512) return false
        val wordIdx = back ushr 6
        val bit = 1L shl (back and 63)
        return (replayMask[wordIdx] and bit) == 0L
    }

    private fun commitReplayLocked(seq: Long): Boolean {
        if (!hasRecvSeq) {
            hasRecvSeq = true
            maxRecvSeq = seq
            Arrays.fill(replayMask, 0L)
            replayMask[0] = 1L
            return true
        }
        val delta = seq.toInt() - maxRecvSeq.toInt()
        if (delta == 0) return false
        if (delta < 0) {
            val back = -delta
            if (back >= 512) return false
            val wordIdx = back ushr 6
            val bit = 1L shl (back and 63)
            if ((replayMask[wordIdx] and bit) != 0L) return false
            replayMask[wordIdx] = replayMask[wordIdx] or bit
            return true
        }
        shiftReplayWindowLeft(replayMask, delta)
        replayMask[0] = replayMask[0] or 1L
        maxRecvSeq = seq
        return true
    }

    private fun shiftReplayWindowLeft(words: LongArray, shift: Int) {
        val n = words.size
        if (shift >= n * 64) {
            Arrays.fill(words, 0L)
            return
        }
        val wordShift = shift ushr 6
        val bitShift = shift and 63
        for (i in n - 1 downTo 0) {
            val src = i - wordShift
            var v = if (src >= 0) words[src] else 0L
            if (bitShift != 0 && src >= 0) {
                val carry = if (src - 1 >= 0) (words[src - 1] ushr (64 - bitShift)) else 0L
                v = (v shl bitShift) or carry
            }
            words[i] = v
        }
    }

    override fun close() {
        closed = true
        synchronized(encLock) { Arrays.fill(encKeyBytes, 0.toByte()) }
        synchronized(decLock) { Arrays.fill(decKeyBytes, 0.toByte()) }
    }
}

object BoBoUdpNackCodec {
    const val MAX_ENTRIES_PER_PACKET = 48

    fun encode(requests: List<BoBoUdpNackRequest>): ByteArray? {
        if (requests.isEmpty()) return null
        val count = minOf(requests.size, MAX_ENTRIES_PER_PACKET)
        val buf = ByteArray(2 + count * 6)
        BoBoUdpAesGcmCrypto.writeUInt16LE(buf, 0, count)
        var pos = 2
        for (i in 0 until count) {
            BoBoUdpAesGcmCrypto.writeUInt32LE(buf, pos, requests[i].frameId)
            BoBoUdpAesGcmCrypto.writeUInt16LE(buf, pos + 4, requests[i].shardIdx)
            pos += 6
        }
        return buf
    }

    fun decode(payload: ByteArray): List<BoBoUdpNackRequest> {
        if (payload.size < 2) return emptyList()
        val count = minOf(BoBoUdpAesGcmCrypto.readUInt16LE(payload, 0), MAX_ENTRIES_PER_PACKET)
        val out = ArrayList<BoBoUdpNackRequest>(count)
        var pos = 2
        for (i in 0 until count) {
            if (pos + 6 > payload.size) break
            val fid = BoBoUdpAesGcmCrypto.readUInt32LE(payload, pos)
            val sidx = BoBoUdpAesGcmCrypto.readUInt16LE(payload, pos + 4)
            out.add(BoBoUdpNackRequest(fid, sidx))
            pos += 6
        }
        return out
    }
}

object BoBoUdpFec {
    const val MAX_SHARD_PAYLOAD_BYTES = 1140
    const val SHARD_HEADER_BYTES = 19
    const val FLAG_KEYFRAME = 1
    const val FLAG_PARITY = 2
    const val FLAG_INTERLEAVED = 4
    const val FLAG_RETRANSMIT = 8

    fun buildVideoShards(
        frameId: Long,
        timestampMs: Long,
        width: Int,
        height: Int,
        isKeyframe: Boolean,
        h264Data: ByteArray,
        groupSizeIn: Int = 3,
        interleavedFec: Boolean = true
    ): List<ByteArray> {
        if (h264Data.isEmpty()) return emptyList()
        val totalLen = h264Data.size
        val dataCount = (totalLen + MAX_SHARD_PAYLOAD_BYTES - 1) / MAX_SHARD_PAYLOAD_BYTES
        val groupSize = maxOf(1, groupSizeIn)
        var parityCount = (dataCount + groupSize - 1) / groupSize
        var effGroup = groupSize
        if (parityCount > 255) {
            effGroup = (dataCount + 254) / 255
            parityCount = (dataCount + effGroup - 1) / effGroup
        }
        val useInterleaved = interleavedFec && parityCount > 0
        val dataFlags = (if (isKeyframe) FLAG_KEYFRAME else 0) or (if (useInterleaved) FLAG_INTERLEAVED else 0)
        val shards = ArrayList<ByteArray>(dataCount + parityCount)

        for (i in 0 until dataCount) {
            val off = i * MAX_SHARD_PAYLOAD_BYTES
            val len = minOf(MAX_SHARD_PAYLOAD_BYTES, totalLen - off)
            val pkt = ByteArray(SHARD_HEADER_BYTES + len)
            writeShardHeader(pkt, frameId, timestampMs, width, height, dataFlags, i, dataCount, effGroup, parityCount)
            System.arraycopy(h264Data, off, pkt, SHARD_HEADER_BYTES, len)
            shards.add(pkt)
        }

        val parityFlags = dataFlags or FLAG_PARITY
        for (g in 0 until parityCount) {
            val start = if (useInterleaved) g else g * effGroup
            val end = if (useInterleaved) dataCount else minOf(dataCount, start + effGroup)
            val step = if (useInterleaved) parityCount else 1
            val maxMemberLen = minOf(MAX_SHARD_PAYLOAD_BYTES, totalLen - start * MAX_SHARD_PAYLOAD_BYTES)
            val pkt = ByteArray(SHARD_HEADER_BYTES + 2 + maxMemberLen)
            writeShardHeader(pkt, frameId, timestampMs, width, height, parityFlags, g, dataCount, effGroup, parityCount)
            var xorLen = 0
            var idx = start
            while (idx < end) {
                val off = idx * MAX_SHARD_PAYLOAD_BYTES
                val len = minOf(MAX_SHARD_PAYLOAD_BYTES, totalLen - off)
                xorLen = xorLen xor len
                for (k in 0 until len) {
                    pkt[SHARD_HEADER_BYTES + 2 + k] =
                        (pkt[SHARD_HEADER_BYTES + 2 + k].toInt() xor h264Data[off + k].toInt()).toByte()
                }
                idx += step
            }
            BoBoUdpAesGcmCrypto.writeUInt16LE(pkt, SHARD_HEADER_BYTES, xorLen)
            shards.add(pkt)
        }
        return shards
    }

    private fun writeShardHeader(
        pkt: ByteArray,
        frameId: Long,
        timestampMs: Long,
        width: Int,
        height: Int,
        flags: Int,
        shardIdx: Int,
        dataCount: Int,
        groupSize: Int,
        parityCount: Int
    ) {
        BoBoUdpAesGcmCrypto.writeUInt32LE(pkt, 0, frameId)
        BoBoUdpAesGcmCrypto.writeUInt32LE(pkt, 4, timestampMs)
        BoBoUdpAesGcmCrypto.writeUInt16LE(pkt, 8, width and 0xFFFF)
        BoBoUdpAesGcmCrypto.writeUInt16LE(pkt, 10, height and 0xFFFF)
        pkt[12] = (flags and 0xFF).toByte()
        BoBoUdpAesGcmCrypto.writeUInt16LE(pkt, 13, shardIdx)
        BoBoUdpAesGcmCrypto.writeUInt16LE(pkt, 15, dataCount)
        pkt[17] = (groupSize and 0xFF).toByte()
        pkt[18] = (parityCount and 0xFF).toByte()
    }
}

/**
 * Reassembles encrypted UDP video shards with grouped & interleaved XOR FEC recovery + selective NACK + PLI.
 */
class BoBoUdpVideoAssembler(private val nackEnabled: Boolean = true) {
    private class FrameBucket(
        val frameId: Long,
        val timestampMs: Long,
        val width: Int,
        val height: Int,
        val isKeyframe: Boolean,
        val dataCount: Int,
        val groupSize: Int,
        val parityCount: Int,
        val interleavedFec: Boolean,
        var lastShardNs: Long
    ) {
        val dataShards = arrayOfNulls<ByteArray>(dataCount)
        val parityShards = if (parityCount > 0) arrayOfNulls<ByteArray>(parityCount) else null
        var receivedDataCount = 0
        var highestDataIdxSeen = -1
        var anyParitySeen = false
        var isComplete = false
        var assembledData: ByteArray? = null
        var nackSentNs: LongArray? = null
        var nackRetries: ByteArray? = null
    }

    private val lock = Any()
    private val buckets = HashMap<Long, FrameBucket>(16)
    private var hasDeliveredFrame = false
    private var lastDeliveredFrameId: Long = 0L
    private var awaitingKeyframe = true
    private var awaitingKeyframeSinceNs = 0L
    private var lastPliNs = 0L
    private var gapFirstSeenNs = 0L

    private var hasSeqWindow = false
    private var seqWindowMax = 0L
    private var seqWindowReceived = 0L
    private var seqWindowExpected = 0L
    private var seqWindowStartNs = 0L

    @Volatile var latestLossPct: Double = 0.0
        private set
    @Volatile var fecRecoveredPackets: Long = 0L
        private set
    @Volatile var nackRequestsSent: Long = 0L
        private set
    val lastDeliveredId: Long get() = synchronized(lock) { lastDeliveredFrameId }

    fun reset(nowNs: Long = System.nanoTime()) {
        synchronized(lock) {
            buckets.clear()
            hasDeliveredFrame = false
            lastDeliveredFrameId = 0L
            awaitingKeyframe = true
            awaitingKeyframeSinceNs = nowNs
            gapFirstSeenNs = 0L
            hasSeqWindow = false
            seqWindowReceived = 0L
            seqWindowExpected = 0L
            seqWindowStartNs = nowNs
            latestLossPct = 0.0
        }
    }

    fun recordReceivedUdpSeq(seq: Long, nowNs: Long = System.nanoTime()) {
        synchronized(lock) {
            if (!hasSeqWindow) {
                hasSeqWindow = true
                seqWindowMax = seq
                seqWindowReceived = 1L
                seqWindowExpected = 1L
                seqWindowStartNs = nowNs
                return
            }
            seqWindowReceived++
            val delta = seq.toInt() - seqWindowMax.toInt()
            if (delta > 0) {
                seqWindowExpected += delta.toLong()
                seqWindowMax = seq
            }
            refreshLossLocked(nowNs)
        }
    }

    private fun refreshLossLocked(nowNs: Long) {
        if (!hasSeqWindow) return
        val elapsedMs = (nowNs - seqWindowStartNs) / 1_000_000.0
        if (elapsedMs >= 1000.0 && seqWindowExpected >= 10L) {
            val lost = maxOf(0L, seqWindowExpected - seqWindowReceived)
            latestLossPct = (lost.toDouble() * 100.0) / seqWindowExpected.toDouble()
            seqWindowReceived = 0L
            seqWindowExpected = 0L
            seqWindowStartNs = nowNs
        }
    }

    fun processShard(
        shardPayload: ByteArray,
        currentRttMs: Double,
        outReady: MutableList<BoBoReassembledVideoFrame>,
        nowNs: Long = System.nanoTime()
    ): Boolean {
        if (shardPayload.size <= BoBoUdpFec.SHARD_HEADER_BYTES) return false
        val frameId = BoBoUdpAesGcmCrypto.readUInt32LE(shardPayload, 0)
        val tsMs = BoBoUdpAesGcmCrypto.readUInt32LE(shardPayload, 4)
        val width = BoBoUdpAesGcmCrypto.readUInt16LE(shardPayload, 8)
        val height = BoBoUdpAesGcmCrypto.readUInt16LE(shardPayload, 10)
        val flags = shardPayload[12].toInt() and 0xFF
        val isKey = (flags and BoBoUdpFec.FLAG_KEYFRAME) != 0
        val isParity = (flags and BoBoUdpFec.FLAG_PARITY) != 0
        val interleaved = (flags and BoBoUdpFec.FLAG_INTERLEAVED) != 0
        val shardIdx = BoBoUdpAesGcmCrypto.readUInt16LE(shardPayload, 13)
        val dataCount = BoBoUdpAesGcmCrypto.readUInt16LE(shardPayload, 15)
        val groupSize = shardPayload[17].toInt() and 0xFF
        val parityCount = shardPayload[18].toInt() and 0xFF
        if (dataCount <= 0 || dataCount > 4096) return false

        synchronized(lock) {
            if (hasDeliveredFrame && (frameId.toInt() - lastDeliveredFrameId.toInt()) <= 0) {
                return false
            }
            if (awaitingKeyframe && !isKey) {
                return shouldTriggerPliLocked(currentRttMs, nowNs)
            }

            var b = buckets[frameId]
            if (b == null) {
                if (buckets.size >= 12) {
                    pruneStaleLocked(frameId, nowNs)
                }
                b = FrameBucket(
                    frameId, tsMs, width, height, isKey, dataCount,
                    maxOf(1, groupSize), parityCount, interleaved, nowNs
                )
                buckets[frameId] = b
            } else {
                b.lastShardNs = nowNs
            }
            if (b.isComplete) return false

            val body = shardPayload.copyOfRange(BoBoUdpFec.SHARD_HEADER_BYTES, shardPayload.size)
            if (isParity) {
                val pArr = b.parityShards
                if (pArr != null && shardIdx in pArr.indices && pArr[shardIdx] == null) {
                    pArr[shardIdx] = body
                    b.anyParitySeen = true
                    tryFecRecoverGroupLocked(b, shardIdx)
                }
            } else {
                if (shardIdx in 0 until b.dataCount && b.dataShards[shardIdx] == null) {
                    b.dataShards[shardIdx] = body
                    b.receivedDataCount++
                    if (shardIdx > b.highestDataIdxSeen) b.highestDataIdxSeen = shardIdx
                    if (b.parityCount > 0) {
                        val g = if (b.interleavedFec) (shardIdx % b.parityCount) else (shardIdx / b.groupSize)
                        tryFecRecoverGroupLocked(b, g)
                    }
                }
            }

            if (b.receivedDataCount >= b.dataCount) {
                assembleBucketLocked(b)
                if (!hasDeliveredFrame || b.isKeyframe || (b.frameId.toInt() - lastDeliveredFrameId.toInt()) == 1) {
                    deliverFromBucketLocked(b, outReady)
                }
            }
            return false
        }
    }

    fun pollRecovery(
        nowNs: Long,
        currentRttMs: Double,
        outNacks: MutableList<BoBoUdpNackRequest>
    ): Boolean {
        outNacks.clear()
        synchronized(lock) {
            refreshLossLocked(nowNs)
            val baseRtt = maxOf(6.0, minOf(350.0, if (currentRttMs > 0.5) currentRttMs else 20.0))
            if (awaitingKeyframe) {
                val waitMs = if (awaitingKeyframeSinceNs == 0L) 1000.0 else (nowNs - awaitingKeyframeSinceNs) / 1_000_000.0
                if (waitMs >= maxOf(260.0, minOf(900.0, baseRtt * 2.5 + 120.0))) {
                    return shouldTriggerPliLocked(baseRtt, nowNs)
                }
                return false
            }

            val ordered = buckets.values.sortedWith(Comparator { a, b ->
                val d = a.frameId.toInt() - b.frameId.toInt()
                if (d < 0) -1 else if (d > 0) 1 else 0
            })
            if (nackEnabled && ordered.isNotEmpty()) {
                val newestId = ordered.last().frameId
                val retryIntervalMs = maxOf(14.0, minOf(80.0, baseRtt * 1.15))
                for (b in ordered) {
                    if (b.isComplete || outNacks.size >= BoBoUdpNackCodec.MAX_ENTRIES_PER_PACKET) continue
                    val hasNewer = (newestId.toInt() - b.frameId.toInt()) > 0
                    val idleMs = (nowNs - b.lastShardNs) / 1_000_000.0
                    val groupCount = if (b.parityCount > 0) b.parityCount else 1
                    for (g in 0 until groupCount) {
                        if (outNacks.size >= BoBoUdpNackCodec.MAX_ENTRIES_PER_PACKET) break
                        val start = if (b.interleavedFec && b.parityCount > 0) g else if (b.parityCount > 0) g * b.groupSize else 0
                        val step = if (b.interleavedFec && b.parityCount > 0) b.parityCount else 1
                        val end = if (b.interleavedFec && b.parityCount > 0) b.dataCount else if (b.parityCount > 0) minOf(b.dataCount, start + b.groupSize) else b.dataCount
                        var missingCount = 0
                        var firstMissing = -1
                        var lastMissing = -1
                        var idx = start
                        while (idx < end) {
                            if (b.dataShards[idx] == null) {
                                if (firstMissing < 0) firstMissing = idx
                                lastMissing = idx
                                missingCount++
                            }
                            idx += step
                        }
                        if (missingCount == 0) continue
                        val eligible = when {
                            missingCount >= 2 -> hasNewer || b.anyParitySeen || idleMs >= 8.0 || b.highestDataIdxSeen >= lastMissing + 3
                            b.parityCount == 0 -> hasNewer || idleMs >= 10.0 || b.highestDataIdxSeen >= firstMissing + 3
                            else -> hasNewer || idleMs >= 14.0
                        }
                        if (!eligible) continue
                        if (b.nackSentNs == null) {
                            b.nackSentNs = LongArray(b.dataCount)
                            b.nackRetries = ByteArray(b.dataCount)
                        }
                        val sentNs = b.nackSentNs!!
                        val retries = b.nackRetries!!
                        idx = start
                        while (idx < end && outNacks.size < BoBoUdpNackCodec.MAX_ENTRIES_PER_PACKET) {
                            if (b.dataShards[idx] == null && retries[idx] < 3) {
                                val prev = sentNs[idx]
                                val sinceMs = if (prev == 0L) Double.MAX_VALUE else (nowNs - prev) / 1_000_000.0
                                if (prev == 0L || sinceMs >= retryIntervalMs) {
                                    sentNs[idx] = nowNs
                                    retries[idx] = (retries[idx] + 1).toByte()
                                    outNacks.add(BoBoUdpNackRequest(b.frameId, idx))
                                    nackRequestsSent++
                                }
                            }
                            idx += step
                        }
                    }
                }
            }

            val nackGraceMs = maxOf(80.0, minOf(240.0, baseRtt * 2.2 + 35.0))
            if (hasDeliveredFrame) {
                val completeFuture = ordered.firstOrNull { it.isComplete && (it.frameId.toInt() - lastDeliveredFrameId.toInt()) > 1 }
                if (completeFuture != null) {
                    if (gapFirstSeenNs == 0L) gapFirstSeenNs = nowNs
                    val waitMs = (nowNs - gapFirstSeenNs) / 1_000_000.0
                    val ahead = completeFuture.frameId.toInt() - lastDeliveredFrameId.toInt()
                    if (waitMs >= nackGraceMs || ahead >= 6) {
                        buckets.clear()
                        gapFirstSeenNs = 0L
                        awaitingKeyframe = true
                        awaitingKeyframeSinceNs = nowNs
                        return shouldTriggerPliLocked(baseRtt, nowNs)
                    }
                }
            }

            val head = ordered.firstOrNull { !it.isComplete }
            if (head != null) {
                val idleMs = (nowNs - head.lastShardNs) / 1_000_000.0
                val limitMs = if (head.isKeyframe) maxOf(260.0, minOf(450.0, baseRtt * 2.6 + 80.0)) else maxOf(180.0, nackGraceMs + 40.0)
                if (idleMs >= limitMs) {
                    buckets.clear()
                    gapFirstSeenNs = 0L
                    awaitingKeyframe = true
                    awaitingKeyframeSinceNs = nowNs
                    return shouldTriggerPliLocked(baseRtt, nowNs)
                }
            }
            return false
        }
    }

    private fun tryFecRecoverGroupLocked(b: FrameBucket, g: Int) {
        val pArr = b.parityShards ?: return
        if (g !in pArr.indices) return
        val par = pArr[g] ?: return
        if (par.size < 2) return
        val start = if (b.interleavedFec) g else g * b.groupSize
        val step = if (b.interleavedFec) b.parityCount else 1
        val end = if (b.interleavedFec) b.dataCount else minOf(b.dataCount, start + b.groupSize)
        if (step <= 0) return

        var missingIdx = -1
        var missingCount = 0
        var i = start
        while (i < end) {
            if (b.dataShards[i] == null) {
                missingIdx = i
                missingCount++
                if (missingCount > 1) return
            }
            i += step
        }
        if (missingCount != 1 || missingIdx < 0) return

        var recLen = BoBoUdpAesGcmCrypto.readUInt16LE(par, 0)
        i = start
        while (i < end) {
            if (i != missingIdx) recLen = recLen xor b.dataShards[i]!!.size
            i += step
        }
        if (recLen <= 0 || recLen > BoBoUdpFec.MAX_SHARD_PAYLOAD_BYTES || recLen + 2 > par.size) return

        val recovered = ByteArray(recLen)
        System.arraycopy(par, 2, recovered, 0, recLen)
        i = start
        while (i < end) {
            if (i != missingIdx) {
                val other = b.dataShards[i]!!
                val n = minOf(recLen, other.size)
                for (k in 0 until n) {
                    recovered[k] = (recovered[k].toInt() xor other[k].toInt()).toByte()
                }
            }
            i += step
        }
        b.dataShards[missingIdx] = recovered
        b.receivedDataCount++
        fecRecoveredPackets++
    }

    private fun assembleBucketLocked(b: FrameBucket) {
        var total = 0
        for (i in 0 until b.dataCount) total += b.dataShards[i]!!.size
        val full = ByteArray(total)
        var pos = 0
        for (i in 0 until b.dataCount) {
            val s = b.dataShards[i]!!
            System.arraycopy(s, 0, full, pos, s.size)
            pos += s.size
        }
        b.assembledData = full
        b.isComplete = true
    }

    private fun deliverFromBucketLocked(first: FrameBucket, outReady: MutableList<BoBoReassembledVideoFrame>) {
        var cur: FrameBucket? = first
        while (cur != null && cur.isComplete) {
            val fid = cur.frameId
            outReady.add(
                BoBoReassembledVideoFrame(
                    fid, cur.timestampMs, cur.width, cur.height, cur.isKeyframe, cur.assembledData!!
                )
            )
            lastDeliveredFrameId = fid
            hasDeliveredFrame = true
            if (cur.isKeyframe) {
                awaitingKeyframe = false
                awaitingKeyframeSinceNs = 0L
            }
            buckets.remove(fid)
            val older = buckets.keys.filter { (it.toInt() - fid.toInt()) < 0 }
            for (k in older) buckets.remove(k)
            gapFirstSeenNs = 0L
            val nextId = (fid + 1L) and 0xFFFFFFFFL
            val nextBucket = buckets[nextId]
            cur = if (nextBucket != null && nextBucket.isComplete) nextBucket else null
        }
    }

    private fun pruneStaleLocked(newestFrameId: Long, nowNs: Long) {
        val toRemove = buckets.keys.filter { (newestFrameId.toInt() - it.toInt()) >= 8 }
        for (k in toRemove) buckets.remove(k)
        if (toRemove.isNotEmpty() && !awaitingKeyframe) {
            awaitingKeyframe = true
            awaitingKeyframeSinceNs = nowNs
        }
    }

    private fun shouldTriggerPliLocked(rttMs: Double, nowNs: Long): Boolean {
        val elapsedMs = if (lastPliNs == 0L) 10000.0 else (nowNs - lastPliNs) / 1_000_000.0
        val cooldown = maxOf(500.0, minOf(1200.0, rttMs * 4.0))
        if (lastPliNs == 0L || elapsedMs >= cooldown) {
            lastPliNs = nowNs
            return true
        }
        return false
    }
}

/**
 * Encrypted UDP+AES-128-GCM+FEC/NACK media client channel for Android.
 * Automatically falls back to TCP+TLS via onFallback() if UDP is silent for >3.5s.
 */
class BoBoUdpChannel(
    private val host: String,
    private val port: Int,
    sessionId: Long,
    keyBase64: String,
    nackEnabled: Boolean,
    private val onVideo: (BoBoVideoPayload, Long) -> Unit,
    private val onAudio: (BoBoAudioPayload) -> Unit,
    private val onRtt: (Double) -> Unit,
    private val onFallback: () -> Unit
) : AutoCloseable {
    private val crypto: BoBoUdpAesGcmCrypto
    private val assembler = BoBoUdpVideoAssembler(nackEnabled)
    private val running = AtomicBoolean(false)
    private val activated = AtomicBoolean(false)
    private val lastRxNs = AtomicLong(0L)
    private val sendLock = Any()

    @Volatile private var socket: DatagramSocket? = null
    @Volatile private var remoteAddr: InetSocketAddress? = null
    @Volatile private var latestRttMs: Double = 20.0
    @Volatile private var prevRttMs: Double = 0.0
    @Volatile private var latestJitterMs: Double = 0.0

    val isActive: Boolean get() = running.get() && activated.get()

    init {
        val keyBytes = Base64.getDecoder().decode(keyBase64)
        crypto = BoBoUdpAesGcmCrypto(sessionId, keyBytes, isHost = false)
        Arrays.fill(keyBytes, 0.toByte())
    }

    fun start() {
        if (!running.compareAndSet(false, true)) return
        Thread({
            try {
                val addr = InetSocketAddress(InetAddress.getByName(host), port)
                remoteAddr = addr
                val ds = DatagramSocket()
                ds.soTimeout = 100
                try { ds.receiveBufferSize = 2 * 1024 * 1024 } catch (_: Exception) {}
                try { ds.sendBufferSize = 131072 } catch (_: Exception) {}
                socket = ds

                Thread({ heartbeatLoop(ds, addr) }, "BoBo7-Udp-Hb").apply { isDaemon = true; start() }
                receiveLoop(ds, addr)
            } catch (_: Exception) {
            } finally {
                close()
            }
        }, "BoBo7-Udp-Rx").apply { isDaemon = true; start() }
    }

    private fun receiveLoop(ds: DatagramSocket, addr: InetSocketAddress) {
        val buf = ByteArray(2048)
        val packet = DatagramPacket(buf, buf.size)
        val ready = ArrayList<BoBoReassembledVideoFrame>(4)
        val nacks = ArrayList<BoBoUdpNackRequest>(16)
        var lastPollNs = 0L

        while (running.get()) {
            try {
                packet.length = buf.size
                ds.receive(packet)
                val opened = crypto.open(buf, packet.length) ?: continue
                val nowNs = System.nanoTime()
                lastRxNs.set(nowNs)

                when (opened.packetType) {
                    BoBoUdpPacketType.HELLO_ACK -> {
                        if (activated.compareAndSet(false, true)) {
                            assembler.reset(nowNs)
                        }
                    }
                    BoBoUdpPacketType.VIDEO_SHARD -> {
                        if (!activated.get()) continue
                        assembler.recordReceivedUdpSeq(opened.seq, nowNs)
                        ready.clear()
                        val needPli = assembler.processShard(opened.plaintext, latestRttMs, ready, nowNs)
                        for (frame in ready) {
                            onVideo(BoBoVideoPayload(frame.width, frame.height, frame.isKeyframe, frame.h264Data), frame.timestampMs)
                        }
                        var pollPli = false
                        if ((nowNs - lastPollNs) >= 8_000_000L) {
                            lastPollNs = nowNs
                            pollPli = assembler.pollRecovery(nowNs, latestRttMs, nacks)
                            if (nacks.isNotEmpty()) sendNacks(ds, addr, nacks)
                        }
                        if (needPli || pollPli) sendFeedback(ds, addr, requestPli = true)
                    }
                    BoBoUdpPacketType.AUDIO -> {
                        if (!activated.get()) continue
                        assembler.recordReceivedUdpSeq(opened.seq, nowNs)
                        BoBoProtocol.parseAudioPayload(opened.plaintext)?.let { onAudio(it) }
                    }
                    BoBoUdpPacketType.PONG -> {
                        if (opened.plaintext.size >= 8) {
                            val sentNs = BoBoUdpAesGcmCrypto.readInt64LE(opened.plaintext, 0)
                            val rtt = (nowNs - sentNs) / 1_000_000.0
                            if (rtt in 0.0..10_000.0) {
                                if (prevRttMs > 0.1) {
                                    latestJitterMs = latestJitterMs * 0.75 + kotlin.math.abs(rtt - prevRttMs) * 0.25
                                }
                                prevRttMs = rtt
                                latestRttMs = rtt
                                onRtt(rtt)
                            }
                        }
                    }
                }
            } catch (_: SocketTimeoutException) {
                if (activated.get()) {
                    val nowNs = System.nanoTime()
                    val pollPli = assembler.pollRecovery(nowNs, latestRttMs, nacks)
                    if (nacks.isNotEmpty()) sendNacks(ds, addr, nacks)
                    if (pollPli) sendFeedback(ds, addr, requestPli = true)
                }
            } catch (_: Exception) {
                if (!running.get()) break
            }
        }
    }

    private fun heartbeatLoop(ds: DatagramSocket, addr: InetSocketAddress) {
        val helloPayload = byteArrayOf(0x42, 0x4F, 0x36, 0x33)
        for (i in 0 until 15) {
            if (!running.get() || activated.get()) break
            sendRaw(ds, addr, BoBoUdpPacketType.HELLO, helloPayload)
            try { Thread.sleep(50L) } catch (_: InterruptedException) { return }
        }
        val startNs = System.nanoTime()
        while (running.get()) {
            try {
                val nowNs = System.nanoTime()
                if (activated.get()) {
                    val last = lastRxNs.get()
                    val silenceMs = if (last == 0L) 0.0 else (nowNs - last) / 1_000_000.0
                    if (silenceMs > 3500.0) {
                        activated.set(false)
                        running.set(false)
                        onFallback()
                        break
                    }
                    val pingBuf = ByteArray(8)
                    BoBoUdpAesGcmCrypto.writeInt64LE(pingBuf, 0, nowNs)
                    sendRaw(ds, addr, BoBoUdpPacketType.PING, pingBuf)
                    sendFeedback(ds, addr, requestPli = false)
                } else {
                    if ((nowNs - startNs) / 1_000_000.0 > 3500.0) {
                        running.set(false)
                        break
                    }
                    sendRaw(ds, addr, BoBoUdpPacketType.HELLO, helloPayload)
                }
                Thread.sleep(250L)
            } catch (_: InterruptedException) {
                break
            } catch (_: Exception) {
                if (!running.get()) break
            }
        }
    }

    private fun sendNacks(ds: DatagramSocket, addr: InetSocketAddress, nacks: List<BoBoUdpNackRequest>) {
        val payload = BoBoUdpNackCodec.encode(nacks) ?: return
        sendRaw(ds, addr, BoBoUdpPacketType.NACK, payload)
    }

    private fun sendFeedback(ds: DatagramSocket, addr: InetSocketAddress, requestPli: Boolean) {
        val fb = ByteArray(11)
        val lossX100 = (assembler.latestLossPct * 100.0).toInt().coerceIn(0, 10000)
        val rttX10 = (latestRttMs * 10.0).toInt().coerceIn(0, 65000)
        val jitterX10 = (latestJitterMs * 10.0).toInt().coerceIn(0, 65535)
        BoBoUdpAesGcmCrypto.writeUInt16LE(fb, 0, lossX100)
        BoBoUdpAesGcmCrypto.writeUInt16LE(fb, 2, rttX10)
        BoBoUdpAesGcmCrypto.writeUInt16LE(fb, 4, jitterX10)
        BoBoUdpAesGcmCrypto.writeUInt32LE(fb, 6, assembler.lastDeliveredId)
        fb[10] = if (requestPli) 1 else 0
        sendRaw(ds, addr, BoBoUdpPacketType.FEEDBACK, fb)
    }

    private fun sendRaw(ds: DatagramSocket, addr: InetSocketAddress, pktType: Int, payload: ByteArray) {
        synchronized(sendLock) {
            if (!running.get()) return
            val sealed = try {
                crypto.seal(pktType, payload)
            } catch (_: IllegalStateException) {
                activated.set(false)
                running.set(false)
                onFallback()
                return
            } ?: return
            try {
                ds.send(DatagramPacket(sealed, sealed.size, addr))
            } catch (_: Exception) {
            }
        }
    }

    override fun close() {
        running.set(false)
        activated.set(false)
        try { socket?.close() } catch (_: Exception) {}
        socket = null
        crypto.close()
    }
}
