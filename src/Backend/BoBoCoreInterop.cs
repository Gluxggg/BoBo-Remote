using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DualRemote
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct BoBoNativeMonitorInfo
    {
        internal int Index;
        internal int Left;
        internal int Top;
        internal int Width;
        internal int Height;
        internal int IsPrimary;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        internal byte[] NameBytes;

        internal string GetName()
        {
            if (NameBytes == null) return "DISPLAY" + (Index + 1);
            int len = 0;
            while (len < NameBytes.Length && NameBytes[len] != 0) len++;
            if (len == 0) return "DISPLAY" + (Index + 1);
            return Encoding.UTF8.GetString(NameBytes, 0, len);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct BoBoNativeClientStats
    {
        internal double Fps;
        internal double DecodeMs;
        internal double RenderMs;
        internal int Width;
        internal int Height;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        internal byte[] DecoderNameBytes;

        internal string GetDecoderName()
        {
            if (DecoderNameBytes == null) return "D3D11VA";
            int len = 0;
            while (len < DecoderNameBytes.Length && DecoderNameBytes[len] != 0) len++;
            if (len == 0) return "D3D11VA";
            return Encoding.UTF8.GetString(DecoderNameBytes, 0, len);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct BoBoInputPacket
    {
        internal byte Kind;   // 1=MouseAbs, 2=MouseRel, 3=MouseBtn, 4=MouseWheel, 5=Key, 6=ReleaseAll
        internal byte Flags;  // down/extended/horizontal
        internal short A;     // x(0..65535) / dx / button / delta / vk
        internal short B;     // y(0..65535) / dy
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void BoBoVideoFrameNativeCallback(
        IntPtr data,
        int length,
        int isKeyframe,
        int width,
        int height,
        uint timestampMs,
        IntPtr userCtx);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void BoBoAudioFrameNativeCallback(
        IntPtr data,
        int length,
        int sampleRate,
        int channels,
        int sampleCount,
        IntPtr userCtx);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void BoBoClientInputNativeCallback(
        ref BoBoInputPacket pkt,
        IntPtr userCtx);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void BoBoClientHotkeyNativeCallback(
        int hotkeyCode,
        int param,
        IntPtr userCtx);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void BoBoPrivacyEscapeNativeCallback(IntPtr userCtx);

    /// <summary>
    /// BoBo V6 全内置原生内核桥接器：
    /// - 从 BoBo远程.exe 内部嵌入的压缩资源 DualRemote.BoBoCore.dll.gz 释放到版本隔离目录并仅以进程内 DLL 加载；
    /// - 不存在任何独立可执行内核（无 sunshine.exe / Moonlight.exe / RustDesk.exe / BoBoStreamPlayer.exe）；
    /// - 进程内直连 DXGI/GDI 采集、MF H.264 编解码、D3D11VA 渲染、WASAPI 回环/播放与键鼠输入注入。
    /// </summary>
    internal static class BoBoCoreInterop
    {
        internal const string EmbeddedResourceName = "DualRemote.BoBoCore.dll.gz";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnSelfTest(byte[] reportBuf, int reportBufLen);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetMonitorCount();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetMonitorInfo(int index, ref BoBoNativeMonitorInfo outInfo);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr FnHostCreate(
            int monitorIndex,
            int targetWidth,
            int targetHeight,
            int fps,
            int bitrateKbps,
            int muteHostSpeakers,
            BoBoVideoFrameNativeCallback onVideo,
            BoBoAudioFrameNativeCallback onAudio,
            IntPtr userCtx);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnHostReconfigure(
            IntPtr handle,
            int monitorIndex,
            int targetWidth,
            int targetHeight,
            int fps,
            int bitrateKbps);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void FnHostRequestKeyframe(IntPtr handle);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void FnHostSetMuteHost(IntPtr handle, int mute);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void FnHostDestroy(IntPtr handle);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void FnHostInjectInput(int monitorIndex, ref BoBoInputPacket pkt);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void FnHostInjectUnicodeText([MarshalAs(UnmanagedType.LPWStr)] string text, int length);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnHostApplySuperDisplay(
            int monitorIndex,
            int clientWidth,
            int clientHeight,
            out int outActualWidth,
            out int outActualHeight);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void FnHostRestoreSuperDisplay();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnHostSetPrivacyInputBlock(
            int enable,
            BoBoPrivacyEscapeNativeCallback onEscape,
            IntPtr userCtx);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnHostIsPrivacyInputBlocked();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr FnClientCreate(
            IntPtr parentHwnd,
            int videoWidth,
            int videoHeight,
            int freeMouse,
            BoBoClientInputNativeCallback onInput,
            BoBoClientHotkeyNativeCallback onHotkey,
            IntPtr userCtx);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnClientPushVideo(
            IntPtr handle,
            byte[] h264Data,
            int length,
            int isKeyframe,
            int srcWidth,
            int srcHeight);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void FnClientPushAudio(
            IntPtr handle,
            byte[] adpcmData,
            int length,
            int sampleRate,
            int channels,
            int sampleCount);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void FnClientResize(IntPtr handle, int width, int height);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void FnClientFocus(IntPtr handle);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void FnClientSetMouseMode(IntPtr handle, int freeMouse);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void FnClientSetVolume(IntPtr handle, int volumePercent);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void FnClientSetScaleMode(IntPtr handle, int aspectFit);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void FnClientSetViewOnly(IntPtr handle, int viewOnly);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void FnClientSetSysKeys(IntPtr handle, int sendSysKeys);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void FnClientSetHostCursorComposited(IntPtr handle, int composited);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void FnClientGetStats(IntPtr handle, ref BoBoNativeClientStats outStats);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void FnClientDestroy(IntPtr handle);

        private static readonly object LoadGate = new object();
        private static bool loaded;
        private static string loadedDllPath = "";
        private static string lastLoadError = "";

        private static FnSelfTest fnSelfTest;
        private static FnGetMonitorCount fnGetMonitorCount;
        private static FnGetMonitorInfo fnGetMonitorInfo;
        private static FnHostCreate fnHostCreate;
        private static FnHostReconfigure fnHostReconfigure;
        private static FnHostRequestKeyframe fnHostRequestKeyframe;
        private static FnHostSetMuteHost fnHostSetMuteHost;
        private static FnHostDestroy fnHostDestroy;
        private static FnHostInjectInput fnHostInjectInput;
        private static FnHostInjectUnicodeText fnHostInjectUnicodeText;
        private static FnHostApplySuperDisplay fnHostApplySuperDisplay;
        private static FnHostRestoreSuperDisplay fnHostRestoreSuperDisplay;
        private static FnHostSetPrivacyInputBlock fnHostSetPrivacyInputBlock;
        private static FnHostIsPrivacyInputBlocked fnHostIsPrivacyInputBlocked;
        private static BoBoPrivacyEscapeNativeCallback activePrivacyEscapeKeepAlive;
        private static FnClientCreate fnClientCreate;
        private static FnClientPushVideo fnClientPushVideo;
        private static FnClientPushAudio fnClientPushAudio;
        private static FnClientResize fnClientResize;
        private static FnClientFocus fnClientFocus;
        private static FnClientSetMouseMode fnClientSetMouseMode;
        private static FnClientSetVolume fnClientSetVolume;
        private static FnClientSetScaleMode fnClientSetScaleMode;
        private static FnClientSetViewOnly fnClientSetViewOnly;
        private static FnClientSetSysKeys fnClientSetSysKeys;
        private static FnClientSetHostCursorComposited fnClientSetHostCursorComposited;
        private static FnClientGetStats fnClientGetStats;
        private static FnClientDestroy fnClientDestroy;

        internal static bool IsLoaded
        {
            get
            {
                string err;
                return EnsureLoaded(out err);
            }
        }

        internal static string LoadedDllPath
        {
            get { return loadedDllPath; }
        }

        internal static bool EnsureLoaded(out string error)
        {
            error = null;
            lock (LoadGate)
            {
                if (loaded) return true;
                try
                {
                    string dllPath = MaterializeEmbeddedCoreDll(out error);
                    if (string.IsNullOrEmpty(dllPath))
                    {
                        lastLoadError = error ?? "未找到内置 BoBoCore 内核。";
                        return false;
                    }

                    IntPtr mod = LoadLibraryW(dllPath);
                    if (mod == IntPtr.Zero)
                    {
                        int gle = Marshal.GetLastWin32Error();
                        error = "加载内置 BoBoCore 内核失败（Win32=" + gle + "）。";
                        lastLoadError = error;
                        return false;
                    }

                    fnSelfTest = Bind<FnSelfTest>(mod, "BoBoCore_SelfTest");
                    fnGetMonitorCount = Bind<FnGetMonitorCount>(mod, "BoBoCore_GetMonitorCount");
                    fnGetMonitorInfo = Bind<FnGetMonitorInfo>(mod, "BoBoCore_GetMonitorInfo");
                    fnHostCreate = Bind<FnHostCreate>(mod, "BoBoHost_Create");
                    fnHostReconfigure = Bind<FnHostReconfigure>(mod, "BoBoHost_Reconfigure");
                    fnHostRequestKeyframe = Bind<FnHostRequestKeyframe>(mod, "BoBoHost_RequestKeyframe");
                    fnHostSetMuteHost = Bind<FnHostSetMuteHost>(mod, "BoBoHost_SetMuteHost");
                    fnHostDestroy = Bind<FnHostDestroy>(mod, "BoBoHost_Destroy");
                    fnHostInjectInput = Bind<FnHostInjectInput>(mod, "BoBoHost_InjectInput");
                    fnHostInjectUnicodeText = Bind<FnHostInjectUnicodeText>(mod, "BoBoHost_InjectUnicodeText");
                    fnHostApplySuperDisplay = Bind<FnHostApplySuperDisplay>(mod, "BoBoHost_ApplySuperDisplay");
                    fnHostRestoreSuperDisplay = Bind<FnHostRestoreSuperDisplay>(mod, "BoBoHost_RestoreSuperDisplay");
                    fnHostSetPrivacyInputBlock = Bind<FnHostSetPrivacyInputBlock>(mod, "BoBoHost_SetPrivacyInputBlock");
                    fnHostIsPrivacyInputBlocked = Bind<FnHostIsPrivacyInputBlocked>(mod, "BoBoHost_IsPrivacyInputBlocked");
                    fnClientCreate = Bind<FnClientCreate>(mod, "BoBoClient_Create");
                    fnClientPushVideo = Bind<FnClientPushVideo>(mod, "BoBoClient_PushVideo");
                    fnClientPushAudio = Bind<FnClientPushAudio>(mod, "BoBoClient_PushAudio");
                    fnClientResize = Bind<FnClientResize>(mod, "BoBoClient_Resize");
                    fnClientFocus = Bind<FnClientFocus>(mod, "BoBoClient_Focus");
                    fnClientSetMouseMode = Bind<FnClientSetMouseMode>(mod, "BoBoClient_SetMouseMode");
                    fnClientSetVolume = Bind<FnClientSetVolume>(mod, "BoBoClient_SetVolume");
                    fnClientSetScaleMode = Bind<FnClientSetScaleMode>(mod, "BoBoClient_SetScaleMode");
                    fnClientSetViewOnly = Bind<FnClientSetViewOnly>(mod, "BoBoClient_SetViewOnly");
                    fnClientSetSysKeys = Bind<FnClientSetSysKeys>(mod, "BoBoClient_SetSysKeys");
                    fnClientSetHostCursorComposited = TryBind<FnClientSetHostCursorComposited>(mod, "BoBoClient_SetHostCursorComposited");
                    fnClientGetStats = Bind<FnClientGetStats>(mod, "BoBoClient_GetStats");
                    fnClientDestroy = Bind<FnClientDestroy>(mod, "BoBoClient_Destroy");

                    loadedDllPath = dllPath;
                    loaded = true;
                    lastLoadError = "";
                    return true;
                }
                catch (Exception ex)
                {
                    error = "初始化内置 BoBoCore 失败：" + ex.Message;
                    lastLoadError = error;
                    return false;
                }
            }
        }

        private static T Bind<T>(IntPtr module, string exportName) where T : class
        {
            IntPtr proc = GetProcAddress(module, exportName);
            if (proc == IntPtr.Zero)
            {
                throw new EntryPointNotFoundException("BoBoCore 缺少导出函数：" + exportName);
            }
            return (T)(object)Marshal.GetDelegateForFunctionPointer(proc, typeof(T));
        }

        private static T TryBind<T>(IntPtr module, string exportName) where T : class
        {
            IntPtr proc = GetProcAddress(module, exportName);
            if (proc == IntPtr.Zero) return null;
            return (T)(object)Marshal.GetDelegateForFunctionPointer(proc, typeof(T));
        }

        private static string MaterializeEmbeddedCoreDll(out string error)
        {
            error = null;
            byte[] dllBytes = TryDecompressEmbeddedResource();
            if (dllBytes == null || dllBytes.Length < 4096)
            {
                // Fallback only for local development when running un-embedded test binary next to repo
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] devCandidates = new string[]
                {
                    Path.Combine(baseDir, "BoBoCore.dll"),
                    Path.Combine(baseDir, "..", "native", "bobo-core", "out", "BoBoCore.dll"),
                    Path.Combine(baseDir, "native", "bobo-core", "out", "BoBoCore.dll")
                };
                for (int i = 0; i < devCandidates.Length; i++)
                {
                    try
                    {
                        string full = Path.GetFullPath(devCandidates[i]);
                        if (File.Exists(full))
                        {
                            dllBytes = File.ReadAllBytes(full);
                            break;
                        }
                    }
                    catch { }
                }
            }

            if (dllBytes == null || dllBytes.Length < 4096)
            {
                error = "未在当前 BoBo远程.exe 中找到内嵌核心模块 DualRemote.BoBoCore.dll.gz。";
                return null;
            }

            string hash12 = ComputeSha256Prefix(dllBytes, 12);
            string coreRoot = Path.Combine(RecentStore.DirPath, "core");
            string verDir = Path.Combine(coreRoot, hash12);
            string targetDll = Path.Combine(verDir, "BoBoCore.dll");
            Directory.CreateDirectory(verDir);

            bool needWrite = true;
            if (File.Exists(targetDll))
            {
                try
                {
                    FileInfo fi = new FileInfo(targetDll);
                    if (fi.Length == dllBytes.Length)
                    {
                        byte[] existing = File.ReadAllBytes(targetDll);
                        if (string.Equals(ComputeSha256Prefix(existing, 12), hash12, StringComparison.OrdinalIgnoreCase))
                        {
                            needWrite = false;
                        }
                    }
                }
                catch { }
            }

            if (needWrite)
            {
                string tmp = targetDll + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tmp, dllBytes);
                try
                {
                    if (File.Exists(targetDll)) File.Delete(targetDll);
                    File.Move(tmp, targetDll);
                }
                catch
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    if (!File.Exists(targetDll)) throw;
                }
            }

            // Best-effort cleanup of stale old hash folders
            try
            {
                string[] dirs = Directory.GetDirectories(coreRoot);
                for (int i = 0; i < dirs.Length; i++)
                {
                    string name = Path.GetFileName(dirs[i]);
                    if (!string.Equals(name, hash12, StringComparison.OrdinalIgnoreCase))
                    {
                        try { Directory.Delete(dirs[i], true); } catch { }
                    }
                }
            }
            catch { }

            return targetDll;
        }

        private static byte[] TryDecompressEmbeddedResource()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream gz = asm.GetManifestResourceStream(EmbeddedResourceName))
                {
                    if (gz == null) return null;
                    using (var gunzip = new GZipStream(gz, CompressionMode.Decompress))
                    using (var ms = new MemoryStream())
                    {
                        byte[] buf = new byte[32768];
                        int n;
                        while ((n = gunzip.Read(buf, 0, buf.Length)) > 0)
                        {
                            ms.Write(buf, 0, n);
                        }
                        return ms.ToArray();
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private static string ComputeSha256Prefix(byte[] data, int hexChars)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(data);
                StringBuilder sb = new StringBuilder(hexChars);
                for (int i = 0; i < hash.Length && sb.Length < hexChars; i++)
                {
                    sb.Append(hash[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }

        internal static bool RunNativeSelfTest(out string report)
        {
            string err;
            if (!EnsureLoaded(out err))
            {
                report = err ?? lastLoadError;
                return false;
            }

            byte[] buf = new byte[4096];
            int rc = fnSelfTest(buf, buf.Length);
            int len = 0;
            while (len < buf.Length && buf[len] != 0) len++;
            report = Encoding.UTF8.GetString(buf, 0, len);
            return rc == 0;
        }

        internal static List<BoBoMonitorDescriptor> EnumerateMonitors()
        {
            var result = new List<BoBoMonitorDescriptor>();
            string err;
            if (!EnsureLoaded(out err)) return result;

            int count = fnGetMonitorCount();
            for (int i = 0; i < count; i++)
            {
                var info = new BoBoNativeMonitorInfo();
                info.NameBytes = new byte[64];
                if (fnGetMonitorInfo(i, ref info) != 0)
                {
                    result.Add(new BoBoMonitorDescriptor
                    {
                        Index = info.Index,
                        Name = info.GetName(),
                        Width = info.Width,
                        Height = info.Height,
                        IsPrimary = info.IsPrimary != 0
                    });
                }
            }
            return result;
        }

        internal static void InjectInput(int monitorIndex, BoBoInputPacket pkt)
        {
            string err;
            if (!EnsureLoaded(out err)) return;
            fnHostInjectInput(monitorIndex, ref pkt);
        }

        internal static void InjectUnicodeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            string err;
            if (!EnsureLoaded(out err)) return;
            fnHostInjectUnicodeText(text, text.Length);
        }

        internal static bool ApplySuperDisplay(int monitorIndex, int clientWidth, int clientHeight, out int actualWidth, out int actualHeight)
        {
            actualWidth = 0;
            actualHeight = 0;
            string err;
            if (!EnsureLoaded(out err) || fnHostApplySuperDisplay == null) return false;
            return fnHostApplySuperDisplay(monitorIndex, clientWidth, clientHeight, out actualWidth, out actualHeight) != 0;
        }

        internal static void RestoreSuperDisplay()
        {
            string err;
            if (!EnsureLoaded(out err) || fnHostRestoreSuperDisplay == null) return;
            try { fnHostRestoreSuperDisplay(); } catch { }
        }

        internal static bool SetPrivacyInputBlock(bool enable, Action onLocalEscape)
        {
            string err;
            if (!EnsureLoaded(out err) || fnHostSetPrivacyInputBlock == null) return false;
            if (!enable)
            {
                int rcOff = fnHostSetPrivacyInputBlock(0, null, IntPtr.Zero);
                activePrivacyEscapeKeepAlive = null;
                return rcOff != 0;
            }
            BoBoPrivacyEscapeNativeCallback cb = null;
            if (onLocalEscape != null)
            {
                Action captured = onLocalEscape;
                cb = delegate(IntPtr ctx)
                {
                    try { captured(); } catch { }
                };
            }
            activePrivacyEscapeKeepAlive = cb;
            int rc = fnHostSetPrivacyInputBlock(1, cb, IntPtr.Zero);
            if (rc == 0) activePrivacyEscapeKeepAlive = null;
            return rc != 0;
        }

        internal static bool IsPrivacyInputBlocked()
        {
            if (!loaded || fnHostIsPrivacyInputBlocked == null) return false;
            try { return fnHostIsPrivacyInputBlocked() != 0; } catch { return false; }
        }

        internal static BoBoHostNativeHandle CreateHostSession(
            int monitorIndex,
            int targetWidth,
            int targetHeight,
            int fps,
            int bitrateKbps,
            bool muteHostSpeakers,
            Action<byte[], bool, int, int, uint> onVideo,
            Action<byte[], int, int, int> onAudio,
            out string error)
        {
            if (!EnsureLoaded(out error)) return null;

            BoBoVideoFrameNativeCallback vidCb = delegate(IntPtr data, int len, int isKey, int w, int h, uint ts, IntPtr ctx)
            {
                if (data == IntPtr.Zero || len <= 0 || onVideo == null) return;
                byte[] copy = new byte[len];
                Marshal.Copy(data, copy, 0, len);
                onVideo(copy, isKey != 0, w, h, ts);
            };

            BoBoAudioFrameNativeCallback audCb = delegate(IntPtr data, int len, int sr, int ch, int samples, IntPtr ctx)
            {
                if (data == IntPtr.Zero || len <= 0 || onAudio == null) return;
                byte[] copy = new byte[len];
                Marshal.Copy(data, copy, 0, len);
                onAudio(copy, sr, ch, samples);
            };

            IntPtr ptr = fnHostCreate(monitorIndex, targetWidth, targetHeight, fps, bitrateKbps,
                muteHostSpeakers ? 1 : 0, vidCb, audCb, IntPtr.Zero);
            if (ptr == IntPtr.Zero)
            {
                error = "创建进程内主机采编引擎失败。";
                return null;
            }
            return new BoBoHostNativeHandle(ptr, vidCb, audCb);
        }

        internal static BoBoClientNativeHandle CreateClientSession(
            IntPtr parentHwnd,
            int videoWidth,
            int videoHeight,
            bool freeMouse,
            Action<BoBoInputPacket> onInput,
            Action<int, int> onHotkey,
            out string error)
        {
            if (!EnsureLoaded(out error)) return null;

            BoBoClientInputNativeCallback inpCb = delegate(ref BoBoInputPacket pkt, IntPtr ctx)
            {
                if (onInput != null) onInput(pkt);
            };

            BoBoClientHotkeyNativeCallback hkCb = delegate(int code, int param, IntPtr ctx)
            {
                if (onHotkey != null) onHotkey(code, param);
            };

            IntPtr ptr = fnClientCreate(parentHwnd, videoWidth, videoHeight, freeMouse ? 1 : 0, inpCb, hkCb, IntPtr.Zero);
            if (ptr == IntPtr.Zero)
            {
                error = "创建进程内 D3D11VA 渲染解码视口失败。";
                return null;
            }
            return new BoBoClientNativeHandle(ptr, inpCb, hkCb);
        }

        internal sealed class BoBoHostNativeHandle : IDisposable
        {
            private IntPtr handle;
            private readonly object gate = new object();
            // Keep strong refs so native callbacks cannot be GC'd
            private readonly BoBoVideoFrameNativeCallback keepVid;
            private readonly BoBoAudioFrameNativeCallback keepAud;

            internal BoBoHostNativeHandle(IntPtr ptr, BoBoVideoFrameNativeCallback vid, BoBoAudioFrameNativeCallback aud)
            {
                handle = ptr;
                keepVid = vid;
                keepAud = aud;
            }

            internal bool Reconfigure(int monitorIndex, int width, int height, int fps, int bitrateKbps)
            {
                lock (gate)
                {
                    if (handle == IntPtr.Zero) return false;
                    return fnHostReconfigure(handle, monitorIndex, width, height, fps, bitrateKbps) != 0;
                }
            }

            internal void RequestKeyframe()
            {
                lock (gate)
                {
                    if (handle != IntPtr.Zero) fnHostRequestKeyframe(handle);
                }
            }

            internal void SetMuteHost(bool mute)
            {
                lock (gate)
                {
                    if (handle != IntPtr.Zero) fnHostSetMuteHost(handle, mute ? 1 : 0);
                }
            }

            public void Dispose()
            {
                IntPtr toDestroy;
                lock (gate)
                {
                    toDestroy = handle;
                    handle = IntPtr.Zero;
                }
                if (toDestroy != IntPtr.Zero)
                {
                    try { fnHostDestroy(toDestroy); } catch { }
                }
                GC.KeepAlive(keepVid);
                GC.KeepAlive(keepAud);
            }
        }

        internal sealed class BoBoClientNativeHandle : IDisposable
        {
            private IntPtr handle;
            private readonly object gate = new object();
            private readonly BoBoClientInputNativeCallback keepInp;
            private readonly BoBoClientHotkeyNativeCallback keepHk;

            internal BoBoClientNativeHandle(IntPtr ptr, BoBoClientInputNativeCallback inp, BoBoClientHotkeyNativeCallback hk)
            {
                handle = ptr;
                keepInp = inp;
                keepHk = hk;
            }

            internal int PushVideo(byte[] h264Data, int length, bool isKeyframe, int srcWidth, int srcHeight)
            {
                lock (gate)
                {
                    if (handle == IntPtr.Zero || h264Data == null || length <= 0) return 0;
                    return fnClientPushVideo(handle, h264Data, length, isKeyframe ? 1 : 0, srcWidth, srcHeight);
                }
            }

            internal void PushAudio(byte[] adpcmData, int length, int sampleRate, int channels, int sampleCount)
            {
                lock (gate)
                {
                    if (handle == IntPtr.Zero || adpcmData == null || length <= 0) return;
                    fnClientPushAudio(handle, adpcmData, length, sampleRate, channels, sampleCount);
                }
            }

            internal void Resize(int width, int height)
            {
                lock (gate)
                {
                    if (handle != IntPtr.Zero) fnClientResize(handle, width, height);
                }
            }

            internal void Focus()
            {
                lock (gate)
                {
                    if (handle != IntPtr.Zero) fnClientFocus(handle);
                }
            }

            internal void SetMouseMode(bool freeMouse)
            {
                lock (gate)
                {
                    if (handle != IntPtr.Zero) fnClientSetMouseMode(handle, freeMouse ? 1 : 0);
                }
            }

            internal void SetVolume(int volumePercent)
            {
                lock (gate)
                {
                    if (handle != IntPtr.Zero) fnClientSetVolume(handle, volumePercent);
                }
            }

            internal void SetScaleMode(bool aspectFit)
            {
                SetScaleMode(aspectFit ? 1 : 0);
            }

            /// <summary>0=实际大小1:1，1=等比居中缩放，2=超级屏全屏自适应无黑边</summary>
            internal void SetScaleMode(int scaleMode)
            {
                lock (gate)
                {
                    if (handle != IntPtr.Zero) fnClientSetScaleMode(handle, scaleMode);
                }
            }

            internal void SetViewOnly(bool viewOnly)
            {
                lock (gate)
                {
                    if (handle != IntPtr.Zero) fnClientSetViewOnly(handle, viewOnly ? 1 : 0);
                }
            }

            internal void SetSysKeys(bool sendSysKeys)
            {
                lock (gate)
                {
                    if (handle != IntPtr.Zero) fnClientSetSysKeys(handle, sendSysKeys ? 1 : 0);
                }
            }

            internal void SetHostCursorComposited(bool composited)
            {
                lock (gate)
                {
                    if (handle != IntPtr.Zero && fnClientSetHostCursorComposited != null)
                    {
                        fnClientSetHostCursorComposited(handle, composited ? 1 : 0);
                    }
                }
            }

            internal NativeStreamStats GetStats(double rttMs, int bitrateKbps)
            {
                var st = new NativeStreamStats
                {
                    RttMs = rttMs,
                    BitrateKbps = bitrateKbps,
                    Codec = "H264",
                    Decoder = "D3D11VA"
                };
                lock (gate)
                {
                    if (handle == IntPtr.Zero) return st;
                    var raw = new BoBoNativeClientStats();
                    raw.DecoderNameBytes = new byte[64];
                    fnClientGetStats(handle, ref raw);
                    st.Fps = raw.Fps;
                    st.DecodeMs = raw.DecodeMs;
                    st.RenderMs = raw.RenderMs;
                    st.Width = raw.Width;
                    st.Height = raw.Height;
                    st.Decoder = raw.GetDecoderName();
                }
                return st;
            }

            public void Dispose()
            {
                IntPtr toDestroy;
                lock (gate)
                {
                    toDestroy = handle;
                    handle = IntPtr.Zero;
                }
                if (toDestroy != IntPtr.Zero)
                {
                    try { fnClientDestroy(toDestroy); } catch { }
                }
                GC.KeepAlive(keepInp);
                GC.KeepAlive(keepHk);
            }
        }
    }
}
