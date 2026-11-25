package com.bobo.remote.media

import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.os.SystemClock
import android.util.Log
import android.view.Surface
import java.nio.ByteBuffer

/** Decodes H.264 on a dedicated worker with a bounded, loss-aware input queue. */
class H264HardwareDecoder(
    private val onNeedKeyframe: () -> Unit,
    private val onFrameRendered: (Int, Int) -> Unit
) {
    private data class Frame(val data: ByteArray, val width: Int, val height: Int, val timestampMs: Long)

    private val thread = HandlerThread("BoBo6-Android-Dec").apply { start() }
    private val handler = Handler(thread.looper)
    private val queueLock = Any()
    private val queue = ArrayDeque<Frame>()
    private var workPosted = false
    private var queuedBytes = 0
    private var queueOverflowed = false
    private var closed = false

    private var codec: MediaCodec? = null
    private var surface: Surface? = null
    private var width = 0
    private var height = 0
    private var config = AvcNalUnits.ParameterSets()
    private var pendingIdr: Frame? = null
    private var lastRenderedSize: Pair<Int, Int>? = null
    private var awaitingIdr = true
    private var lastKeyRequestMs = -KEYFRAME_REQUEST_INTERVAL_MS
    private val bufferInfo = MediaCodec.BufferInfo()
    private val pollOutput = Runnable { pollOutput() }
    private val askForKeyframe = Runnable {
        if (surface?.isValid == true) {
            lastKeyRequestMs = SystemClock.elapsedRealtime()
            onNeedKeyframe()
        }
    }

    fun attachSurface(newSurface: Surface?) {
        synchronized(queueLock) {
            if (closed) return
            handler.post {
                if (surface === newSurface) return@post
                releaseCodec()
                handler.removeCallbacks(askForKeyframe)
                surface = newSurface
                lastRenderedSize = null
                if (newSurface?.isValid == true) {
                    pendingIdr?.let { process(it) }
                    pendingIdr = null
                    requestKeyframe()
                }
            }
        }
    }

    fun decode(data: ByteArray, width: Int, height: Int, timestampMs: Long) {
        if (data.isEmpty() || data.size > MAX_QUEUED_BYTES || width !in 1..8192 || height !in 1..8192) return
        synchronized(queueLock) {
            if (closed) return
            if (queue.size == MAX_QUEUED_FRAMES || queuedBytes + data.size > MAX_QUEUED_BYTES) {
                queue.clear()
                queuedBytes = 0
                queueOverflowed = true
            }
            queue.addLast(Frame(data, width, height, timestampMs))
            queuedBytes += data.size
            if (!workPosted) {
                workPosted = true
                handler.post(::processNext)
            }
        }
    }

    fun release() {
        synchronized(queueLock) {
            if (closed) return
            closed = true
            queue.clear()
            queuedBytes = 0
            handler.post {
                releaseCodec()
                handler.removeCallbacks(askForKeyframe)
                surface = null
                pendingIdr = null
            }
            thread.quitSafely()
        }
    }

    private fun processNext() {
        val (frame, overflowed) = synchronized(queueLock) {
            val next = queue.removeFirstOrNull()
            val lost = queueOverflowed
            queueOverflowed = false
            if (next == null) workPosted = false else queuedBytes -= next.data.size
            next to lost
        }
        if (overflowed) {
            releaseCodec()
            pendingIdr = null
        }
        if (frame == null) return
        process(frame)
        synchronized(queueLock) {
            if (queue.isEmpty()) workPosted = false else handler.post(::processNext)
        }
    }

    private fun process(frame: Frame) {
        val units = AvcNalUnits.inspect(frame.data)
        val idr = units.any { it.type == 5 }
        if (frame.width != width || frame.height != height) {
            releaseCodec()
            pendingIdr = null
            config = AvcNalUnits.ParameterSets()
            width = frame.width
            height = frame.height
        }
        if (config.update(frame.data, units)) {
            if (!idr) pendingIdr = null
            releaseCodec()
        }

        val outputSurface = surface
        if (outputSurface?.isValid != true) {
            if (idr) pendingIdr = frame
            return
        }
        if (awaitingIdr && !idr) {
            // SPS and PPS may arrive in separate packets ahead of the first IDR.
            if (units.any { it.type in 1..4 } || units.isEmpty()) requestKeyframe()
            return
        }
        if (units.none { it.type in 1..5 }) return

        try {
            val decoder = codec ?: configure(outputSurface, frame.data.size) ?: run {
                requestKeyframe()
                return
            }
            drainOutput(decoder)
            val slot = decoder.dequeueInputBuffer(2_000L)
            if (slot < 0) {
                recover()
                return
            }
            val input = decoder.getInputBuffer(slot)
            if (input == null || input.capacity() < frame.data.size) {
                recover()
                return
            }
            input.clear()
            input.put(frame.data)
            decoder.queueInputBuffer(slot, 0, frame.data.size, frame.timestampMs * 1000L, 0)
            awaitingIdr = false
            if (idr) handler.removeCallbacks(askForKeyframe)
            drainOutput(decoder)
            handler.removeCallbacks(pollOutput)
            handler.postDelayed(pollOutput, 4L)
        } catch (error: RuntimeException) {
            Log.w(TAG, "Video decoder failed", error)
            recover()
        } catch (error: java.io.IOException) {
            Log.w(TAG, "Video decoder unavailable", error)
            recover()
        }
    }

    private fun configure(output: Surface, firstFrameSize: Int): MediaCodec? {
        val sps = config.sps ?: return null
        val pps = config.pps ?: return null
        val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height).apply {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
            setInteger(MediaFormat.KEY_PRIORITY, 0)
            setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, maxOf(firstFrameSize, (width * height).coerceIn(1_048_576, 8_388_608)))
            setByteBuffer("csd-0", ByteBuffer.wrap(sps))
            setByteBuffer("csd-1", ByteBuffer.wrap(pps))
        }
        val created = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
        try {
            created.configure(format, output, null, 0)
            created.start()
        } catch (error: RuntimeException) {
            runCatching { created.release() }
            throw error
        }
        codec = created
        return created
    }

    private fun pollOutput() {
        val decoder = codec ?: return
        try {
            drainOutput(decoder)
            handler.postDelayed(pollOutput, 8L)
        } catch (error: RuntimeException) {
            Log.w(TAG, "Video output failed", error)
            recover()
        }
    }

    private fun drainOutput(decoder: MediaCodec) {
        repeat(MAX_QUEUED_FRAMES) {
            val index = decoder.dequeueOutputBuffer(bufferInfo, 0L)
            if (index == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) return@repeat
            if (index < 0) return
            val render = bufferInfo.size > 0
            decoder.releaseOutputBuffer(index, render)
            if (render && lastRenderedSize != (width to height)) {
                lastRenderedSize = width to height
                onFrameRendered(width, height)
            }
        }
    }

    private fun recover() {
        releaseCodec()
        requestKeyframe()
    }

    private fun requestKeyframe() {
        val delay = (KEYFRAME_REQUEST_INTERVAL_MS - (SystemClock.elapsedRealtime() - lastKeyRequestMs)).coerceAtLeast(0L)
        handler.removeCallbacks(askForKeyframe)
        handler.postDelayed(askForKeyframe, delay)
    }

    private fun releaseCodec() {
        handler.removeCallbacks(pollOutput)
        awaitingIdr = true
        val old = codec ?: return
        codec = null
        runCatching { old.stop() }
        runCatching { old.release() }
    }

    private companion object {
        const val TAG = "BoBoVideoDecoder"
        const val MAX_QUEUED_FRAMES = 5
        const val MAX_QUEUED_BYTES = 16 * 1024 * 1024
        const val KEYFRAME_REQUEST_INTERVAL_MS = 500L
    }
}
