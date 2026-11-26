using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DualRemote
{
    /// <summary>本机网络地址快照。</summary>
    internal sealed class HostSnapshot
    {
        internal string LanIpv4 = "";
        internal string TailscaleIpv4 = "";
        internal string PublicIpv6 = "";
    }

    internal static class HostNetworkInfo
    {
        internal const string EasyTierReleasesUrl = "https://github.com/EasyTier/EasyTier/releases";

        /// <summary>收集本机局域网、Tailscale 与公网 IPv6 地址。</summary>
        internal static HostSnapshot Collect()
        {
            var snap = new HostSnapshot();
            CollectAddresses(snap);
            if (string.IsNullOrEmpty(snap.TailscaleIpv4) && BoBoEmbeddedTailnet.Default.IsRunning)
            {
                EmbeddedTailnetSnapshot emb = BoBoEmbeddedTailnet.Default.LatestSnapshot;
                if (emb != null && !string.IsNullOrEmpty(emb.SelfIp4))
                    snap.TailscaleIpv4 = emb.SelfIp4;
            }
            return snap;
        }

        private static void CollectAddresses(HostSnapshot snap)
        {
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    string name = nic.Name ?? "";
                    string desc = nic.Description ?? "";
                    if (name.IndexOf("WSL", StringComparison.OrdinalIgnoreCase) >= 0
                        || desc.IndexOf("Hyper-V", StringComparison.OrdinalIgnoreCase) >= 0
                        || desc.IndexOf("VirtualBox", StringComparison.OrdinalIgnoreCase) >= 0
                        || desc.IndexOf("VMware", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    IPInterfaceProperties props;
                    try { props = nic.GetIPProperties(); }
                    catch { continue; }
                    if (props == null) continue;

                    bool isTailscale = name.IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) >= 0
                        || desc.IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) >= 0;
                    foreach (UnicastIPAddressInformation uni in props.UnicastAddresses)
                    {
                        IPAddress addr = uni.Address;
                        if (addr == null) continue;
                        if (addr.AddressFamily == AddressFamily.InterNetwork)
                        {
                            byte[] b = addr.GetAddressBytes();
                            if (b[0] == 169 && b[1] == 254) continue; // APIPA
                            if (isTailscale || (b[0] == 100 && b[1] >= 64 && b[1] <= 127))
                            {
                                if (snap.TailscaleIpv4.Length == 0) snap.TailscaleIpv4 = addr.ToString();
                            }
                            else if (b[0] == 192 && b[1] == 168 && b[2] == 137)
                            {
                                // Windows 移动热点虚拟网卡，优先让位给真实局域网 IP
                                if (snap.LanIpv4.Length == 0) snap.LanIpv4 = addr.ToString();
                            }
                            else
                            {
                                if (snap.LanIpv4.Length == 0 || snap.LanIpv4.StartsWith("192.168.137.", StringComparison.Ordinal))
                                    snap.LanIpv4 = addr.ToString();
                            }
                        }
                        else if (addr.AddressFamily == AddressFamily.InterNetworkV6 && !isTailscale)
                        {
                            if (addr.IsIPv6LinkLocal || addr.IsIPv6SiteLocal || addr.IsIPv6Multicast || addr.IsIPv6Teredo) continue;
                            byte[] b = addr.GetAddressBytes();
                            // 仅保留全球单播公网 IPv6。
                            if ((b[0] & 0xE0) == 0x20)
                            {
                                string text = addr.ToString();
                                int zone = text.IndexOf('%');
                                if (zone > 0) text = text.Substring(0, zone);
                                if (snap.PublicIpv6.Length == 0) snap.PublicIpv6 = text;
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }
    }
}
