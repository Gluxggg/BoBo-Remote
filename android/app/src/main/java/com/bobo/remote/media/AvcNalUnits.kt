package com.bobo.remote.media

import java.io.ByteArrayOutputStream

/** Reads Annex-B access units and normalizes length-prefixed MediaCodec output. */
internal object AvcNalUnits {
    private val startCode = byteArrayOf(0, 0, 0, 1)
    private const val MAX_NAL_UNITS = 1024

    data class Nal(val type: Int, val offset: Int, val end: Int)

    class ParameterSets {
        var sps: ByteArray? = null
            private set
        var pps: ByteArray? = null
            private set

        fun update(data: ByteArray, units: List<Nal>): Boolean {
            var changed = false
            for (unit in units) {
                if (unit.type != 7 && unit.type != 8) continue
                if (unit.end - unit.offset > 65_535) continue
                val value = startCode + data.copyOfRange(unit.offset, unit.end)
                if (unit.type == 7 && sps?.contentEquals(value) != true) {
                    sps = value
                    changed = true
                }
                if (unit.type == 8 && pps?.contentEquals(value) != true) {
                    pps = value
                    changed = true
                }
            }
            return changed
        }

        fun prependMissing(data: ByteArray, units: List<Nal>): ByteArray? {
            val sequence = sps ?: return null
            val picture = pps ?: return null
            if (units.any { it.type == 7 } && units.any { it.type == 8 }) return data
            // Put the sets in decoding order when a codec supplies only one inline.
            val output = ByteArrayOutputStream(sequence.size + picture.size + data.size)
            output.write(sequence)
            output.write(picture)
            var position = 0
            for (unit in units) {
                if (unit.type != 7 && unit.type != 8) continue
                var prefix = unit.offset - 3
                if (prefix > position && data[prefix - 1] == 0.toByte()) prefix--
                output.write(data, position, prefix - position)
                position = unit.end
            }
            output.write(data, position, data.size - position)
            return output.toByteArray()
        }
    }

    fun inspect(data: ByteArray): List<Nal> {
        val units = ArrayList<Nal>()
        var prefix = findStartCode(data, 0)
        while (prefix >= 0 && units.size < MAX_NAL_UNITS) {
            val payload = prefix + if (data[prefix + 2] == 0.toByte()) 4 else 3
            prefix = findStartCode(data, payload)
            val end = if (prefix >= 0) prefix else data.size
            if (payload < end) units.add(Nal(data[payload].toInt() and 0x1f, payload, end))
        }
        return units
    }

    fun toAnnexB(data: ByteArray, codecConfig: Boolean = false): ByteArray? {
        val start = findStartCode(data, 0)
        if (start >= 0 && (0 until start).all { data[it] == 0.toByte() }) {
            val header = start + if (data[start + 2] == 0.toByte()) 4 else 3
            if (header < data.size && isNalHeader(data[header])) return data
        }
        if (codecConfig && data.isNotEmpty()) {
            if ((data[0].toInt() and 0x1f) in 7..8) return startCode + data
            readConfigurationRecord(data)?.let { return it }
        }
        var offset = 0
        var count = 0
        val output = ByteArrayOutputStream(data.size)
        while (data.size - offset >= 5 && count < MAX_NAL_UNITS) {
            val size = ((data[offset].toInt() and 0xff) shl 24) or
                ((data[offset + 1].toInt() and 0xff) shl 16) or
                ((data[offset + 2].toInt() and 0xff) shl 8) or (data[offset + 3].toInt() and 0xff)
            offset += 4
            if (size <= 0 || size > data.size - offset || !isNalHeader(data[offset])) return null
            output.write(startCode)
            output.write(data, offset, size)
            offset += size
            count++
        }
        return if (offset == data.size && count > 0) output.toByteArray() else null
    }

    private fun readConfigurationRecord(data: ByteArray): ByteArray? {
        if (data.size < 7 || data[0] != 1.toByte() || (data[5].toInt() and 0xe0) != 0xe0) return null
        val output = ByteArrayOutputStream(data.size)
        var offset = 6
        var count = data[5].toInt() and 0x1f
        for (type in 7..8) {
            repeat(count) {
                if (data.size - offset < 3) return null
                val size = ((data[offset].toInt() and 0xff) shl 8) or (data[offset + 1].toInt() and 0xff)
                offset += 2
                if (size <= 0 || size > data.size - offset || (data[offset].toInt() and 0x1f) != type) return null
                output.write(startCode)
                output.write(data, offset, size)
                offset += size
            }
            if (type == 7) {
                if (offset >= data.size) return null
                count = data[offset++].toInt() and 0xff
            }
        }
        return output.toByteArray().takeIf { it.isNotEmpty() }
    }

    private fun isNalHeader(value: Byte): Boolean = value >= 0 && (value.toInt() and 0x1f) in 1..23

    private fun findStartCode(data: ByteArray, from: Int): Int {
        for (i in from until data.size - 2) {
            if (data[i] != 0.toByte() || data[i + 1] != 0.toByte()) continue
            if (data[i + 2] == 1.toByte() ||
                (data[i + 2] == 0.toByte() && i + 3 < data.size && data[i + 3] == 1.toByte())
            ) return i
        }
        return -1
    }
}
