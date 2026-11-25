package com.bobo.remote.media

import android.Manifest
import android.annotation.SuppressLint
import android.content.Context
import android.content.pm.PackageManager
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioPlaybackCaptureConfiguration
import android.media.AudioRecord
import android.media.projection.MediaProjection
import android.os.Build
import android.util.Log
import androidx.annotation.RequiresApi
import com.bobo.remote.protocol.ImaAdpcmCodec

/** Reads 10 ms stereo blocks from apps that permit Android playback capture. */
@RequiresApi(Build.VERSION_CODES.Q)
internal class PlaybackAudioCapture private constructor(private val record: AudioRecord) {
    private val samples = ShortArray(FRAMES * 2)
    private var pendingSamples = 0

    fun drain(publish: (sampleRate: Int, frames: Int, data: ByteArray) -> Unit): Boolean {
        return try {
            repeat(3) {
                val read = record.read(samples, pendingSamples, samples.size - pendingSamples, AudioRecord.READ_NON_BLOCKING)
                if (read < 0) return false
                if (read == 0) return true
                pendingSamples += read
                if (pendingSamples == samples.size) {
                    publish(SAMPLE_RATE, FRAMES, ImaAdpcmCodec.encodeStereo(samples, FRAMES))
                    pendingSamples = 0
                }
            }
            true
        } catch (error: RuntimeException) {
            Log.w(TAG, "Android playback capture ended", error)
            false
        }
    }

    fun release() {
        runCatching { record.stop() }
        runCatching { record.release() }
    }

    companion object {
        private const val TAG = "BoBoPlaybackCapture"
        private const val SAMPLE_RATE = 48_000
        private const val FRAMES = SAMPLE_RATE / 100

        @SuppressLint("MissingPermission")
        fun start(context: Context, projection: MediaProjection): PlaybackAudioCapture? {
            if (context.checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) return null
            val minimum = AudioRecord.getMinBufferSize(SAMPLE_RATE, AudioFormat.CHANNEL_IN_STEREO, AudioFormat.ENCODING_PCM_16BIT)
            if (minimum <= 0) return null
            var record: AudioRecord? = null
            return try {
                val config = AudioPlaybackCaptureConfiguration.Builder(projection)
                    .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
                    .addMatchingUsage(AudioAttributes.USAGE_GAME)
                    .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
                    .build()
                val recorder = AudioRecord.Builder()
                    .setAudioFormat(AudioFormat.Builder()
                        .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                        .setSampleRate(SAMPLE_RATE)
                        .setChannelMask(AudioFormat.CHANNEL_IN_STEREO)
                        .build())
                    .setBufferSizeInBytes(maxOf(minimum, FRAMES * 4 * 4))
                    .setAudioPlaybackCaptureConfig(config)
                    .build()
                record = recorder
                check(recorder.state == AudioRecord.STATE_INITIALIZED && recorder.channelCount == 2 && recorder.sampleRate == SAMPLE_RATE)
                recorder.startRecording()
                check(recorder.recordingState == AudioRecord.RECORDSTATE_RECORDING)
                PlaybackAudioCapture(recorder)
            } catch (error: RuntimeException) {
                runCatching { record?.release() }
                Log.w(TAG, "Stereo playback capture unavailable", error)
                null
            }
        }
    }
}
