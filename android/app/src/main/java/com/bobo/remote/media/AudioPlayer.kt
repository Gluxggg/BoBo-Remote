package com.bobo.remote.media

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.os.Handler
import android.os.HandlerThread
import android.util.Log
import com.bobo.remote.protocol.ImaAdpcmCodec

/** Bounded playback for the protocol's interleaved stereo IMA-ADPCM blocks. */
class AudioPlayer {
    private data class Packet(val rate: Int, val frames: Int, val data: ByteArray)

    private val worker = HandlerThread("BoBo6-Android-Audio").apply { start() }
    private val handler = Handler(worker.looper)
    private val lock = Any()
    private val queue = ArrayDeque<Packet>()
    private var posted = false
    private var closed = false
    @Volatile private var muted = false
    private var track: AudioTrack? = null
    private var configuredRate = 0

    fun playAdpcm(sampleRate: Int, channels: Int, sampleCount: Int, adpcmData: ByteArray) {
        if (channels != 2 || sampleRate !in 8_000..192_000 || sampleCount !in 1..sampleRate / 10 ||
            adpcmData.size != 8 + sampleCount
        ) return
        synchronized(lock) {
            if (closed || muted) return
            if (queue.size == MAX_PACKETS) queue.removeFirst()
            queue.addLast(Packet(sampleRate, sampleCount, adpcmData))
            if (!posted) {
                posted = true
                handler.post(::playNext)
            }
        }
    }

    fun setMutedState(mute: Boolean) {
        synchronized(lock) {
            if (closed || muted == mute) return
            muted = mute
            queue.clear()
            if (mute) handler.post(::releaseTrack)
        }
    }

    fun release() {
        synchronized(lock) {
            if (closed) return
            closed = true
            queue.clear()
            handler.post(::releaseTrack)
            worker.quitSafely()
        }
    }

    private fun playNext() {
        val packet = synchronized(lock) {
            queue.removeFirstOrNull().also { if (it == null) posted = false }
        } ?: return
        if (!muted) {
            try {
                val player = ensureTrack(packet.rate)
                val pcm = ImaAdpcmCodec.decodeStereo(packet.data, packet.frames)
                val written = player.write(pcm, 0, pcm.size, AudioTrack.WRITE_NON_BLOCKING)
                if (written < 0) releaseTrack()
            } catch (error: RuntimeException) {
                Log.w(TAG, "Audio playback failed", error)
                releaseTrack()
            }
        }
        synchronized(lock) {
            if (queue.isEmpty()) posted = false else handler.post(::playNext)
        }
    }

    private fun ensureTrack(sampleRate: Int): AudioTrack {
        track?.takeIf { configuredRate == sampleRate }?.let { return it }
        releaseTrack()
        val minBytes = AudioTrack.getMinBufferSize(
            sampleRate, AudioFormat.CHANNEL_OUT_STEREO, AudioFormat.ENCODING_PCM_16BIT
        )
        check(minBytes > 0) { "Unsupported stereo playback rate $sampleRate" }
        val player = AudioTrack.Builder()
            .setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA).build())
            .setAudioFormat(AudioFormat.Builder()
                .setSampleRate(sampleRate)
                .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                .setChannelMask(AudioFormat.CHANNEL_OUT_STEREO)
                .build())
            .setTransferMode(AudioTrack.MODE_STREAM)
            .setBufferSizeInBytes(maxOf(minBytes, sampleRate * 4 / 25))
            .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
            .build()
        track = player
        configuredRate = sampleRate
        check(player.state == AudioTrack.STATE_INITIALIZED)
        player.play()
        return player
    }

    private fun releaseTrack() {
        val player = track ?: return
        track = null
        configuredRate = 0
        runCatching { player.pause() }
        runCatching { player.flush() }
        runCatching { player.release() }
    }

    private companion object {
        const val TAG = "BoBoAudioPlayer"
        const val MAX_PACKETS = 4
    }
}
