package com.bobo.remote.media

import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Build
import android.view.Surface
import java.nio.ByteBuffer

/**
 * Android 硬件 H.264 Annex-B 低延迟解码器（直接输出到 SurfaceView，零拷贝 GPU 渲染）。
 * 支持动态分辨率切换（例如超级屏切换或手机竖横屏旋转）与首帧自动等待 IDR 关键帧。
 */
class H264HardwareDecoder {
    private val lock = Any()
    private var codec: MediaCodec? = null
    private var surface: Surface? = null
    private var currentWidth = 0
    private var currentHeight = 0
    private var receivedKeyframe = false
    private val bufferInfo = MediaCodec.BufferInfo()

    @Volatile var decodedFrames: Long = 0L
        private set

    fun attachSurface(surface: Surface?) {
        synchronized(lock) {
            if (this.surface === surface) return
            releaseCodecLocked()
            this.surface = surface
            this.receivedKeyframe = false
        }
    }

    fun decode(h264AnnexB: ByteArray, width: Int, height: Int, isKeyframe: Boolean, timestampMs: Long) {
        if (h264AnnexB.isEmpty() || width <= 0 || height <= 0) return
        synchronized(lock) {
            val surf = surface ?: return
            if (!surf.isValid) return

            if (codec == null || width != currentWidth || height != currentHeight) {
                if (!isKeyframe && !containsSps(h264AnnexB)) {
                    return // Wait for IDR + SPS/PPS before configuring decoder
                }
                configureLocked(surf, width, height, h264AnnexB)
            }
            if (!receivedKeyframe) {
                if (!isKeyframe && !containsIdr(h264AnnexB)) return
                receivedKeyframe = true
            }

            val mc = codec ?: return
            try {
                val inIdx = mc.dequeueInputBuffer(8000L)
                if (inIdx >= 0) {
                    val inBuf: ByteBuffer? = mc.getInputBuffer(inIdx)
                    if (inBuf != null) {
                        inBuf.clear()
                        if (h264AnnexB.size <= inBuf.capacity()) {
                            inBuf.put(h264AnnexB)
                            val flags = if (isKeyframe) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0
                            mc.queueInputBuffer(inIdx, 0, h264AnnexB.size, timestampMs * 1000L, flags)
                        } else {
                            mc.queueInputBuffer(inIdx, 0, 0, 0L, 0)
                        }
                    }
                }

                var outIdx = mc.dequeueOutputBuffer(bufferInfo, 0L)
                while (outIdx >= 0) {
                    mc.releaseOutputBuffer(outIdx, true)
                    decodedFrames++
                    outIdx = mc.dequeueOutputBuffer(bufferInfo, 0L)
                }
            } catch (_: Exception) {
                releaseCodecLocked()
                receivedKeyframe = false
            }
        }
    }

    fun release() {
        synchronized(lock) {
            releaseCodecLocked()
            surface = null
            receivedKeyframe = false
        }
    }

    private fun configureLocked(surf: Surface, width: Int, height: Int, firstFrame: ByteArray) {
        releaseCodecLocked()
        try {
            val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
            }
            format.setInteger(MediaFormat.KEY_PRIORITY, 0)
            extractSpsPps(firstFrame)?.let { (sps, pps) ->
                format.setByteBuffer("csd-0", ByteBuffer.wrap(sps))
                format.setByteBuffer("csd-1", ByteBuffer.wrap(pps))
            }
            val mc = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
            mc.configure(format, surf, null, 0)
            mc.start()
            codec = mc
            currentWidth = width
            currentHeight = height
            receivedKeyframe = false
        } catch (_: Exception) {
            releaseCodecLocked()
        }
    }

    private fun releaseCodecLocked() {
        val mc = codec
        codec = null
        currentWidth = 0
        currentHeight = 0
        if (mc != null) {
            try { mc.stop() } catch (_: Exception) {}
            try { mc.release() } catch (_: Exception) {}
        }
    }

    private fun containsSps(data: ByteArray): Boolean = extractNalUnits(data).any { (it.nalType) == 7 }
    private fun containsIdr(data: ByteArray): Boolean = extractNalUnits(data).any { (it.nalType) == 5 }

    private fun extractSpsPps(data: ByteArray): Pair<ByteArray, ByteArray>? {
        val units = extractNalUnits(data)
        val sps = units.firstOrNull { it.nalType == 7 }?.withStartCode ?: return null
        val pps = units.firstOrNull { it.nalType == 8 }?.withStartCode ?: return null
        return Pair(sps, pps)
    }

    private data class NalUnit(val nalType: Int, val withStartCode: ByteArray)

    private fun extractNalUnits(data: ByteArray): List<NalUnit> {
        val starts = ArrayList<Int>()
        var i = 0
        while (i + 3 < data.size) {
            if (data[i].toInt() == 0 && data[i + 1].toInt() == 0) {
                if (data[i + 2].toInt() == 1) {
                    starts.add(i)
                    i += 3
                    continue
                } else if (i + 3 < data.size && data[i + 2].toInt() == 0 && data[i + 3].toInt() == 1) {
                    starts.add(i)
                    i += 4
                    continue
                }
            }
            i++
        }
        if (starts.isEmpty()) return emptyList()
        val result = ArrayList<NalUnit>(starts.size)
        for (idx in starts.indices) {
            val start = starts[idx]
            val end = if (idx + 1 < starts.size) starts[idx + 1] else data.size
            val hdrOffset = if (start + 3 < end && data[start + 2].toInt() == 0) start + 4 else start + 3
            if (hdrOffset < end) {
                val nalType = data[hdrOffset].toInt() and 0x1F
                result.add(NalUnit(nalType, data.copyOfRange(start, end)))
            }
        }
        return result
    }
}
