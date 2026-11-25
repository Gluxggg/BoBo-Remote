import Foundation
import Security
import XCTest
@testable import BoBoAppleCore

final class BoBoGoldenVectorsTests: XCTestCase {

    private func loadGoldenVectors() throws -> [String: Any] {
        let thisFile = URL(fileURLWithPath: #filePath)
        // apple/BoBoAppleCore/Tests/BoBoAppleCoreTests/BoBoGoldenVectorsTests.swift -> repo root
        let repoRoot = thisFile
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
        let goldenURL = repoRoot.appendingPathComponent("protocol/golden-vectors.json")
        let data = try Data(contentsOf: goldenURL)
        return try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    func testGoldenVectorsByteExact() throws {
        let root = try loadGoldenVectors()

        // 1. Device code derivation
        let dc = try XCTUnwrap(root["deviceCodeDerivation"] as? [String: Any])
        XCTAssertEqual(
            BoBoIdentityStore.deriveNineDigitCode(try XCTUnwrap(dc["thumbprintHex"] as? String)),
            try XCTUnwrap(dc["expectedDeviceCode"] as? String)
        )

        // 2. PIN HMAC proof
        let pp = try XCTUnwrap(root["pinProof"] as? [String: Any])
        XCTAssertEqual(
            BoBoIdentityStore.computePinProof(
                pin: try XCTUnwrap(pp["pin"] as? String),
                nonceHex: try XCTUnwrap(pp["nonceHex"] as? String),
                clientThumbprint: try XCTUnwrap(pp["clientThumbprint"] as? String),
                hostThumbprint: try XCTUnwrap(pp["hostThumbprint"] as? String)
            ),
            try XCTUnwrap(pp["expectedProofHex"] as? String)
        )

        // 3. Video H.264 frame
        let vf = try XCTUnwrap(root["videoFrame"] as? [String: Any])
        let vPayload = BoBoProtocol.buildVideoPayload(
            width: try XCTUnwrap(vf["width"] as? Int),
            height: try XCTUnwrap(vf["height"] as? Int),
            isKeyframe: try XCTUnwrap(vf["isKeyframe"] as? Bool),
            h264Data: BoBoProtocol.hexToBytes(try XCTUnwrap(vf["h264Hex"] as? String))
        )
        XCTAssertEqual(BoBoProtocol.bytesToHex(vPayload), try XCTUnwrap(vf["payloadHex"] as? String))
        let vFrame = BoBoProtocol.encodeFrame(
            type: .videoH264,
            flagsOrTimestamp: UInt32(try XCTUnwrap(vf["timestampMs"] as? Int)),
            payload: vPayload
        )
        XCTAssertEqual(BoBoProtocol.bytesToHex(vFrame), try XCTUnwrap(vf["frameHex"] as? String))

        // 4. Audio IMA-ADPCM encode + decode + 9B frame
        let af = try XCTUnwrap(root["audioAdpcm"] as? [String: Any])
        let pcmNums = try XCTUnwrap(af["pcmInterleaved"] as? [Int])
        let pcmIn = pcmNums.map { Int16($0) }
        let sampleCount = try XCTUnwrap(af["sampleCount"] as? Int)
        let adpcm = ImaAdpcmCodec.encodeStereo(pcmIn, sampleFrames: sampleCount)
        XCTAssertEqual(BoBoProtocol.bytesToHex(adpcm), try XCTUnwrap(af["adpcmPacketHex"] as? String))

        let decoded = ImaAdpcmCodec.decodeStereo(adpcm, sampleFrames: sampleCount)
        let expDecoded = (try XCTUnwrap(af["expectedDecodedPcm"] as? [Int])).map { Int16($0) }
        XCTAssertEqual(decoded, expDecoded)

        let aPayload = BoBoProtocol.buildAudioPayload(
            sampleRate: try XCTUnwrap(af["sampleRate"] as? Int),
            channels: try XCTUnwrap(af["channels"] as? Int),
            sampleCount: sampleCount,
            adpcmData: adpcm
        )
        XCTAssertEqual(BoBoProtocol.bytesToHex(aPayload), try XCTUnwrap(af["payloadHex"] as? String))
        let aFrame = BoBoProtocol.encodeFrame(type: .audioAdpcm, flagsOrTimestamp: 0, payload: aPayload)
        XCTAssertEqual(BoBoProtocol.bytesToHex(aFrame), try XCTUnwrap(af["frameHex"] as? String))

        // 5. All 8 InputEvent payloads
        let inp = try XCTUnwrap(root["inputPackets"] as? [String: [String: Any]])
        XCTAssertEqual(
            BoBoProtocol.bytesToHex(BoBoProtocol.buildInputPayload(BoBoProtocol.buildMouseAbs(nx: 32767, ny: 32768))),
            inp["mouseAbsCenter"]?["payloadHex"] as? String
        )
        XCTAssertEqual(
            BoBoProtocol.bytesToHex(BoBoProtocol.buildInputPayload(BoBoProtocol.buildMouseRel(dx: -18, dy: 42))),
            inp["mouseRel"]?["payloadHex"] as? String
        )
        XCTAssertEqual(
            BoBoProtocol.bytesToHex(BoBoProtocol.buildInputPayload(BoBoProtocol.buildMouseButton(button: 3, down: true))),
            inp["mouseBtnRightDown"]?["payloadHex"] as? String
        )
        XCTAssertEqual(
            BoBoProtocol.bytesToHex(BoBoProtocol.buildInputPayload(BoBoProtocol.buildMouseWheel(delta: -120, horizontal: false))),
            inp["mouseWheelDown"]?["payloadHex"] as? String
        )
        XCTAssertEqual(
            BoBoProtocol.bytesToHex(BoBoProtocol.buildInputPayload(BoBoProtocol.buildKey(vk: 13, down: true, extended: false))),
            inp["keyEnterDown"]?["payloadHex"] as? String
        )
        XCTAssertEqual(
            BoBoProtocol.bytesToHex(BoBoProtocol.buildInputPayload(BoBoProtocol.buildReleaseAll())),
            inp["releaseAll"]?["payloadHex"] as? String
        )
        XCTAssertEqual(
            BoBoProtocol.bytesToHex(BoBoProtocol.buildInputPayload(BoBoProtocol.buildTouch(pointerId: 1, action: 0, nx: 16384, ny: 32768))),
            inp["touchPointer1Down"]?["payloadHex"] as? String
        )
        XCTAssertEqual(
            BoBoProtocol.bytesToHex(BoBoProtocol.buildInputPayload(BoBoProtocol.buildMobileNav(BoBoProtocol.mobileNavBack))),
            inp["mobileNavBack"]?["payloadHex"] as? String
        )

        // 6. FileChunk frame
        let fc = try XCTUnwrap(root["fileChunk"] as? [String: Any])
        let fPayload = BoBoProtocol.buildFileChunkPayload(
            transferId16: try XCTUnwrap(fc["transferId"] as? String),
            offset: Int64(try XCTUnwrap(fc["offset"] as? Int)),
            data: BoBoProtocol.hexToBytes(try XCTUnwrap(fc["chunkHex"] as? String))
        )
        XCTAssertEqual(BoBoProtocol.bytesToHex(fPayload), try XCTUnwrap(fc["payloadHex"] as? String))
        let fFrame = BoBoProtocol.encodeFrame(type: .fileChunk, flagsOrTimestamp: 0, payload: fPayload)
        XCTAssertEqual(BoBoProtocol.bytesToHex(fFrame), try XCTUnwrap(fc["frameHex"] as? String))
    }

    func testSelfSignedX509AndPkcs12AndTlsLoopback() async throws {
        let tmpRoot = FileManager.default.temporaryDirectory.appendingPathComponent("bobo-apple-test-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: tmpRoot) }

        let hostId = BoBoIdentityStore(storageDir: tmpRoot.appendingPathComponent("host"), defaultDeviceName: "TestMacHost")
        let clientId = BoBoIdentityStore(storageDir: tmpRoot.appendingPathComponent("client"), defaultDeviceName: "TestAppleClient")
        defer {
            hostId.removeKeychainIdentity()
            clientId.removeKeychainIdentity()
        }
        hostId.ensureLoaded()
        clientId.ensureLoaded()

        XCTAssertEqual(hostId.certThumbprint.count, 64)
        XCTAssertEqual(hostId.deviceCode.count, 9)
        XCTAssertNotNil(SecCertificateCreateWithData(nil, hostId.certificateDer as CFData))

        // Verify P0-3 RSA challenge proof-of-possession
        let testNonce = BoBoIdentityStore.generateNonceHex(byteCount: 16)
        let goodSig = clientId.signAuthChallenge(nonceHex: testNonce, hostThumbprint: hostId.certThumbprint)
        XCTAssertFalse(goodSig.isEmpty)
        XCTAssertTrue(BoBoIdentityStore.verifyAuthChallenge(
            clientCertDerBase64: clientId.certificateDerBase64,
            expectedClientThumbprint: clientId.certThumbprint,
            nonceHex: testNonce,
            hostThumbprint: hostId.certThumbprint,
            signatureBase64: goodSig
        ))
        XCTAssertFalse(BoBoIdentityStore.verifyAuthChallenge(
            clientCertDerBase64: clientId.certificateDerBase64,
            expectedClientThumbprint: clientId.certThumbprint,
            nonceHex: "00000000000000000000000000000000",
            hostThumbprint: hostId.certThumbprint,
            signatureBase64: goodSig
        ))

        // Verify P0-6 cross-OS filename sanitization
        XCTAssertEqual(BoBoProtocol.sanitizeFileName("..\\..\\evil.exe"), "evil.exe")
        XCTAssertEqual(BoBoProtocol.sanitizeFileName("../a/b/report.pdf"), "report.pdf")
        XCTAssertEqual(BoBoProtocol.sanitizeFileName(".."), "received.bin")
        XCTAssertEqual(BoBoProtocol.sanitizeFileName("a:b*c?.txt"), "a_b_c_.txt")

        // Verify PKCS#12 -> SecIdentity + real NWListener TLS 1.2 <-> NWConnection loopback!
        let server = BoBoHostServer(
            identity: hostId,
            downloadDir: tmpRoot.appendingPathComponent("downloads"),
            port: 0
        )
        let boundPort = try await server.start()
        XCTAssertGreaterThan(boundPort, 0)
        defer { server.stop() }

        let probe1 = await BoBoStreamClient.probeHost(host: "127.0.0.1", port: boundPort, identity: clientId)
        XCTAssertTrue(probe1.reachable)
        XCTAssertTrue(probe1.isBoBoHost)
        XCTAssertFalse(probe1.authorized)
        XCTAssertEqual(probe1.platform, BoBoProtocol.currentPlatform)

        // Verify P0-4 5-attempt PIN brute-force lockout
        let lockSess = server.beginPairing(ttlSeconds: 120)
        let wrongPin = lockSess.pin == "0000" ? "9999" : "0000"
        for _ in 0..<BoBoLocalPairSession.maxFailedAttempts {
            let badRes = await BoBoStreamClient.pairWithPin(host: "127.0.0.1", port: boundPort, pin: wrongPin, identity: clientId)
            XCTAssertFalse(badRes.ok)
        }
        // Session must be invalidated after 5 failures even if the real PIN is supplied next
        let postLockRes = await BoBoStreamClient.pairWithPin(host: "127.0.0.1", port: boundPort, pin: lockSess.pin, identity: clientId)
        XCTAssertFalse(postLockRes.ok)

        // Fresh pairing session should succeed with RSA proof + PIN HMAC
        let pairSess = server.beginPairing(ttlSeconds: 120)
        let pairRes = await BoBoStreamClient.pairWithPin(host: "127.0.0.1", port: boundPort, pin: pairSess.pin, identity: clientId)
        XCTAssertTrue(pairRes.ok, "Pair failed: \(pairRes.error)")

        let probe2 = await BoBoStreamClient.probeHost(host: "127.0.0.1", port: boundPort, identity: clientId)
        XCTAssertTrue(probe2.authorized)

        let sampleURL = tmpRoot.appendingPathComponent("apple-loopback.txt")
        let payloadText = "BoBoRemote V6.2 Apple TLS 1.2 SHA-256 loopback!"
        try payloadText.write(to: sampleURL, atomically: true, encoding: .utf8)
        let fileRes = await BoBoStreamClient.sendFile(host: "127.0.0.1", port: boundPort, fileURL: sampleURL, identity: clientId)
        XCTAssertTrue(fileRes.ok, "File send failed: \(fileRes.info)")
        let receivedText = try String(contentsOfFile: fileRes.info, encoding: .utf8)
        XCTAssertEqual(receivedText, payloadText)
    }
}
