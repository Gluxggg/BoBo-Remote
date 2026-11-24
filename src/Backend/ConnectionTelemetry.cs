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
                || IsRfc1918_172(host))
                return "private_ipv4";
            if (host.StartsWith("100.", StringComparison.OrdinalIgnoreCase)) return "overlay_ipv4";
            return "ipv4_or_hostname";
        }

        private static bool IsRfc1918_172(string host)
        {
            if (string.IsNullOrEmpty(host) || !host.StartsWith("172.", StringComparison.Ordinal)) return false;
            int dot = host.IndexOf('.', 4);
            if (dot <= 4) return false;
            int second;
            return int.TryParse(host.Substring(4, dot - 4), NumberStyles.None, CultureInfo.InvariantCulture, out second)
                && second >= 16 && second <= 31;
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            StringBuilder sb = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                if (c == '\\') sb.Append("\\\\");
                else if (c == '"') sb.Append("\\\"");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20) sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)c);
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
