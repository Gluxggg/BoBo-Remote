using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace DualRemote
{
    internal static class ConnectionTelemetry
    {
        private static string LogPath
        {
            get { return Path.Combine(RecentStore.DirPath, "sessions.jsonl"); }
        }

        internal static void RecordSubmitted(string engine, string host, int width, int height,
            int fps, int bitrateKbps, bool hevc, bool abr, string message)
        {
            Append("submitted", engine, host, width, height, fps, bitrateKbps, hevc, abr, message);
        }

        internal static void RecordFailed(string engine, string host, int width, int height,
            int fps, int bitrateKbps, bool hevc, bool abr, string message)
        {
            Append("failed", engine, host, width, height, fps, bitrateKbps, hevc, abr, message);
        }

        private static void Append(string state, string engine, string host, int width, int height,
            int fps, int bitrateKbps, bool hevc, bool abr, string message)
        {
            try
            {
                Directory.CreateDirectory(RecentStore.DirPath);
                string line = "{"
                    + "\"schema_version\":1,"
                    + "\"session_id\":\"" + Guid.NewGuid().ToString("N") + "\","
                    + "\"state\":\"" + Escape(state) + "\","
                    + "\"engine\":\"" + Escape(engine) + "\","
                    + "\"target_kind\":\"" + TargetKind(host) + "\","
                    + "\"width\":" + width.ToString(CultureInfo.InvariantCulture) + ","
                    + "\"height\":" + height.ToString(CultureInfo.InvariantCulture) + ","
                    + "\"fps\":" + fps.ToString(CultureInfo.InvariantCulture) + ","
                    + "\"bitrate_kbps\":" + bitrateKbps.ToString(CultureInfo.InvariantCulture) + ","
                    + "\"hevc\":" + (hevc ? "true" : "false") + ","
                    + "\"abr\":" + (abr ? "true" : "false") + ","
                    + "\"message\":\"" + Escape(message) + "\","
                    + "\"created_at_utc\":\"" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\""
                    + "}" + Environment.NewLine;
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string TargetKind(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return "unknown";
            if (host.IndexOf(':') >= 0) return "ipv6_or_hostname";
            if (host.StartsWith("192.168.", StringComparison.OrdinalIgnoreCase)
                || host.StartsWith("10.", StringComparison.OrdinalIgnoreCase)
                || host.StartsWith("172.16.", StringComparison.OrdinalIgnoreCase)
                || host.StartsWith("172.17.", StringComparison.OrdinalIgnoreCase)
                || host.StartsWith("172.18.", StringComparison.OrdinalIgnoreCase)
                || host.StartsWith("172.19.", StringComparison.OrdinalIgnoreCase)
                || host.StartsWith("172.2", StringComparison.OrdinalIgnoreCase)
                || host.StartsWith("172.3", StringComparison.OrdinalIgnoreCase))
                return "private_ipv4";
            if (host.StartsWith("100.", StringComparison.OrdinalIgnoreCase)) return "overlay_ipv4";
            return "ipv4_or_hostname";
        }

        private static string Escape(string value)
        {
            if (value == null) return "";
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }
}
