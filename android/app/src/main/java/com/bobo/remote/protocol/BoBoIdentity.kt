package com.bobo.remote.protocol

import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.File
import java.math.BigInteger
import java.nio.charset.StandardCharsets
import java.security.KeyFactory
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.MessageDigest
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.Signature
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.security.spec.PKCS8EncodedKeySpec
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone
import java.util.UUID
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.TrustManager
import javax.net.ssl.X509TrustManager

data class BoBoAuthorizedPeer(
    val nodeId: String,
    val deviceCode: String,
    val deviceName: String,
    val certThumbprint: String,
    val lastHost: String,
    val authorizedAtMs: Long = System.currentTimeMillis()
)

data class BoBoLocalPairSession(
    val pin: String,
    val createdMs: Long,
    val expiresMs: Long
) {
    val isExpired: Boolean get() = System.currentTimeMillis() >= expiresMs
    val remainingSeconds: Int get() = ((expiresMs - System.currentTimeMillis() + 999L) / 1000L).toInt().coerceAtLeast(0)
}

/**
 * 零外部依赖纯 Kotlin RSA-2048 自签名 X.509 v3 证书、9 位设备码派生与 4 位临时配对码 HMAC 证明。
 * 与 Windows 端 BoBoIdentity.cs 和 Apple 端 BoBoIdentity.swift 保持字节级一致。
 */
class BoBoIdentityStore(
    private val storageDir: File,
    private val defaultDeviceName: String = "Android Phone"
) {
    private val lock = Any()
    private var loaded = false

    lateinit var privateKey: PrivateKey
        private set
    lateinit var certificate: X509Certificate
        private set
    var nodeId: String = ""
        private set
    var certThumbprint: String = ""
        private set
    var deviceCode: String = ""
        private set
    var deviceName: String = defaultDeviceName
        private set

    private val peers = LinkedHashMap<String, BoBoAuthorizedPeer>()

    val formattedDeviceCode: String get() = formatDeviceCode(deviceCode)

    fun ensureLoaded() {
        synchronized(lock) {
            if (loaded) return
            if (!storageDir.exists()) storageDir.mkdirs()

            val keyFile = File(storageDir, "bobo_identity_key.der")
            val certFile = File(storageDir, "bobo_identity_cert.der")
            val metaFile = File(storageDir, "bobo_identity.json")

            var restored = false
            if (keyFile.exists() && certFile.exists()) {
                try {
                    val keyBytes = keyFile.readBytes()
                    val certBytes = certFile.readBytes()
                    val kf = KeyFactory.getInstance("RSA")
                    privateKey = kf.generatePrivate(PKCS8EncodedKeySpec(keyBytes))
                    val cf = CertificateFactory.getInstance("X.509")
                    certificate = cf.generateCertificate(ByteArrayInputStream(certBytes)) as X509Certificate
                    restored = true
                } catch (_: Exception) {
                    restored = false
                }
            }

            if (!restored) {
                val (kp, cert) = generateSelfSignedRsa2048Cert(BoBoProtocol.TLS_SNI_HOST)
                privateKey = kp.private
                certificate = cert
                keyFile.writeBytes(privateKey.encoded)
                certFile.writeBytes(certificate.encoded)
            }

            certThumbprint = computeCertSha256Hex(certificate.encoded)
            deviceCode = deriveNineDigitCode(certThumbprint)
            nodeId = certThumbprint.substring(0, 24)
            deviceName = defaultDeviceName

            if (metaFile.exists()) {
                try {
                    val json = JSONObject(metaFile.readText(StandardCharsets.UTF_8))
                    val savedNode = json.optString("nodeId", "")
                    if (savedNode.isNotBlank()) nodeId = savedNode
                    val savedName = json.optString("deviceName", "")
                    if (savedName.isNotBlank()) deviceName = savedName
                    val arr = json.optJSONArray("authorizedPeers")
                    if (arr != null) {
                        for (i in 0 until arr.length()) {
                            val p = arr.optJSONObject(i) ?: continue
                            val peer = BoBoAuthorizedPeer(
                                nodeId = p.optString("nodeId", ""),
                                deviceCode = p.optString("deviceCode", ""),
                                deviceName = p.optString("deviceName", ""),
                                certThumbprint = p.optString("certThumbprint", "").lowercase(Locale.US),
                                lastHost = p.optString("lastHost", ""),
                                authorizedAtMs = p.optLong("authorizedAtMs", System.currentTimeMillis())
                            )
                            if (peer.certThumbprint.isNotBlank()) {
                                peers[peer.certThumbprint] = peer
                            }
                        }
                    }
                } catch (_: Exception) {
                }
            } else {
                if (nodeId.isBlank()) nodeId = UUID.randomUUID().toString().replace("-", "").substring(0, 24)
                saveMetaLocked()
            }

            loaded = true
        }
    }

    fun createServerSslContext(): SSLContext {
        ensureLoaded()
        val ks = KeyStore.getInstance(KeyStore.getDefaultType())
        ks.load(null, null)
        val pwd = "bobo6".toCharArray()
        ks.setKeyEntry("bobo", privateKey, pwd, arrayOf(certificate))
        val kmf = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm())
        kmf.init(ks, pwd)
        val ctx = SSLContext.getInstance("TLSv1.2")
        ctx.init(kmf.keyManagers, null, SecureRandom())
        return ctx
    }

    fun isPeerAuthorized(peerNodeId: String, peerThumbprint: String): Boolean {
        ensureLoaded()
        val normThumb = peerThumbprint.trim().lowercase(Locale.US)
        if (normThumb.isEmpty()) return false
        synchronized(lock) {
            return peers.containsKey(normThumb) ||
                peers.values.any { it.nodeId.equals(peerNodeId, ignoreCase = true) && it.certThumbprint == normThumb }
        }
    }

    fun authorizeOrUpdatePeer(peer: BoBoAuthorizedPeer) {
        ensureLoaded()
        val normThumb = peer.certThumbprint.trim().lowercase(Locale.US)
        if (normThumb.isEmpty()) return
        synchronized(lock) {
            peers[normThumb] = peer.copy(certThumbprint = normThumb)
            saveMetaLocked()
        }
    }

    fun listAuthorizedPeers(): List<BoBoAuthorizedPeer> {
        ensureLoaded()
        synchronized(lock) {
            return peers.values.toList()
        }
    }

    fun removeAuthorizedPeer(thumbprint: String) {
        ensureLoaded()
        synchronized(lock) {
            if (peers.remove(thumbprint.trim().lowercase(Locale.US)) != null) {
                saveMetaLocked()
            }
        }
    }

    fun findPeerByCodeOrHost(query: String): BoBoAuthorizedPeer? {
        ensureLoaded()
        val normCode = normalizeDeviceCode(query)
        val trimmed = query.trim()
        synchronized(lock) {
            return peers.values.firstOrNull {
                (normCode.length == 9 && normalizeDeviceCode(it.deviceCode) == normCode) ||
                    it.lastHost.equals(trimmed, ignoreCase = true)
            }
        }
    }

    private fun saveMetaLocked() {
        try {
            val root = JSONObject()
            root.put("nodeId", nodeId)
            root.put("deviceCode", deviceCode)
            root.put("deviceName", deviceName)
            root.put("certThumbprint", certThumbprint)
            val arr = JSONArray()
            for (p in peers.values) {
                arr.put(JSONObject().apply {
                    put("nodeId", p.nodeId)
                    put("deviceCode", p.deviceCode)
                    put("deviceName", p.deviceName)
                    put("certThumbprint", p.certThumbprint)
                    put("lastHost", p.lastHost)
                    put("authorizedAtMs", p.authorizedAtMs)
                })
            }
            root.put("authorizedPeers", arr)
            File(storageDir, "bobo_identity.json").writeText(root.toString(2), StandardCharsets.UTF_8)
        } catch (_: Exception) {
        }
    }

    companion object {
        private val secureRandom = SecureRandom()

        fun computeCertSha256Hex(derBytes: ByteArray): String {
            val digest = MessageDigest.getInstance("SHA-256").digest(derBytes)
            return BoBoProtocol.bytesToHex(digest)
        }

        fun deriveNineDigitCode(thumbprintHex: String): String {
            val input = ("BOBO6-CODE:" + thumbprintHex.lowercase(Locale.US)).toByteArray(StandardCharsets.UTF_8)
            val hash = MessageDigest.getInstance("SHA-256").digest(input)
            val u32 = BoBoProtocol.readUInt32LE(hash, 0)
            val code = (u32 % 900_000_000L) + 100_000_000L
            return code.toString()
        }

        fun computePinProof(
            pin: String,
            nonceHex: String,
            clientThumbprint: String,
            hostThumbprint: String
        ): String {
            val keyBytes = ("BOBO6-PIN:$pin").toByteArray(StandardCharsets.UTF_8)
            val msg = (nonceHex.lowercase(Locale.US) + "|" +
                clientThumbprint.lowercase(Locale.US) + "|" +
                hostThumbprint.lowercase(Locale.US)).toByteArray(StandardCharsets.UTF_8)
            val mac = Mac.getInstance("HmacSHA256")
            mac.init(SecretKeySpec(keyBytes, "HmacSHA256"))
            return BoBoProtocol.bytesToHex(mac.doFinal(msg))
        }

        fun fixedTimeEqualsHex(a: String, b: String): Boolean {
            if (a.length != b.length || a.isEmpty()) return false
            var diff = 0
            for (i in a.indices) {
                diff = diff or (a[i].lowercaseChar().code xor b[i].lowercaseChar().code)
            }
            return diff == 0
        }

        fun generateFourDigitPin(): String {
            val n = secureRandom.nextInt(10000)
            return String.format(Locale.US, "%04d", n)
        }

        fun generateNonceHex(bytes: Int = 16): String {
            val buf = ByteArray(bytes)
            secureRandom.nextBytes(buf)
            return BoBoProtocol.bytesToHex(buf)
        }

        fun normalizeDeviceCode(input: String): String = input.filter { it.isDigit() }

        fun formatDeviceCode(code: String): String {
            val digits = normalizeDeviceCode(code)
            return if (digits.length == 9) {
                "${digits.substring(0, 3)} ${digits.substring(3, 6)} ${digits.substring(6, 9)}"
            } else {
                digits
            }
        }

        fun createCapturingClientSslContext(onServerThumbprint: (String) -> Unit): SSLContext {
            val tm = object : X509TrustManager {
                override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) = Unit
                override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
                    val leaf = chain?.firstOrNull()
                        ?: throw java.security.cert.CertificateException("Empty server certificate chain")
                    onServerThumbprint(computeCertSha256Hex(leaf.encoded))
                }
                override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
            }
            val ctx = SSLContext.getInstance("TLSv1.2")
            ctx.init(null, arrayOf<TrustManager>(tm), secureRandom)
            return ctx
        }

        /**
         * 纯 ASN.1 DER 构造自签名 RSA-2048 X.509 v3 证书（不依赖 sun.security 或 BouncyCastle）。
         */
        fun generateSelfSignedRsa2048Cert(commonName: String): Pair<KeyPair, X509Certificate> {
            val kpg = KeyPairGenerator.getInstance("RSA")
            kpg.initialize(2048, secureRandom)
            val kp = kpg.generateKeyPair()

            val serialBytes = ByteArray(16).also { secureRandom.nextBytes(it) }
            serialBytes[0] = (serialBytes[0].toInt() and 0x7F).toByte()
            if (serialBytes[0].toInt() == 0) serialBytes[0] = 1
            val serial = BigInteger(1, serialBytes)

            val now = System.currentTimeMillis()
            val notBefore = Date(now - 86_400_000L) // -1 day for clock skew
            val notAfter = Date(now + 10L * 365L * 86_400_000L) // +10 years

            // sha256WithRSAEncryption OID: 1.2.840.113549.1.1.11
            val sigAlgId = derSeq(
                derOid(intArrayOf(1, 2, 840, 113549, 1, 1, 11)),
                byteArrayOf(0x05, 0x00) // NULL
            )

            // X.500 Name: CN=<commonName> (OID 2.5.4.3)
            val dn = derSeq(
                derSet(
                    derSeq(
                        derOid(intArrayOf(2, 5, 4, 3)),
                        derUtf8String(commonName)
                    )
                )
            )

            val validity = derSeq(derUtcTime(notBefore), derUtcTime(notAfter))

            // SubjectAltName extension: DNS:<commonName> (OID 2.5.29.17)
            val sanValue = derSeq(derContextPrimitive(2, commonName.toByteArray(StandardCharsets.US_ASCII)))
            val sanExt = derSeq(
                derOid(intArrayOf(2, 5, 29, 17)),
                derOctetString(sanValue)
            )
            // BasicConstraints: CA=FALSE (OID 2.5.29.19)
            val bcExt = derSeq(
                derOid(intArrayOf(2, 5, 29, 19)),
                byteArrayOf(0x01, 0x01, 0xFF.toByte()), // critical = TRUE
                derOctetString(derSeq())
            )
            val extensions = derExplicitTag(3, derSeq(bcExt, sanExt))

            val tbs = derSeq(
                derExplicitTag(0, derInteger(BigInteger.valueOf(2))), // version v3
                derInteger(serial),
                sigAlgId,
                dn,
                validity,
                dn,
                kp.public.encoded, // already DER SubjectPublicKeyInfo
                extensions
            )

            val signer = Signature.getInstance("SHA256withRSA")
            signer.initSign(kp.private)
            signer.update(tbs)
            val signatureBytes = signer.sign()

            val fullCert = derSeq(
                tbs,
                sigAlgId,
                derBitString(signatureBytes)
            )

            val cf = CertificateFactory.getInstance("X.509")
            val cert = cf.generateCertificate(ByteArrayInputStream(fullCert)) as X509Certificate
            return Pair(kp, cert)
        }

        private fun derTag(tag: Int, content: ByteArray): ByteArray {
            val out = ByteArrayOutputStream(content.size + 6)
            out.write(tag)
            val len = content.size
            when {
                len < 0x80 -> out.write(len)
                len <= 0xFF -> {
                    out.write(0x81)
                    out.write(len)
                }
                len <= 0xFFFF -> {
                    out.write(0x82)
                    out.write((len ushr 8) and 0xFF)
                    out.write(len and 0xFF)
                }
                else -> {
                    out.write(0x83)
                    out.write((len ushr 16) and 0xFF)
                    out.write((len ushr 8) and 0xFF)
                    out.write(len and 0xFF)
                }
            }
            out.write(content)
            return out.toByteArray()
        }

        private fun concat(vararg parts: ByteArray): ByteArray {
            val out = ByteArrayOutputStream()
            for (p in parts) out.write(p)
            return out.toByteArray()
        }

        private fun derSeq(vararg items: ByteArray) = derTag(0x30, concat(*items))
        private fun derSet(vararg items: ByteArray) = derTag(0x31, concat(*items))
        private fun derExplicitTag(num: Int, inner: ByteArray) = derTag(0xA0 or num, inner)
        private fun derContextPrimitive(num: Int, bytes: ByteArray) = derTag(0x80 or num, bytes)
        private fun derInteger(v: BigInteger) = derTag(0x02, v.toByteArray())
        private fun derOctetString(b: ByteArray) = derTag(0x04, b)
        private fun derUtf8String(s: String) = derTag(0x0C, s.toByteArray(StandardCharsets.UTF_8))

        private fun derBitString(bytes: ByteArray): ByteArray {
            val body = ByteArray(bytes.size + 1)
            body[0] = 0
            System.arraycopy(bytes, 0, body, 1, bytes.size)
            return derTag(0x03, body)
        }

        private fun derUtcTime(date: Date): ByteArray {
            val fmt = SimpleDateFormat("yyMMddHHmmss'Z'", Locale.US)
            fmt.timeZone = TimeZone.getTimeZone("UTC")
            return derTag(0x17, fmt.format(date).toByteArray(StandardCharsets.US_ASCII))
        }

        private fun derOid(arcs: IntArray): ByteArray {
            val out = ByteArrayOutputStream()
            out.write(arcs[0] * 40 + arcs[1])
            for (i in 2 until arcs.size) {
                var v = arcs[i]
                val stack = IntArray(6)
                var top = 0
                stack[top++] = v and 0x7F
                v = v ushr 7
                while (v > 0) {
                    stack[top++] = (v and 0x7F) or 0x80
                    v = v ushr 7
                }
                for (k in top - 1 downTo 0) out.write(stack[k])
            }
            return derTag(0x06, out.toByteArray())
        }
    }
}
