import CommonCrypto
import CryptoKit
import Foundation
import Security

public struct BoBoAuthorizedPeer: Codable, Equatable, Identifiable, Sendable {
    public var id: String { certThumbprint }
    public var nodeId: String
    public var deviceCode: String
    public var deviceName: String
    public var certThumbprint: String
    public var lastHost: String
    public var authorizedAt: Date

    public init(
        nodeId: String,
        deviceCode: String,
        deviceName: String,
        certThumbprint: String,
        lastHost: String,
        authorizedAt: Date = Date()
    ) {
        self.nodeId = nodeId
        self.deviceCode = deviceCode
        self.deviceName = deviceName
        self.certThumbprint = certThumbprint.lowercased()
        self.lastHost = lastHost
        self.authorizedAt = authorizedAt
    }
}

public struct BoBoLocalPairSession: Equatable, Sendable {
    public var pin: String
    public var createdAt: Date
    public var expiresAt: Date

    public var isExpired: Bool { Date() >= expiresAt }
    public var remainingSeconds: Int { max(0, Int(ceil(expiresAt.timeIntervalSinceNow))) }

    public init(pin: String, createdAt: Date, expiresAt: Date) {
        self.pin = pin
        self.createdAt = createdAt
        self.expiresAt = expiresAt
    }
}

/// 纯 Apple 原生身份与证书库（零第三方依赖）：
/// - 本地生成 RSA-2048 密钥对与自签名 DER X.509 v3 证书（与 Windows / Android TLS 1.2 100% 互通）
/// - 9 位设备码派生与 4 位临时配对码 HMAC-SHA256 证明（通过 golden-vectors.json 字节级校验）
/// - 内存构造 RFC 7292 PKCS#12 (PBES2 AES-256-CBC + SHA-256) 导入 SecIdentity 供 NWListener TLS 1.2 使用
public final class BoBoIdentityStore: @unchecked Sendable {
    private let lock = NSLock()
    private let storageDir: URL
    private let defaultDeviceName: String
    private var loaded = false

    public private(set) var privateKeyDer: Data = Data() // PKCS#1 RSA private key DER
    public private(set) var certificateDer: Data = Data() // X.509 v3 DER
    public private(set) var nodeId: String = ""
    public private(set) var certThumbprint: String = ""
    public private(set) var deviceCode: String = ""
    public private(set) var deviceName: String = ""

    private var peers: [String: BoBoAuthorizedPeer] = [:]
    private var cachedSecIdentity: SecIdentity?

    public var formattedDeviceCode: String { Self.formatDeviceCode(deviceCode) }

    public init(storageDir: URL, defaultDeviceName: String) {
        self.storageDir = storageDir
        self.defaultDeviceName = defaultDeviceName
        self.deviceName = defaultDeviceName
    }

    public func ensureLoaded() {
        lock.lock()
        defer { lock.unlock() }
        if loaded { return }

        try? FileManager.default.createDirectory(at: storageDir, withIntermediateDirectories: true)
        let keyURL = storageDir.appendingPathComponent("bobo_identity_key.der")
        let certURL = storageDir.appendingPathComponent("bobo_identity_cert.der")
        let metaURL = storageDir.appendingPathComponent("bobo_identity.json")

        if let kData = try? Data(contentsOf: keyURL),
           let cData = try? Data(contentsOf: certURL),
           !kData.isEmpty,
           SecCertificateCreateWithData(nil, cData as CFData) != nil {
            privateKeyDer = kData
            certificateDer = cData
        } else if let generated = try? Self.generateSelfSignedRsa2048Cert(commonName: BoBoProtocol.tlsSniHost) {
            privateKeyDer = generated.privateKeyPkcs1Der
            certificateDer = generated.certificateDer
            try? privateKeyDer.write(to: keyURL, options: .atomic)
            try? certificateDer.write(to: certURL, options: .atomic)
        }

        certThumbprint = Self.computeCertSha256Hex(certificateDer)
        deviceCode = Self.deriveNineDigitCode(certThumbprint)
        nodeId = String(certThumbprint.prefix(24))
        deviceName = defaultDeviceName

        if let metaData = try? Data(contentsOf: metaURL),
           let meta = try? JSONDecoder().decode(PersistedMeta.self, from: metaData) {
            if !meta.nodeId.isEmpty { nodeId = meta.nodeId }
            if !meta.deviceName.isEmpty { deviceName = meta.deviceName }
            for p in meta.authorizedPeers where !p.certThumbprint.isEmpty {
                peers[p.certThumbprint.lowercased()] = p
            }
        } else {
            saveMetaLocked()
        }
        loaded = true
    }

    public func loadSecIdentity() throws -> SecIdentity {
        ensureLoaded()
        lock.lock()
        if let cached = cachedSecIdentity {
            lock.unlock()
            return cached
        }
        let pkcs1 = privateKeyDer
        let cert = certificateDer
        lock.unlock()

        let password = "bobo6"
        let p12 = try Self.buildPkcs12(privateKeyPkcs1Der: pkcs1, certificateDer: cert, password: password)
        var rawItems: CFArray?
        let options: [String: Any] = [kSecImportExportPassphrase as String: password]
        let status = SecPKCS12Import(p12 as CFData, options as CFDictionary, &rawItems)
        guard status == errSecSuccess,
              let items = rawItems as? [[String: Any]],
              let first = items.first,
              let identityVal = first[kSecImportItemIdentity as String] else {
            throw NSError(domain: "BoBoIdentity", code: Int(status), userInfo: [
                NSLocalizedDescriptionKey: "SecPKCS12Import failed (OSStatus=\(status))"
            ])
        }
        let identity = identityVal as! SecIdentity
        lock.lock()
        cachedSecIdentity = identity
        lock.unlock()
        return identity
    }

    public func isPeerAuthorized(peerNodeId: String, peerThumbprint: String) -> Bool {
        ensureLoaded()
        let norm = peerThumbprint.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard !norm.isEmpty else { return false }
        lock.lock()
        defer { lock.unlock() }
        if peers[norm] != nil { return true }
        return peers.values.contains {
            $0.nodeId.caseInsensitiveCompare(peerNodeId) == .orderedSame && $0.certThumbprint == norm
        }
    }

    public func authorizeOrUpdatePeer(_ peer: BoBoAuthorizedPeer) {
        ensureLoaded()
        let norm = peer.certThumbprint.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard !norm.isEmpty else { return }
        lock.lock()
        var copy = peer
        copy.certThumbprint = norm
        peers[norm] = copy
        saveMetaLocked()
        lock.unlock()
    }

    public func listAuthorizedPeers() -> [BoBoAuthorizedPeer] {
        ensureLoaded()
        lock.lock()
        defer { lock.unlock() }
        return Array(peers.values)
    }

    public func removeAuthorizedPeer(thumbprint: String) {
        ensureLoaded()
        lock.lock()
        if peers.removeValue(forKey: thumbprint.lowercased()) != nil {
            saveMetaLocked()
        }
        lock.unlock()
    }

    public func findPeerByCodeOrHost(_ query: String) -> BoBoAuthorizedPeer? {
        ensureLoaded()
        let normCode = Self.normalizeDeviceCode(query)
        let trimmed = query.trimmingCharacters(in: .whitespacesAndNewlines)
        lock.lock()
        defer { lock.unlock() }
        return peers.values.first {
            (normCode.count == 9 && Self.normalizeDeviceCode($0.deviceCode) == normCode)
                || $0.lastHost.caseInsensitiveCompare(trimmed) == .orderedSame
        }
    }

    private func saveMetaLocked() {
        let meta = PersistedMeta(
            nodeId: nodeId,
            deviceCode: deviceCode,
            deviceName: deviceName,
            certThumbprint: certThumbprint,
            authorizedPeers: Array(peers.values)
        )
        if let encoded = try? JSONEncoder().encode(meta) {
            try? encoded.write(to: storageDir.appendingPathComponent("bobo_identity.json"), options: .atomic)
        }
    }

    private struct PersistedMeta: Codable {
        var nodeId: String
        var deviceCode: String
        var deviceName: String
        var certThumbprint: String
        var authorizedPeers: [BoBoAuthorizedPeer]
    }

    // MARK: - Protocol Cryptographic Algorithms

    public static func computeCertSha256Hex(_ derBytes: Data) -> String {
        let digest = SHA256.hash(data: derBytes)
        return BoBoProtocol.bytesToHex(Data(digest))
    }

    public static func deriveNineDigitCode(_ thumbprintHex: String) -> String {
        let msg = Data(("BOBO6-CODE:" + thumbprintHex.lowercased()).utf8)
        let digest = Data(SHA256.hash(data: msg))
        let u32 = UInt64(BoBoProtocol.readUInt32LE(digest, offset: 0))
        let code = (u32 % 900_000_000) + 100_000_000
        return String(code)
    }

    public static func computePinProof(
        pin: String,
        nonceHex: String,
        clientThumbprint: String,
        hostThumbprint: String
    ) -> String {
        let key = SymmetricKey(data: Data(("BOBO6-PIN:" + pin).utf8))
        let msg = Data("\(nonceHex.lowercased())|\(clientThumbprint.lowercased())|\(hostThumbprint.lowercased())".utf8)
        let mac = HMAC<SHA256>.authenticationCode(for: msg, using: key)
        return BoBoProtocol.bytesToHex(Data(mac))
    }

    public static func fixedTimeEqualsHex(_ a: String, _ b: String) -> Bool {
        let ab = Array(a.lowercased().utf8)
        let bb = Array(b.lowercased().utf8)
        guard ab.count == bb.count, !ab.isEmpty else { return false }
        var diff: UInt8 = 0
        for i in 0..<ab.count { diff |= ab[i] ^ bb[i] }
        return diff == 0
    }

    public static func generateFourDigitPin() -> String {
        let n = Int.random(in: 0..<10000)
        return String(format: "%04d", n)
    }

    public static func generateNonceHex(byteCount: Int = 16) -> String {
        var buf = Data(count: byteCount)
        _ = buf.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault, byteCount, $0.baseAddress!) }
        return BoBoProtocol.bytesToHex(buf)
    }

    public static func normalizeDeviceCode(_ input: String) -> String {
        String(input.filter { $0.isNumber })
    }

    public static func formatDeviceCode(_ code: String) -> String {
        let d = normalizeDeviceCode(code)
        guard d.count == 9 else { return d }
        let i1 = d.index(d.startIndex, offsetBy: 3)
        let i2 = d.index(d.startIndex, offsetBy: 6)
        return "\(d[..<i1]) \(d[i1..<i2]) \(d[i2...])"
    }

    // MARK: - Pure-Swift RSA-2048 Self-Signed X.509 v3 DER Certificate

    public struct GeneratedCert {
        public let privateKeyPkcs1Der: Data
        public let certificateDer: Data
    }

    public static func generateSelfSignedRsa2048Cert(commonName: String) throws -> GeneratedCert {
        let attrs: [String: Any] = [
            kSecAttrKeyType as String: kSecAttrKeyTypeRSA,
            kSecAttrKeySizeInBits as String: 2048
        ]
        var err: Unmanaged<CFError>?
        guard let privKey = SecKeyCreateRandomKey(attrs as CFDictionary, &err),
              let pubKey = SecKeyCopyPublicKey(privKey),
              let privPkcs1 = SecKeyCopyExternalRepresentation(privKey, &err) as Data?,
              let pubPkcs1 = SecKeyCopyExternalRepresentation(pubKey, &err) as Data? else {
            throw err?.takeRetainedValue() ?? NSError(domain: "BoBoIdentity", code: -1)
        }

        // Wrap PKCS#1 RSAPublicKey into SubjectPublicKeyInfo (AlgorithmIdentifier rsaEncryption 1.2.840.113549.1.1.1 + NULL)
        let rsaAlgId = derSeq([derOid([1, 2, 840, 113549, 1, 1, 1]), Data([0x05, 0x00])])
        let spki = derSeq([rsaAlgId, derBitString(pubPkcs1)])

        // sha256WithRSAEncryption: 1.2.840.113549.1.1.11
        let sigAlgId = derSeq([derOid([1, 2, 840, 113549, 1, 1, 11]), Data([0x05, 0x00])])

        // Random positive 16-byte serial number
        var serial = Data(count: 16)
        _ = serial.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault, 16, $0.baseAddress!) }
        serial[0] &= 0x7F
        if serial[0] == 0 { serial[0] = 1 }

        let now = Date()
        let notBefore = now.addingTimeInterval(-86400)
        let notAfter = now.addingTimeInterval(10 * 365 * 86400)

        let dn = derSeq([
            derSet([
                derSeq([derOid([2, 5, 4, 3]), derUtf8String(commonName)])
            ])
        ])
        let validity = derSeq([derUtcTime(notBefore), derUtcTime(notAfter)])

        // Extensions: BasicConstraints + SAN DNS:<commonName>
        let bcExt = derSeq([derOid([2, 5, 29, 19]), Data([0x01, 0x01, 0xFF]), derOctetString(derSeq([]))])
        let sanVal = derSeq([derTag(0x82, Data(commonName.utf8))])
        let sanExt = derSeq([derOid([2, 5, 29, 17]), derOctetString(sanVal)])
        let exts = derTag(0xA3, derSeq([bcExt, sanExt]))

        let tbs = derSeq([
            derTag(0xA0, derInteger(Data([0x02]))), // v3
            derInteger(serial),
            sigAlgId,
            dn,
            validity,
            dn,
            spki,
            exts
        ])

        guard let sig = SecKeyCreateSignature(privKey, .rsaSignatureMessagePKCS1v15SHA256, tbs as CFData, &err) as Data? else {
            throw err?.takeRetainedValue() ?? NSError(domain: "BoBoIdentity", code: -2)
        }

        let fullCert = derSeq([tbs, sigAlgId, derBitString(sig)])
        guard SecCertificateCreateWithData(nil, fullCert as CFData) != nil else {
            throw NSError(domain: "BoBoIdentity", code: -3, userInfo: [NSLocalizedDescriptionKey: "Invalid generated X.509 DER"])
        }
        return GeneratedCert(privateKeyPkcs1Der: privPkcs1, certificateDer: fullCert)
    }

    // MARK: - Minimal RFC 7292 PKCS#12 Builder (PBES2 AES-256-CBC + HMAC-SHA256)

    public static func buildPkcs12(privateKeyPkcs1Der: Data, certificateDer: Data, password: String) throws -> Data {
        // Wrap PKCS#1 RSAPrivateKey inside PKCS#8 PrivateKeyInfo
        let rsaAlgId = derSeq([derOid([1, 2, 840, 113549, 1, 1, 1]), Data([0x05, 0x00])])
        let pkcs8 = derSeq([
            derInteger(Data([0x00])),
            rsaAlgId,
            derOctetString(privateKeyPkcs1Der)
        ])

        var localKeyId = Data(count: 20)
        let certHash = Data(SHA256.hash(data: certificateDer))
        localKeyId = certHash.prefix(20)
        let localKeyIdAttr = derSeq([
            derOid([1, 2, 840, 113549, 1, 9, 21]), // localKeyId
            derSet([derOctetString(localKeyId)])
        ])
        let bagAttrs = derSet([localKeyIdAttr])

        // 1. Key SafeBag (pkcs8ShroudedKeyBag = 1.2.840.113549.1.12.10.1.2)
        let (encKeyAlg, encKeyData) = try pbes2Encrypt(plaintext: pkcs8, password: password)
        let encryptedPrivateKeyInfo = derSeq([encKeyAlg, derOctetString(encKeyData)])
        let keyBag = derSeq([
            derOid([1, 2, 840, 113549, 1, 12, 10, 1, 2]),
            derTag(0xA0, encryptedPrivateKeyInfo),
            bagAttrs
        ])
        let keySafeContents = derSeq([keyBag])
        let keyContentInfo = derSeq([
            derOid([1, 2, 840, 113549, 1, 7, 1]), // data
            derTag(0xA0, derOctetString(keySafeContents))
        ])

        // 2. Cert SafeBag (certBag = 1.2.840.113549.1.12.10.1.3, x509Certificate = 1.2.840.113549.1.9.22.1)
        let x509BagVal = derSeq([
            derOid([1, 2, 840, 113549, 1, 9, 22, 1]),
            derTag(0xA0, derOctetString(certificateDer))
        ])
        let certBag = derSeq([
            derOid([1, 2, 840, 113549, 1, 12, 10, 1, 3]),
            derTag(0xA0, x509BagVal),
            bagAttrs
        ])
        let certSafeContents = derSeq([certBag])
        let (encCertAlg, encCertData) = try pbes2Encrypt(plaintext: certSafeContents, password: password)
        let encryptedData = derSeq([
            derInteger(Data([0x00])),
            derSeq([
                derOid([1, 2, 840, 113549, 1, 7, 1]), // data
                encCertAlg,
                derTag(0x80, encCertData)
            ])
        ])
        let certContentInfo = derSeq([
            derOid([1, 2, 840, 113549, 1, 7, 6]), // encryptedData
            derTag(0xA0, encryptedData)
        ])

        // 3. AuthenticatedSafe
        let authSafeBytes = derSeq([keyContentInfo, certContentInfo])
        let pfxContentInfo = derSeq([
            derOid([1, 2, 840, 113549, 1, 7, 1]),
            derTag(0xA0, derOctetString(authSafeBytes))
        ])

        // 4. MacData (PKCS#12 KDF with SHA-256 + HMAC-SHA256)
        var macSalt = Data(count: 16)
        _ = macSalt.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault, 16, $0.baseAddress!) }
        let macIterations = 2048
        let macKey = pkcs12DeriveKeySha256(password: password, salt: macSalt, iterations: macIterations, idByte: 3, keyLen: 32)
        let macBytes = Data(HMAC<SHA256>.authenticationCode(for: authSafeBytes, using: SymmetricKey(data: macKey)))
        let digestInfo = derSeq([
            derSeq([derOid([2, 16, 840, 1, 101, 3, 4, 2, 1]), Data([0x05, 0x00])]), // sha256
            derOctetString(macBytes)
        ])
        let macData = derSeq([
            digestInfo,
            derOctetString(macSalt),
            derInteger(encodePositiveInt(macIterations))
        ])

        return derSeq([
            derInteger(Data([0x03])), // PFX version 3
            pfxContentInfo,
            macData
        ])
    }

    private static func pbes2Encrypt(plaintext: Data, password: String) throws -> (algId: Data, ciphertext: Data) {
        var salt = Data(count: 16)
        var iv = Data(count: 16)
        _ = salt.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault, 16, $0.baseAddress!) }
        _ = iv.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault, 16, $0.baseAddress!) }
        let iterations: UInt32 = 2048

        var key = Data(count: 32)
        let pwdBytes = Array(password.utf8)
        let status = key.withUnsafeMutableBytes { keyPtr in
            salt.withUnsafeBytes { saltPtr in
                CCKeyDerivationPBKDF(
                    CCPBKDFAlgorithm(kCCPBKDF2),
                    password,
                    pwdBytes.count,
                    saltPtr.bindMemory(to: UInt8.self).baseAddress,
                    salt.count,
                    CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256),
                    iterations,
                    keyPtr.bindMemory(to: UInt8.self).baseAddress,
                    32
                )
            }
        }
        guard status == kCCSuccess else {
            throw NSError(domain: "BoBoIdentity", code: Int(status))
        }

        let outCap = plaintext.count + kCCBlockSizeAES128
        var outBuf = Data(count: outCap)
        var outLen = 0
        let encStatus = outBuf.withUnsafeMutableBytes { outPtr in
            plaintext.withUnsafeBytes { inPtr in
                key.withUnsafeBytes { keyPtr in
                    iv.withUnsafeBytes { ivPtr in
                        CCCrypt(
                            CCOperation(kCCEncrypt),
                            CCAlgorithm(kCCAlgorithmAES),
                            CCOptions(kCCOptionPKCS7Padding),
                            keyPtr.baseAddress, 32,
                            ivPtr.baseAddress,
                            inPtr.baseAddress, plaintext.count,
                            outPtr.baseAddress, outCap,
                            &outLen
                        )
                    }
                }
            }
        }
        guard encStatus == kCCSuccess else {
            throw NSError(domain: "BoBoIdentity", code: Int(encStatus))
        }
        outBuf.count = outLen

        // PBKDF2-params (with hmacWithSHA256 = 1.2.840.113549.2.9)
        let pbkdf2Params = derSeq([
            derOctetString(salt),
            derInteger(encodePositiveInt(Int(iterations))),
            derSeq([derOid([1, 2, 840, 113549, 2, 9]), Data([0x05, 0x00])])
        ])
        let kdfAlg = derSeq([derOid([1, 2, 840, 113549, 1, 5, 12]), pbkdf2Params])
        // aes256-CBC = 2.16.840.1.101.3.4.1.42
        let encScheme = derSeq([derOid([2, 16, 840, 1, 101, 3, 4, 1, 42]), derOctetString(iv)])
        // PBES2 = 1.2.840.113549.1.5.13
        let pbes2Alg = derSeq([derOid([1, 2, 840, 113549, 1, 5, 13]), derSeq([kdfAlg, encScheme])])
        return (pbes2Alg, outBuf)
    }

    // RFC 7292 Appendix B.2 PKCS#12 KDF using SHA-256 (u=32, v=64)
    private static func pkcs12DeriveKeySha256(password: String, salt: Data, iterations: Int, idByte: UInt8, keyLen: Int) -> Data {
        let u = 32
        let v = 64
        // BMPString (UTF-16BE) + 2-byte NUL terminator
        var pwdBmp = Data()
        for scalar in password.utf16 {
            pwdBmp.append(UInt8((scalar >> 8) & 0xFF))
            pwdBmp.append(UInt8(scalar & 0xFF))
        }
        if !pwdBmp.isEmpty {
            pwdBmp.append(contentsOf: [0x00, 0x00])
        }

        let d = Data(repeating: idByte, count: v)
        func repeatToBlock(_ input: Data) -> Data {
            guard !input.isEmpty else { return Data() }
            let target = ((input.count + v - 1) / v) * v
            var res = Data(capacity: target)
            for i in 0..<target { res.append(input[input.startIndex + (i % input.count)]) }
            return res
        }
        var I = repeatToBlock(salt) + repeatToBlock(pwdBmp)
        let c = (keyLen + u - 1) / u
        var derived = Data()

        for _ in 0..<c {
            var A = Data(SHA256.hash(data: d + I))
            for _ in 1..<iterations {
                A = Data(SHA256.hash(data: A))
            }
            derived.append(A)
            if derived.count >= keyLen { break }

            // B = repeat A to v bytes
            var B = Data(capacity: v)
            for j in 0..<v { B.append(A[j % u]) }
            // I_j = (I_j + B + 1) mod 2^(8*v)
            var offset = 0
            while offset < I.count {
                var carry = 1
                for k in stride(from: v - 1, through: 0, by: -1) {
                    let sum = Int(I[offset + k]) + Int(B[k]) + carry
                    I[offset + k] = UInt8(sum & 0xFF)
                    carry = sum >> 8
                }
                offset += v
            }
        }
        return derived.prefix(keyLen)
    }

    // MARK: - ASN.1 DER Primitives

    private static func derTag(_ tag: UInt8, _ content: Data) -> Data {
        var out = Data()
        out.append(tag)
        let len = content.count
        if len < 0x80 {
            out.append(UInt8(len))
        } else if len <= 0xFF {
            out.append(0x81)
            out.append(UInt8(len))
        } else if len <= 0xFFFF {
            out.append(0x82)
            out.append(UInt8((len >> 8) & 0xFF))
            out.append(UInt8(len & 0xFF))
        } else {
            out.append(0x83)
            out.append(UInt8((len >> 16) & 0xFF))
            out.append(UInt8((len >> 8) & 0xFF))
            out.append(UInt8(len & 0xFF))
        }
        out.append(content)
        return out
    }

    private static func derSeq(_ items: [Data]) -> Data {
        var body = Data()
        for item in items { body.append(item) }
        return derTag(0x30, body)
    }

    private static func derSet(_ items: [Data]) -> Data {
        var body = Data()
        for item in items { body.append(item) }
        return derTag(0x31, body)
    }

    private static func derInteger(_ unsignedBigEndian: Data) -> Data {
        var bytes = unsignedBigEndian
        while bytes.count > 1 && bytes.first == 0 { bytes.removeFirst() }
        if let first = bytes.first, (first & 0x80) != 0 {
            bytes.insert(0x00, at: 0)
        }
        return derTag(0x02, bytes)
    }

    private static func encodePositiveInt(_ value: Int) -> Data {
        var v = value
        var bytes = [UInt8]()
        repeat {
            bytes.insert(UInt8(v & 0xFF), at: 0)
            v >>= 8
        } while v > 0
        return Data(bytes)
    }

    private static func derOctetString(_ data: Data) -> Data { derTag(0x04, data) }
    private static func derUtf8String(_ s: String) -> Data { derTag(0x0C, Data(s.utf8)) }

    private static func derBitString(_ data: Data) -> Data {
        var body = Data([0x00])
        body.append(data)
        return derTag(0x03, body)
    }

    private static func derUtcTime(_ date: Date) -> Data {
        let fmt = DateFormatter()
        fmt.locale = Locale(identifier: "en_US_POSIX")
        fmt.timeZone = TimeZone(secondsFromGMT: 0)
        fmt.dateFormat = "yyMMddHHmmss'Z'"
        return derTag(0x17, Data(fmt.string(from: date).utf8))
    }

    private static func derOid(_ arcs: [Int]) -> Data {
        var body = Data()
        body.append(UInt8(arcs[0] * 40 + arcs[1]))
        for i in 2..<arcs.count {
            var v = arcs[i]
            var stack = [UInt8]()
            stack.append(UInt8(v & 0x7F))
            v >>= 7
            while v > 0 {
                stack.append(UInt8((v & 0x7F) | 0x80))
                v >>= 7
            }
            for b in stack.reversed() { body.append(b) }
        }
        return derTag(0x06, body)
    }
}
