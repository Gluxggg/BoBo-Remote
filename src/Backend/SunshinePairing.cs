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
using System.Web.Script.Serialization;

namespace DualRemote
{
    /// <summary>本机 Sunshine 待确认的一次配对请求。只包含界面显示需要的信息。</summary>
    internal sealed class PendingPairRequest
    {
        internal string Id = "";
        internal string DeviceName = "";
        internal string StartedAt = "";
        internal string ExpiresAt = "";
    }

    /// <summary>本机已授权客户端的只读快照；不含证书、证书指纹、令牌或私钥。</summary>
    internal sealed class PairedClientInfo
    {
        internal string Id = "";
        internal string Name = "";
        internal bool Enabled = true;
        internal bool Streaming;
    }

    /// <summary>
    /// 本机 Sunshine WebUI 配对接口。只访问 localhost；请求时使用本机 CA 做证书固定，
    /// 每次临时读取 local-auth.token，不缓存、不打印、不写入任何文件。
    /// PIN 确认和客户端列表都只是原生结果；调用方仍需用配对完成信号或客户端列表确认握手。
    /// </summary>
    internal static class SunshinePairing
    {
        internal const int DefaultWebUiPort = 47990;
        internal const int RequestTimeoutMs = 4500;
        internal const int MaximumResponseBytes = 256 * 1024;
        private const int MaximumTokenBytes = 16 * 1024;

        private const string LocalTokenHeader = "X-Sunshine-Local-Token";
        private const string PendingPath = "/api/pairing/pending?wait=0";
        private const string PinPath = "/api/pairing/pin";
        private const string PendingPathPrefix = "/api/pairing/pending/";
        private const string ClientsPath = "/api/clients/list";

        /// <summary>
        /// 只读检查本机 Sunshine 配对接口。先验证构件、令牌、证书和端口，再对 pending
        /// 接口发一次 GET 探活；不会确认、拒绝或创建任何请求。
        /// </summary>
        internal static bool TryGetAvailability(out string reason)
        {
            return TryGetAvailability(BundledHost.ActiveRoot, BundledHost.ActiveConfigDirectory, out reason);
        }

        internal static bool TryGetAvailability(string root, string configDirectory, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(root))
                root = HostNetworkInfo.DefaultSunshineRoot;
            if (string.IsNullOrEmpty(configDirectory))
                configDirectory = Path.Combine(root, "config");
            if (!Directory.Exists(root))
            {
                reason = "本机未安装 Sunshine，无法生成应用内配对码。";
                return false;
            }
            if (!File.Exists(Path.Combine(root, "sunshine.exe")))
            {
                reason = "本机 Sunshine 组件不完整：找不到 sunshine.exe。";
                return false;
            }

            string tokenPath = Path.Combine(configDirectory, "local-auth.token");
            if (!File.Exists(tokenPath))
            {
                reason = "本机 Sunshine 尚未生成本地认证令牌，无法使用应用内配对。";
                return false;
            }
            try
            {
                if (ReadLocalToken(tokenPath).Length == 0)
                {
                    reason = "本机 Sunshine 的本地认证令牌为空，无法使用应用内配对。";
                    return false;
                }
            }
            catch
            {
                reason = "当前 Windows 用户无法读取本机 Sunshine 的本地认证令牌。";
                return false;
            }

            string certPath = Path.Combine(configDirectory, "credentials", "cacert.pem");
            if (!File.Exists(certPath))
            {
                reason = "本机 Sunshine 缺少 CA 证书，无法安全访问配对接口。";
                return false;
            }
            string certificateThumbprint;
            string certificateError;
            if (!TryReadCertificateThumbprint(certPath, out certificateThumbprint, out certificateError))
            {
                reason = certificateError;
                return false;
            }

            Dictionary<string, string> config;
            try
            {
                config = HostNetworkInfo.ParseKeyValues(Path.Combine(configDirectory, "sunshine.conf"));
            }
            catch
            {
                reason = "无法读取本机 Sunshine 配置，无法确定配对接口端口。";
                return false;
            }
            int port;
            string portError;
            if (!TryParseWebUiPort(config, out port, out portError))
            {
                reason = portError;
                return false;
            }

            List<PendingPairRequest> ignored;
            string probeError;
            if (TryGetPending(configDirectory, out ignored, out probeError)) return true;
            reason = probeError;
            return false;
        }

        /// <summary>查询待确认请求；只应在用户主动生成配对码的有效期内调用。</summary>
        internal static bool TryGetPending(out List<PendingPairRequest> requests, out string error)
        {
            return TryGetPending(BundledHost.ActiveConfigDirectory, out requests, out error);
        }

        internal static bool TryGetPending(string configDirectory, out List<PendingPairRequest> requests, out string error)
        {
            requests = new List<PendingPairRequest>();
            error = null;
            HttpWebRequest request;
            if (!TryCreateLocalRequest(configDirectory, "GET", PendingPath, out request, out error)) return false;
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    string body;
                    if (!TryReadBoundedBody(response, out body, out error)) return false;
                    return TryParsePending(body, out requests, out error);
                }
            }
            catch (WebException ex)
            {
                error = DescribeHttpFailure(ex, "查询本机配对请求");
                return false;
            }
            catch (Exception ex)
            {
                error = "查询本机配对请求失败：" + SafeText(ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 提交本次待确认请求的 PIN。status=true 只代表 Sunshine 接收了 PIN；
        /// 调用方必须继续用 TryGetPairedClients 的 UUID 基线差集确认新的已授权客户端。
        /// </summary>
        internal static bool TryConfirm(string pin, string id, string name, out string error)
        {
            return TryConfirm(BundledHost.ActiveConfigDirectory, pin, id, name, out error);
        }

        internal static bool TryConfirm(string configDirectory, string pin, string id, string name, out string error)
        {
            error = null;
            string pinError;
            if (!Engines.TryValidatePairPin(pin, out pinError))
            {
                error = pinError;
                return false;
            }
            if (!IsSafeRequestId(id))
            {
                error = "待确认请求编号无效，请刷新后重试。";
                return false;
            }

            HttpWebRequest request;
            if (!TryCreateLocalRequest(configDirectory, "POST", PinPath, out request, out error)) return false;
            try
            {
                string payloadJson = BuildConfirmBody(pin, NormalizeDeviceName(name), id);
                byte[] bytes = new UTF8Encoding(false).GetBytes(payloadJson);
                request.ContentType = "application/json; charset=utf-8";
                request.ContentLength = bytes.Length;
                using (Stream stream = request.GetRequestStream())
                {
                    stream.Write(bytes, 0, bytes.Length);
                }
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    string body;
                    if (!TryReadBoundedBody(response, out body, out error)) return false;
                    bool success;
                    string statusError;
                    if (!TryParseStatus(body, out success, out statusError))
                    {
                        error = "本机 Sunshine 返回了无法识别的确认结果：" + statusError;
                        return false;
                    }
                    if (!success)
                    {
                        error = statusError.Length > 0
                            ? "本机 Sunshine 未接受配对码：" + statusError
                            : "本机 Sunshine 未接受配对码，请确认双方的四位码一致。";
                        return false;
                    }
                    return true;
                }
            }
            catch (WebException ex)
            {
                error = DescribeHttpFailure(ex, "接收本机配对码");
                return false;
            }
            catch (Exception ex)
            {
                error = "接收本机配对码失败：" + SafeText(ex.Message);
                return false;
            }
        }

        /// <summary>拒绝待确认请求；无法解析的响应不算拒绝成功。</summary>
        internal static bool TryReject(string id, out string error)
        {
            return TryReject(BundledHost.ActiveConfigDirectory, id, out error);
        }

        internal static bool TryReject(string configDirectory, string id, out string error)
        {
            error = null;
            if (!IsSafeRequestId(id))
            {
                error = "待确认请求编号无效，请刷新后重试。";
                return false;
            }

            HttpWebRequest request;
            if (!TryCreateLocalRequest(configDirectory, "DELETE", BuildPendingPath(id), out request, out error)) return false;
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    string body;
                    if (!TryReadBoundedBody(response, out body, out error)) return false;
                    bool success;
                    string statusError;
                    if (!TryParseStatus(body, out success, out statusError))
                    {
                        error = "本机 Sunshine 返回了无法识别的拒绝结果：" + statusError;
                        return false;
                    }
                    if (!success)
                    {
                        error = statusError.Length > 0
                            ? "本机 Sunshine 未拒绝配对：" + statusError
                            : "本机 Sunshine 未拒绝配对，请刷新后重试。";
                        return false;
                    }
                    return true;
                }
            }
            catch (WebException ex)
            {
                error = DescribeHttpFailure(ex, "拒绝本机配对");
                return false;
            }
            catch (Exception ex)
            {
                error = "拒绝本机配对失败：" + SafeText(ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 只读取已授权客户端快照。官方 GET /api/clients/list 返回 named_certs，是握手后的
        /// 服务端授权记录；本方法只保留 uuid/name/enabled/streaming 四个字段。
        /// </summary>
        internal static bool TryGetPairedClients(out List<PairedClientInfo> clients, out string error)
        {
            return TryGetPairedClients(BundledHost.ActiveConfigDirectory, out clients, out error);
        }

        internal static bool TryGetPairedClients(string configDirectory, out List<PairedClientInfo> clients, out string error)
        {
            clients = new List<PairedClientInfo>();
            error = null;
            HttpWebRequest request;
            if (!TryCreateLocalRequest(configDirectory, "GET", ClientsPath, out request, out error)) return false;
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    string body;
                    if (!TryReadBoundedBody(response, out body, out error)) return false;
                    return TryParsePairedClients(body, out clients, out error);
                }
            }
            catch (WebException ex)
            {
                error = DescribeHttpFailure(ex, "读取已授权客户端");
                return false;
            }
            catch (Exception ex)
            {
                error = "读取已授权客户端失败：" + SafeText(ex.Message);
                return false;
            }
        }

        /// <summary>生成 4 位数字配对码；使用加密随机数，不写盘。</summary>
        internal static string GeneratePin()
        {
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
            {
                byte[] bytes = new byte[2];
                while (true)
                {
                    rng.GetBytes(bytes);
                    int value = (bytes[0] << 8) | bytes[1];
                    if (value < 60000)
                        return (value % 10000).ToString("0000", CultureInfo.InvariantCulture);
                }
            }
        }

        internal static bool TryParsePending(string json, out List<PendingPairRequest> requests, out string error)
        {
            requests = new List<PendingPairRequest>();
            error = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "本机 Sunshine 没有返回待确认请求数据。";
                return false;
            }
            try
            {
                Dictionary<string, object> root = DeserializeObject(json);
                object value;
                if (!root.TryGetValue("requests", out value) || value == null)
                {
                    error = "本机 Sunshine 的待确认请求缺少 requests 数组。";
                    return false;
                }
                IEnumerable items = value as IEnumerable;
                if (items == null || value is string)
                {
                    error = "本机 Sunshine 的待确认请求格式不正确。";
                    return false;
                }
                foreach (object item in items)
                {
                    Dictionary<string, object> entry = item as Dictionary<string, object>;
                    if (entry == null)
                    {
                        error = "本机 Sunshine 的待确认请求包含无法识别条目。";
                        return false;
                    }
                    string id = GetText(entry, "id");
                    if (!IsSafeRequestId(id)) continue;
                    PendingPairRequest request = new PendingPairRequest();
                    request.Id = id;
                    request.DeviceName = FirstNonEmpty(GetText(entry, "device_name"), GetText(entry, "deviceName"));
                    request.StartedAt = FirstNonEmpty(GetText(entry, "started_at"), GetText(entry, "startedAt"));
                    request.ExpiresAt = FirstNonEmpty(GetText(entry, "expires_at"), GetText(entry, "expiresAt"));
                    requests.Add(request);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "无法解析本机 Sunshine 的待确认请求：" + SafeText(ex.Message);
                return false;
            }
        }

        internal static bool TryParseStatus(string json, out bool success, out string error)
        {
            success = false;
            error = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "响应为空。";
                return false;
            }
            try
            {
                Dictionary<string, object> root = DeserializeObject(json);
                object value;
                if (!root.TryGetValue("status", out value))
                {
                    error = "响应缺少 status 字段。";
                    return false;
                }
                if (!(value is bool))
                {
                    error = "响应 status 字段不是布尔值。";
                    return false;
                }
                success = (bool)value;
                if (!success)
                {
                    error = SafeText(FirstNonEmpty(GetText(root, "error"), GetText(root, "message")));
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "无法解析配对响应：" + SafeText(ex.Message);
                return false;
            }
        }

        internal static bool TryParsePairedClients(string json, out List<PairedClientInfo> clients, out string error)
        {
            clients = new List<PairedClientInfo>();
            error = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "本机 Sunshine 没有返回已授权客户端数据。";
                return false;
            }
            try
            {
                Dictionary<string, object> root = DeserializeObject(json);
                object value;
                if (!root.TryGetValue("named_certs", out value) || value == null)
                {
                    error = "本机 Sunshine 的已授权客户端列表格式不正确。";
                    return false;
                }
                IEnumerable items = value as IEnumerable;
                if (items == null || value is string)
                {
                    error = "本机 Sunshine 的已授权客户端列表格式不正确。";
                    return false;
                }
                foreach (object item in items)
                {
                    Dictionary<string, object> entry = item as Dictionary<string, object>;
                    if (entry == null) continue;
                    string id = GetText(entry, "uuid");
                    if (!IsSafeClientUuid(id)) continue;
                    PairedClientInfo client = new PairedClientInfo();
                    client.Id = id.ToUpperInvariant();
                    client.Name = SanitizeClientName(GetText(entry, "name"));
                    bool flag;
                    if (TryReadBooleanValue(entry, "enabled", true, out flag)) client.Enabled = flag;
                    if (TryReadBooleanValue(entry, "streaming", false, out flag)) client.Streaming = flag;
                    clients.Add(client);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "无法解析已授权客户端列表：" + SafeText(ex.Message);
                return false;
            }
        }

        internal static bool TryParseWebUiPort(string conf, out int port, out string error)
        {
            return TryParseWebUiPort(ParseConfig(conf), out port, out error);
        }

        internal static bool TryParseWebUiPort(Dictionary<string, string> map, out int port, out string error)
        {
            port = DefaultWebUiPort;
            error = null;
            if (map == null) return true;
            string value;
            if (map.TryGetValue("web_ui_port", out value))
                return TryAcceptPort(value, "web_ui_port", out port, out error);
            if (map.TryGetValue("https_port", out value))
                return TryAcceptPort(value, "https_port", out port, out error);
            if (!map.TryGetValue("port", out value)) return true;
            int basePort;
            if (!int.TryParse(FirstToken(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out basePort)
                || basePort <= 0 || basePort >= 65535)
            {
                error = "sunshine.conf 的 port 配置无效，无法确定 WebUI 端口。";
                return false;
            }
            port = basePort + 1;
            return true;
        }

        internal static string BuildConfirmBody(string pin, string name, string id)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["pin"] = pin;
            payload["name"] = name;
            payload["id"] = id;
            return new JavaScriptSerializer().Serialize(payload);
        }

        internal static string BuildPendingPath(string id)
        {
            return PendingPathPrefix + Uri.EscapeDataString(id == null ? "" : id);
        }

        internal static bool IsSafeRequestId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
            foreach (char c in id)
            {
                bool allowed = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                    || (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (!allowed) return false;
            }
            return true;
        }

        internal static bool IsSafeClientUuid(string uuid)
        {
            if (string.IsNullOrEmpty(uuid) || uuid.Length != 36) return false;
            for (int i = 0; i < uuid.Length; i++)
            {
                char c = uuid[i];
                if (i == 8 || i == 13 || i == 18 || i == 23)
                {
                    if (c != '-') return false;
                }
                else
                {
                    bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                    if (!hex) return false;
                }
            }
            return true;
        }

        private static bool TryCreateLocalRequest(string configDirectory, string method, string path,
            out HttpWebRequest request, out string error)
        {
            request = null;
            error = null;
            try
            {
                if (string.IsNullOrEmpty(configDirectory)) configDirectory = BundledHost.ActiveConfigDirectory;
                string token = ReadLocalToken(Path.Combine(configDirectory, "local-auth.token"));
                if (token.Length == 0)
                {
                    error = "本机 Sunshine 的本地认证令牌为空，无法使用应用内配对。";
                    return false;
                }
                string expectedThumbprint;
                string certificateError;
                if (!TryReadCertificateThumbprint(
                    Path.Combine(configDirectory, "credentials", "cacert.pem"),
                    out expectedThumbprint, out certificateError))
                {
                    error = certificateError;
                    return false;
                }

                Dictionary<string, string> config = HostNetworkInfo.ParseKeyValues(
                    Path.Combine(configDirectory, "sunshine.conf"));
                int port;
                if (!TryParseWebUiPort(config, out port, out error)) return false;

                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                request = (HttpWebRequest)WebRequest.Create(
                    "https://localhost:" + port.ToString(CultureInfo.InvariantCulture) + path);
                request.Method = method;
                request.Proxy = null;
                request.AllowAutoRedirect = false;
                request.KeepAlive = false;
                request.Timeout = RequestTimeoutMs;
                request.ReadWriteTimeout = RequestTimeoutMs;
                request.Headers[LocalTokenHeader] = token;
                request.ServerCertificateValidationCallback = delegate(object sender, X509Certificate certificate,
                    X509Chain chain, SslPolicyErrors errors)
                {
                    return certificate != null && string.Equals(certificate.GetCertHashString(),
                        expectedThumbprint, StringComparison.OrdinalIgnoreCase);
                };
                return true;
            }
            catch (Exception ex)
            {
                request = null;
                error = "无法准备本机 Sunshine 配对请求：" + SafeText(ex.Message);
                return false;
            }
        }

        private static bool TryReadBoundedBody(HttpWebResponse response, out string body, out string error)
        {
            body = null;
            error = null;
            if (response == null)
            {
                error = "本机 Sunshine 没有返回响应。";
                return false;
            }
            try
            {
                using (Stream stream = response.GetResponseStream())
                {
                    if (stream == null)
                    {
                        error = "本机 Sunshine 响应没有正文。";
                        return false;
                    }
                    using (MemoryStream buffer = new MemoryStream())
                    {
                        byte[] chunk = new byte[8192];
                        int total = 0;
                        while (true)
                        {
                            int read = stream.Read(chunk, 0, chunk.Length);
                            if (read <= 0) break;
                            total += read;
                            if (total > MaximumResponseBytes)
                            {
                                error = "本机 Sunshine 响应超过允许大小，已中止。";
                                return false;
                            }
                            buffer.Write(chunk, 0, read);
                        }
                        body = Encoding.UTF8.GetString(buffer.ToArray());
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                error = "读取本机 Sunshine 响应失败：" + SafeText(ex.Message);
                return false;
            }
        }

        private static string DescribeHttpFailure(WebException ex, string operation)
        {
            if (ex == null) return operation + "失败。";
            if (ex.Status == WebExceptionStatus.Timeout)
                return operation + "超时，请确认本机 Sunshine 服务正在运行。";
            if (ex.Status == WebExceptionStatus.TrustFailure)
                return "本机 Sunshine 证书与本机 CA 不一致，已中止请求。";
            HttpWebResponse response = ex.Response as HttpWebResponse;
            if (response == null)
            {
                if (ex.Status == WebExceptionStatus.ConnectFailure)
                    return "本机被控服务未运行或配对接口不可达，请先启动 Sunshine 后再试。";
                return operation + "失败：" + SafeText(ex.Message);
            }
            int status = (int)response.StatusCode;
            try
            {
                switch (status)
                {
                    case 400: return "配对码错误或请求参数无效，请确认双方输入的四位码一致。";
                    case 401:
                    case 403: return "本机 Sunshine 拒绝了配对接口访问，请确认本地认证文件未被修改。";
                    case 404: return "待确认请求或客户端已不存在，可能已过期或被取消。";
                    case 409: return "该配对请求已被处理，请刷新设备状态后重试。";
                    default:
                        return operation + "失败（HTTP " + status.ToString(CultureInfo.InvariantCulture)
                            + "），请确认本机 Sunshine 版本支持应用内配对。";
                }
            }
            finally
            {
                response.Close();
            }
        }

        internal static Dictionary<string, object> DeserializeObject(string json)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = MaximumResponseBytes;
            Dictionary<string, object> root = serializer.Deserialize<Dictionary<string, object>>(json);
            if (root == null) throw new FormatException("响应不是 JSON 对象。");
            return root;
        }

        private static bool TryReadCertificateThumbprint(string path, out string thumbprint, out string error)
        {
            thumbprint = null;
            error = null;
            try
            {
                FileInfo info = new FileInfo(path);
                if (!info.Exists || info.Length <= 0 || info.Length > 64 * 1024)
                {
                    error = "本机 Sunshine 的 CA 证书缺失或大小异常。";
                    return false;
                }
                byte[] raw = File.ReadAllBytes(path);
                string text = Encoding.ASCII.GetString(raw);
                const string beginMarker = "-----BEGIN CERTIFICATE-----";
                const string endMarker = "-----END CERTIFICATE-----";
                int begin = text.IndexOf(beginMarker, StringComparison.Ordinal);
                int end = begin < 0 ? -1 : text.IndexOf(endMarker, begin + beginMarker.Length, StringComparison.Ordinal);
                if (begin >= 0 && end > begin)
                {
                    StringBuilder base64 = new StringBuilder();
                    for (int i = begin + beginMarker.Length; i < end; i++)
                    {
                        if (!char.IsWhiteSpace(text[i])) base64.Append(text[i]);
                    }
                    using (X509Certificate2 certificate = new X509Certificate2(Convert.FromBase64String(base64.ToString())))
                    {
                        thumbprint = certificate.Thumbprint;
                    }
                }
                else
                {
                    using (X509Certificate2 certificate = new X509Certificate2(raw))
                    {
                        thumbprint = certificate.Thumbprint;
                    }
                }
                if (!string.IsNullOrEmpty(thumbprint)) return true;
                error = "本机 Sunshine 的 CA 证书没有有效指纹。";
                return false;
            }
            catch
            {
                thumbprint = null;
                error = "本机 Sunshine 的 CA 证书无法解析。";
                return false;
            }
        }

        private static string ReadLocalToken(string path)
        {
            FileInfo info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaximumTokenBytes)
                throw new InvalidDataException("local-auth.token 大小异常。");
            return File.ReadAllText(path, Encoding.UTF8).Trim();
        }

        private static Dictionary<string, string> ParseConfig(string text)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return map;
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int separator = line.IndexOf('=');
                if (separator <= 0) continue;
                map[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
            }
            return map;
        }

        private static bool TryAcceptPort(string value, string key, out int port, out string error)
        {
            port = DefaultWebUiPort;
            error = null;
            if (!int.TryParse(FirstToken(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
                || port <= 0 || port > 65535)
            {
                error = "sunshine.conf 的 " + key + " 配置无效，无法确定 WebUI 端口。";
                return false;
            }
            return true;
        }

        private static string FirstToken(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string[] parts = value.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? "" : parts[0];
        }

        private static string NormalizeDeviceName(string name)
        {
            string text = string.IsNullOrEmpty(name) ? Environment.MachineName : name.Trim();
            StringBuilder builder = new StringBuilder();
            foreach (char c in text)
            {
                if (char.IsControl(c)) continue;
                builder.Append(c);
                if (builder.Length >= 64) break;
            }
            return builder.Length == 0 ? Environment.MachineName : builder.ToString();
        }

        private static string SanitizeClientName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            StringBuilder builder = new StringBuilder();
            foreach (char c in name)
            {
                if (char.IsControl(c)) continue;
                builder.Append(c);
                if (builder.Length >= 80) break;
            }
            return builder.ToString();
        }

        private static string GetText(Dictionary<string, object> data, string key)
        {
            object value;
            if (!data.TryGetValue(key, out value) || value == null) return "";
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static bool TryReadBoolean(object value, out bool result)
        {
            result = false;
            if (value is bool) { result = (bool)value; return true; }
            return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out result);
        }

        private static bool TryReadBooleanValue(Dictionary<string, object> data, string key, bool fallback, out bool result)
        {
            result = fallback;
            object value;
            if (!data.TryGetValue(key, out value) || value == null) return false;
            return TryReadBoolean(value, out result);
        }

        private static string FirstNonEmpty(string first, string second)
        {
            return string.IsNullOrEmpty(first) ? second : first;
        }

        private static string SafeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            StringBuilder builder = new StringBuilder();
            foreach (char c in text)
            {
                if (c == '\r' || c == '\n' || char.IsControl(c)) builder.Append(' ');
                else builder.Append(c);
                if (builder.Length >= 160) break;
            }
            return builder.ToString().Trim();
        }
    }
}
