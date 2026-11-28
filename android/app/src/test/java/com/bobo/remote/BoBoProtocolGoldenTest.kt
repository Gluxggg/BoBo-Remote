package com.bobo.remote

import com.bobo.remote.network.BoBoHostServer
import com.bobo.remote.network.BoBoReassembledVideoFrame
import com.bobo.remote.network.BoBoStreamClient
import com.bobo.remote.network.BoBoUdpAesGcmCrypto
import com.bobo.remote.network.BoBoUdpFec
import com.bobo.remote.network.BoBoUdpNackCodec
import com.bobo.remote.network.BoBoUdpNackRequest
import com.bobo.remote.network.BoBoUdpPacketType
import com.bobo.remote.network.BoBoUdpVideoAssembler
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

        // 7. v7.3 Standard AEAD AES-128-GCM UDP golden vector + tamper/replay/nonce-cap/FEC/NACK tests
        val gcm = root.getJSONObject("udpAesGcm")
        val masterKey = BoBoProtocol.hexToBytes(gcm.getString("masterKey32Hex"))
        val sid = gcm.getLong("sessionId")
        val seq = gcm.getLong("seq")
        val pktType = gcm.getInt("packetType")
        val isH2c = gcm.getBoolean("isHostToClient")
        val derivedKey = BoBoUdpAesGcmCrypto.deriveDirectionKey(
            masterKey,
            sid,
            if (isH2c) "BoBo7.3-UDP-GCM-H2C" else "BoBo7.3-UDP-GCM-C2H"
        )
        assertEquals(gcm.getString("expectedKeyHex"), BoBoProtocol.bytesToHex(derivedKey))

        val hostGcm = BoBoUdpAesGcmCrypto(sid, masterKey, isHost = isH2c)
        val clientGcm = BoBoUdpAesGcmCrypto(sid, masterKey, isHost = !isH2c)
        try {
            hostGcm.forceSendSeqForTest(seq - 1L)
            val plain = BoBoProtocol.hexToBytes(gcm.getString("plaintextHex"))
            val sealed = hostGcm.seal(pktType, plain)!!
            assertEquals(gcm.getString("expectedDatagramHex"), BoBoProtocol.bytesToHex(sealed))

            // Tampered tag/header MUST fail without mutating replay window
            val tampered = sealed.copyOf()
            tampered[tampered.size - 1] = (tampered[tampered.size - 1].toInt() xor 0xFF).toByte()
            assertTrue(clientGcm.open(tampered) == null)

            // Original packet still accepted after forged packet attempt, then replay rejected
            val opened = clientGcm.open(sealed)!!
            assertEquals(pktType, opened.packetType)
            assertEquals(seq, opened.seq)
            assertArrayEquals(plain, opened.plaintext)
            assertTrue(clientGcm.open(sealed) == null)

            // Empty payload AEAD round-trip
            val emptySealed = hostGcm.seal(BoBoUdpPacketType.PING, ByteArray(0))!!
            val emptyOpened = clientGcm.open(emptySealed)!!
            assertEquals(0, emptyOpened.plaintext.size)

            // Nonce exhaustion guard (0xFFFFFF00 fail-closed)
            hostGcm.forceSendSeqForTest(BoBoUdpAesGcmCrypto.MAX_SAFE_SEQUENCE)
            var exhaustedThrown = false
            try {
                hostGcm.seal(BoBoUdpPacketType.PING, ByteArray(0))
            } catch (_: IllegalStateException) {
                exhaustedThrown = true
            }
            assertTrue("Nonce exhaustion must fail closed", exhaustedThrown)
        } finally {
            hostGcm.close()
            clientGcm.close()
        }

        // Interleaved XOR FEC burst-loss recovery (drop 2 consecutive shards across interleaved groups!)
        val fakeIdr = ByteArray(6800) { i -> ((i * 41 + 7) and 0xFF).toByte() }
        val shards = BoBoUdpFec.buildVideoShards(1L, 500L, 1920, 1080, true, fakeIdr, groupSizeIn = 2, interleavedFec = true)
        val assembler = BoBoUdpVideoAssembler(nackEnabled = true)
        val ready = ArrayList<BoBoReassembledVideoFrame>()
        for (s in shards) {
            val isParity = (s[12].toInt() and BoBoUdpFec.FLAG_PARITY) != 0
            val sIdx = BoBoUdpAesGcmCrypto.readUInt16LE(s, 13)
            if (!isParity && (sIdx == 1 || sIdx == 2)) continue
            assembler.processShard(s, 15.0, ready)
        }
        assertEquals(1, ready.size)
        assertArrayEquals(fakeIdr, ready[0].h264Data)
        assertTrue(assembler.fecRecoveredPackets >= 2L)

        // Selective NACK encode/decode round-trip
        val nackIn = listOf(BoBoUdpNackRequest(12L, 3), BoBoUdpNackRequest(12L, 5))
        val nackDecoded = BoBoUdpNackCodec.decode(BoBoUdpNackCodec.encode(nackIn)!!)
        assertEquals(nackIn, nackDecoded)
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

                // 2. PIN brute-force lockout (5 wrong attempts invalidate PIN)
                val lockSess = server.beginPairing(120)
                val badLockPin = if (lockSess.pin == "0000") "1111" else "0000"
                repeat(5) {
                    assertFalse(BoBoStreamClient.pairWithPin("127.0.0.1", port, badLockPin, clientId).first)
                }
                assertFalse(BoBoStreamClient.pairWithPin("127.0.0.1", port, lockSess.pin, clientId).first)
                assertTrue(server.getActivePairing() == null)

                // 2b. Pair with fresh 4-digit PIN + RSA signature proof
                val pairSess = server.beginPairing(120)
                val wrongPair = BoBoStreamClient.pairWithPin("127.0.0.1", port, if (pairSess.pin == "0000") "1111" else "0000", clientId)
                assertFalse(wrongPair.first)

                val okPair = BoBoStreamClient.pairWithPin("127.0.0.1", port, pairSess.pin, clientId)
                assertTrue("Pairing with valid PIN must succeed: ${okPair.third}", okPair.first)

                // 2c. Filename sanitization check
                assertEquals("evil.exe", BoBoProtocol.sanitizeFileName("..\\..\\Windows\\evil.exe"))
                assertEquals("received.bin", BoBoProtocol.sanitizeFileName(".."))
                assertEquals("_CON.txt", BoBoProtocol.sanitizeFileName("CON.txt"))

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

    @Test
    fun verifyEncryptedIdentityBackupMigrationRoundTrip() {
        val rootDir = Files.createTempDirectory("bobo-android-backup-test").toFile()
        try {
            val orig = BoBoIdentityStore(File(rootDir, "orig"), "PixelSource")
            orig.ensureLoaded()
            val peerThumb = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            assertTrue(
                orig.authorizeOrUpdatePeer(
                    com.bobo.remote.protocol.BoBoAuthorizedPeer(
                        nodeId = "peer-node-01",
                        deviceCode = "123456789",
                        deviceName = "DesktopOffice",
                        certThumbprint = peerThumb,
                        lastHost = "192.168.1.50"
                    )
                )
            )
            val origCode = orig.deviceCode
            val origThumb = orig.certThumbprint
            val origNode = orig.nodeId

            val backupJson = orig.exportEncryptedBackup("BoBoSecret#761")
            val restored = BoBoIdentityStore(File(rootDir, "restored"), "PixelTarget")
            restored.ensureLoaded()
            assertTrue(restored.certThumbprint != origThumb)

            // Wrong passphrase MUST fail
            var wrongFailed = false
            try {
                restored.importEncryptedBackup(backupJson, "WrongPass")
            } catch (_: Exception) {
                wrongFailed = true
            }
            assertTrue("Wrong passphrase must fail GCM authentication", wrongFailed)

            // Correct passphrase restores exact identity & peers & working RSA key
            restored.importEncryptedBackup(backupJson, "BoBoSecret#761")
            assertEquals(origCode, restored.deviceCode)
            assertEquals(origThumb, restored.certThumbprint)
            assertEquals(origNode, restored.nodeId)
            assertTrue(restored.isPeerAuthorized("peer-node-01", peerThumb))

            val sig = restored.signAuthChallenge("abcdef01", origThumb)
            assertTrue(
                BoBoIdentityStore.verifyAuthChallenge(
                    restored.certificateDerBase64,
                    origThumb,
                    "abcdef01",
                    origThumb,
                    sig
                )
            )
        } finally {
            rootDir.deleteRecursively()
        }
    }
}
