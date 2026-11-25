package com.bobo.remote.protocol

/**
 * 标准立体声 IMA-ADPCM 编解码器（48kHz 2ch，与 BoBoCore.dll 及 golden-vectors.json 字节级一致）。
 * 格式：8 字节头 [predL:i16LE, idxL:u8, 0, predR:i16LE, idxR:u8, 0] + sampleFrames 字节（每字节 low4=L, high4=R）。
 */
object ImaAdpcmCodec {
    private val STEP_TABLE = intArrayOf(
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31,
        34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143,
        157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658,
        724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024,
        3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
        15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
    )

    private val INDEX_TABLE = intArrayOf(
        -1, -1, -1, -1, 2, 4, 6, 8,
        -1, -1, -1, -1, 2, 4, 6, 8
    )

    fun encodeStereo(pcmInterleaved: ShortArray, sampleFrames: Int = pcmInterleaved.size / 2): ByteArray {
        if (sampleFrames <= 0 || pcmInterleaved.size < sampleFrames * 2) return ByteArray(0)
        val out = ByteArray(8 + sampleFrames)
        var predL = pcmInterleaved[0].toInt()
        var idxL = 0
        var predR = pcmInterleaved[1].toInt()
        var idxR = 0

        BoBoProtocol.writeInt16LE(out, 0, predL.toShort())
        out[2] = idxL.toByte()
        out[3] = 0
        BoBoProtocol.writeInt16LE(out, 4, predR.toShort())
        out[6] = idxR.toByte()
        out[7] = 0

        for (i in 0 until sampleFrames) {
            val sL = pcmInterleaved[i * 2].toInt()
            val sR = pcmInterleaved[i * 2 + 1].toInt()

            var diffL = sL - predL
            var nibL = 0
            if (diffL < 0) {
                nibL = 8
                diffL = -diffL
            }
            val stepL = STEP_TABLE[idxL]
            var diffqL = stepL shr 3
            if (diffL >= stepL) { nibL = nibL or 4; diffL -= stepL; diffqL += stepL }
            if (diffL >= (stepL shr 1)) { nibL = nibL or 2; diffL -= (stepL shr 1); diffqL += (stepL shr 1) }
            if (diffL >= (stepL shr 2)) { nibL = nibL or 1; diffqL += (stepL shr 2) }
            predL = (if ((nibL and 8) != 0) predL - diffqL else predL + diffqL).coerceIn(-32768, 32767)
            idxL = (idxL + INDEX_TABLE[nibL]).coerceIn(0, 88)

            var diffR = sR - predR
            var nibR = 0
            if (diffR < 0) {
                nibR = 8
                diffR = -diffR
            }
            val stepR = STEP_TABLE[idxR]
            var diffqR = stepR shr 3
            if (diffR >= stepR) { nibR = nibR or 4; diffR -= stepR; diffqR += stepR }
            if (diffR >= (stepR shr 1)) { nibR = nibR or 2; diffR -= (stepR shr 1); diffqR += (stepR shr 1) }
            if (diffR >= (stepR shr 2)) { nibR = nibR or 1; diffqR += (stepR shr 2) }
            predR = (if ((nibR and 8) != 0) predR - diffqR else predR + diffqR).coerceIn(-32768, 32767)
            idxR = (idxR + INDEX_TABLE[nibR]).coerceIn(0, 88)

            out[8 + i] = ((nibL and 0x0F) or ((nibR and 0x0F) shl 4)).toByte()
        }
        return out
    }

    fun decodeStereo(adpcm: ByteArray, sampleFrames: Int = (adpcm.size - 8).coerceAtLeast(0)): ShortArray {
        if (adpcm.size < 8 || sampleFrames <= 0) return ShortArray(0)
        val count = minOf(sampleFrames, adpcm.size - 8)
        val out = ShortArray(count * 2)
        var predL = BoBoProtocol.readInt16LE(adpcm, 0).toInt()
        var idxL = (adpcm[2].toInt() and 0xFF).coerceIn(0, 88)
        var predR = BoBoProtocol.readInt16LE(adpcm, 4).toInt()
        var idxR = (adpcm[6].toInt() and 0xFF).coerceIn(0, 88)

        for (i in 0 until count) {
            val packed = adpcm[8 + i].toInt() and 0xFF
            val nibL = packed and 0x0F
            val nibR = (packed ushr 4) and 0x0F

            val stepL = STEP_TABLE[idxL]
            var diffqL = stepL shr 3
            if ((nibL and 4) != 0) diffqL += stepL
            if ((nibL and 2) != 0) diffqL += stepL shr 1
            if ((nibL and 1) != 0) diffqL += stepL shr 2
            predL = (if ((nibL and 8) != 0) predL - diffqL else predL + diffqL).coerceIn(-32768, 32767)
            idxL = (idxL + INDEX_TABLE[nibL]).coerceIn(0, 88)

            val stepR = STEP_TABLE[idxR]
            var diffqR = stepR shr 3
            if ((nibR and 4) != 0) diffqR += stepR
            if ((nibR and 2) != 0) diffqR += stepR shr 1
            if ((nibR and 1) != 0) diffqR += stepR shr 2
            predR = (if ((nibR and 8) != 0) predR - diffqR else predR + diffqR).coerceIn(-32768, 32767)
            idxR = (idxR + INDEX_TABLE[nibR]).coerceIn(0, 88)

            out[i * 2] = predL.toShort()
            out[i * 2 + 1] = predR.toShort()
        }
        return out
    }
}
