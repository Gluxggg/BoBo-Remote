package com.bobo.remote.media

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class AvcNalUnitsTest {
    private val sps = byteArrayOf(0, 0, 0, 1, 0x67, 0x11)
    private val pps = byteArrayOf(0, 0, 0, 1, 0x68, 0x22)
    private val idr = byteArrayOf(0, 0, 0, 1, 0x65, 0x33)

    @Test
    fun cachesSeparateParameterSetsAndAvoidsDuplicatingInlineConfiguration() {
        val sets = AvcNalUnits.ParameterSets()
        assertTrue(sets.update(sps, AvcNalUnits.inspect(sps)))
        assertNull(sets.prependMissing(idr, AvcNalUnits.inspect(idr)))
        assertTrue(sets.update(pps, AvcNalUnits.inspect(pps)))
        assertFalse(sets.update(sps, AvcNalUnits.inspect(sps)))
        val expected = sps + pps + idr
        assertArrayEquals(expected, sets.prependMissing(idr, AvcNalUnits.inspect(idr)))
        assertArrayEquals(expected, sets.prependMissing(sps + idr, AvcNalUnits.inspect(sps + idr)))
        assertArrayEquals(expected, sets.prependMissing(expected, AvcNalUnits.inspect(expected)))
    }

    @Test
    fun normalizesLengthPrefixedAccessUnitWithAStartCodeLikeLength() {
        val nal = ByteArray(256) { 0x11 }
        nal[0] = 0x65
        val avcc = byteArrayOf(0, 0, 1, 0) + nal
        val annexB = requireNotNull(AvcNalUnits.toAnnexB(avcc))
        assertArrayEquals(byteArrayOf(0, 0, 0, 1) + nal, annexB)
        assertEquals(listOf(5), AvcNalUnits.inspect(annexB).map { it.type })
        assertNull(AvcNalUnits.toAnnexB(avcc.copyOf(avcc.size - 1)))
    }
}
