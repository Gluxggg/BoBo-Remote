package com.bobo.remote

import com.bobo.remote.network.BoBoHostServer
import com.bobo.remote.network.BoBoStreamClient
import com.bobo.remote.protocol.BoBoIdentityStore
import com.bobo.remote.protocol.BoBoPacketType
import com.bobo.remote.protocol.BoBoProtocol
import com.bobo.remote.protocol.ImaAdpcmCodec
import org.json.JSONObject
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.File
import java.nio.file.Files

/**
 * 强校验 Android 端与 protocol/golden-vectors.json 字节级一致，并运行真实 TLS 1.2 端到端回环测试。
 */
class BoBoProtocolGoldenTest {

    @Test
    fun verifyGoldenVectorsByteExact() {
        // Load authoritative protocol/golden-vectors.json when present in repo tree
        val candidatePaths = listOf(
            File("../../protocol/golden-vectors.json"),
            File("../protocol/golden-vectors.json"),
            File("protocol/golden-vectors.json")
        )
        val goldenFile = candidatePaths.firstOrNull { it.exists() }
        assertTrue("protocol/golden-vectors.json must exist in repo", goldenFile != null)
        val root = JSONObject(goldenFile!!.readText(Charsets.UTF_8))

        // 1. Device code derivation
        val dc = root.getJSONObject("deviceCodeDerivation")
        assertEquals(
            dc.getString("expectedDeviceCode"),
            BoBoIdentityStore.deriveNineDigitCode(dc.getString("thumbprintHex"))
        )

        // 2. PIN HMAC proof
        val pp = root.getJSONObject("pinProof")
        assertEquals(
            pp.getString("expectedProofHex"),
            BoBoIdentityStore.computePinProof(
                pp.getString("pin"),
                pp.getString("nonceHex"),
                pp.getString("clientThumbprint"),
                pp.getString("hostThumbprint")
            )
        )

        // 3. Video H.264 payload + 9B frame
        val vf = root.getJSONObject("videoFrame")
        val vPayload = BoBoProtocol.buildVideoPayload(
            vf.getInt("width"),
            vf.getInt("height"),
            vf.getBoolean("isKeyframe"),
            BoBoProtocol.hexToBytes(vf.getString("h264Hex"))
        )
        assertEquals(vf.getString("payloadHex"), BoBoProtocol.bytesToHex(vPayload))
        val vFrame = BoBoProtocol.encodeFrame(BoBoPacketType.VIDEO_H264, vf.getLong("timestampMs"), vPayload)
        assertEquals(vf.getString("frameHex"), BoBoProtocol.bytesToHex(vFrame))

        // 4. Audio IMA-ADPCM encode + decode + 9B frame
        val af = root.getJSONObject("audioAdpcm")
        val pcmArr = af.getJSONArray("pcmInterleaved")
        val pcmIn = ShortArray(pcmArr.length()) { pcmArr.getInt(it).toShort() }
        val encodedAdpcm = ImaAdpcmCodec.encodeStereo(pcmIn, af.getInt("sampleCount"))
        assertEquals(af.getString("adpcmPacketHex"), BoBoProtocol.bytesToHex(encodedAdpcm))

        val decodedPcm = ImaAdpcmCodec.decodeStereo(encodedAdpcm, af.getInt("sampleCount"))
        val expDecArr = af.getJSONArray("expectedDecodedPcm")
        val expDec = ShortArray(expDecArr.length()) { expDecArr.getInt(it).toShort() }
        assertArrayEquals(expDec, decodedPcm)

        val aPayload = BoBoProtocol.buildAudioPayload(
            af.getInt("sampleRate"),
            af.getInt("channels"),
            af.getInt("sampleCount"),
            encodedAdpcm
        )
        assertEquals(af.getString("payloadHex"), BoBoProtocol.bytesToHex(aPayload))
        val aFrame = BoBoProtocol.encodeFrame(BoBoPacketType.AUDIO_ADPCM, 0L, aPayload)
        assertEquals(af.getString("frameHex"), BoBoProtocol.bytesToHex(aFrame))

        // 5. All 8 InputEvent packets (including V6.2 touch kind=7 and mobile nav kind=8)
        val inp = root.getJSONObject("inputPackets")
        assertEquals(
            inp.getJSONObject("mouseAbsCenter").getString("payloadHex"),
            BoBoProtocol.bytesToHex(BoBoProtocol.buildInputPayload(BoBoProtocol.buildMouseAbs(32767, 32768)))
        )
        assertEquals(
            inp.getJSONObject("mouseRel").getString("payloadHex"),
            BoBoProtocol.bytesToHex(BoBoProtocol.buildInputPayload(BoBoProtocol.buildMouseRel(-18, 42)))
        )
        assertEquals(
            inp.getJSONObject("mouseBtnRightDown").getString("payloadHex"),
            BoBoProtocol.bytesToHex(BoBoProtocol.buildInputPayload(BoBoProtocol.buildMouseButton(3, true)))
        )
        assertEquals(
            inp.getJSONObject("mouseWheelDown").getString("payloadHex"),
            BoBoProtocol.bytesToHex(BoBoProtocol.buildInputPayload(BoBoProtocol.buildMouseWheel(-120, false)))
        )
        assertEquals(
            inp.getJSONObject("keyEnterDown").getString("payloadHex"),
            BoBoProtocol.bytesToHex(BoBoProtocol.buildInputPayload(BoBoProtocol.buildKey(13, true, false)))
        )
        assertEquals(
            inp.getJSONObject("releaseAll").getString("payloadHex"),
            BoBoProtocol.bytesToHex(BoBoProtocol.buildInputPayload(BoBoProtocol.buildReleaseAll()))
        )
        assertEquals(
            inp.getJSONObject("touchPointer1Down").getString("payloadHex"),
            BoBoProtocol.bytesToHex(BoBoProtocol.buildInputPayload(BoBoProtocol.buildTouch(1, 0, 16384, 32768)))
        )
        assertEquals(
            inp.getJSONObject("mobileNavBack").getString("payloadHex"),
            BoBoProtocol.bytesToHex(BoBoProtocol.buildInputPayload(BoBoProtocol.buildMobileNav(BoBoProtocol.MOBILE_NAV_BACK)))
        )

        // 6. FileChunk payload + 9B frame + stream read round-trip
        val fc = root.getJSONObject("fileChunk")
        val fPayload = BoBoProtocol.buildFileChunkPayload(
            fc.getString("transferId"),
            fc.getLong("offset"),
            BoBoProtocol.hexToBytes(fc.getString("chunkHex"))
        )
        assertEquals(fc.getString("payloadHex"), BoBoProtocol.bytesToHex(fPayload))
        val fFrame = BoBoProtocol.encodeFrame(BoBoPacketType.FILE_CHUNK, 0L, fPayload)
        assertEquals(fc.getString("frameHex"), BoBoProtocol.bytesToHex(fFrame))

        val parsedBack = BoBoProtocol.readFrame(ByteArrayInputStream(fFrame))!!
        assertEquals(BoBoPacketType.FILE_CHUNK, parsedBack.type)
        val parsedChunk = BoBoProtocol.parseFileChunkPayload(parsedBack.payload)!!
        assertEquals(fc.getString("transferId"), parsedChunk.transferId)
        assertEquals(fc.getLong("offset"), parsedChunk.offset)
        assertEquals(fc.getString("chunkHex"), BoBoProtocol.bytesToHex(parsedChunk.data))
    }

    @Test
    fun verifyTls12SelfSignedCertPairingAndFileTransferLoopback() {
        val rootDir = Files.createTempDirectory("bobo-android-test").toFile()
        try {
            val hostId = BoBoIdentityStore(File(rootDir, "host"), "TestAndroidHost")
            val clientId = BoBoIdentityStore(File(rootDir, "client"), "TestAndroidClient")
            val dlDir = File(rootDir, "downloads")

            val server = BoBoHostServer(hostId, dlDir, requestedPort = 0)
            val startRes = server.start()
            assertTrue("TLS host server should start: ${startRes.exceptionOrNull()}", startRes.isSuccess)
            val port = server.boundPort
            try {
                // 1. Probe before pairing -> reachable, android platform, unauthorized
                val probe1 = BoBoStreamClient.probeHost("127.0.0.1", port, clientId)
                assertTrue(probe1.reachable && probe1.isBoBoHost && probe1.allowIncoming)
                assertFalse(probe1.authorized)
                assertEquals(BoBoProtocol.PLATFORM_ANDROID, probe1.platform)
                assertEquals(BoBoProtocol.CONTROL_MODE_FULL, probe1.hostControlMode)

                // 2. Pair with 4-digit PIN
                val pairSess = server.beginPairing(120)
                val wrongPair = BoBoStreamClient.pairWithPin("127.0.0.1", port, "0000", clientId)
                if (pairSess.pin != "0000") assertFalse(wrongPair.first)

                val okPair = BoBoStreamClient.pairWithPin("127.0.0.1", port, pairSess.pin, clientId)
                assertTrue("Pairing with valid PIN must succeed: ${okPair.third}", okPair.first)

                // 3. Probe after pairing -> authorized
                val probe2 = BoBoStreamClient.probeHost("127.0.0.1", port, clientId)
                assertTrue(probe2.authorized)

                // 4. TLS SHA-256 file transfer
                val srcFile = File(rootDir, "hello-bobo.txt")
                srcFile.writeText("BoBoRemote V6.2 Android TLS SHA-256 loopback!", Charsets.UTF_8)
                val (fileOk, savedPath) = BoBoStreamClient.sendFile("127.0.0.1", port, srcFile, clientId)
                assertTrue("File transfer must succeed: $savedPath", fileOk)
                assertEquals(srcFile.readText(), File(savedPath).readText())
            } finally {
                server.stop()
            }
        } finally {
            rootDir.deleteRecursively()
        }
    }
}
