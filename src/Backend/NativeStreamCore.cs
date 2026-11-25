using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace DualRemote
{
    internal sealed class NativeStreamStats
    {
        internal double Fps;
        internal double DecodeMs;
        internal double RenderMs;
        internal double RttMs;
        internal double PacketLossPct;
        internal int BitrateKbps;
        internal int Width;
        internal int Height;
        internal string Codec = "H264";
        internal string Decoder = "D3D11VA";
    }

    internal sealed class NativePlayerEvent
    {
        internal string EventType = "";
        internal string Name = "";
        internal string State = "";
        internal string Message = "";
        internal string Reason = "";
        internal string Codec = "";
        internal string Decoder = "";
        internal int Code;
        internal int Width;
        internal int Height;
        internal int Fps;
        internal bool FreeMouse;
        internal ulong ViewportHwnd;
        internal NativeStreamStats Stats;
    }

    /// <summary>
    /// 管理内置 V5 原生播放核 BoBoStreamPlayer.exe（D3D11VA + moonlight-common-c + WASAPI）
    /// 的定位、稳定目录部署（规避非系统盘 Low-IL 拦截并自动伴随 opus.dll）以及 JSON-Lines IPC 编解码。
    /// </summary>
    internal static class NativeStreamCore
    {
        internal const string PlayerExeName = "BoBoStreamPlayer.exe";
        internal const string RuntimeSubDir = "runtime\\native-player";
        private static readonly object StageGate = new object();

        internal static string AppDir
        {
            get { return Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory); }
        }

        internal static string RuntimeDir
        {
            get { return Path.Combine(RecentStore.DirPath, RuntimeSubDir); }
        }

        internal static string RuntimePlayerExe
        {
            get { return Path.Combine(RuntimeDir, PlayerExeName); }
        }

        internal static string FindBundledPlayerExe()
        {
            string[] candidates = new string[]
            {
                Path.Combine(AppDir, PlayerExeName),
                Path.Combine(AppDir, "engines", "NativePlayer", PlayerExeName),
                Path.Combine(AppDir, "native", "bobo-stream-player", "out", PlayerExeName)
            };
            foreach (string path in candidates)
            {
                try
                {
                    if (File.Exists(path)) return Path.GetFullPath(path);
                }
                catch { }
            }
            return null;
        }

        internal static bool IsAvailable
        {
            get
            {
                return FindBundledPlayerExe() != null || File.Exists(RuntimePlayerExe);
            }
        }

        private static string FindOpusDll()
        {
            string[] candidates = new string[]
            {
                Path.Combine(AppDir, "opus.dll"),
                Path.Combine(AppDir, "engines", "NativePlayer", "opus.dll"),
                Path.Combine(AppDir, "engines", "MoonlightVPlus", "opus.dll"),
                Path.Combine(AppDir, "engines", "Moonlight", "opus.dll"),
                Path.Combine(FoundationClient.EngineDir, "opus.dll")
            };
            foreach (string path in candidates)
            {
                try
                {
                    if (File.Exists(path)) return Path.GetFullPath(path);
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// 将随包 BoBoStreamPlayer.exe（及 opus.dll）同步到 %LOCALAPPDATA%\双端远程\runtime\native-player\，
        /// 保证在 Medium 完整性级别稳定运行且升级时不锁主目录文件。
        /// </summary>
        internal static bool EnsureRuntimePlayerExe(out string exePath, out string error)
        {
            exePath = null;
            error = null;
            lock (StageGate)
            {
                try
                {
                    Directory.CreateDirectory(RuntimeDir);
                    string bundled = FindBundledPlayerExe();
                    string target = RuntimePlayerExe;

                    if (bundled != null)
                    {
                        bool needCopy = !File.Exists(target) || !FilesHaveSameSha256(bundled, target);
                        if (needCopy)
                        {
                            try
                            {
                                string tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                                File.Copy(bundled, tmp, true);
                                if (File.Exists(target)) File.Delete(target);
                                File.Move(tmp, target);
                            }
                            catch
                            {
                                // 若旧进程正占用 target，直接复用已有文件或回退到新名称
                                if (!File.Exists(target)) throw;
                            }
                        }
                    }

                    if (!File.Exists(target))
                    {
                        error = "未找到内置原生串流播放核 BoBoStreamPlayer.exe，请重新安装或更新 BoBo Remote。";
                        return false;
                    }

                    // 顺带准备 opus.dll，启用低延迟 WASAPI 远程音频解码
                    string opusSrc = FindOpusDll();
                    if (opusSrc != null)
                    {
                        string opusDst = Path.Combine(RuntimeDir, "opus.dll");
                        if (!File.Exists(opusDst) || new FileInfo(opusSrc).Length != new FileInfo(opusDst).Length)
                        {
                            try { File.Copy(opusSrc, opusDst, true); } catch { }
                        }
                    }

                    exePath = target;
                    return true;
                }
                catch (Exception ex)
                {
                    error = "准备内置原生串流内核失败：" + ex.Message;
                    return false;
                }
            }
        }

        internal static string BuildStreamArgs(IntPtr parentHwnd, GameStreamLaunchResult launch,
            int width, int height, int fps, int bitrateKbps, bool preferHevc, bool freeMouse, bool showHud, bool enableAudio)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("--stream");
            sb.Append(" --parent-hwnd=").Append(((ulong)parentHwnd.ToInt64()).ToString(CultureInfo.InvariantCulture));
            sb.Append(" --host=").Append(QuoteArg(launch.Host));
            sb.Append(" --rtsp-url=").Append(QuoteArg(launch.RtspUrl));
            sb.Append(" --app-version=").Append(QuoteArg(string.IsNullOrEmpty(launch.AppVersion) ? "7.1.431.-1" : launch.AppVersion));
            sb.Append(" --gfe-version=").Append(QuoteArg(string.IsNullOrEmpty(launch.GfeVersion) ? "3.23.0.74" : launch.GfeVersion));
            sb.Append(" --server-codec-support=").Append(launch.ServerCodecModeSupport.ToString(CultureInfo.InvariantCulture));
            sb.Append(" --rikey=").Append(launch.RiKeyHex);
            sb.Append(" --rikeyid=").Append(launch.RiKeyId.ToString(CultureInfo.InvariantCulture));
            sb.Append(" --width=").Append(width.ToString(CultureInfo.InvariantCulture));
            sb.Append(" --height=").Append(height.ToString(CultureInfo.InvariantCulture));
            sb.Append(" --fps=").Append(fps.ToString(CultureInfo.InvariantCulture));
            sb.Append(" --bitrate=").Append(bitrateKbps.ToString(CultureInfo.InvariantCulture));
            sb.Append(" --codec=").Append(preferHevc ? "HEVC" : "H264");
            sb.Append(" --free-mouse=").Append(freeMouse ? "1" : "0");
            sb.Append(" --audio=").Append(enableAudio ? "1" : "0");
            sb.Append(" --hud=").Append(showHud ? "1" : "0");
            return sb.ToString();
        }

        internal static string BuildResizeCommand(int width, int height)
        {
            return "{\"cmd\":\"resize\",\"width\":" + Math.Max(16, width).ToString(CultureInfo.InvariantCulture)
                 + ",\"height\":" + Math.Max(16, height).ToString(CultureInfo.InvariantCulture) + "}";
        }

        internal static string BuildMouseModeCommand(bool freeMouse)
        {
            return "{\"cmd\":\"set_mouse_mode\",\"free\":" + (freeMouse ? "true" : "false") + "}";
        }

        internal static string BuildHudCommand(bool enabled)
        {
            return "{\"cmd\":\"set_hud\",\"enabled\":" + (enabled ? "true" : "false") + "}";
        }

        internal static string BuildVolumeCommand(int volumePercent)
        {
            int v = Math.Max(0, Math.Min(100, volumePercent));
            return "{\"cmd\":\"set_volume\",\"volume\":" + v.ToString(CultureInfo.InvariantCulture) + "}";
        }

        internal static string BuildScaleModeCommand(bool aspectFit)
        {
            return "{\"cmd\":\"set_scale_mode\",\"fit\":" + (aspectFit ? "true" : "false") + "}";
        }

        internal static string BuildViewOnlyCommand(bool viewOnly)
        {
            return "{\"cmd\":\"set_view_only\",\"enabled\":" + (viewOnly ? "true" : "false") + "}";
        }

        internal static string BuildSysKeysCommand(bool sendSysKeys)
        {
            return "{\"cmd\":\"set_sys_keys\",\"enabled\":" + (sendSysKeys ? "true" : "false") + "}";
        }

        internal static string BuildSendKeysCommand(int[] vks)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"cmd\":\"send_keys\",\"vks\":[");
            if (vks != null)
            {
                for (int i = 0; i < vks.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(vks[i].ToString(CultureInfo.InvariantCulture));
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        internal static string BuildSendTextCommand(string utf8Text)
        {
            return "{\"cmd\":\"send_text\",\"text\":\"" + EscapeJson(utf8Text ?? "") + "\"}";
        }

        internal static string BuildDisconnectCommand()
        {
            return "{\"cmd\":\"disconnect\"}";
        }

        internal static NativePlayerEvent TryParseEventLine(string jsonLine)
        {
            if (string.IsNullOrWhiteSpace(jsonLine)) return null;
            string trimmed = jsonLine.Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal)) return null;
            try
            {
                var ser = new JavaScriptSerializer();
                var dict = ser.DeserializeObject(trimmed) as IDictionary<string, object>;
                if (dict == null) return null;

                var ev = new NativePlayerEvent();
                ev.EventType = GetString(dict, "event");
                if (string.IsNullOrEmpty(ev.EventType)) return null;

                ev.Name = GetString(dict, "stage");
                if (string.IsNullOrEmpty(ev.Name)) ev.Name = GetString(dict, "name");
                ev.State = GetString(dict, "status");
                if (string.IsNullOrEmpty(ev.State)) ev.State = GetString(dict, "state");
                ev.Message = GetString(dict, "message");
                ev.Reason = GetString(dict, "reason");
                ev.Codec = GetString(dict, "codec");
                ev.Decoder = GetString(dict, "decoder");
                ev.Code = dict.ContainsKey("errorCode") ? GetInt(dict, "errorCode", 0) : GetInt(dict, "code", 0);
                ev.Width = GetInt(dict, "width", 0);
                ev.Height = GetInt(dict, "height", 0);
                ev.Fps = GetInt(dict, "fps", 0);
                ev.FreeMouse = GetBool(dict, "free", true);
                ev.ViewportHwnd = GetUlong(dict, "hwnd", 0);

                if (ev.EventType == "stats")
                {
                    ev.Stats = new NativeStreamStats
                    {
                        Fps = GetDouble(dict, "fps", 0),
                        DecodeMs = GetDouble(dict, "decodeMs", 0),
                        RenderMs = GetDouble(dict, "renderMs", 0),
                        RttMs = GetDouble(dict, "rttMs", 0),
                        PacketLossPct = GetDouble(dict, "packetLossPct", 0),
                        BitrateKbps = GetInt(dict, "bitrateKbps", 0),
                        Width = GetInt(dict, "width", 0),
                        Height = GetInt(dict, "height", 0),
                        Codec = GetString(dict, "codec"),
                        Decoder = GetString(dict, "decoder")
                    };
                }
                return ev;
            }
            catch
            {
                return null;
            }
        }

        internal static string TranslateStageLabel(string stageName)
        {
            if (string.IsNullOrEmpty(stageName)) return "正在建立远程连接…";
            string s = stageName.ToLowerInvariant();
            if (s.IndexOf("platform", StringComparison.Ordinal) >= 0) return "正在初始化内置串流内核…";
            if (s.IndexOf("name resolution", StringComparison.Ordinal) >= 0) return "正在解析目标主机地址…";
            if (s.IndexOf("audio stream", StringComparison.Ordinal) >= 0) return "正在建立音频加密通道…";
            if (s.IndexOf("rtsp", StringComparison.Ordinal) >= 0) return "正在进行 RTSP 串流握手…";
            if (s.IndexOf("control stream", StringComparison.Ordinal) >= 0) return "正在建立低延迟控制通道…";
            if (s.IndexOf("video stream", StringComparison.Ordinal) >= 0) return "正在启动 D3D11VA 硬件解码通道…";
            if (s.IndexOf("input stream", StringComparison.Ordinal) >= 0) return "正在建立键鼠加密输入通道…";
            return "正在连接：" + stageName + "…";
        }

        private static string QuoteArg(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            if (value.IndexOfAny(new char[] { ' ', '\t', '"' }) < 0) return value;
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string EscapeJson(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static bool FilesHaveSameSha256(string pathA, string pathB)
        {
            try
            {
                FileInfo a = new FileInfo(pathA);
                FileInfo b = new FileInfo(pathB);
                if (!a.Exists || !b.Exists || a.Length != b.Length) return false;
                return string.Equals(ComputeSha256(pathA), ComputeSha256(pathB), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(fs);
                return GameStreamClient.BytesToHex(hash);
            }
        }

        private static string GetString(IDictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
        }

        private static int GetInt(IDictionary<string, object> d, string k, int fallback)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return fallback;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static ulong GetUlong(IDictionary<string, object> d, string k, ulong fallback)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return fallback;
            try { return Convert.ToUInt64(v, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static double GetDouble(IDictionary<string, object> d, string k, double fallback)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return fallback;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static bool GetBool(IDictionary<string, object> d, string k, bool fallback)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return fallback;
            if (v is bool) return (bool)v;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
            if (s == "true" || s == "1") return true;
            if (s == "false" || s == "0") return false;
            return fallback;
        }
    }
}
