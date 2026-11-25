using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace DualRemote
{
    internal enum UpdatePackageKind
    {
        None,
        LightZip,
        FullSetup
    }

    internal sealed class UpdateCheckResult
    {
        internal bool Checked;
        internal bool HasUpdate;
        internal string CurrentVersion = "";
        internal string LatestVersion = "";
        internal string ReleaseTag = "";
        internal string ReleasePageUrl = "";
        internal string ReleaseNotesSummary = "";
        internal UpdatePackageKind PackageKind = UpdatePackageKind.None;
        internal string AssetName = "";
        internal string DownloadUrl = "";
        internal string ExpectedSha256 = "";
        internal long AssetSizeBytes;
        internal string Error = "";
    }

    internal sealed class ReleaseManifestInfo
    {
        internal string TagName = "";
        internal string HtmlUrl = "";
        internal string Body = "";
        internal string Sha256SumsUrl = "";
        internal string LightZipUrl = "";
        internal long LightZipSize;
        internal string SetupExeUrl = "";
        internal long SetupExeSize;
    }

    /// <summary>
    /// 客户端内置自动更新模块（基于 GitHub Releases + SHA256SUMS.txt 强校验）：
    /// - 引擎版本未变时自动走轻量增量包（BoBoRemote-Update-x64.zip，~200 KB，秒级更新）；
    /// - 上游引擎升级或本地组件不完整时自动走完整安装包（BoBoRemote-Setup-x64.exe --silent --preserve-autostart）；
    /// - 强制校验 SHA-256 与可信 GitHub HTTPS 域名，绝不执行未校验文件。
    /// </summary>
    internal static class AppUpdater
    {
        internal const string Repository = "Gluxggg/BoBo-Remote";
        internal const string LatestReleaseApiUrl = "https://api.github.com/repos/Gluxggg/BoBo-Remote/releases/latest";
        internal const string ReleasesWebUrl = "https://github.com/Gluxggg/BoBo-Remote/releases/latest";
        internal const string LightUpdateAssetName = "BoBoRemote-Update-x64.zip";
        internal const string FullSetupAssetName = "BoBoRemote-Setup-x64.exe";
        internal const string Sha256SumsAssetName = "SHA256SUMS.txt";
        internal const string VersionResourceName = "DualRemote.app-version.txt";
        internal const string FallbackVersion = "6.0.0";

        /// <summary>当前内嵌架构指纹；与 ci/release-package.ps1 写入 SHA256SUMS.txt 的 # engines= 对齐。从 V5 升级至 V6 时强制走完整安装包以彻底清理旧外置引擎。</summary>
        internal const string CurrentEnginesStamp = "bobo-v6-allinone-inproc";

        private const string UninstallRegSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BoBoRemote";
        private const int ApiTimeoutMs = 12000;
        private const int DownloadTimeoutMs = 60000;
        private const int MaxJsonBytes = 512 * 1024;
        private const int MaxSumsBytes = 64 * 1024;

        private static string cachedVersion;

        internal static string CurrentVersion
        {
            get
            {
                if (cachedVersion != null) return cachedVersion;
                try
                {
                    using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(VersionResourceName))
                    {
                        if (stream != null)
                        {
                            using (var reader = new StreamReader(stream, Encoding.UTF8))
                            {
                                string text = (reader.ReadToEnd() ?? "").Trim().TrimStart('v', 'V');
                                if (!string.IsNullOrEmpty(text))
                                {
                                    cachedVersion = text;
                                    return cachedVersion;
                                }
                            }
                        }
                    }
                }
                catch { }
                cachedVersion = FallbackVersion;
                return cachedVersion;
            }
        }

        internal static string UpdatesCacheRoot
        {
            get { return Path.Combine(RecentStore.DirPath, "updates"); }
        }

        internal static int CompareVersions(string left, string right)
        {
            int[] a = ParseVersionNumbers(left);
            int[] b = ParseVersionNumbers(right);
            int len = Math.Max(a.Length, b.Length);
            for (int i = 0; i < len; i++)
            {
                int va = i < a.Length ? a[i] : 0;
                int vb = i < b.Length ? b[i] : 0;
                if (va != vb) return va < vb ? -1 : 1;
            }
            return 0;
        }

        private static int[] ParseVersionNumbers(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new[] { 0, 0, 0 };
            string cleaned = raw.Trim().TrimStart('v', 'V');
            int dash = cleaned.IndexOf('-');
            if (dash >= 0) cleaned = cleaned.Substring(0, dash);
            string[] parts = cleaned.Split('.');
            var nums = new List<int>();
            foreach (string part in parts)
            {
                int val;
                if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out val) && val >= 0)
                    nums.Add(val);
                else
                    break;
            }
            return nums.Count == 0 ? new[] { 0, 0, 0 } : nums.ToArray();
        }

        internal static bool IsTrustedDownloadUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            Uri uri;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri)) return false;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
            string host = (uri.Host ?? "").ToLowerInvariant();
            if (host == "github.com")
            {
                return uri.AbsolutePath.StartsWith("/" + Repository + "/", StringComparison.OrdinalIgnoreCase);
            }
            if (host == "api.github.com")
            {
                return uri.AbsolutePath.StartsWith("/repos/" + Repository + "/", StringComparison.OrdinalIgnoreCase);
            }
            return host == "objects.githubusercontent.com"
                || host == "release-assets.githubusercontent.com"
                || host == "github-releases.githubusercontent.com";
        }

        internal static bool TryParseLatestReleaseJson(string json, out ReleaseManifestInfo info, out string error)
        {
            info = null;
            error = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "更新服务器返回内容为空。";
                return false;
            }
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = MaxJsonBytes * 2 };
                var root = serializer.DeserializeObject(json) as IDictionary<string, object>;
                if (root == null)
                {
                    error = "无法解析更新响应格式。";
                    return false;
                }
                string tag = ReadString(root, "tag_name").Trim();
                if (string.IsNullOrEmpty(tag))
                {
                    error = "最新发布缺少版本号标签。";
                    return false;
                }
                var parsed = new ReleaseManifestInfo
                {
                    TagName = tag,
                    HtmlUrl = ReadString(root, "html_url").Trim(),
                    Body = ReadString(root, "body")
                };
                if (string.IsNullOrEmpty(parsed.HtmlUrl))
                    parsed.HtmlUrl = ReleasesWebUrl;

                object assetsObj;
                if (root.TryGetValue("assets", out assetsObj) && assetsObj is IEnumerable)
                {
                    foreach (object item in (IEnumerable)assetsObj)
                    {
                        var asset = item as IDictionary<string, object>;
                        if (asset == null) continue;
                        string name = ReadString(asset, "name").Trim();
                        string dl = ReadString(asset, "browser_download_url").Trim();
                        long size = ReadLong(asset, "size");
                        if (!IsTrustedDownloadUrl(dl)) continue;

                        if (string.Equals(name, Sha256SumsAssetName, StringComparison.OrdinalIgnoreCase))
                        {
                            parsed.Sha256SumsUrl = dl;
                        }
                        else if (string.Equals(name, LightUpdateAssetName, StringComparison.OrdinalIgnoreCase))
                        {
                            parsed.LightZipUrl = dl;
                            parsed.LightZipSize = size;
                        }
                        else if (string.Equals(name, FullSetupAssetName, StringComparison.OrdinalIgnoreCase))
                        {
                            parsed.SetupExeUrl = dl;
                            parsed.SetupExeSize = size;
                        }
                    }
                }
                info = parsed;
                return true;
            }
            catch (Exception ex)
            {
                error = "解析版本信息失败：" + SafeOneLine(ex.Message);
                return false;
            }
        }

        internal static bool ParseSha256Sums(string content, out string enginesStamp, out Dictionary<string, string> hashes)
        {
            enginesStamp = "";
            hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(content)) return false;

            string[] lines = content.Replace("\r\n", "\n").Split('\n');
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;
                if (line[0] == '#')
                {
                    const string prefix = "# engines=";
                    if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        enginesStamp = line.Substring(prefix.Length).Trim();
                    continue;
                }
                int space = line.IndexOfAny(new[] { ' ', '\t' });
                if (space != 64) continue;
                string hash = line.Substring(0, 64).Trim();
                if (!IsHex64(hash)) continue;
                string file = line.Substring(space).Trim().TrimStart('*');
                if (file.Length == 0) continue;
                hashes[file] = hash.ToLowerInvariant();
            }
            return hashes.Count > 0;
        }

        internal static void SelectBestPlan(
            ReleaseManifestInfo release,
            string remoteEnginesStamp,
            Dictionary<string, string> hashes,
            bool localEnginesIntact,
            bool canWriteAppDir,
            UpdateCheckResult result)
        {
            result.PackageKind = UpdatePackageKind.None;
            if (release == null || hashes == null || !canWriteAppDir) return;

            string lightHash;
            bool enginesMatch = !string.IsNullOrEmpty(remoteEnginesStamp)
                && string.Equals(remoteEnginesStamp, CurrentEnginesStamp, StringComparison.OrdinalIgnoreCase);
            if (localEnginesIntact
                && enginesMatch
                && !string.IsNullOrEmpty(release.LightZipUrl)
                && hashes.TryGetValue(LightUpdateAssetName, out lightHash))
            {
                result.PackageKind = UpdatePackageKind.LightZip;
                result.AssetName = LightUpdateAssetName;
                result.DownloadUrl = release.LightZipUrl;
                result.ExpectedSha256 = lightHash;
                result.AssetSizeBytes = release.LightZipSize;
                return;
            }

            string setupHash;
            if (!string.IsNullOrEmpty(release.SetupExeUrl)
                && hashes.TryGetValue(FullSetupAssetName, out setupHash))
            {
                result.PackageKind = UpdatePackageKind.FullSetup;
                result.AssetName = FullSetupAssetName;
                result.DownloadUrl = release.SetupExeUrl;
                result.ExpectedSha256 = setupHash;
                result.AssetSizeBytes = release.SetupExeSize;
            }
        }

        internal static bool IsSafeReleaseTag(string tag)
        {
            if (string.IsNullOrEmpty(tag) || tag.Length > 64) return false;
            if (tag.IndexOf("..", StringComparison.Ordinal) >= 0) return false;
            char first = tag[0];
            bool validFirst = first == 'v' || first == 'V' || (first >= '0' && first <= '9');
            if (!validFirst) return false;
            for (int i = 0; i < tag.Length; i++)
            {
                char c = tag[i];
                bool isAsciiAlnum = (c >= 'a' && c <= 'z')
                    || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9');
                bool allowed = isAsciiAlnum || c == '.' || c == '-' || c == '_';
                if (!allowed) return false;
            }
            return true;
        }

        internal static bool TryExtractTagFromReleaseUrl(string urlOrPath, out string tag)
        {
            tag = null;
            if (string.IsNullOrWhiteSpace(urlOrPath)) return false;
            string trimmed = urlOrPath.Trim();
            string pathPart;
            if (trimmed.StartsWith("/", StringComparison.Ordinal))
            {
                pathPart = trimmed;
            }
            else
            {
                Uri uri;
                if (!Uri.TryCreate(trimmed, UriKind.Absolute, out uri)) return false;
                if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
                if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return false;
                pathPart = uri.AbsolutePath ?? "";
            }

            int q = pathPart.IndexOfAny(new[] { '?', '#' });
            if (q >= 0) pathPart = pathPart.Substring(0, q);
            pathPart = pathPart.TrimEnd('/');

            string prefix = "/" + Repository + "/releases/tag/";
            if (!pathPart.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

            string rawSeg;
            try
            {
                rawSeg = Uri.UnescapeDataString(pathPart.Substring(prefix.Length)).Trim('/');
            }
            catch
            {
                return false;
            }
            if (rawSeg.IndexOf('/') >= 0 || !IsSafeReleaseTag(rawSeg)) return false;
            tag = rawSeg;
            return true;
        }

        internal static ReleaseManifestInfo BuildReleaseFromTag(string tag)
        {
            if (!IsSafeReleaseTag(tag)) return null;
            string escaped = Uri.EscapeDataString(tag);
            string dlPrefix = "https://github.com/" + Repository + "/releases/download/" + escaped + "/";
            return new ReleaseManifestInfo
            {
                TagName = tag,
                HtmlUrl = "https://github.com/" + Repository + "/releases/tag/" + escaped,
                Body = "",
                Sha256SumsUrl = dlPrefix + Sha256SumsAssetName,
                LightZipUrl = dlPrefix + LightUpdateAssetName,
                SetupExeUrl = dlPrefix + FullSetupAssetName
            };
        }

        private static bool TryResolveLatestReleaseByRedirect(out ReleaseManifestInfo info, out string error)
        {
            info = null;
            error = null;
            try
            {
                HttpWebRequest req = CreateGitHubRequest(ReleasesWebUrl, "text/html,application/xhtml+xml", ApiTimeoutMs);
                req.AllowAutoRedirect = false;
                string candidate = null;
                string responseUri = null;
                try
                {
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    {
                        candidate = resp.Headers != null ? resp.Headers["Location"] : null;
                        if (resp.ResponseUri != null) responseUri = resp.ResponseUri.AbsoluteUri;
                    }
                }
                catch (WebException ex)
                {
                    var webResp = ex.Response as HttpWebResponse;
                    if (webResp != null)
                    {
                        try
                        {
                            candidate = webResp.Headers != null ? webResp.Headers["Location"] : null;
                            if (webResp.ResponseUri != null) responseUri = webResp.ResponseUri.AbsoluteUri;
                        }
                        finally
                        {
                            webResp.Close();
                        }
                    }
                    else
                    {
                        throw;
                    }
                }

                string tag;
                if (TryExtractTagFromReleaseUrl(candidate, out tag) || TryExtractTagFromReleaseUrl(responseUri, out tag))
                {
                    info = BuildReleaseFromTag(tag);
                    return info != null;
                }

                // Fallback within redirect resolver: HEAD with AllowAutoRedirect in case an upstream proxy intercepts 302
                HttpWebRequest headReq = CreateGitHubRequest(ReleasesWebUrl, "text/html,application/xhtml+xml", ApiTimeoutMs);
                headReq.Method = "HEAD";
                headReq.AllowAutoRedirect = true;
                using (var headResp = (HttpWebResponse)headReq.GetResponse())
                {
                    string finalUri = headResp.ResponseUri != null ? headResp.ResponseUri.AbsoluteUri : "";
                    if (TryExtractTagFromReleaseUrl(finalUri, out tag))
                    {
                        info = BuildReleaseFromTag(tag);
                        return info != null;
                    }
                }

                error = "未从 GitHub Releases 页面解析到最新版本标签。";
                return false;
            }
            catch (WebException ex)
            {
                if (ex.Response != null)
                {
                    try { ex.Response.Close(); } catch { }
                }
                error = "无法连接 GitHub Releases 检查更新（可开启系统代理或稍后重试）：" + SafeOneLine(ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                error = "检查 GitHub Releases 最新版本失败：" + SafeOneLine(ex.Message);
                return false;
            }
        }

        private static void TryEnrichReleaseNotesBestEffort(ReleaseManifestInfo release)
        {
            if (release == null || string.IsNullOrEmpty(release.TagName)) return;
            try
            {
                HttpWebRequest req = CreateGitHubRequest(LatestReleaseApiUrl, "application/vnd.github+json", 4000);
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (Stream stream = resp.GetResponseStream())
                {
                    if (stream == null) return;
                    using (var ms = new MemoryStream())
                    {
                        byte[] buf = new byte[8192];
                        int n;
                        int total = 0;
                        while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                        {
                            total += n;
                            if (total > MaxJsonBytes) return;
                            ms.Write(buf, 0, n);
                        }
                        string json = Encoding.UTF8.GetString(ms.ToArray());
                        ReleaseManifestInfo apiInfo;
                        string ignoredErr;
                        if (TryParseLatestReleaseJson(json, out apiInfo, out ignoredErr)
                            && apiInfo != null
                            && string.Equals(apiInfo.TagName, release.TagName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrEmpty(apiInfo.Body)) release.Body = apiInfo.Body;
                            if (apiInfo.LightZipSize > 0) release.LightZipSize = apiInfo.LightZipSize;
                            if (apiInfo.SetupExeSize > 0) release.SetupExeSize = apiInfo.SetupExeSize;
                        }
                    }
                }
            }
            catch (WebException ex)
            {
                if (ex.Response != null)
                {
                    try { ex.Response.Close(); } catch { }
                }
            }
            catch { }
        }

        internal static Task<UpdateCheckResult> CheckForUpdateAsync()
        {
            return Task.Run(delegate { return CheckForUpdateSync(); });
        }

        private static UpdateCheckResult CheckForUpdateSync()
        {
            var result = new UpdateCheckResult
            {
                CurrentVersion = CurrentVersion,
                ReleasePageUrl = ReleasesWebUrl
            };

            try
            {
                ReleaseManifestInfo release;
                string redirectError;
                if (!TryResolveLatestReleaseByRedirect(out release, out redirectError))
                {
                    string json;
                    string fetchError;
                    if (!TryDownloadText(LatestReleaseApiUrl, "application/vnd.github+json", MaxJsonBytes, out json, out fetchError))
                    {
                        result.Error = !string.IsNullOrEmpty(redirectError) ? redirectError : fetchError;
                        return result;
                    }

                    string parseError;
                    if (!TryParseLatestReleaseJson(json, out release, out parseError))
                    {
                        result.Error = parseError;
                        return result;
                    }
                }

                result.Checked = true;
                result.ReleaseTag = release.TagName;
                result.LatestVersion = release.TagName.TrimStart('v', 'V');
                result.ReleasePageUrl = release.HtmlUrl;
                result.HasUpdate = CompareVersions(result.CurrentVersion, result.LatestVersion) < 0;

                if (!result.HasUpdate) return result;

                if (string.IsNullOrEmpty(release.Body))
                {
                    TryEnrichReleaseNotesBestEffort(release);
                }
                result.ReleaseNotesSummary = SummarizeReleaseNotes(release.Body);

                if (!string.IsNullOrEmpty(release.Sha256SumsUrl))
                {
                    string sumsText;
                    string sumsErr;
                    if (TryDownloadText(release.Sha256SumsUrl, "text/plain", MaxSumsBytes, out sumsText, out sumsErr))
                    {
                        string remoteEngines;
                        Dictionary<string, string> hashes;
                        if (ParseSha256Sums(sumsText, out remoteEngines, out hashes))
                        {
                            bool localIntact = Engines.RustDeskPresent
                                && Engines.MoonlightPresent
                                && FoundationClient.IsAvailable
                                && BundledHost.BundledAvailable;
                            bool writable = CanWriteDirectory(Engines.AppDir);
                            SelectBestPlan(release, remoteEngines, hashes, localIntact, writable, result);
                        }
                    }
                }
                return result;
            }
            catch (Exception ex)
            {
                result.Error = "检查更新失败：" + SafeOneLine(ex.Message);
                return result;
            }
        }

        internal static Task<string> DownloadAndVerifyAsync(
            UpdateCheckResult update,
            Action<int, string> onProgress,
            CancellationToken cancellationToken)
        {
            return Task.Run(delegate { return DownloadAndVerifySync(update, onProgress, cancellationToken); }, cancellationToken);
        }

        private static string DownloadAndVerifySync(
            UpdateCheckResult update,
            Action<int, string> onProgress,
            CancellationToken ct)
        {
            if (update == null || update.PackageKind == UpdatePackageKind.None)
                throw new InvalidOperationException("当前新版本缺少可校验的自动更新包，请前往 Releases 页面手动下载。");
            if (!IsTrustedDownloadUrl(update.DownloadUrl))
                throw new InvalidOperationException("更新下载地址不在可信 GitHub 域名白名单内，已拒绝下载。");
            if (!IsHex64(update.ExpectedSha256))
                throw new InvalidOperationException("缺少有效的 SHA-256 校验值，已拒绝下载。");

            string safeTag = SanitizePathSegment(update.ReleaseTag);
            string workDir = Path.Combine(UpdatesCacheRoot, safeTag);
            Directory.CreateDirectory(workDir);

            string destPath = Path.Combine(workDir, update.AssetName);
            if (File.Exists(destPath))
            {
                if (string.Equals(ComputeFileSha256(destPath), update.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    if (onProgress != null) onProgress(100, "已校验本地缓存更新包，准备安装…");
                    return destPath;
                }
                TryDeleteFile(destPath);
            }

            string partPath = destPath + ".part";
            TryDeleteFile(partPath);

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            HttpWebRequest req = CreateGitHubRequest(update.DownloadUrl, "application/octet-stream", DownloadTimeoutMs);
            try
            {
                using (ct.Register(delegate { try { req.Abort(); } catch { } }))
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (Stream input = resp.GetResponseStream())
                {
                    if (input == null) throw new IOException("更新文件响应流为空。");
                    long total = resp.ContentLength > 0 ? resp.ContentLength : update.AssetSizeBytes;
                    long received = 0;
                    int lastReportedPct = -1;

                    using (var sha = SHA256.Create())
                    using (var output = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        byte[] buf = new byte[64 * 1024];
                        int n;
                        while ((n = input.Read(buf, 0, buf.Length)) > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            output.Write(buf, 0, n);
                            sha.TransformBlock(buf, 0, n, null, 0);
                            received += n;

                            int pct = total > 0 ? (int)Math.Min(99, (received * 100L) / total) : 50;
                            if (pct != lastReportedPct)
                            {
                                lastReportedPct = pct;
                                if (onProgress != null)
                                    onProgress(pct, "正在下载 " + update.ReleaseTag + "（" + FormatSize(received)
                                        + (total > 0 ? " / " + FormatSize(total) : "") + "）…");
                            }
                        }
                        sha.TransformFinalBlock(new byte[0], 0, 0);
                        string actual = BytesToHex(sha.Hash);
                        if (!string.Equals(actual, update.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException("更新包 SHA-256 校验不匹配，文件可能已损坏或被篡改，已中止更新。");
                        }
                    }
                }
            }
            catch
            {
                TryDeleteFile(partPath);
                throw;
            }

            if (File.Exists(destPath)) TryDeleteFile(destPath);
            File.Move(partPath, destPath);
            if (onProgress != null) onProgress(100, "SHA-256 校验通过，正在启动更新…");
            return destPath;
        }

        internal static bool TryLaunchDownloadedUpdate(UpdateCheckResult update, string downloadedPath, out string error)
        {
            error = null;
            if (update == null || string.IsNullOrEmpty(downloadedPath) || !File.Exists(downloadedPath))
            {
                error = "未找到已下载的更新文件。";
                return false;
            }
            try
            {
                int selfPid = Process.GetCurrentProcess().Id;
                string appDir = Engines.AppDir.TrimEnd('\\');

                if (update.PackageKind == UpdatePackageKind.LightZip)
                {
                    string extractDir = Path.Combine(Path.GetDirectoryName(downloadedPath), "extracted");
                    if (Directory.Exists(extractDir)) TryDeleteDirectory(extractDir);
                    Directory.CreateDirectory(extractDir);
                    ExtractLightUpdateZip(downloadedPath, extractDir);

                    string helperExe = Path.Combine(extractDir, "BoBo远程.exe");
                    if (!File.Exists(helperExe))
                    {
                        error = "轻量更新包内缺少主程序 BoBo远程.exe。";
                        return false;
                    }

                    string args = string.Format(
                        CultureInfo.InvariantCulture,
                        "--apply-update --wait-pid={0} --source-dir=\"{1}\" --target-dir=\"{2}\" --version=\"{3}\"",
                        selfPid, extractDir, appDir, update.LatestVersion);
                    var psi = new ProcessStartInfo(helperExe, args)
                    {
                        WorkingDirectory = extractDir,
                        UseShellExecute = false
                    };
                    Process proc = Process.Start(psi);
                    if (proc != null) proc.Dispose();
                    return true;
                }
                else if (update.PackageKind == UpdatePackageKind.FullSetup)
                {
                    string args = string.Format(
                        CultureInfo.InvariantCulture,
                        "--silent --dir=\"{0}\" --wait-pid={1} --preserve-autostart",
                        appDir, selfPid);
                    var psi = new ProcessStartInfo(downloadedPath, args)
                    {
                        WorkingDirectory = Path.GetDirectoryName(downloadedPath),
                        UseShellExecute = false
                    };
                    Process proc = Process.Start(psi);
                    if (proc != null) proc.Dispose();
                    return true;
                }

                error = "不支持的更新包类型。";
                return false;
            }
            catch (Exception ex)
            {
                error = "启动更新程序失败：" + SafeOneLine(ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 以临时目录解压出的新版 BoBo远程.exe 身份运行：等待旧进程退出，原子覆盖目标目录文件，
        /// 更新系统卸载项版本号，并自动重新拉起新版主程序。
        /// </summary>
        internal static int RunApplyUpdate(string[] args)
        {
            int waitPid = 0;
            string sourceDir = null;
            string targetDir = null;
            string version = "";

            foreach (string raw in args)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                string arg = raw.Trim();
                if (arg.StartsWith("--wait-pid=", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(arg.Substring(11), NumberStyles.Integer, CultureInfo.InvariantCulture, out waitPid);
                else if (arg.StartsWith("--source-dir=", StringComparison.OrdinalIgnoreCase))
                    sourceDir = arg.Substring(13).Trim().Trim('"');
                else if (arg.StartsWith("--target-dir=", StringComparison.OrdinalIgnoreCase))
                    targetDir = arg.Substring(13).Trim().Trim('"');
                else if (arg.StartsWith("--version=", StringComparison.OrdinalIgnoreCase))
                    version = arg.Substring(10).Trim().Trim('"');
            }

            if (string.IsNullOrWhiteSpace(sourceDir) || string.IsNullOrWhiteSpace(targetDir)) return 2;
            string fullSource = Path.GetFullPath(sourceDir).TrimEnd('\\');
            string fullTarget = Path.GetFullPath(targetDir).TrimEnd('\\');
            string normUpdates = Path.GetFullPath(UpdatesCacheRoot).TrimEnd('\\') + "\\";
            if (!fullSource.StartsWith(normUpdates, StringComparison.OrdinalIgnoreCase)) return 3;
            if (!Directory.Exists(fullSource) || !Directory.Exists(fullTarget)) return 4;

            if (waitPid > 0)
            {
                try
                {
                    using (Process p = Process.GetProcessById(waitPid))
                    {
                        if (!p.WaitForExit(10000))
                        {
                            try { p.CloseMainWindow(); } catch { }
                            p.WaitForExit(3000);
                        }
                    }
                }
                catch { }
            }

            // 额外留出短暂缓冲让旧进程释放单实例 Mutex 与文件锁
            Thread.Sleep(350);

            try
            {
                CopyTreeWithRetry(fullSource, fullTarget);
                UpdateUninstallDisplayVersion(fullTarget, version);

                string launchedExe = Path.Combine(fullTarget, "BoBo远程.exe");
                if (File.Exists(launchedExe))
                {
                    var psi = new ProcessStartInfo(launchedExe,
                        string.IsNullOrEmpty(version) ? "" : "--updated-version=" + version)
                    {
                        WorkingDirectory = fullTarget,
                        UseShellExecute = false
                    };
                    Process proc = Process.Start(psi);
                    if (proc != null) proc.Dispose();
                }
                return 0;
            }
            catch
            {
                return 5;
            }
        }

        internal static void CleanupOldUpdateCacheAsync()
        {
            Task.Run(delegate
            {
                try
                {
                    string root = UpdatesCacheRoot;
                    if (!Directory.Exists(root)) return;
                    string currentExe = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
                    if (currentExe.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                        return;
                    foreach (string dir in Directory.GetDirectories(root))
                    {
                        TryDeleteDirectory(dir);
                    }
                }
                catch { }
            });
        }

        internal static string NormalizeLightZipEntry(string entryFullName)
        {
            if (string.IsNullOrWhiteSpace(entryFullName)) return "";
            string s = entryFullName.Replace('/', '\\').TrimStart('\\');
            if (s.StartsWith("BoBo远程\\", StringComparison.OrdinalIgnoreCase))
                s = s.Substring("BoBo远程\\".Length);
            else if (string.Equals(s, "BoBo远程", StringComparison.OrdinalIgnoreCase))
                s = "";
            return s;
        }

        private static void ExtractLightUpdateZip(string zipPath, string destRoot)
        {
            string normRoot = Path.GetFullPath(destRoot).TrimEnd('\\') + "\\";
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string rel = NormalizeLightZipEntry(entry.FullName);
                    if (string.IsNullOrEmpty(rel)) continue;
                    bool isDir = entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\");
                    string fullDest = Path.GetFullPath(Path.Combine(normRoot, rel.TrimEnd('\\')));
                    if (!fullDest.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("更新包内检测到非法路径：" + entry.FullName);
                    if (isDir)
                    {
                        Directory.CreateDirectory(fullDest);
                    }
                    else
                    {
                        string parent = Path.GetDirectoryName(fullDest);
                        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                        using (Stream src = entry.Open())
                        using (var dst = new FileStream(fullDest, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            src.CopyTo(dst);
                        }
                    }
                }
            }
        }

        private static void CopyTreeWithRetry(string srcRoot, string dstRoot)
        {
            foreach (string file in Directory.GetFiles(srcRoot))
            {
                string dest = Path.Combine(dstRoot, Path.GetFileName(file));
                CopyFileWithRetry(file, dest);
            }
            foreach (string sub in Directory.GetDirectories(srcRoot))
            {
                string destSub = Path.Combine(dstRoot, Path.GetFileName(sub));
                Directory.CreateDirectory(destSub);
                CopyTreeWithRetry(sub, destSub);
            }
        }

        private static void CopyFileWithRetry(string src, string dst)
        {
            Exception last = null;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    File.Copy(src, dst, true);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Thread.Sleep(250);
                }
            }
            if (last != null) throw last;
        }

        private static void UpdateUninstallDisplayVersion(string installDir, string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(UninstallRegSubKey, true))
                {
                    if (key == null) return;
                    string loc = Convert.ToString(key.GetValue("InstallLocation", "")).TrimEnd('\\');
                    if (string.Equals(loc, installDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    {
                        key.SetValue("DisplayVersion", version, RegistryValueKind.String);
                    }
                }
            }
            catch { }
        }

        private static bool CanWriteDirectory(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                string probe = Path.Combine(dir, ".update-write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "ok", Encoding.ASCII);
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static HttpWebRequest CreateGitHubRequest(string url, string accept, int timeoutMs)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.UserAgent = "BoBoRemote/" + CurrentVersion;
            req.Accept = accept;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.AllowAutoRedirect = true;
            req.MaximumAutomaticRedirections = 5;
            return req;
        }

        private static bool TryDownloadText(string url, string accept, int maxBytes, out string content, out string error)
        {
            content = null;
            error = null;
            try
            {
                HttpWebRequest req = CreateGitHubRequest(url, accept, ApiTimeoutMs);
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (Stream stream = resp.GetResponseStream())
                {
                    if (stream == null)
                    {
                        error = "更新服务器响应为空。";
                        return false;
                    }
                    using (var ms = new MemoryStream())
                    {
                        byte[] buf = new byte[8192];
                        int n;
                        int total = 0;
                        while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                        {
                            total += n;
                            if (total > maxBytes)
                            {
                                error = "更新响应体积异常。";
                                return false;
                            }
                            ms.Write(buf, 0, n);
                        }
                        content = Encoding.UTF8.GetString(ms.ToArray());
                        return true;
                    }
                }
            }
            catch (WebException ex)
            {
                error = "无法连接 GitHub Releases 检查更新（可开启系统代理或稍后重试）：" + SafeOneLine(ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                error = "获取更新信息失败：" + SafeOneLine(ex.Message);
                return false;
            }
        }

        private static string ComputeFileSha256(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                return BytesToHex(sha.ComputeHash(fs));
            }
        }

        private static string BytesToHex(byte[] bytes)
        {
            if (bytes == null) return "";
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static bool IsHex64(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length != 64) return false;
            foreach (char c in text)
            {
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return false;
            }
            return true;
        }

        private static string ReadString(IDictionary<string, object> map, string key)
        {
            object val;
            if (map != null && map.TryGetValue(key, out val) && val != null)
                return Convert.ToString(val) ?? "";
            return "";
        }

        private static long ReadLong(IDictionary<string, object> map, string key)
        {
            object val;
            if (map != null && map.TryGetValue(key, out val) && val != null)
            {
                long n;
                if (long.TryParse(Convert.ToString(val), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                    return n;
            }
            return 0L;
        }

        private static string SummarizeReleaseNotes(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return "";
            string[] lines = body.Replace("\r\n", "\n").Split('\n');
            var kept = new List<string>();
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith("```", StringComparison.Ordinal))
                    continue;
                kept.Add(line.TrimStart('-', '*', ' '));
                if (kept.Count >= 3) break;
            }
            string summary = string.Join("；", kept.ToArray());
            return summary.Length > 180 ? summary.Substring(0, 180) + "…" : summary;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024L)
                return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            if (bytes >= 1024L)
                return (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        private static string SanitizePathSegment(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "latest";
            var sb = new StringBuilder();
            foreach (char c in text.Trim())
            {
                if (char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_') sb.Append(c);
            }
            return sb.Length == 0 ? "latest" : sb.ToString();
        }

        private static string SafeOneLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length > 140 ? s.Substring(0, 140) + "…" : s;
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void TryDeleteDirectory(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }
}
