package com.bobo.remote.media

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import com.bobo.remote.protocol.ImaAdpcmCodec

/**
 * 48kHz 立体声低延迟 PCM 播放器（实时解码来自 Windows/macOS/Android 被控端的 IMA-ADPCM 帧）。
 */
class AudioPlayer {
    private val lock = Any()
    private var track: AudioTrack? = null
    private var configuredRate = 0
    @Volatile var muted: Boolean = false
    @Volatile var volume: Float = 1.0f
        set(value) {
            field = value.coerceIn(0f, 1f)
            synchronized(lock) {
                track?.setVolume(if (muted) 0f else field)
            }
        }

    fun playAdpcm(sampleRate: Int, channels: Int, sampleCount: Int, adpcmData: ByteArray) {
        if (muted || adpcmData.size < 8 || sampleCount <= 0) return
        val pcm = ImaAdpcmCodec.decodeStereo(adpcmData, sampleCount)
        if (pcm.isEmpty()) return
        synchronized(lock) {
            ensureTrackLocked(if (sampleRate > 0) sampleRate else 48000, if (channels == 1) 1 else 2)
            try {
                track?.write(pcm, 0, pcm.size, AudioTrack.WRITE_NON_BLOCKING)
            } catch (_: Exception) {
            }
        }
    }

    fun setMutedState(mute: Boolean) {
        muted = mute
        synchronized(lock) {
            track?.setVolume(if (mute) 0f else volume)
        }
    }

    fun release() {
        synchronized(lock) {
            val t = track
            track = null
            configuredRate = 0
            if (t != null) {
                try { t.stop() } catch (_: Exception) {}
                try { t.release() } catch (_: Exception) {}
            }
        }
    }

    private fun ensureTrackLocked(sampleRate: Int, channels: Int) {
        if (track != null && configuredRate == sampleRate) return
        releaseTrackOnlyLocked()
        val chMask = if (channels == 1) AudioFormat.CHANNEL_OUT_MONO else AudioFormat.CHANNEL_OUT_STEREO
        val minBuf = AudioTrack.getMinBufferSize(sampleRate, chMask, AudioFormat.ENCODING_PCM_16BIT)
            .coerceAtLeast(sampleRate / 10 * 4)
        val attrs = AudioAttributes.Builder()
            .setUsage(AudioAttributes.USAGE_MEDIA)
            .setContentType(AudioAttributes.CONTENT_TYPE_MOVIE)
            .build()
        val fmt = AudioFormat.Builder()
            .setSampleRate(sampleRate)
            .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
            .setChannelMask(chMask)
            .build()
        val built = AudioTrack.Builder()
            .setAudioAttributes(attrs)
            .setAudioFormat(fmt)
            .setTransferMode(AudioTrack.MODE_STREAM)
            .setBufferSizeInBytes(minBuf)
            .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
            .build()
        built.setVolume(if (muted) 0f else volume)
        built.play()
        track = built
        configuredRate = sampleRate
    }

    private fun releaseTrackOnlyLocked() {
        val t = track
        track = null
        if (t != null) {
            try { t.stop() } catch (_: Exception) {}
            try { t.release() } catch (_: Exception) {}
        }
    }
}
