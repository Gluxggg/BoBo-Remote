using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Web.Script.Serialization;

namespace DualRemote
{
    internal sealed class BoBoAuthorizedPeer
    {
        internal string NodeId = "";
        internal string DeviceCode = "";
        internal string DeviceName = "";
        internal string CertThumbprint = "";
        internal string LastHost = "";
        internal string PairedAtUtc = "";
        internal string LastConnectedUtc = "";
    }

    /// <summary>
    /// BoBo V6 独立双端身份与授权存储（与外部 Sunshine / Moonlight / RustDesk 彻底隔离）：
    /// - 本地生成 RSA-2048 自签名 TLS 证书（node.pfx）与确定性 9 位 BoBo 设备码；
    /// - 维护本机双向信任名单（authorized-peers.json），严格校验 NodeId + 证书指纹；
    /// - 4 位临时配对码使用加密随机源 + HMAC-SHA256 双向挑战校验，杜绝任何外部内核绕过授权。
    /// </summary>
    internal sealed class BoBoIdentityStore
    {
        private const string PfxPassword = "bobo-v6-identity-key";
        private static readonly byte[] DpapiMagic = Encoding.ASCII.GetBytes("BDP1");
        private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("BoBoRemote-V6-NodePfx-v1");
        private static readonly object DefaultGate = new object();
        private static BoBoIdentityStore defaultInstance;

        private readonly object gate = new object();
        private readonly string storeDir;
        private bool loaded;
        private X509Certificate2 nodeCert;
        private string nodeId = "";
        private string deviceCode = "";
        private string deviceName = "";
        private string certThumbprint = "";
        private List<BoBoAuthorizedPeer> peers = new List<BoBoAuthorizedPeer>();

        internal static BoBoIdentityStore Default
        {
            get
            {
                if (defaultInstance != null) return defaultInstance;
                lock (DefaultGate)
                {
                    if (defaultInstance == null)
                    {
                        defaultInstance = new BoBoIdentityStore(Path.Combine(RecentStore.DirPath, "bobo-identity"));
                    }
                    return defaultInstance;
                }
            }
        }

        internal BoBoIdentityStore(string directory)
        {
            storeDir = directory;
        }

        internal string StoreDirectory { get { return storeDir; } }

        internal X509Certificate2 Certificate
        {
            get { EnsureLoaded(); return nodeCert; }
        }

        internal string NodeId
        {
            get { EnsureLoaded(); return nodeId; }
        }

        internal string DeviceCode
        {
            get { EnsureLoaded(); return deviceCode; }
        }

        internal string FormattedDeviceCode
        {
            get { return FormatDeviceCode(DeviceCode); }
        }

        internal string DeviceName
        {
            get { EnsureLoaded(); return deviceName; }
        }

        internal string CertThumbprint
        {
            get { EnsureLoaded(); return certThumbprint; }
        }

        internal string CertDerBase64
        {
            get
            {
                EnsureLoaded();
                return nodeCert != null ? Convert.ToBase64String(nodeCert.RawData) : "";
            }
        }

        internal string SignAuthChallenge(string nonceHex, string hostThumbprint)
        {
            EnsureLoaded();
            if (nodeCert == null) return "";
            byte[] msg = BuildAuthChallengeBytes(nonceHex, certThumbprint, hostThumbprint);
            using (RSA rsa = nodeCert.GetRSAPrivateKey())
            {
                if (rsa == null) return "";
                byte[] sig = rsa.SignData(msg, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                return Convert.ToBase64String(sig);
            }
        }

        internal static bool VerifyAuthChallenge(
            string clientCertDerBase64,
            string expectedClientThumbprint,
            string nonceHex,
            string hostThumbprint,
            string signatureBase64)
        {
            if (string.IsNullOrWhiteSpace(clientCertDerBase64) ||
                string.IsNullOrWhiteSpace(expectedClientThumbprint) ||
                string.IsNullOrWhiteSpace(nonceHex) ||
                string.IsNullOrWhiteSpace(hostThumbprint) ||
                string.IsNullOrWhiteSpace(signatureBase64))
            {
                return false;
            }
            try
            {
                byte[] der = Convert.FromBase64String(clientCertDerBase64.Trim());
                if (der.Length < 128 || der.Length > 16384) return false;
                string actualThumb = ComputeCertSha256Hex(der);
                if (!FixedTimeEqualsHex(actualThumb, expectedClientThumbprint.Trim())) return false;

                byte[] sig = Convert.FromBase64String(signatureBase64.Trim());
                if (sig.Length < 64 || sig.Length > 1024) return false;

                byte[] msg = BuildAuthChallengeBytes(nonceHex, actualThumb, hostThumbprint);
                using (var cert = new X509Certificate2(der))
                using (RSA rsa = cert.GetRSAPublicKey())
                {
                    if (rsa == null) return false;
                    return rsa.VerifyData(msg, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }
            }
            catch
            {
                return false;
            }
        }

        internal static byte[] BuildAuthChallengeBytes(string nonceHex, string clientThumbprint, string hostThumbprint)
        {
            string canonical = "BOBO6-AUTH:" +
                (nonceHex ?? "").Trim().ToLowerInvariant() + "|" +
                (clientThumbprint ?? "").Trim().ToLowerInvariant() + "|" +
                (hostThumbprint ?? "").Trim().ToLowerInvariant();
            return Encoding.UTF8.GetBytes(canonical);
        }

        internal static string FormatDeviceCode(string code)
        {
            string digits = NormalizeDeviceCode(code);
            if (digits.Length != 9) return code ?? "";
            return digits.Substring(0, 3) + " " + digits.Substring(3, 3) + " " + digits.Substring(6, 3);
        }

        internal static string NormalizeDeviceCode(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            StringBuilder sb = new StringBuilder(9);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c >= '0' && c <= '9') sb.Append(c);
                else if (char.IsWhiteSpace(c) || c == '-') continue;
                else return "";
            }
            return sb.Length == 9 ? sb.ToString() : "";
        }

        internal static bool IsNineDigitCode(string raw)
        {
            return NormalizeDeviceCode(raw).Length == 9;
        }

        internal void EnsureLoaded()
        {
            lock (gate)
            {
                if (loaded && nodeCert != null) return;

                Directory.CreateDirectory(storeDir);
                string pfxPath = Path.Combine(storeDir, "node.pfx");
                string metaPath = Path.Combine(storeDir, "node.json");

                X509Certificate2 cert = null;
                if (File.Exists(pfxPath))
                {
                    byte[] raw = File.ReadAllBytes(pfxPath);
                    bool wasDpapi;
                    byte[] pfxBytes = UnprotectPfxBytes(raw, out wasDpapi);
                    cert = LoadPfxForSchannel(pfxBytes);
                    if (!cert.HasPrivateKey)
                    {
                        cert.Dispose();
                        throw new CryptographicException("BoBo 本机证书缺少私钥。");
                    }
                    if (!wasDpapi)
                    {
                        // Retain the existing identity if storage is temporarily unavailable.
                        try { WriteProtectedPfxFile(pfxPath, pfxBytes); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                        catch (CryptographicException) { }
                    }
                }
                else
                {
                    cert = CreateAndSaveSelfSignedCert(pfxPath);
                }

                nodeCert = cert;
                certThumbprint = ComputeCertSha256Hex(cert.RawData);
                deviceCode = DeriveNineDigitCode(certThumbprint);
                deviceName = Environment.MachineName;
                nodeId = certThumbprint.Substring(0, 24);

                // Read existing custom nodeId if saved
                if (File.Exists(metaPath))
                {
                    try
                    {
                        var ser = new JavaScriptSerializer();
                        var map = ser.DeserializeObject(File.ReadAllText(metaPath, Encoding.UTF8)) as Dictionary<string, object>;
                        if (map != null)
                        {
                            string savedId = ReadStr(map, "nodeId");
                            string savedThumb = ReadStr(map, "certThumbprint");
                            if (!string.IsNullOrEmpty(savedId) && string.Equals(savedThumb, certThumbprint, StringComparison.OrdinalIgnoreCase))
                            {
                                nodeId = savedId;
                            }
                        }
                    }
                    catch { }
                }

                SaveMetaUnlocked(metaPath);
                LoadPeersUnlocked();
                loaded = true;
            }
        }

        private static X509Certificate2 CreateAndSaveSelfSignedCert(string pfxPath)
        {
            using (RSA rsa = RSA.Create(2048))
            {
                var req = new CertificateRequest(
                    "CN=BoBoRemote-V6-Node",
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
                req.CertificateExtensions.Add(
                    new X509BasicConstraintsExtension(false, false, 0, false));
                req.CertificateExtensions.Add(
                    new X509KeyUsageExtension(
                        X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                        false));
                req.CertificateExtensions.Add(
                    new X509EnhancedKeyUsageExtension(
                        new OidCollection
                        {
                            new Oid("1.3.6.1.5.5.7.3.1"), // Server Authentication
                            new Oid("1.3.6.1.5.5.7.3.2")  // Client Authentication
                        },
                        false));

                DateTimeOffset now = DateTimeOffset.UtcNow;
                using (X509Certificate2 created = req.CreateSelfSigned(now.AddDays(-1), now.AddYears(20)))
                {
                    byte[] pfxBytes = created.Export(X509ContentType.Pfx, PfxPassword);
                    WriteProtectedPfxFile(pfxPath, pfxBytes);
                    return LoadPfxForSchannel(pfxBytes);
                }
            }
        }

        private static void WriteProtectedPfxFile(string pfxPath, byte[] pfxBytes)
        {
            byte[] encrypted = ProtectedData.Protect(pfxBytes, DpapiEntropy, DataProtectionScope.CurrentUser);
            byte[] payload = new byte[DpapiMagic.Length + encrypted.Length];
            Buffer.BlockCopy(DpapiMagic, 0, payload, 0, DpapiMagic.Length);
            Buffer.BlockCopy(encrypted, 0, payload, DpapiMagic.Length, encrypted.Length);
            string tmp = pfxPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(tmp, payload);
                if (File.Exists(pfxPath)) File.Replace(tmp, pfxPath, null);
                else File.Move(tmp, pfxPath);
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }

        private static byte[] UnprotectPfxBytes(byte[] raw, out bool wasDpapi)
        {
            wasDpapi = false;
            if (raw != null && raw.Length > DpapiMagic.Length &&
                raw[0] == DpapiMagic[0] && raw[1] == DpapiMagic[1] &&
                raw[2] == DpapiMagic[2] && raw[3] == DpapiMagic[3])
            {
                wasDpapi = true;
                byte[] cipher = new byte[raw.Length - DpapiMagic.Length];
                Buffer.BlockCopy(raw, DpapiMagic.Length, cipher, 0, cipher.Length);
                return ProtectedData.Unprotect(cipher, DpapiEntropy, DataProtectionScope.CurrentUser);
            }
            return raw;
        }

        private static X509Certificate2 LoadPfxForSchannel(byte[] pfxBytes)
        {
            try
            {
                return new X509Certificate2(
                    pfxBytes,
                    PfxPassword,
                    X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
            }
            catch
            {
                return new X509Certificate2(
                    pfxBytes,
                    PfxPassword,
                    X509KeyStorageFlags.Exportable);
            }
        }

        private void SaveMetaUnlocked(string metaPath)
        {
            try
            {
                var meta = new Dictionary<string, object>();
                meta["nodeId"] = nodeId;
                meta["deviceCode"] = deviceCode;
                meta["deviceName"] = deviceName;
                meta["certThumbprint"] = certThumbprint;
                var ser = new JavaScriptSerializer();
                File.WriteAllText(metaPath, ser.Serialize(meta), new UTF8Encoding(false));
            }
            catch { }
        }

        private string PeersFilePath
        {
            get { return Path.Combine(storeDir, "authorized-peers.json"); }
        }

        private void LoadPeersUnlocked()
        {
            peers = new List<BoBoAuthorizedPeer>();
            if (!File.Exists(PeersFilePath)) return;
            try
            {
                var ser = new JavaScriptSerializer();
                object parsed = ser.DeserializeObject(File.ReadAllText(PeersFilePath, Encoding.UTF8));
                object[] items = parsed as object[];
                if (items == null)
                {
                    var dict = parsed as Dictionary<string, object>;
                    if (dict != null && dict.ContainsKey("peers")) items = dict["peers"] as object[];
                }
                if (items == null) return;
                for (int i = 0; i < items.Length; i++)
                {
                    var m = items[i] as Dictionary<string, object>;
                    if (m == null) continue;
                    string nid = ReadStr(m, "nodeId");
                    string thumb = ReadStr(m, "certThumbprint");
                    if (string.IsNullOrEmpty(nid) || string.IsNullOrEmpty(thumb)) continue;
                    peers.Add(new BoBoAuthorizedPeer
                    {
                        NodeId = nid,
                        DeviceCode = ReadStr(m, "deviceCode"),
                        DeviceName = ReadStr(m, "deviceName"),
                        CertThumbprint = thumb,
                        LastHost = ReadStr(m, "lastHost"),
                        PairedAtUtc = ReadStr(m, "pairedAtUtc"),
                        LastConnectedUtc = ReadStr(m, "lastConnectedUtc")
                    });
                }
            }
            catch { }
        }

        private void SavePeersUnlocked()
        {
            try
            {
                Directory.CreateDirectory(storeDir);
                var list = new List<Dictionary<string, object>>();
                for (int i = 0; i < peers.Count; i++)
                {
                    BoBoAuthorizedPeer p = peers[i];
                    var m = new Dictionary<string, object>();
                    m["nodeId"] = p.NodeId ?? "";
                    m["deviceCode"] = p.DeviceCode ?? "";
                    m["deviceName"] = p.DeviceName ?? "";
                    m["certThumbprint"] = p.CertThumbprint ?? "";
                    m["lastHost"] = p.LastHost ?? "";
                    m["pairedAtUtc"] = p.PairedAtUtc ?? "";
                    m["lastConnectedUtc"] = p.LastConnectedUtc ?? "";
                    list.Add(m);
                }
                var root = new Dictionary<string, object>();
                root["peers"] = list;
                var ser = new JavaScriptSerializer();
                File.WriteAllText(PeersFilePath, ser.Serialize(root), new UTF8Encoding(false));
            }
            catch { }
        }

        internal BoBoAuthorizedPeer FindPeerByNodeId(string peerNodeId)
        {
            if (string.IsNullOrWhiteSpace(peerNodeId)) return null;
            EnsureLoaded();
            lock (gate)
            {
                for (int i = 0; i < peers.Count; i++)
                {
                    if (string.Equals(peers[i].NodeId, peerNodeId, StringComparison.OrdinalIgnoreCase))
                        return peers[i];
                }
                return null;
            }
        }

        internal bool IsPeerAuthorized(string peerNodeId, string peerCertThumbprint)
        {
            if (string.IsNullOrWhiteSpace(peerNodeId) || string.IsNullOrWhiteSpace(peerCertThumbprint)) return false;
            EnsureLoaded();
            lock (gate)
            {
                for (int i = 0; i < peers.Count; i++)
                {
                    if (string.Equals(peers[i].NodeId, peerNodeId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(peers[i].CertThumbprint, peerCertThumbprint, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        internal bool AuthorizeOrUpdatePeer(BoBoAuthorizedPeer peer)
        {
            if (peer == null || string.IsNullOrWhiteSpace(peer.NodeId) || !BoBoProtocol.IsValidHexSha256(peer.CertThumbprint)) return false;
            EnsureLoaded();
            lock (gate)
            {
                string nowIso = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                BoBoAuthorizedPeer existing = null;
                for (int i = 0; i < peers.Count; i++)
                {
                    if (string.Equals(peers[i].NodeId, peer.NodeId, StringComparison.OrdinalIgnoreCase))
                    {
                        existing = peers[i];
                        if (!FixedTimeEqualsHex(existing.CertThumbprint, peer.CertThumbprint)) return false;
                        break;
                    }
                    if (FixedTimeEqualsHex(peers[i].CertThumbprint, peer.CertThumbprint)) return false;
                }
                if (existing != null)
                {
                    if (!string.IsNullOrWhiteSpace(peer.DeviceCode)) existing.DeviceCode = peer.DeviceCode;
                    if (!string.IsNullOrWhiteSpace(peer.DeviceName)) existing.DeviceName = peer.DeviceName;
                    if (!string.IsNullOrWhiteSpace(peer.LastHost)) existing.LastHost = peer.LastHost;
                    existing.LastConnectedUtc = nowIso;
                }
                else
                {
                    peers.Insert(0, new BoBoAuthorizedPeer
                    {
                        NodeId = peer.NodeId,
                        DeviceCode = peer.DeviceCode ?? "",
                        DeviceName = string.IsNullOrWhiteSpace(peer.DeviceName) ? peer.NodeId : peer.DeviceName,
                        CertThumbprint = peer.CertThumbprint,
                        LastHost = peer.LastHost ?? "",
                        PairedAtUtc = string.IsNullOrWhiteSpace(peer.PairedAtUtc) ? nowIso : peer.PairedAtUtc,
                        LastConnectedUtc = nowIso
                    });
                }
                SavePeersUnlocked();
                return true;
            }
        }

        internal bool RevokePeer(string peerNodeId)
        {
            if (string.IsNullOrWhiteSpace(peerNodeId)) return false;
            EnsureLoaded();
            lock (gate)
            {
                int removed = peers.RemoveAll(delegate(BoBoAuthorizedPeer p)
                {
                    return string.Equals(p.NodeId, peerNodeId, StringComparison.OrdinalIgnoreCase);
                });
                if (removed > 0) SavePeersUnlocked();
                return removed > 0;
            }
        }

        internal List<BoBoAuthorizedPeer> ListAuthorizedPeers()
        {
            EnsureLoaded();
            lock (gate)
            {
                var copy = new List<BoBoAuthorizedPeer>(peers.Count);
                for (int i = 0; i < peers.Count; i++)
                {
                    BoBoAuthorizedPeer p = peers[i];
                    copy.Add(new BoBoAuthorizedPeer
                    {
                        NodeId = p.NodeId,
                        DeviceCode = p.DeviceCode,
                        DeviceName = p.DeviceName,
                        CertThumbprint = p.CertThumbprint,
                        LastHost = p.LastHost,
                        PairedAtUtc = p.PairedAtUtc,
                        LastConnectedUtc = p.LastConnectedUtc
                    });
                }
                return copy;
            }
        }

        internal BoBoAuthorizedPeer FindPeerByCodeOrHost(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;
            EnsureLoaded();
            string normalizedCode = NormalizeDeviceCode(query);
            string trimmed = query.Trim();
            lock (gate)
            {
                for (int i = 0; i < peers.Count; i++)
                {
                    BoBoAuthorizedPeer p = peers[i];
                    if (normalizedCode.Length == 9 && string.Equals(NormalizeDeviceCode(p.DeviceCode), normalizedCode, StringComparison.Ordinal))
                        return p;
                    if (string.Equals(p.LastHost, trimmed, StringComparison.OrdinalIgnoreCase))
                        return p;
                    if (string.Equals(p.DeviceName, trimmed, StringComparison.OrdinalIgnoreCase))
                        return p;
                }
                return null;
            }
        }

        internal static string GenerateFourDigitPin()
        {
            byte[] b = new byte[4];
            using (var rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(b);
            }
            uint v = BitConverter.ToUInt32(b, 0) % 10000u;
            return v.ToString("D4", CultureInfo.InvariantCulture);
        }

        internal static string GenerateRandomNonceHex(int byteCount)
        {
            byte[] b = new byte[byteCount];
            using (var rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(b);
            }
            return ToHexLower(b);
        }

        internal static string ComputePinProof(string pin, string nonceHex, string clientThumbprint, string hostThumbprint)
        {
            byte[] key = Encoding.UTF8.GetBytes("BOBO6-PIN:" + (pin ?? ""));
            byte[] msg = Encoding.UTF8.GetBytes(
                (nonceHex ?? "").ToLowerInvariant() + "|" +
                (clientThumbprint ?? "").ToLowerInvariant() + "|" +
                (hostThumbprint ?? "").ToLowerInvariant());
            using (var hmac = new HMACSHA256(key))
            {
                return ToHexLower(hmac.ComputeHash(msg));
            }
        }

        internal static bool FixedTimeEqualsHex(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                diff |= char.ToLowerInvariant(a[i]) ^ char.ToLowerInvariant(b[i]);
            }
            return diff == 0;
        }

        internal static string ComputeCertSha256Hex(byte[] rawCertData)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return ToHexLower(sha.ComputeHash(rawCertData));
            }
        }

        internal static string DeriveNineDigitCode(string thumbHex)
        {
            byte[] hash;
            using (SHA256 sha = SHA256.Create())
            {
                hash = sha.ComputeHash(Encoding.UTF8.GetBytes("BOBO6-CODE:" + (thumbHex ?? "")));
            }
            uint num = BitConverter.ToUInt32(hash, 0) % 900000000u + 100000000u;
            return num.ToString("D9", CultureInfo.InvariantCulture);
        }

        private static string ToHexLower(byte[] bytes)
        {
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }

        private static string ReadStr(Dictionary<string, object> map, string key)
        {
            object val;
            if (map != null && map.TryGetValue(key, out val) && val != null) return val.ToString();
            return "";
        }
    }
}
