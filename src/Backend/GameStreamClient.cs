using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace DualRemote
{
    internal sealed class GameStreamServerInfo
    {
        internal string Host = "";
        internal string HostName = "";
        internal string UniqueId = "";
        internal string AppVersion = "7.1.431.-1";
        internal string GfeVersion = "3.23.0.74";
        internal string SunshineVersion = "";
        internal int HttpPort = 47989;
        internal int HttpsPort = 47984;
        internal int ServerCodecModeSupport = 1;
        internal bool Paired;
        internal int CurrentGame;
        internal string State = "";
    }

    internal sealed class GameStreamAppInfo
    {
        internal int Id;
        internal string Title = "";
    }

    internal sealed class GameStreamLaunchResult
    {
        internal bool Success;
        internal string Host = "";
        internal string HostName = "";
        internal int HttpPort = 47989;
        internal int HttpsPort = 47984;
        internal string AppTitle = "Desktop";
        internal int AppId;
        internal string RtspUrl = "";
        internal string AppVersion = "7.1.431.-1";
        internal string GfeVersion = "3.23.0.74";
        internal int ServerCodecModeSupport = 1;
        internal string RiKeyHex = "";
        internal uint RiKeyId;
        internal string Error = "";
    }

    internal sealed class NativePairedHostRecord
    {
        internal string Host = "";
        internal string HostName = "";
        internal string ServerUniqueId = "";
        internal int HttpPort = 47989;
        internal int HttpsPort = 47984;
        internal string ServerCertPem = "";
        internal string PairedAtUtc = "";
    }

    /// <summary>
    /// BoBo Remote V5 原生内置 GameStream / Sunshine 协议客户端（纯 .NET 4.8，零外置 Moonlight 依赖）：
    /// - 本地管理 RSA-2048 客户端证书与 uniqueid（首次启动自动迁移旧版 Moonlight.ini 已配对身份，无需重新配对）；
    /// - 原生实现 5 阶段 4 位 PIN 配对握手（SHA-256 / AES-128-ECB / RSA-SHA256 / TLS 双向认证）；
    /// - 原生通过 HTTPS 双向认证拉取 /serverinfo、/applist、/launch、/resume 与 /cancel，
    ///   直接驱动嵌入式原生播放核 BoBoStreamPlayer.exe（moonlight-common-c + D3D11VA + WASAPI）。
    /// </summary>
    internal static class GameStreamClient
    {
        internal const int DefaultHttpPort = 47989;
        internal const int DefaultHttpsPort = 47984;
        private const string PfxPassword = "bobo-native-v5-client";
        private const int DefaultRequestTimeoutMs = 8000;
        private const int PairPhase1TimeoutMs = 120000;
        private const int MaxResponseBytes = 2 * 1024 * 1024;
        private static readonly object IdentityGate = new object();
        private static readonly object HostsGate = new object();

        private static X509Certificate2 cachedClientCert;
        private static string cachedClientCertPem;
        private static string cachedUniqueId;

        internal static string StoreDir
        {
            get { return Path.Combine(RecentStore.DirPath, "native-client"); }
        }

        internal static string ClientPfxPath
        {
            get { return Path.Combine(StoreDir, "client.pfx"); }
        }

        internal static string ClientCertPemPath
        {
            get { return Path.Combine(StoreDir, "client.crt"); }
        }

        internal static string UniqueIdPath
        {
            get { return Path.Combine(StoreDir, "uniqueid.txt"); }
        }

        internal static string HostsJsonPath
        {
            get { return Path.Combine(StoreDir, "hosts.json"); }
        }

        internal static bool EnsureIdentity(out X509Certificate2 cert, out string certPem, out string uniqueId, out string error)
        {
            return EnsureIdentityInDir(StoreDir, true, out cert, out certPem, out uniqueId, out error);
        }

        internal static bool EnsureIdentityInDir(string dir, bool allowLegacyMigration,
            out X509Certificate2 cert, out string certPem, out string uniqueId, out string error)
        {
            cert = null;
            certPem = null;
            uniqueId = null;
            error = null;

            bool isDefaultDir = string.Equals(
                Path.GetFullPath(dir).TrimEnd('\\'),
                Path.GetFullPath(StoreDir).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);

            lock (IdentityGate)
            {
                if (isDefaultDir && cachedClientCert != null && !string.IsNullOrEmpty(cachedClientCertPem) && !string.IsNullOrEmpty(cachedUniqueId))
                {
                    cert = cachedClientCert;
                    certPem = cachedClientCertPem;
                    uniqueId = cachedUniqueId;
                    return true;
                }

                try
                {
                    Directory.CreateDirectory(dir);
                    string pfxPath = Path.Combine(dir, "client.pfx");
                    string pemPath = Path.Combine(dir, "client.crt");
                    string uidPath = Path.Combine(dir, "uniqueid.txt");

                    if (File.Exists(pfxPath) && File.Exists(pemPath) && File.Exists(uidPath))
                    {
                        byte[] pfxBytes = File.ReadAllBytes(pfxPath);
                        X509Certificate2 loaded = new X509Certificate2(
                            pfxBytes,
                            PfxPassword,
                            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
                        string loadedPem = File.ReadAllText(pemPath, Encoding.ASCII).Replace("\r\n", "\n");
                        string loadedUid = File.ReadAllText(uidPath, Encoding.ASCII).Trim();
                        if (loaded.HasPrivateKey && !string.IsNullOrWhiteSpace(loadedPem) && loadedUid.Length >= 8)
                        {
                            cert = loaded;
                            certPem = loadedPem;
                            uniqueId = loadedUid;
                            if (isDefaultDir)
                            {
                                cachedClientCert = cert;
                                cachedClientCertPem = certPem;
                                cachedUniqueId = uniqueId;
                            }
                            return true;
                        }
                    }

                    // 首次初始化时优先尝试无感迁移旧版 Moonlight.ini 的客户端证书/私钥/已配对主机，免重新配对
                    if (allowLegacyMigration && TryMigrateFromLegacyMoonlight(dir, out cert, out certPem, out uniqueId))
                    {
                        if (isDefaultDir)
                        {
                            cachedClientCert = cert;
                            cachedClientCertPem = certPem;
                            cachedUniqueId = uniqueId;
                        }
                        return true;
                    }

                    // 生成全新的 2048 位 RSA 自签名客户端证书与 16 位 hex uniqueid
                    using (RSA rsa = RSA.Create(2048))
                    {
                        var req = new CertificateRequest(
                            "CN=NVIDIA GameStream Client",
                            rsa,
                            HashAlgorithmName.SHA256,
                            RSASignaturePadding.Pkcs1);
                        DateTimeOffset now = DateTimeOffset.UtcNow;
                        using (X509Certificate2 created = req.CreateSelfSigned(now.AddDays(-1), now.AddYears(20)))
                        {
                            byte[] pfxBytes = created.Export(X509ContentType.Pfx, PfxPassword);
                            string pem = FormatCertPem(created.RawData);
                            string uid = GenerateRandomHex(8);

                            File.WriteAllBytes(pfxPath, pfxBytes);
                            File.WriteAllText(pemPath, pem, Encoding.ASCII);
                            File.WriteAllText(uidPath, uid, Encoding.ASCII);

                            cert = new X509Certificate2(
                                pfxBytes,
                                PfxPassword,
                                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
                            certPem = pem;
                            uniqueId = uid;
                            if (isDefaultDir)
                            {
                                cachedClientCert = cert;
                                cachedClientCertPem = certPem;
                                cachedUniqueId = uniqueId;
                            }
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    error = "初始化内置串流客户端证书失败：" + ex.Message;
                    return false;
                }
            }
        }

        private static bool TryMigrateFromLegacyMoonlight(string targetDir,
            out X509Certificate2 cert, out string certPem, out string uniqueId)
        {
            cert = null;
            certPem = null;
            uniqueId = null;
            string[] candidates = new string[]
            {
                FoundationClient.SettingsPath,
                FoundationClient.BundledSettingsPath,
                FoundationClient.BaseSettingsPath
            };
            foreach (string iniPath in candidates)
            {
                try
                {
                    if (string.IsNullOrEmpty(iniPath) || !File.Exists(iniPath)) continue;
                    string text = File.ReadAllText(iniPath, Encoding.UTF8);
                    string rawCert, rawKey, rawUid;
                    if (!TryParseLegacyMoonlightIdentity(text, out rawCert, out rawKey, out rawUid))
                        continue;

                    X509Certificate2 loaded;
                    if (!TryCombinePemCertAndKey(rawCert, rawKey, out loaded))
                        continue;

                    byte[] pfxBytes = loaded.Export(X509ContentType.Pfx, PfxPassword);
                    string normalizedPem = FormatCertPem(loaded.RawData);
                    string uid = string.IsNullOrWhiteSpace(rawUid) ? GenerateRandomHex(8) : rawUid.Trim();

                    File.WriteAllBytes(Path.Combine(targetDir, "client.pfx"), pfxBytes);
                    File.WriteAllText(Path.Combine(targetDir, "client.crt"), normalizedPem, Encoding.ASCII);
                    File.WriteAllText(Path.Combine(targetDir, "uniqueid.txt"), uid, Encoding.ASCII);
                    loaded.Dispose();

                    cert = new X509Certificate2(
                        pfxBytes,
                        PfxPassword,
                        X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
                    certPem = normalizedPem;
                    uniqueId = uid;

                    // 顺带迁移旧版已配对主机列表
                    MigrateLegacyPairedHosts(text, Path.Combine(targetDir, "hosts.json"));
                    return true;
                }
                catch
                {
                }
            }
            return false;
        }

        internal static bool TryParseLegacyMoonlightIdentity(string iniText,
            out string certPem, out string keyPem, out string uniqueId)
        {
            certPem = null;
            keyPem = null;
            uniqueId = null;
            if (string.IsNullOrEmpty(iniText)) return false;

            bool inGeneral = false;
            string[] lines = iniText.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    string section = line.Substring(1, line.Length - 2).Trim();
                    inGeneral = string.Equals(section, "General", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inGeneral) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();
                if (k == "certificate")
                {
                    certPem = DecodeQtByteArray(v);
                }
                else if (k == "key")
                {
                    keyPem = DecodeQtByteArray(v);
                }
                else if (k == "uniqueid")
                {
                    uniqueId = v.Trim('"');
                }
            }

            return !string.IsNullOrEmpty(certPem)
                && certPem.IndexOf("BEGIN CERTIFICATE", StringComparison.Ordinal) >= 0
                && !string.IsNullOrEmpty(keyPem)
                && keyPem.IndexOf("PRIVATE KEY", StringComparison.Ordinal) >= 0;
        }

        internal static string DecodeQtByteArray(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                s = s.Substring(1, s.Length - 2);
            const string prefix = "@ByteArray(";
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && s.EndsWith(")", StringComparison.Ordinal))
                s = s.Substring(prefix.Length, s.Length - prefix.Length - 1);

            StringBuilder sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char n = s[i + 1];
                    if (n == 'n') { sb.Append('\n'); i += 2; continue; }
                    if (n == 'r') { sb.Append('\r'); i += 2; continue; }
                    if (n == 't') { sb.Append('\t'); i += 2; continue; }
                    if (n == '\\') { sb.Append('\\'); i += 2; continue; }
                    if (n == '"') { sb.Append('"'); i += 2; continue; }
                    if (n == 'x' && i + 3 < s.Length)
                    {
                        int h1 = HexVal(s[i + 2]);
                        int h2 = HexVal(s[i + 3]);
                        if (h1 >= 0 && h2 >= 0)
                        {
                            sb.Append((char)((h1 << 4) | h2));
                            i += 4;
                            continue;
                        }
                    }
                }
                sb.Append(s[i]);
                i++;
            }
            return sb.ToString();
        }

        private static void MigrateLegacyPairedHosts(string iniText, string hostsJsonPath)
        {
            try
            {
                if (string.IsNullOrEmpty(iniText)) return;
                bool inHosts = false;
                var map = new Dictionary<int, Dictionary<string, string>>();
                string[] lines = iniText.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string rawLine in lines)
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                    if (line[0] == '[' && line[line.Length - 1] == ']')
                    {
                        inHosts = string.Equals(line.Substring(1, line.Length - 2).Trim(), "hosts", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (!inHosts) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().Replace('/', '\\');
                    string val = line.Substring(eq + 1).Trim();
                    int slash = key.IndexOf('\\');
                    if (slash <= 0) continue;
                    int idx;
                    if (!int.TryParse(key.Substring(0, slash), NumberStyles.None, CultureInfo.InvariantCulture, out idx))
                        continue;
                    string sub = key.Substring(slash + 1).ToLowerInvariant();
                    Dictionary<string, string> rec;
                    if (!map.TryGetValue(idx, out rec))
                    {
                        rec = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        map[idx] = rec;
                    }
                    rec[sub] = val;
                }

                List<NativePairedHostRecord> existing = LoadPairedHostsFromPath(hostsJsonPath);
                bool changed = false;
                foreach (Dictionary<string, string> rec in map.Values)
                {
                    string srvCertRaw;
                    if (!rec.TryGetValue("srvcert", out srvCertRaw)) continue;
                    string srvCert = DecodeQtByteArray(srvCertRaw);
                    if (srvCert.IndexOf("BEGIN CERTIFICATE", StringComparison.Ordinal) < 0) continue;

                    string hostname = rec.ContainsKey("hostname") ? FoundationClient.UnescapeQtIniString(rec["hostname"]) : "";
                    string uuid = rec.ContainsKey("uuid") ? rec["uuid"].Trim('"') : "";
                    string[] addrKeys = new string[] { "manualaddress", "localaddress", "remoteaddress", "ipv6address" };
                    foreach (string ak in addrKeys)
                    {
                        string addr;
                        if (!rec.TryGetValue(ak, out addr) || string.IsNullOrWhiteSpace(addr)) continue;
                        string err;
                        string clean = Engines.NormalizeHost(addr.Trim('"'), out err);
                        if (string.IsNullOrEmpty(clean)) continue;
                        UpsertHostRecord(existing, new NativePairedHostRecord
                        {
                            Host = clean,
                            HostName = hostname,
                            ServerUniqueId = uuid,
                            HttpPort = DefaultHttpPort,
                            HttpsPort = DefaultHttpsPort,
                            ServerCertPem = srvCert,
                            PairedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                        });
                        changed = true;
                    }
                }
                if (changed) SavePairedHostsToPath(hostsJsonPath, existing);
            }
            catch
            {
            }
        }

        internal static bool TryCombinePemCertAndKey(string certPem, string keyPem, out X509Certificate2 combined)
        {
            combined = null;
            try
            {
                byte[] certDer = DecodePemBlock(certPem, "CERTIFICATE");
                if (certDer == null || certDer.Length < 64) return false;

                RSAParameters rsaParams;
                if (!TryParsePemRsaPrivateKey(keyPem, out rsaParams)) return false;

                using (var pubCert = new X509Certificate2(certDer))
                using (var rsa = new RSACryptoServiceProvider(2048, new CspParameters(24)))
                {
                    rsa.ImportParameters(rsaParams);
                    combined = RSACertificateExtensions.CopyWithPrivateKey(pubCert, rsa);
                    return combined != null && combined.HasPrivateKey;
                }
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryParsePemRsaPrivateKey(string keyPem, out RSAParameters rsaParams)
        {
            rsaParams = new RSAParameters();
            if (string.IsNullOrEmpty(keyPem)) return false;

            byte[] pkcs1 = DecodePemBlock(keyPem, "RSA PRIVATE KEY");
            if (pkcs1 == null)
            {
                byte[] pkcs8 = DecodePemBlock(keyPem, "PRIVATE KEY");
                if (pkcs8 == null) return false;
                pkcs1 = UnwrapPkcs8RsaPrivateKey(pkcs8);
                if (pkcs1 == null) return false;
            }

            try
            {
                Asn1Reader r = new Asn1Reader(pkcs1);
                Asn1Reader seq = r.ReadSequence();
                seq.ReadIntegerBytes(); // version
                byte[] n = TrimLeadingZero(seq.ReadIntegerBytes());
                byte[] e = TrimLeadingZero(seq.ReadIntegerBytes());
                byte[] d = TrimLeadingZero(seq.ReadIntegerBytes());
                byte[] p = TrimLeadingZero(seq.ReadIntegerBytes());
                byte[] q = TrimLeadingZero(seq.ReadIntegerBytes());
                byte[] dp = TrimLeadingZero(seq.ReadIntegerBytes());
                byte[] dq = TrimLeadingZero(seq.ReadIntegerBytes());
                byte[] iq = TrimLeadingZero(seq.ReadIntegerBytes());

                int modLen = n.Length;
                int halfLen = (modLen + 1) / 2;
                rsaParams.Modulus = PadLeft(n, modLen);
                rsaParams.Exponent = e;
                rsaParams.D = PadLeft(d, modLen);
                rsaParams.P = PadLeft(p, halfLen);
                rsaParams.Q = PadLeft(q, halfLen);
                rsaParams.DP = PadLeft(dp, halfLen);
                rsaParams.DQ = PadLeft(dq, halfLen);
                rsaParams.InverseQ = PadLeft(iq, halfLen);
                return modLen >= 128;
            }
            catch
            {
                return false;
            }
        }

        private static byte[] UnwrapPkcs8RsaPrivateKey(byte[] pkcs8Der)
        {
            try
            {
                Asn1Reader r = new Asn1Reader(pkcs8Der);
                Asn1Reader seq = r.ReadSequence();
                seq.ReadIntegerBytes(); // version = 0
                seq.SkipNext();         // AlgorithmIdentifier SEQUENCE
                return seq.ReadOctetString();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>提取 X.509 证书 DER 尾部的原始签名比特串（GameStream 配对校验规范必需）。</summary>
        internal static byte[] ExtractX509SignatureBytes(byte[] certDer)
        {
            if (certDer == null || certDer.Length < 32)
                throw new InvalidDataException("无效的 X.509 证书数据。");
            Asn1Reader root = new Asn1Reader(certDer);
            Asn1Reader certSeq = root.ReadSequence();
            certSeq.SkipNext(); // tbsCertificate
            certSeq.SkipNext(); // signatureAlgorithm
            return certSeq.ReadBitStringBytes();
        }

        internal static bool IsHostPaired(string host, string hostName, out bool paired, out string detail)
        {
            paired = false;
            detail = null;
            string hostErr;
            string cleanHost = Engines.NormalizeHost(host, out hostErr);
            if (cleanHost == null)
            {
                detail = hostErr;
                return false;
            }

            X509Certificate2 cert;
            string certPem, uid, idErr;
            if (!EnsureIdentity(out cert, out certPem, out uid, out idErr))
            {
                detail = idErr;
                return false;
            }

            // 1. 若目标主机在线，优先直接探测真实 HTTPS /serverinfo 的 PairStatus（权威状态）
            int httpsPort = DefaultHttpsPort;
            NativePairedHostRecord stored = FindPairedHost(cleanHost, hostName);
            if (stored != null && stored.HttpsPort > 0) httpsPort = stored.HttpsPort;

            GameStreamServerInfo liveInfo;
            string liveErr;
            if (TryGetServerInfoHttps(cleanHost, httpsPort, cert, uid, 2500, out liveInfo, out liveErr))
            {
                paired = liveInfo.Paired;
                if (paired)
                {
                    RememberPairedHost(new NativePairedHostRecord
                    {
                        Host = cleanHost,
                        HostName = !string.IsNullOrEmpty(liveInfo.HostName) ? liveInfo.HostName : (hostName ?? ""),
                        ServerUniqueId = liveInfo.UniqueId,
                        HttpPort = liveInfo.HttpPort,
                        HttpsPort = liveInfo.HttpsPort,
                        ServerCertPem = stored != null ? stored.ServerCertPem : "",
                        PairedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                    });
                }
                else
                {
                    detail = "该主机尚未授权当前内置客户端。";
                }
                return true;
            }

            // 2. 主机暂未响应或使用非默认端口时，再检查本地已保存/迁移的配对记录
            if (stored != null)
            {
                paired = true;
                return true;
            }

            detail = "本机尚未与该主机完成内置配对。";
            return true;
        }

        internal static bool TryGetServerInfoHttp(string host, int httpPort, string uniqueId, int timeoutMs,
            out GameStreamServerInfo info, out string error)
        {
            info = null;
            error = null;
            string url = BuildUrl("http", host, httpPort > 0 ? httpPort : DefaultHttpPort,
                "/serverinfo?uniqueid=" + Uri.EscapeDataString(uniqueId) + "&uuid=" + Guid.NewGuid().ToString("N"));
            string xml;
            if (!TryHttpGet(url, null, timeoutMs, CancellationToken.None, out xml, out error))
                return false;
            return TryParseServerInfoXml(xml, host, httpPort > 0 ? httpPort : DefaultHttpPort, out info, out error);
        }

        internal static bool TryGetServerInfoHttps(string host, int httpsPort, X509Certificate2 clientCert,
            string uniqueId, int timeoutMs, out GameStreamServerInfo info, out string error)
        {
            info = null;
            error = null;
            string url = BuildUrl("https", host, httpsPort > 0 ? httpsPort : DefaultHttpsPort,
                "/serverinfo?uniqueid=" + Uri.EscapeDataString(uniqueId) + "&uuid=" + Guid.NewGuid().ToString("N"));
            string xml;
            if (!TryHttpGet(url, clientCert, timeoutMs, CancellationToken.None, out xml, out error))
                return false;
            return TryParseServerInfoXml(xml, host, DefaultHttpPort, out info, out error);
        }

        internal static bool TryParseServerInfoXml(string xml, string host, int defaultHttpPort,
            out GameStreamServerInfo info, out string error)
        {
            info = null;
            error = null;
            if (string.IsNullOrWhiteSpace(xml))
            {
                error = "被控端返回了空的 /serverinfo 响应。";
                return false;
            }
            string statusCode = ReadXmlAttribute(xml, "root", "status_code");
            if (statusCode == "401")
            {
                // Sunshine 对未授权客户端在 HTTPS /serverinfo 返回 status_code="401"，明确表示尚未配对
                info = new GameStreamServerInfo
                {
                    Host = host ?? "",
                    HttpPort = defaultHttpPort > 0 ? defaultHttpPort : DefaultHttpPort,
                    HttpsPort = DefaultHttpsPort,
                    Paired = false
                };
                return true;
            }
            if (!string.IsNullOrEmpty(statusCode) && statusCode != "200")
            {
                string msg = ReadXmlAttribute(xml, "root", "status_message");
                error = "被控端拒绝了请求（status=" + statusCode + (string.IsNullOrEmpty(msg) ? "" : "，" + msg) + "）。";
                return false;
            }
            var parsed = new GameStreamServerInfo();
            parsed.Host = host ?? "";
            parsed.HostName = ReadXmlTag(xml, "hostname");
            parsed.UniqueId = ReadXmlTag(xml, "uniqueid");
            string appVer = ReadXmlTag(xml, "appversion");
            if (!string.IsNullOrEmpty(appVer)) parsed.AppVersion = appVer;
            string gfeVer = ReadXmlTag(xml, "GfeVersion");
            if (!string.IsNullOrEmpty(gfeVer)) parsed.GfeVersion = gfeVer;
            parsed.SunshineVersion = ReadXmlTag(xml, "SunshineVersion");
            parsed.HttpPort = ParseIntOrDefault(ReadXmlTag(xml, "ExternalPort"), defaultHttpPort > 0 ? defaultHttpPort : DefaultHttpPort);
            parsed.HttpsPort = ParseIntOrDefault(ReadXmlTag(xml, "HttpsPort"), DefaultHttpsPort);
            parsed.ServerCodecModeSupport = ParseIntOrDefault(ReadXmlTag(xml, "ServerCodecModeSupport"), 1);
            parsed.Paired = string.Equals(ReadXmlTag(xml, "PairStatus"), "1", StringComparison.Ordinal);
            parsed.CurrentGame = ParseIntOrDefault(ReadXmlTag(xml, "currentgame"), 0);
            parsed.State = ReadXmlTag(xml, "state");
            info = parsed;
            return true;
        }

        internal static List<GameStreamAppInfo> ParseAppListXml(string xml)
        {
            var list = new List<GameStreamAppInfo>();
            if (string.IsNullOrEmpty(xml)) return list;
            int pos = 0;
            while (pos < xml.Length)
            {
                int start = xml.IndexOf("<App>", pos, StringComparison.OrdinalIgnoreCase);
                if (start < 0) break;
                int end = xml.IndexOf("</App>", start + 5, StringComparison.OrdinalIgnoreCase);
                if (end < 0) break;
                string block = xml.Substring(start + 5, end - (start + 5));
                int id = ParseIntOrDefault(ReadXmlTag(block, "ID"), 0);
                string title = ReadXmlTag(block, "AppTitle").Trim();
                if (id != 0 && !string.IsNullOrEmpty(title))
                {
                    list.Add(new GameStreamAppInfo { Id = id, Title = title });
                }
                pos = end + 6;
            }
            return list;
        }

        /// <summary>
        /// 原生执行 5 阶段 GameStream 4 位 PIN 配对握手（无需调用任何外置 Moonlight 进程）。
        /// </summary>
        internal static bool PairHost(string host, string pin4, CancellationToken cancellationToken,
            Action<string> onProgress, out string pairedHostName, out string error)
        {
            pairedHostName = "";
            error = null;

            string hostErr;
            string cleanHost = Engines.NormalizeHost(host, out hostErr);
            if (cleanHost == null)
            {
                error = hostErr;
                return false;
            }
            if (!Engines.TryValidatePairPin(pin4, out error))
            {
                return false;
            }

            X509Certificate2 clientCert;
            string clientCertPem, uniqueId;
            if (!EnsureIdentity(out clientCert, out clientCertPem, out uniqueId, out error))
            {
                return false;
            }

            Report(onProgress, "正在连接被控端获取协议信息…");
            GameStreamServerInfo serverInfo;
            if (!TryGetServerInfoHttp(cleanHost, DefaultHttpPort, uniqueId, DefaultRequestTimeoutMs, out serverInfo, out error))
            {
                error = "无法连接到被控端 47989 端口：" + error;
                return false;
            }
            pairedHostName = serverInfo.HostName;

            int httpPort = DefaultHttpPort;
            int httpsPort = serverInfo.HttpsPort > 0 ? serverInfo.HttpsPort : DefaultHttpsPort;
            bool useSha256 = GetAppVersionMajor(serverInfo.AppVersion) >= 7;
            int hashLen = useSha256 ? 32 : 20;

            // 先测试是否其实已经配对成功
            GameStreamServerInfo httpsInfo;
            string httpsCheckErr;
            if (TryGetServerInfoHttps(cleanHost, httpsPort, clientCert, uniqueId, 3000, out httpsInfo, out httpsCheckErr)
                && httpsInfo.Paired)
            {
                RememberPairedHost(new NativePairedHostRecord
                {
                    Host = cleanHost,
                    HostName = !string.IsNullOrEmpty(httpsInfo.HostName) ? httpsInfo.HostName : serverInfo.HostName,
                    ServerUniqueId = httpsInfo.UniqueId,
                    HttpPort = httpPort,
                    HttpsPort = httpsPort,
                    PairedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                });
                return true;
            }

            byte[] salt = GenerateRandomBytes(16);
            byte[] aesKey = DerivePairingAesKey(salt, pin4, useSha256);
            string clientCertHex = BytesToHex(Encoding.ASCII.GetBytes(clientCertPem));

            // 阶段 1：提交客户端证书与盐值，阻塞等待被控端确认 4 位配对码
            Report(onProgress, "已发起内置授权申请，请在被控端确认 4 位配对码…");
            string p1Url = BuildUrl("http", cleanHost, httpPort,
                "/pair?uniqueid=" + Uri.EscapeDataString(uniqueId)
                + "&uuid=" + Guid.NewGuid().ToString("N")
                + "&devicename=roth&updateState=1&phrase=getservercert"
                + "&salt=" + BytesToHex(salt)
                + "&clientcert=" + clientCertHex);
            string p1Xml;
            if (!TryHttpGet(p1Url, null, PairPhase1TimeoutMs, cancellationToken, out p1Xml, out error))
            {
                TryUnpairBestEffort(cleanHost, httpPort, uniqueId);
                return false;
            }
            if (!IsXmlPairedTrue(p1Xml))
            {
                error = "被控端未接受配对请求或配对码错误，请核对 4 位码后重试。";
                TryUnpairBestEffort(cleanHost, httpPort, uniqueId);
                return false;
            }
            string plainCertHex = ReadXmlTag(p1Xml, "plaincert");
            if (string.IsNullOrEmpty(plainCertHex))
            {
                error = "被控端未返回服务端证书，配对中止。";
                TryUnpairBestEffort(cleanHost, httpPort, uniqueId);
                return false;
            }

            byte[] serverCertPemBytes = HexToBytes(plainCertHex);
            string serverCertPem = Encoding.ASCII.GetString(serverCertPemBytes);
            byte[] serverCertDer = DecodePemBlock(serverCertPem, "CERTIFICATE");
            if (serverCertDer == null)
            {
                error = "无法解析被控端返回的 X.509 证书。";
                TryUnpairBestEffort(cleanHost, httpPort, uniqueId);
                return false;
            }

            using (var serverCert = new X509Certificate2(serverCertDer))
            {
                byte[] serverCertSig = ExtractX509SignatureBytes(serverCert.RawData);
                byte[] clientCertSig = ExtractX509SignatureBytes(clientCert.RawData);

                // 阶段 2：发送加密后的随机 clientChallenge
                Report(onProgress, "正在进行加密身份挑战…");
                byte[] clientChallenge = GenerateRandomBytes(16);
                byte[] encChallenge = AesEcbEncrypt(aesKey, clientChallenge);
                string p2Url = BuildUrl("http", cleanHost, httpPort,
                    "/pair?uniqueid=" + Uri.EscapeDataString(uniqueId)
                    + "&uuid=" + Guid.NewGuid().ToString("N")
                    + "&devicename=roth&updateState=1&clientchallenge=" + BytesToHex(encChallenge));
                string p2Xml;
                if (!TryHttpGet(p2Url, null, DefaultRequestTimeoutMs, cancellationToken, out p2Xml, out error))
                {
                    TryUnpairBestEffort(cleanHost, httpPort, uniqueId);
                    return false;
                }
                if (!IsXmlPairedTrue(p2Xml))
                {
                    error = "阶段 2 配对码校验失败（请确认两端输入了相同的 4 位数字）。";
                    TryUnpairBestEffort(cleanHost, httpPort, uniqueId);
                    return false;
                }
                byte[] encServerResp = HexToBytes(ReadXmlTag(p2Xml, "challengeresponse"));
                byte[] decServerResp = AesEcbDecrypt(aesKey, encServerResp);
                if (decServerResp.Length < hashLen + 16)
                {
                    error = "被控端挑战响应长度异常。";
                    TryUnpairBestEffort(cleanHost, httpPort, uniqueId);
                    return false;
                }
                byte[] serverResponseHash = new byte[hashLen];
                byte[] serverChallenge = new byte[16];
                Buffer.BlockCopy(decServerResp, 0, serverResponseHash, 0, hashLen);
                Buffer.BlockCopy(decServerResp, hashLen, serverChallenge, 0, 16);

                // 阶段 3：计算客户端响应哈希并验证被控端签名
                byte[] clientSecret = GenerateRandomBytes(16);
                byte[] challengeRespHash = ComputeHash(useSha256, ConcatBytes(serverChallenge, clientCertSig, clientSecret));
                byte[] challengeRespBlock = PadToBlock16(challengeRespHash);
                byte[] encChallengeResp = AesEcbEncrypt(aesKey, challengeRespBlock);

                string p3Url = BuildUrl("http", cleanHost, httpPort,
                    "/pair?uniqueid=" + Uri.EscapeDataString(uniqueId)
                    + "&uuid=" + Guid.NewGuid().ToString("N")
                    + "&devicename=roth&updateState=1&serverchallengeresp=" + BytesToHex(encChallengeResp));
                string p3Xml;
                if (!TryHttpGet(p3Url, null, DefaultRequestTimeoutMs, cancellationToken, out p3Xml, out error))
                {
                    TryUnpairBestEffort(cleanHost, httpPort, uniqueId);
                    return false;
                }
                if (!IsXmlPairedTrue(p3Xml))
                {
                    error = "阶段 3 密钥验证失败（4 位配对码不匹配）。";
                    TryUnpairBestEffort(cleanHost, httpPort, uniqueId);
                    return false;
                }
                byte[] pairingSecret = HexToBytes(ReadXmlTag(p3Xml, "pairingsecret"));
                if (pairingSecret.Length <= 16)
                {
                    error = "被控端返回的配对签名无效。";
                    TryUnpairBestEffort(cleanHost, httpPort, uniqueId);
                    return false;
                }
                byte[] serverSecret = new byte[16];
                byte[] serverSig = new byte[pairingSecret.Length - 16];
                Buffer.BlockCopy(pairingSecret, 0, serverSecret, 0, 16);
                Buffer.BlockCopy(pairingSecret, 16, serverSig, 0, serverSig.Length);

                byte[] expectedServerHash = ComputeHash(useSha256, ConcatBytes(clientChallenge, serverCertSig, serverSecret));
                if (!ConstantTimeEquals(expectedServerHash, serverResponseHash))
                {
                    error = "被控端身份哈希校验不通过（疑似配对码不一致），已中止连接。";
                    TryUnpairBestEffort(cleanHost, httpPort, uniqueId);
                    return false;
                }
                if (!VerifyRsaSignature(serverCert, serverSecret, serverSig, useSha256))
                {
                    error = "被控端证书 RSA 签名验证失败，已中止连接。";
                    TryUnpairBestEffort(cleanHost, httpPort, uniqueId);
                    return false;
                }

                // 阶段 4：发送客户端密钥与 RSA 签名
                Report(onProgress, "正在完成双向证书互信…");
                byte[] clientSig = SignRsaData(clientCert, clientSecret, useSha256);
                byte[] clientPairingSecret = ConcatBytes(clientSecret, clientSig);
                string p4Url = BuildUrl("http", cleanHost, httpPort,
                    "/pair?uniqueid=" + Uri.EscapeDataString(uniqueId)
                    + "&uuid=" + Guid.NewGuid().ToString("N")
                    + "&devicename=roth&updateState=1&clientpairingsecret=" + BytesToHex(clientPairingSecret));
                string p4Xml;
                if (!TryHttpGet(p4Url, null, DefaultRequestTimeoutMs, cancellationToken, out p4Xml, out error))
                {
                    TryUnpairBestEffort(cleanHost, httpPort, uniqueId);
                    return false;
                }
                if (!IsXmlPairedTrue(p4Xml))
                {
                    error = "阶段 4 客户端签名未获通过。";
                    TryUnpairBestEffort(cleanHost, httpPort, uniqueId);
                    return false;
                }

                // 阶段 5：HTTPS 双向 TLS 最终确认
                string p5Url = BuildUrl("https", cleanHost, httpsPort,
                    "/pair?uniqueid=" + Uri.EscapeDataString(uniqueId)
                    + "&uuid=" + Guid.NewGuid().ToString("N")
                    + "&devicename=roth&updateState=1&phrase=pairchallenge");
                string p5Xml;
                if (!TryHttpGet(p5Url, clientCert, DefaultRequestTimeoutMs, cancellationToken, out p5Xml, out error))
                {
                    error = "阶段 5 HTTPS 双向验证失败：" + error;
                    return false;
                }
                if (!IsXmlPairedTrue(p5Xml))
                {
                    error = "HTTPS 双向授权确认未通过。";
                    return false;
                }

                RememberPairedHost(new NativePairedHostRecord
                {
                    Host = cleanHost,
                    HostName = serverInfo.HostName,
                    ServerUniqueId = serverInfo.UniqueId,
                    HttpPort = httpPort,
                    HttpsPort = httpsPort,
                    ServerCertPem = serverCertPem,
                    PairedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                });
                return true;
            }
        }

        /// <summary>
        /// 通过 HTTPS 双向认证拉取远程应用列表并启动/恢复远程桌面串流，返回 RTSP URL 与加密会话参数。
        /// </summary>
        internal static GameStreamLaunchResult LaunchDesktopSession(string host, int width, int height, int fps,
            bool hostAudio, CancellationToken cancellationToken)
        {
            var res = new GameStreamLaunchResult();
            string hostErr;
            string cleanHost = Engines.NormalizeHost(host, out hostErr);
            if (cleanHost == null)
            {
                res.Error = hostErr;
                return res;
            }
            res.Host = cleanHost;

            X509Certificate2 clientCert;
            string clientCertPem, uniqueId, idErr;
            if (!EnsureIdentity(out clientCert, out clientCertPem, out uniqueId, out idErr))
            {
                res.Error = idErr;
                return res;
            }

            int httpPort = DefaultHttpPort;
            int httpsPort = DefaultHttpsPort;
            NativePairedHostRecord stored = FindPairedHost(cleanHost, null);
            if (stored != null)
            {
                if (stored.HttpPort > 0) httpPort = stored.HttpPort;
                if (stored.HttpsPort > 0) httpsPort = stored.HttpsPort;
            }

            // 先通过 HTTP 读取最新 HttpsPort（兼容动态端口）
            GameStreamServerInfo httpInfo;
            string httpErr;
            if (TryGetServerInfoHttp(cleanHost, httpPort, uniqueId, 4000, out httpInfo, out httpErr))
            {
                if (httpInfo.HttpsPort > 0) httpsPort = httpInfo.HttpsPort;
            }

            GameStreamServerInfo serverInfo;
            string infoErr;
            if (!TryGetServerInfoHttps(cleanHost, httpsPort, clientCert, uniqueId, DefaultRequestTimeoutMs, out serverInfo, out infoErr))
            {
                res.Error = "无法建立 HTTPS 加密连接到 " + cleanHost + ":" + httpsPort + "：" + infoErr;
                return res;
            }
            if (!serverInfo.Paired)
            {
                res.Error = "NOT_PAIRED";
                return res;
            }

            res.HostName = serverInfo.HostName;
            res.HttpPort = httpPort;
            res.HttpsPort = httpsPort;
            res.AppVersion = serverInfo.AppVersion;
            res.GfeVersion = serverInfo.GfeVersion;
            res.ServerCodecModeSupport = serverInfo.ServerCodecModeSupport;

            // 拉取应用列表并自动匹配桌面应用（Desktop / 桌面）
            string appListUrl = BuildUrl("https", cleanHost, httpsPort,
                "/applist?uniqueid=" + Uri.EscapeDataString(uniqueId) + "&uuid=" + Guid.NewGuid().ToString("N"));
            string appListXml, appListErr;
            if (!TryHttpGet(appListUrl, clientCert, DefaultRequestTimeoutMs, cancellationToken, out appListXml, out appListErr))
            {
                res.Error = "获取被控端应用列表失败：" + appListErr;
                return res;
            }
            List<GameStreamAppInfo> apps = ParseAppListXml(appListXml);
            if (apps.Count == 0)
            {
                res.Error = "被控端 Sunshine 未配置任何桌面应用。";
                return res;
            }

            List<string> titles = new List<string>(apps.Count);
            foreach (GameStreamAppInfo a in apps) titles.Add(a.Title);
            string bestTitle = Engines.SelectBestDesktopAppName(titles);
            GameStreamAppInfo chosen = apps[0];
            foreach (GameStreamAppInfo a in apps)
            {
                if (string.Equals(a.Title, bestTitle, StringComparison.OrdinalIgnoreCase))
                {
                    chosen = a;
                    break;
                }
            }
            res.AppId = chosen.Id;
            res.AppTitle = chosen.Title;

            // 若被控端有其他残留应用在跑，先干净关闭，避免会话冲突
            if (serverInfo.CurrentGame != 0 && serverInfo.CurrentGame != chosen.Id)
            {
                CancelSession(cleanHost, httpsPort);
            }

            byte[] riKeyBytes = GenerateRandomBytes(16);
            byte[] riKeyIdBytes = GenerateRandomBytes(4);
            uint riKeyId = ((uint)riKeyIdBytes[0] << 24)
                         | ((uint)riKeyIdBytes[1] << 16)
                         | ((uint)riKeyIdBytes[2] << 8)
                         | (uint)riKeyIdBytes[3];
            string riKeyHex = BytesToHex(riKeyBytes);
            res.RiKeyHex = riKeyHex;
            res.RiKeyId = riKeyId;

            string verb = (serverInfo.CurrentGame == chosen.Id) ? "/resume" : "/launch";
            string query = verb
                + "?uniqueid=" + Uri.EscapeDataString(uniqueId)
                + "&uuid=" + Guid.NewGuid().ToString("N")
                + "&appid=" + chosen.Id.ToString(CultureInfo.InvariantCulture)
                + "&mode=" + width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture) + "x" + fps.ToString(CultureInfo.InvariantCulture)
                + "&additionalStates=1&sops=0"
                + "&rikey=" + riKeyHex
                + "&rikeyid=" + riKeyId.ToString(CultureInfo.InvariantCulture)
                + "&localAudioPlayMode=" + (hostAudio ? "1" : "0")
                + "&surroundAudioInfo=196610"
                + "&remoteControllersBitmap=0&gcmap=0&gcpersist=0&corever=1";

            string launchUrl = BuildUrl("https", cleanHost, httpsPort, query);
            string launchXml, launchErr;
            if (!TryHttpGet(launchUrl, clientCert, 15000, cancellationToken, out launchXml, out launchErr))
            {
                res.Error = "请求启动远程桌面会话失败：" + launchErr;
                return res;
            }

            string statusCode = ReadXmlAttribute(launchXml, "root", "status_code");
            if (!string.IsNullOrEmpty(statusCode) && statusCode != "200")
            {
                string statusMsg = ReadXmlAttribute(launchXml, "root", "status_message");
                // 若 /resume 失败则尝试先 /cancel 再 /launch
                if (verb == "/resume")
                {
                    CancelSession(cleanHost, httpsPort);
                    string retryUrl = BuildUrl("https", cleanHost, httpsPort, "/launch" + query.Substring(verb.Length));
                    if (!TryHttpGet(retryUrl, clientCert, 15000, cancellationToken, out launchXml, out launchErr))
                    {
                        res.Error = "重新启动远程桌面会话失败：" + launchErr;
                        return res;
                    }
                    statusCode = ReadXmlAttribute(launchXml, "root", "status_code");
                    statusMsg = ReadXmlAttribute(launchXml, "root", "status_message");
                }
                if (!string.IsNullOrEmpty(statusCode) && statusCode != "200")
                {
                    res.Error = "被控端启动桌面失败：" + (string.IsNullOrEmpty(statusMsg) ? ("status=" + statusCode) : statusMsg);
                    return res;
                }
            }

            string rtspUrl = ReadXmlTag(launchXml, "sessionUrl0");
            if (string.IsNullOrEmpty(rtspUrl))
            {
                rtspUrl = "rtsp://" + FormatHostForUrl(cleanHost) + ":48010";
            }
            res.RtspUrl = rtspUrl;
            res.Success = true;
            return res;
        }

        /// <summary>向远程 Sunshine 发送 HTTPS /cancel，确保主控端退出时被控端立即终止会话。</summary>
        internal static bool CancelSession(string host, int httpsPort)
        {
            try
            {
                string hostErr;
                string cleanHost = Engines.NormalizeHost(host, out hostErr);
                if (cleanHost == null) return false;
                X509Certificate2 clientCert;
                string certPem, uniqueId, idErr;
                if (!EnsureIdentity(out clientCert, out certPem, out uniqueId, out idErr))
                    return false;
                int port = httpsPort > 0 ? httpsPort : DefaultHttpsPort;
                string url = BuildUrl("https", cleanHost, port,
                    "/cancel?uniqueid=" + Uri.EscapeDataString(uniqueId) + "&uuid=" + Guid.NewGuid().ToString("N"));
                string xml, err;
                return TryHttpGet(url, clientCert, 4000, CancellationToken.None, out xml, out err);
            }
            catch
            {
                return false;
            }
        }

        private static void TryUnpairBestEffort(string host, int httpPort, string uniqueId)
        {
            try
            {
                string url = BuildUrl("http", host, httpPort,
                    "/unpair?uniqueid=" + Uri.EscapeDataString(uniqueId) + "&uuid=" + Guid.NewGuid().ToString("N"));
                string xml, err;
                TryHttpGet(url, null, 2000, CancellationToken.None, out xml, out err);
            }
            catch { }
        }

        internal static NativePairedHostRecord FindPairedHost(string host, string hostName)
        {
            List<NativePairedHostRecord> list = LoadPairedHostsFromPath(HostsJsonPath);
            foreach (NativePairedHostRecord rec in list)
            {
                if (!string.IsNullOrEmpty(host) && string.Equals(rec.Host, host, StringComparison.OrdinalIgnoreCase))
                    return rec;
            }
            if (!string.IsNullOrEmpty(hostName))
            {
                foreach (NativePairedHostRecord rec in list)
                {
                    if (!string.IsNullOrEmpty(rec.HostName) && string.Equals(rec.HostName, hostName, StringComparison.OrdinalIgnoreCase))
                        return rec;
                }
            }
            return null;
        }

        internal static void RememberPairedHost(NativePairedHostRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.Host)) return;
            lock (HostsGate)
            {
                Directory.CreateDirectory(StoreDir);
                List<NativePairedHostRecord> list = LoadPairedHostsFromPath(HostsJsonPath);
                UpsertHostRecord(list, record);
                SavePairedHostsToPath(HostsJsonPath, list);
            }
        }

        private static void UpsertHostRecord(List<NativePairedHostRecord> list, NativePairedHostRecord record)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].Host, record.Host, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(record.ServerCertPem))
                        record.ServerCertPem = list[i].ServerCertPem;
                    list[i] = record;
                    return;
                }
            }
            list.Add(record);
        }

        private static List<NativePairedHostRecord> LoadPairedHostsFromPath(string path)
        {
            var result = new List<NativePairedHostRecord>();
            try
            {
                if (!File.Exists(path)) return result;
                string json = File.ReadAllText(path, Encoding.UTF8);
                var ser = new JavaScriptSerializer();
                var arr = ser.DeserializeObject(json) as IEnumerable;
                if (arr == null) return result;
                foreach (object item in arr)
                {
                    var dict = item as IDictionary<string, object>;
                    if (dict == null) continue;
                    var rec = new NativePairedHostRecord
                    {
                        Host = ReadDictStr(dict, "host"),
                        HostName = ReadDictStr(dict, "hostname"),
                        ServerUniqueId = ReadDictStr(dict, "serverUniqueId"),
                        HttpPort = ReadDictInt(dict, "httpPort", DefaultHttpPort),
                        HttpsPort = ReadDictInt(dict, "httpsPort", DefaultHttpsPort),
                        ServerCertPem = ReadDictStr(dict, "serverCertPem"),
                        PairedAtUtc = ReadDictStr(dict, "pairedAtUtc")
                    };
                    if (!string.IsNullOrEmpty(rec.Host)) result.Add(rec);
                }
            }
            catch { }
            return result;
        }

        private static void SavePairedHostsToPath(string path, List<NativePairedHostRecord> list)
        {
            try
            {
                var payload = new List<Dictionary<string, object>>(list.Count);
                foreach (NativePairedHostRecord rec in list)
                {
                    var d = new Dictionary<string, object>();
                    d["host"] = rec.Host ?? "";
                    d["hostname"] = rec.HostName ?? "";
                    d["serverUniqueId"] = rec.ServerUniqueId ?? "";
                    d["httpPort"] = rec.HttpPort > 0 ? rec.HttpPort : DefaultHttpPort;
                    d["httpsPort"] = rec.HttpsPort > 0 ? rec.HttpsPort : DefaultHttpsPort;
                    d["serverCertPem"] = rec.ServerCertPem ?? "";
                    d["pairedAtUtc"] = rec.PairedAtUtc ?? "";
                    payload.Add(d);
                }
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var ser = new JavaScriptSerializer();
                File.WriteAllText(path, ser.Serialize(payload), new UTF8Encoding(false));
            }
            catch { }
        }

        private static bool TryHttpGet(string url, X509Certificate2 clientCert, int timeoutMs,
            CancellationToken cancellationToken, out string body, out string error)
        {
            body = null;
            error = null;
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.Proxy = null; // 直连局域网 / Tailscale / 本机，禁止被系统代理劫持
                req.KeepAlive = false;
                req.UserAgent = "BoBoRemote/5.0";
                if (clientCert != null)
                {
                    req.ClientCertificates.Add(clientCert);
                    req.ServerCertificateValidationCallback = delegate(
                        object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
                    {
                        return certificate != null;
                    };
                }

                using (cancellationToken.Register(delegate { try { req.Abort(); } catch { } }))
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream stream = resp.GetResponseStream())
                {
                    if (stream == null)
                    {
                        body = "";
                        return true;
                    }
                    using (var ms = new MemoryStream())
                    {
                        byte[] buf = new byte[8192];
                        int n;
                        int total = 0;
                        while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                        {
                            total += n;
                            if (total > MaxResponseBytes)
                            {
                                error = "被控端响应超出大小限制。";
                                return false;
                            }
                            ms.Write(buf, 0, n);
                        }
                        body = Encoding.UTF8.GetString(ms.ToArray());
                        return true;
                    }
                }
            }
            catch (WebException wex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    error = "操作已取消。";
                    return false;
                }
                if (wex.Response != null)
                {
                    try
                    {
                        using (var resp = (HttpWebResponse)wex.Response)
                        using (var s = resp.GetResponseStream())
                        using (var sr = new StreamReader(s, Encoding.UTF8))
                        {
                            string errBody = sr.ReadToEnd();
                            if (!string.IsNullOrWhiteSpace(errBody) && errBody.IndexOf("<root", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                body = errBody;
                                return true;
                            }
                        }
                    }
                    catch { }
                }
                error = wex.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = cancellationToken.IsCancellationRequested ? "操作已取消。" : ex.Message;
                return false;
            }
        }

        private static string BuildUrl(string scheme, string host, int port, string pathAndQuery)
        {
            return scheme + "://" + FormatHostForUrl(host) + ":" + port.ToString(CultureInfo.InvariantCulture) + pathAndQuery;
        }

        private static string FormatHostForUrl(string host)
        {
            if (string.IsNullOrEmpty(host)) return "";
            if (host.IndexOf(':') >= 0 && !host.StartsWith("[", StringComparison.Ordinal))
                return "[" + host + "]";
            return host;
        }

        private static void Report(Action<string> cb, string text)
        {
            if (cb != null)
            {
                try { cb(text); } catch { }
            }
        }

        // ---- Crypto helpers ----

        internal static byte[] DerivePairingAesKey(byte[] salt, string pin4, bool useSha256)
        {
            byte[] pinBytes = Encoding.ASCII.GetBytes(pin4);
            byte[] fullHash = ComputeHash(useSha256, ConcatBytes(salt, pinBytes));
            byte[] key = new byte[16];
            Buffer.BlockCopy(fullHash, 0, key, 0, 16);
            return key;
        }

        internal static byte[] ComputeHash(bool useSha256, byte[] data)
        {
            if (useSha256)
            {
                using (var sha = SHA256.Create()) return sha.ComputeHash(data);
            }
            using (var sha1 = SHA1.Create()) return sha1.ComputeHash(data);
        }

        internal static byte[] AesEcbEncrypt(byte[] key16, byte[] plainBlocks)
        {
            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                aes.KeySize = 128;
                aes.Key = key16;
                using (var enc = aes.CreateEncryptor())
                {
                    return enc.TransformFinalBlock(plainBlocks, 0, plainBlocks.Length);
                }
            }
        }

        internal static byte[] AesEcbDecrypt(byte[] key16, byte[] cipherBlocks)
        {
            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                aes.KeySize = 128;
                aes.Key = key16;
                using (var dec = aes.CreateDecryptor())
                {
                    return dec.TransformFinalBlock(cipherBlocks, 0, cipherBlocks.Length);
                }
            }
        }

        private static byte[] SignRsaData(X509Certificate2 certWithPrivateKey, byte[] data, bool useSha256)
        {
            using (RSA rsa = RSACertificateExtensions.GetRSAPrivateKey(certWithPrivateKey))
            {
                if (rsa == null) throw new InvalidOperationException("客户端证书缺少 RSA 私钥。");
                return rsa.SignData(data, useSha256 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
            }
        }

        private static bool VerifyRsaSignature(X509Certificate2 cert, byte[] data, byte[] signature, bool useSha256)
        {
            using (RSA rsa = RSACertificateExtensions.GetRSAPublicKey(cert))
            {
                if (rsa == null) return false;
                return rsa.VerifyData(data, signature, useSha256 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
            }
        }

        private static byte[] PadToBlock16(byte[] data)
        {
            int rem = data.Length % 16;
            if (rem == 0) return data;
            byte[] padded = new byte[data.Length + (16 - rem)];
            Buffer.BlockCopy(data, 0, padded, 0, data.Length);
            return padded;
        }

        private static byte[] ConcatBytes(params byte[][] arrays)
        {
            int total = 0;
            foreach (byte[] a in arrays) if (a != null) total += a.Length;
            byte[] result = new byte[total];
            int offset = 0;
            foreach (byte[] a in arrays)
            {
                if (a == null || a.Length == 0) continue;
                Buffer.BlockCopy(a, 0, result, offset, a.Length);
                offset += a.Length;
            }
            return result;
        }

        private static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= (a[i] ^ b[i]);
            return diff == 0;
        }

        internal static byte[] GenerateRandomBytes(int length)
        {
            byte[] buf = new byte[length];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(buf);
            }
            return buf;
        }

        internal static string GenerateRandomHex(int byteLength)
        {
            return BytesToHex(GenerateRandomBytes(byteLength));
        }

        internal static string BytesToHex(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";
            char[] c = new char[bytes.Length * 2];
            const string hex = "0123456789abcdef";
            for (int i = 0; i < bytes.Length; i++)
            {
                c[i * 2] = hex[(bytes[i] >> 4) & 0xF];
                c[i * 2 + 1] = hex[bytes[i] & 0xF];
            }
            return new string(c);
        }

        internal static byte[] HexToBytes(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return new byte[0];
            string clean = hex.Trim();
            if ((clean.Length & 1) != 0) throw new FormatException("无效的十六进制字符串长度。");
            byte[] bytes = new byte[clean.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                int hi = HexVal(clean[i * 2]);
                int lo = HexVal(clean[i * 2 + 1]);
                if (hi < 0 || lo < 0) throw new FormatException("包含非法十六进制字符。");
                bytes[i] = (byte)((hi << 4) | lo);
            }
            return bytes;
        }

        private static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        internal static string FormatCertPem(byte[] certDer)
        {
            string b64 = Convert.ToBase64String(certDer);
            StringBuilder sb = new StringBuilder();
            sb.Append("-----BEGIN CERTIFICATE-----\n");
            for (int i = 0; i < b64.Length; i += 64)
            {
                int len = Math.Min(64, b64.Length - i);
                sb.Append(b64, i, len).Append('\n');
            }
            sb.Append("-----END CERTIFICATE-----\n");
            return sb.ToString();
        }

        internal static byte[] DecodePemBlock(string pem, string label)
        {
            if (string.IsNullOrEmpty(pem)) return null;
            string begin = "-----BEGIN " + label + "-----";
            string end = "-----END " + label + "-----";
            int s = pem.IndexOf(begin, StringComparison.Ordinal);
            if (s < 0) return null;
            s += begin.Length;
            int e = pem.IndexOf(end, s, StringComparison.Ordinal);
            if (e < 0) return null;
            StringBuilder b64 = new StringBuilder(e - s);
            for (int i = s; i < e; i++)
            {
                char ch = pem[i];
                if (!char.IsWhiteSpace(ch)) b64.Append(ch);
            }
            return Convert.FromBase64String(b64.ToString());
        }

        // ---- XML & Parsing helpers (zero DTD/XXE) ----

        internal static string ReadXmlTag(string xml, string tagName)
        {
            if (string.IsNullOrEmpty(xml) || string.IsNullOrEmpty(tagName)) return "";
            string open = "<" + tagName + ">";
            string close = "</" + tagName + ">";
            int s = xml.IndexOf(open, StringComparison.OrdinalIgnoreCase);
            if (s < 0) return "";
            s += open.Length;
            int e = xml.IndexOf(close, s, StringComparison.OrdinalIgnoreCase);
            if (e < 0) return "";
            return DecodeXmlEntities(xml.Substring(s, e - s));
        }

        internal static string ReadXmlAttribute(string xml, string tagName, string attrName)
        {
            if (string.IsNullOrEmpty(xml)) return "";
            string openPrefix = "<" + tagName;
            int tagStart = xml.IndexOf(openPrefix, StringComparison.OrdinalIgnoreCase);
            if (tagStart < 0) return "";
            int tagEnd = xml.IndexOf('>', tagStart);
            if (tagEnd < 0) return "";
            string header = xml.Substring(tagStart, tagEnd - tagStart);
            string needle = attrName + "=\"";
            int attrStart = header.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (attrStart < 0) return "";
            attrStart += needle.Length;
            int attrEnd = header.IndexOf('"', attrStart);
            if (attrEnd < 0) return "";
            return DecodeXmlEntities(header.Substring(attrStart, attrEnd - attrStart));
        }

        private static string DecodeXmlEntities(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('&') < 0) return s ?? "";
            return s.Replace("&lt;", "<")
                    .Replace("&gt;", ">")
                    .Replace("&quot;", "\"")
                    .Replace("&apos;", "'")
                    .Replace("&amp;", "&");
        }

        private static bool IsXmlPairedTrue(string xml)
        {
            return string.Equals(ReadXmlTag(xml, "paired").Trim(), "1", StringComparison.Ordinal);
        }

        private static int GetAppVersionMajor(string appVersion)
        {
            if (string.IsNullOrWhiteSpace(appVersion)) return 7;
            int dot = appVersion.IndexOf('.');
            string head = dot > 0 ? appVersion.Substring(0, dot) : appVersion;
            int major;
            return int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out major) ? major : 7;
        }

        private static int ParseIntOrDefault(string text, int fallback)
        {
            int v;
            return int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        private static string ReadDictStr(IDictionary<string, object> dict, string key)
        {
            object v;
            return dict.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
        }

        private static int ReadDictInt(IDictionary<string, object> dict, string key, int fallback)
        {
            object v;
            if (!dict.TryGetValue(key, out v) || v == null) return fallback;
            int n;
            return int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : fallback;
        }

        private static byte[] TrimLeadingZero(byte[] bytes)
        {
            int idx = 0;
            while (idx < bytes.Length - 1 && bytes[idx] == 0) idx++;
            if (idx == 0) return bytes;
            byte[] res = new byte[bytes.Length - idx];
            Buffer.BlockCopy(bytes, idx, res, 0, res.Length);
            return res;
        }

        private static byte[] PadLeft(byte[] bytes, int targetLen)
        {
            if (bytes.Length == targetLen) return bytes;
            if (bytes.Length > targetLen)
            {
                byte[] trimmed = new byte[targetLen];
                Buffer.BlockCopy(bytes, bytes.Length - targetLen, trimmed, 0, targetLen);
                return trimmed;
            }
            byte[] padded = new byte[targetLen];
            Buffer.BlockCopy(bytes, 0, padded, targetLen - bytes.Length, bytes.Length);
            return padded;
        }

        // ---- Minimal ASN.1 DER Reader ----

        private sealed class Asn1Reader
        {
            private readonly byte[] data;
            private int offset;
            private readonly int end;

            internal Asn1Reader(byte[] data) : this(data, 0, data.Length) { }

            private Asn1Reader(byte[] data, int offset, int end)
            {
                this.data = data;
                this.offset = offset;
                this.end = end;
            }

            internal Asn1Reader ReadSequence()
            {
                int len = ReadTagAndLength(0x30);
                int childStart = offset;
                offset += len;
                return new Asn1Reader(data, childStart, childStart + len);
            }

            internal byte[] ReadIntegerBytes()
            {
                int len = ReadTagAndLength(0x02);
                byte[] buf = new byte[len];
                Buffer.BlockCopy(data, offset, buf, 0, len);
                offset += len;
                return buf;
            }

            internal byte[] ReadOctetString()
            {
                int len = ReadTagAndLength(0x04);
                byte[] buf = new byte[len];
                Buffer.BlockCopy(data, offset, buf, 0, len);
                offset += len;
                return buf;
            }

            internal byte[] ReadBitStringBytes()
            {
                int len = ReadTagAndLength(0x03);
                if (len < 1) throw new InvalidDataException("ASN.1 BIT STRING 长度无效。");
                int unusedBits = data[offset++];
                if (unusedBits != 0) throw new InvalidDataException("不支持非零 unusedBits 的签名位串。");
                byte[] buf = new byte[len - 1];
                Buffer.BlockCopy(data, offset, buf, 0, buf.Length);
                offset += buf.Length;
                return buf;
            }

            internal void SkipNext()
            {
                if (offset >= end) throw new InvalidDataException("ASN.1 数据提前结束。");
                offset++; // tag
                int len = ReadLength();
                offset += len;
                if (offset > end) throw new InvalidDataException("ASN.1 元素越界。");
            }

            private int ReadTagAndLength(byte expectedTag)
            {
                if (offset >= end) throw new InvalidDataException("ASN.1 数据提前结束。");
                byte tag = data[offset++];
                if (tag != expectedTag)
                    throw new InvalidDataException("ASN.1 标签不匹配：期望 0x" + expectedTag.ToString("X2") + "，实际 0x" + tag.ToString("X2"));
                int len = ReadLength();
                if (offset + len > end) throw new InvalidDataException("ASN.1 长度越界。");
                return len;
            }

            private int ReadLength()
            {
                if (offset >= end) throw new InvalidDataException("ASN.1 长度字节缺失。");
                int first = data[offset++];
                if ((first & 0x80) == 0) return first;
                int numBytes = first & 0x7F;
                if (numBytes == 0 || numBytes > 4 || offset + numBytes > end)
                    throw new InvalidDataException("ASN.1 扩展长度无效。");
                int len = 0;
                for (int i = 0; i < numBytes; i++)
                {
                    len = (len << 8) | data[offset++];
                }
                return len;
            }
        }
    }
}
