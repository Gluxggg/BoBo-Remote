using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DualRemote
{
    internal sealed class BoBoUdpCandidate
    {
        internal const string KindLan = "lan";
        internal const string KindDirectIPv4 = "direct4";
        internal const string KindDirectIPv6 = "ipv6";
        internal const string KindStunSrflx = "srflx";
        internal const string KindRelay = "relay";
        internal const string KindTsNet = "tsnet";

        internal string Kind = KindDirectIPv4;
        internal string Host = "";
        internal int Port;
        internal int Priority;
        internal bool SymmetricHint;

        internal bool TryGetIPEndPoint(out IPEndPoint ep)
        {
            ep = null;
            if (Port <= 0 || Port > 65535 || string.IsNullOrWhiteSpace(Host)) return false;
            IPAddress ip;
            if (!IPAddress.TryParse(Host.Trim().Trim('[', ']'), out ip)) return false;
            ep = new IPEndPoint(ip, Port);
            return true;
        }

        internal static int GetRankForKind(string kind)
        {
            if (string.Equals(kind, KindLan, StringComparison.OrdinalIgnoreCase)) return 60;
            if (string.Equals(kind, KindDirectIPv6, StringComparison.OrdinalIgnoreCase)) return 50;
            if (string.Equals(kind, KindDirectIPv4, StringComparison.OrdinalIgnoreCase)) return 45;
            if (string.Equals(kind, KindStunSrflx, StringComparison.OrdinalIgnoreCase)) return 40;
            if (string.Equals(kind, KindRelay, StringComparison.OrdinalIgnoreCase)) return 20;
            if (string.Equals(kind, KindTsNet, StringComparison.OrdinalIgnoreCase)) return 10;
            return 0;
        }

        internal static string FormatDisplayLabel(string kind)
        {
            if (string.Equals(kind, KindLan, StringComparison.OrdinalIgnoreCase)) return "局域网 UDP";
            if (string.Equals(kind, KindDirectIPv6, StringComparison.OrdinalIgnoreCase)) return "直连 IPv6";
            if (string.Equals(kind, KindDirectIPv4, StringComparison.OrdinalIgnoreCase)) return "直连 IPv4";
            if (string.Equals(kind, KindStunSrflx, StringComparison.OrdinalIgnoreCase)) return "STUN 打洞直连";
            if (string.Equals(kind, KindRelay, StringComparison.OrdinalIgnoreCase)) return "VPS UDP 中转";
            if (string.Equals(kind, KindTsNet, StringComparison.OrdinalIgnoreCase)) return "内置跨网 UDP";
            return "UDP";
        }
    }

    internal static class BoBoNatTraversal
    {
        private const uint StunMagicCookie = 0x2112A442u;
        private const ushort StunBindingRequest = 0x0001;
        private const ushort StunBindingResponse = 0x0101;
        private const ushort AttrMappedAddress = 0x0001;
        private const ushort AttrXorMappedAddress = 0x0020;

        internal static bool IsTailnetIp(IPAddress ip)
        {
            if (ip == null) return false;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = ip.GetAddressBytes();
                return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
            }
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                byte[] b = ip.GetAddressBytes();
                // Tailscale ULA fd7a:115c:a1e0::/48
                return b[0] == 0xfd && b[1] == 0x7a && b[2] == 0x11 && b[3] == 0x5c && b[4] == 0xa1 && b[5] == 0xe0;
            }
            return false;
        }

        internal static bool IsPrivateLanIPv4(IPAddress ip)
        {
            if (ip == null) return false;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] b = ip.GetAddressBytes();
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            return false;
        }

        internal static bool IsUsableGlobalIPv6(IPAddress ip)
        {
            if (ip == null || ip.AddressFamily != AddressFamily.InterNetworkV6) return false;
            if (ip.IsIPv4MappedToIPv6 || IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal || ip.IsIPv6Teredo)
                return false;
            byte[] b = ip.GetAddressBytes();
            // Require 2000::/3 Global Unicast, exclude 2001:db8::/32 doc & 2002::/16 6to4/relay noise if needed
            if ((b[0] & 0xE0) != 0x20) return false;
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return false;
            return true;
        }

        internal static bool IsAllowedRemoteCandidateIp(IPAddress ip)
        {
            if (ip == null) return false;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (IPAddress.IsLoopback(ip) || IsTailnetIp(ip)) return false;

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = ip.GetAddressBytes();
                if (b[0] == 0) return false;                  // 0.0.0.0/8
                if (b[0] == 169 && b[1] == 254) return false; // Link-local APIPA
                if (b[0] >= 224) return false;                // Multicast / reserved
                return true;
            }
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal || ip.IsIPv6Teredo)
                    return false;
                byte[] b = ip.GetAddressBytes();
                // Reject ULA fc00::/7 & doc 2001:db8::/32
                if ((b[0] & 0xFE) == 0xFC) return false;
                if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return false;
                return (b[0] & 0xE0) == 0x20;
            }
            return false;
        }

        internal static Socket CreateDualStackUdpSocket(int port)
        {
            Socket sock;
            try
            {
                sock = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                sock.DualMode = true;
                sock.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            }
            catch
            {
                sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                sock.Bind(new IPEndPoint(IPAddress.Any, port));
            }
            ConfigureLowLatencyUdpSocket(sock);
            return sock;
        }

        internal static void ConfigureLowLatencyUdpSocket(Socket sock)
        {
            if (sock == null) return;
            try
            {
                sock.SendBufferSize = 2 * 1024 * 1024;
                sock.ReceiveBufferSize = 4 * 1024 * 1024;
            }
            catch { }
            try
            {
                // Disable Windows SIO_UDP_CONNRESET (ICMP port unreachable throwing SocketException on connected/unconnected recv).
                const int SIO_UDP_CONNRESET = -1744830452;
                sock.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch { }
        }

        internal static int SendDualStack(Socket sock, byte[] data, int length, IPEndPoint target)
        {
            if (sock == null || data == null || length <= 0 || target == null) return 0;
            IPEndPoint dest = target;
            if (sock.AddressFamily == AddressFamily.InterNetworkV6 && target.AddressFamily == AddressFamily.InterNetwork)
            {
                dest = new IPEndPoint(target.Address.MapToIPv6(), target.Port);
            }
            return sock.SendTo(data, 0, length, SocketFlags.None, dest);
        }

        internal static IPEndPoint NormalizeRemoteEndPoint(EndPoint ep)
        {
            var ipep = ep as IPEndPoint;
            if (ipep == null) return null;
            IPAddress addr = ipep.Address;
            if (addr != null && addr.IsIPv4MappedToIPv6)
            {
                return new IPEndPoint(addr.MapToIPv4(), ipep.Port);
            }
            return ipep;
        }

        internal static IPAddress TryGetKernelRoutedLocalAddress(AddressFamily family)
        {
            Socket probe = null;
            try
            {
                probe = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
                // Connect on a UDP socket performs a pure kernel routing table lookup without transmitting any packet.
                IPAddress remoteProbeIp = (family == AddressFamily.InterNetworkV6)
                    ? IPAddress.Parse("2400:3200::1")
                    : IPAddress.Parse("223.5.5.5");
                probe.Connect(new IPEndPoint(remoteProbeIp, 53));
                var localEp = probe.LocalEndPoint as IPEndPoint;
                if (localEp == null || localEp.Address == null) return null;
                IPAddress addr = localEp.Address;
                if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
                if (family == AddressFamily.InterNetworkV6)
                {
                    if (!IsUsableGlobalIPv6(addr)) return null;
                    string s = addr.ToString();
                    int pct = s.IndexOf('%');
                    if (pct > 0)
                    {
                        IPAddress clean;
                        if (IPAddress.TryParse(s.Substring(0, pct), out clean)) return clean;
                    }
                    return addr;
                }
                return IsPrivateLanIPv4(addr) ? addr : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (probe != null) { try { probe.Close(); } catch { } }
            }
        }

        private static bool IsPreferredUnicastAddress(UnicastIPAddressInformation uni)
        {
            if (uni == null) return true;
            try
            {
                return uni.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred;
            }
            catch
            {
                return true;
            }
        }

        internal static List<BoBoUdpCandidate> GatherLocalCandidates(int mediaPort, IPEndPoint stunMapped)
        {
            IList<IPEndPoint> list = stunMapped != null ? new IPEndPoint[] { stunMapped } : null;
            return GatherLocalCandidates(mediaPort, list, false);
        }

        internal static List<BoBoUdpCandidate> GatherLocalCandidates(
            int mediaPort,
            IList<IPEndPoint> stunMappedEndpoints,
            bool symmetricIPv4Hint)
        {
            var list = new List<BoBoUdpCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Add STUN-verified external/mapped IPv6 FIRST so exact outbound socket tuple matches host/client firewalls.
            var ipv4Stun = new List<IPEndPoint>();
            if (stunMappedEndpoints != null)
            {
                for (int i = 0; i < stunMappedEndpoints.Count; i++)
                {
                    IPEndPoint ep = stunMappedEndpoints[i];
                    if (ep == null || ep.Port <= 0 || ep.Port > 65535 || !IsAllowedRemoteCandidateIp(ep.Address)) continue;
                    IPAddress mappedIp = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
                    if (mappedIp.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        string key6 = mappedIp.ToString() + ":" + ep.Port.ToString(CultureInfo.InvariantCulture);
                        if (seen.Add(key6))
                        {
                            list.Add(new BoBoUdpCandidate
                            {
                                Kind = (ep.Port == mediaPort) ? BoBoUdpCandidate.KindDirectIPv6 : BoBoUdpCandidate.KindStunSrflx,
                                Host = mappedIp.ToString(),
                                Port = ep.Port,
                                Priority = (ep.Port == mediaPort) ? 118 : 116
                            });
                        }
                    }
                    else
                    {
                        ipv4Stun.Add(new IPEndPoint(mappedIp, ep.Port));
                    }
                }
            }

            if (mediaPort > 0 && mediaPort <= 65535)
            {
                // 2. Kernel-routed default Global IPv6 and default LAN IPv4 (exact OS outbound interface selection).
                IPAddress routedV6 = TryGetKernelRoutedLocalAddress(AddressFamily.InterNetworkV6);
                if (routedV6 != null)
                {
                    string key = routedV6.ToString() + ":" + mediaPort.ToString(CultureInfo.InvariantCulture);
                    if (seen.Add(key))
                    {
                        list.Add(new BoBoUdpCandidate
                        {
                            Kind = BoBoUdpCandidate.KindDirectIPv6,
                            Host = routedV6.ToString(),
                            Port = mediaPort,
                            Priority = 115
                        });
                    }
                }

                IPAddress routedLan4 = TryGetKernelRoutedLocalAddress(AddressFamily.InterNetwork);
                if (routedLan4 != null)
                {
                    string key = routedLan4.ToString() + ":" + mediaPort.ToString(CultureInfo.InvariantCulture);
                    if (seen.Add(key))
                    {
                        list.Add(new BoBoUdpCandidate
                        {
                            Kind = BoBoUdpCandidate.KindLan,
                            Host = routedLan4.ToString(),
                            Port = mediaPort,
                            Priority = 125
                        });
                    }
                }
            }

            // 3. IPv4 STUN Server-Reflexive endpoints (carry Symmetric NAT hint when observed).
            for (int i = 0; i < ipv4Stun.Count; i++)
            {
                IPEndPoint ep4 = ipv4Stun[i];
                string key4 = ep4.Address.ToString() + ":" + ep4.Port.ToString(CultureInfo.InvariantCulture);
                if (seen.Add(key4))
                {
                    list.Add(new BoBoUdpCandidate
                    {
                        Kind = BoBoUdpCandidate.KindStunSrflx,
                        Host = ep4.Address.ToString(),
                        Port = ep4.Port,
                        Priority = Math.Max(90, 102 - i),
                        SymmetricHint = symmetricIPv4Hint
                    });
                }
            }

            if (mediaPort <= 0 || mediaPort > 65535) return list;

            var ipv6List = new List<string>();
            var lanList = new List<string>();
            try
            {
                NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < nics.Length; i++)
                {
                    NetworkInterface nic = nics[i];
                    if (nic == null || nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    string desc = (nic.Description ?? "") + " " + (nic.Name ?? "");
                    if (desc.IndexOf("Loopback", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    IPInterfaceProperties props = null;
                    try { props = nic.GetIPProperties(); } catch { continue; }
                    if (props == null || props.UnicastAddresses == null) continue;
                    foreach (UnicastIPAddressInformation uni in props.UnicastAddresses)
                    {
                        if (!IsPreferredUnicastAddress(uni)) continue;
                        IPAddress addr = uni != null ? uni.Address : null;
                        if (addr == null) continue;
                        if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
                        if (IsUsableGlobalIPv6(addr))
                        {
                            string s = addr.ToString();
                            int pctIdx = s.IndexOf('%');
                            if (pctIdx > 0) s = s.Substring(0, pctIdx);
                            if (ipv6List.Count < 4) ipv6List.Add(s);
                        }
                        else if (IsPrivateLanIPv4(addr) && lanList.Count < 4)
                        {
                            lanList.Add(addr.ToString());
                        }
                    }
                }
            }
            catch { }

            for (int i = 0; i < ipv6List.Count; i++)
            {
                string key = ipv6List[i] + ":" + mediaPort.ToString(CultureInfo.InvariantCulture);
                if (seen.Add(key))
                {
                    list.Add(new BoBoUdpCandidate
                    {
                        Kind = BoBoUdpCandidate.KindDirectIPv6,
                        Host = ipv6List[i],
                        Port = mediaPort,
                        Priority = 110 - i
                    });
                }
            }
            for (int i = 0; i < lanList.Count; i++)
            {
                string key = lanList[i] + ":" + mediaPort.ToString(CultureInfo.InvariantCulture);
                if (seen.Add(key))
                {
                    list.Add(new BoBoUdpCandidate
                    {
                        Kind = BoBoUdpCandidate.KindLan,
                        Host = lanList[i],
                        Port = mediaPort,
                        Priority = 120 - i
                    });
                }
            }

            return list;
        }

        internal static List<IPEndPoint> ExpandPunchEndpoints(IList<BoBoUdpCandidate> candidates, int maxTotal)
        {
            var result = new List<IPEndPoint>();
            if (candidates == null || candidates.Count == 0 || maxTotal <= 0) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // First pass: add all exact candidate endpoints in priority/rank order
            for (int i = 0; i < candidates.Count && result.Count < maxTotal; i++)
            {
                IPEndPoint ep;
                if (candidates[i] == null || !candidates[i].TryGetIPEndPoint(out ep)) continue;
                IPAddress normAddr = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
                ep = new IPEndPoint(normAddr, ep.Port);
                string key = ep.Address.ToString() + ":" + ep.Port.ToString(CultureInfo.InvariantCulture);
                if (seen.Add(key))
                {
                    result.Add(ep);
                }
            }

            // Second pass: for Symmetric NAT IPv4 srflx endpoints, probe tight sequential CGNAT port offsets
            int[] deltas = new int[] { 1, -1, 2, -2, 3, 4 };
            for (int i = 0; i < candidates.Count && result.Count < maxTotal; i++)
            {
                BoBoUdpCandidate c = candidates[i];
                IPEndPoint ep;
                if (c == null || !c.SymmetricHint || !c.TryGetIPEndPoint(out ep)) continue;
                IPAddress addr = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
                if (addr.AddressFamily != AddressFamily.InterNetwork) continue;

                for (int d = 0; d < deltas.Length && result.Count < maxTotal; d++)
                {
                    int np = ep.Port + deltas[d];
                    if (np < 1024 || np > 65535) continue;
                    string key = addr.ToString() + ":" + np.ToString(CultureInfo.InvariantCulture);
                    if (seen.Add(key))
                    {
                        result.Add(new IPEndPoint(addr, np));
                    }
                }
            }

            return result;
        }

        internal static List<object> ToProtocolObjects(IList<BoBoUdpCandidate> candidates)
        {
            var items = new List<object>();
            if (candidates == null) return items;
            int cap = Math.Min(12, candidates.Count);
            for (int i = 0; i < cap; i++)
            {
                BoBoUdpCandidate c = candidates[i];
                if (c == null || string.IsNullOrWhiteSpace(c.Host) || c.Port <= 0 || c.Port > 65535) continue;
                var obj = new Dictionary<string, object>
                {
                    { "kind", c.Kind ?? BoBoUdpCandidate.KindDirectIPv4 },
                    { "host", c.Host.Trim() },
                    { "port", c.Port },
                    { "priority", c.Priority }
                };
                if (c.SymmetricHint) obj["sym"] = true;
                items.Add(obj);
            }
            return items;
        }

        internal static List<BoBoUdpCandidate> ParseCandidatesFromMessage(Dictionary<string, object> msg)
        {
            return ParseCandidatesFromKey(msg, "candidates");
        }

        internal static List<BoBoUdpCandidate> ParseCandidatesFromKey(Dictionary<string, object> msg, string keyName)
        {
            var result = new List<BoBoUdpCandidate>();
            if (msg == null || string.IsNullOrEmpty(keyName) || !msg.ContainsKey(keyName)) return result;
            object rawObj = msg[keyName];
            var list = rawObj as System.Collections.IEnumerable;
            if (list == null) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (object item in list)
            {
                var d = item as Dictionary<string, object>;
                if (d == null)
                {
                    var idict = item as IDictionary<string, object>;
                    if (idict != null) d = new Dictionary<string, object>(idict);
                }
                if (d == null) continue;
                string kind = BoBoProtocol.GetStr(d, "kind", BoBoUdpCandidate.KindDirectIPv4);
                string host = BoBoProtocol.GetStr(d, "host", "").Trim().Trim('[', ']');
                int port = BoBoProtocol.GetInt(d, "port", 0);
                int prio = BoBoProtocol.GetInt(d, "priority", 50);
                bool symHint = BoBoProtocol.GetBool(d, "sym", false);
                if (port <= 0 || port > 65535 || string.IsNullOrEmpty(host)) continue;

                IPAddress ip;
                if (!IPAddress.TryParse(host, out ip)) continue;
                if (!IsAllowedRemoteCandidateIp(ip)) continue;
                if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

                string normHost = ip.ToString();
                string key = normHost + ":" + port.ToString(CultureInfo.InvariantCulture);
                if (!seen.Add(key)) continue;
                result.Add(new BoBoUdpCandidate
                {
                    Kind = kind,
                    Host = normHost,
                    Port = port,
                    Priority = prio,
                    SymmetricHint = symHint
                });
                if (result.Count >= 12) break;
            }
            return result;
        }

        internal static List<Tuple<string, int>> BuildDefaultStunServers(string customHost, int customPort)
        {
            var servers = new List<Tuple<string, int>>();
            if (!string.IsNullOrWhiteSpace(customHost) && customPort > 0 && customPort <= 65535)
            {
                servers.Add(Tuple.Create(customHost.Trim(), customPort));
            }
            servers.Add(Tuple.Create("stun.miwifi.com", 3478));
            servers.Add(Tuple.Create("stun.chat.bilibili.com", 3478));
            servers.Add(Tuple.Create("stun.qq.com", 3478));
            servers.Add(Tuple.Create("stun.cloudflare.com", 3478));
            servers.Add(Tuple.Create("stun.syncthing.net", 3478));
            return servers;
        }

        internal sealed class StunDualProbeResult
        {
            internal IPEndPoint PrimaryIPv4;
            internal IPEndPoint SecondaryIPv4;
            internal IPEndPoint MappedIPv6;

            internal bool IsSymmetricIPv4
            {
                get
                {
                    return PrimaryIPv4 != null && SecondaryIPv4 != null && PrimaryIPv4.Port != SecondaryIPv4.Port;
                }
            }

            internal List<IPEndPoint> AllEndpoints()
            {
                var list = new List<IPEndPoint>(3);
                if (MappedIPv6 != null) list.Add(MappedIPv6);
                if (PrimaryIPv4 != null) list.Add(PrimaryIPv4);
                if (SecondaryIPv4 != null &&
                    (PrimaryIPv4 == null || !SecondaryIPv4.Equals(PrimaryIPv4)))
                {
                    list.Add(SecondaryIPv4);
                }
                return list;
            }
        }

        internal static byte[] BuildStunBindingRequest(byte[] txId12)
        {
            byte[] req = new byte[20];
            req[0] = (byte)(StunBindingRequest >> 8);
            req[1] = (byte)(StunBindingRequest & 0xFF);
            req[2] = 0;
            req[3] = 0;
            req[4] = (byte)(StunMagicCookie >> 24);
            req[5] = (byte)((StunMagicCookie >> 16) & 0xFF);
            req[6] = (byte)((StunMagicCookie >> 8) & 0xFF);
            req[7] = (byte)(StunMagicCookie & 0xFF);
            if (txId12 != null && txId12.Length >= 12)
            {
                Buffer.BlockCopy(txId12, 0, req, 8, 12);
            }
            return req;
        }

        internal static bool IsPossibleStunResponse(byte[] data, int len)
        {
            if (data == null || len < 20 || len > 512) return false;
            return data[0] == 0x01 && data[1] == 0x01 &&
                   data[4] == 0x21 && data[5] == 0x12 && data[6] == 0xA4 && data[7] == 0x42;
        }

        internal static bool TryParseStunBindingResponse(
            byte[] data,
            int len,
            byte[] expectedTxId12,
            out byte[] txIdOut,
            out IPEndPoint mappedEp)
        {
            txIdOut = null;
            mappedEp = null;
            if (!IsPossibleStunResponse(data, len)) return false;

            int msgLen = (data[2] << 8) | data[3];
            if (20 + msgLen > len) return false;

            if (expectedTxId12 != null)
            {
                if (expectedTxId12.Length < 12) return false;
                for (int i = 0; i < 12; i++)
                {
                    if (data[8 + i] != expectedTxId12[i]) return false;
                }
            }

            txIdOut = new byte[12];
            Buffer.BlockCopy(data, 8, txIdOut, 0, 12);

            IPEndPoint xorEp = null;
            IPEndPoint plainEp = null;
            int offset = 20;
            int end = 20 + msgLen;
            while (offset + 4 <= end)
            {
                int attrType = (data[offset] << 8) | data[offset + 1];
                int attrLen = (data[offset + 2] << 8) | data[offset + 3];
                offset += 4;
                if (attrLen < 0 || offset + attrLen > end) break;

                if (attrType == AttrXorMappedAddress && attrLen >= 8)
                {
                    byte family = data[offset + 1];
                    int port = ((data[offset + 2] << 8) | data[offset + 3]) ^ (int)(StunMagicCookie >> 16);
                    if (family == 0x01 && attrLen >= 8 && port > 0 && port <= 65535)
                    {
                        byte[] ipBytes = new byte[4];
                        for (int i = 0; i < 4; i++) ipBytes[i] = (byte)(data[offset + 4 + i] ^ data[4 + i]);
                        xorEp = new IPEndPoint(new IPAddress(ipBytes), port);
                    }
                    else if (family == 0x02 && attrLen >= 20 && port > 0 && port <= 65535)
                    {
                        byte[] ipBytes = new byte[16];
                        for (int i = 0; i < 16; i++) ipBytes[i] = (byte)(data[offset + 4 + i] ^ data[4 + i]);
                        xorEp = new IPEndPoint(new IPAddress(ipBytes), port);
                    }
                }
                else if (attrType == AttrMappedAddress && attrLen >= 8)
                {
                    byte family = data[offset + 1];
                    int port = (data[offset + 2] << 8) | data[offset + 3];
                    if (family == 0x01 && attrLen >= 8 && port > 0 && port <= 65535)
                    {
                        byte[] ipBytes = new byte[4];
                        Buffer.BlockCopy(data, offset + 4, ipBytes, 0, 4);
                        plainEp = new IPEndPoint(new IPAddress(ipBytes), port);
                    }
                    else if (family == 0x02 && attrLen >= 20 && port > 0 && port <= 65535)
                    {
                        byte[] ipBytes = new byte[16];
                        Buffer.BlockCopy(data, offset + 4, ipBytes, 0, 16);
                        plainEp = new IPEndPoint(new IPAddress(ipBytes), port);
                    }
                }

                int padded = (attrLen + 3) & ~3;
                offset += padded;
            }

            mappedEp = xorEp ?? plainEp;
            return mappedEp != null;
        }

        internal sealed class StunProbeSession
        {
            private readonly ConcurrentDictionary<string, TaskCompletionSource<IPEndPoint>> pending =
                new ConcurrentDictionary<string, TaskCompletionSource<IPEndPoint>>(StringComparer.Ordinal);
            private readonly object rngGate = new object();
            private readonly RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();

            internal bool TryHandlePacket(byte[] data, int len)
            {
                if (!IsPossibleStunResponse(data, len)) return false;
                byte[] txId;
                IPEndPoint mapped;
                if (!TryParseStunBindingResponse(data, len, null, out txId, out mapped) || txId == null)
                    return false;
                string key = Convert.ToBase64String(txId);
                TaskCompletionSource<IPEndPoint> tcs;
                if (pending.TryRemove(key, out tcs))
                {
                    tcs.TrySetResult(mapped);
                    return true;
                }
                return false;
            }

            internal async Task<IPEndPoint> ProbeServerAsync(
                Socket sock,
                string host,
                int port,
                int timeoutMs,
                CancellationToken ct)
            {
                if (sock == null || string.IsNullOrWhiteSpace(host) || port <= 0 || port > 65535)
                    return null;
                try
                {
                    IPAddress[] addrs = null;
                    IPAddress parsed;
                    if (IPAddress.TryParse(host.Trim().Trim('[', ']'), out parsed))
                    {
                        addrs = new IPAddress[] { parsed };
                    }
                    else
                    {
                        Task<IPAddress[]> dnsTask = Dns.GetHostAddressesAsync(host.Trim());
                        Task done = await Task.WhenAny(dnsTask, Task.Delay(Math.Min(600, timeoutMs), ct)).ConfigureAwait(false);
                        if (done != dnsTask) return null;
                        addrs = await dnsTask.ConfigureAwait(false);
                    }
                    if (addrs == null || addrs.Length == 0) return null;

                    IPAddress chosen = null;
                    for (int i = 0; i < addrs.Length; i++)
                    {
                        if (addrs[i].AddressFamily == AddressFamily.InterNetwork)
                        {
                            chosen = addrs[i];
                            break;
                        }
                    }
                    if (chosen == null && sock.AddressFamily == AddressFamily.InterNetworkV6)
                        chosen = addrs[0];
                    if (chosen == null) return null;

                    byte[] txId = new byte[12];
                    lock (rngGate) { rng.GetBytes(txId); }
                    string key = Convert.ToBase64String(txId);
                    var tcs = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
                    pending[key] = tcs;

                    byte[] pkt = BuildStunBindingRequest(txId);
                    var ep = new IPEndPoint(chosen, port);
                    SendDualStack(sock, pkt, pkt.Length, ep);

                    using (ct.Register(delegate { tcs.TrySetCanceled(); }))
                    {
                        Task timeoutTask = Task.Delay(Math.Max(150, timeoutMs), ct);
                        Task finished = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
                        if (finished == tcs.Task && !tcs.Task.IsFaulted && !tcs.Task.IsCanceled)
                        {
                            return await tcs.Task.ConfigureAwait(false);
                        }
                    }
                    TaskCompletionSource<IPEndPoint> removedTcs;
                    pending.TryRemove(key, out removedTcs);
                    return null;
                }
                catch
                {
                    return null;
                }
            }

            private async Task<IPEndPoint> ProbeResolvedEndPointAsync(
                Socket sock,
                IPEndPoint ep,
                int timeoutMs,
                CancellationToken ct)
            {
                if (sock == null || ep == null || ep.Port <= 0 || ep.Port > 65535) return null;
                byte[] txId = new byte[12];
                lock (rngGate) { rng.GetBytes(txId); }
                string key = Convert.ToBase64String(txId);
                var tcs = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
                pending[key] = tcs;
                try
                {
                    byte[] pkt = BuildStunBindingRequest(txId);
                    SendDualStack(sock, pkt, pkt.Length, ep);
                    using (ct.Register(delegate { tcs.TrySetCanceled(); }))
                    {
                        Task timeoutTask = Task.Delay(Math.Max(150, timeoutMs), ct);
                        Task finished = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
                        if (finished == tcs.Task && !tcs.Task.IsFaulted && !tcs.Task.IsCanceled)
                        {
                            return await tcs.Task.ConfigureAwait(false);
                        }
                    }
                    return null;
                }
                catch
                {
                    return null;
                }
                finally
                {
                    TaskCompletionSource<IPEndPoint> removedTcs;
                    pending.TryRemove(key, out removedTcs);
                }
            }

            private async Task ProbeHostTargetsAsync(
                Socket sock,
                string host,
                int port,
                bool canProbeV6,
                int timeoutMs,
                StunDualProbeResult result,
                object resultGate,
                CancellationToken ct)
            {
                if (sock == null || string.IsNullOrWhiteSpace(host) || port <= 0 || port > 65535) return;
                IPAddress[] addrs = null;
                try
                {
                    IPAddress parsed;
                    if (IPAddress.TryParse(host.Trim().Trim('[', ']'), out parsed))
                    {
                        addrs = new IPAddress[] { parsed };
                    }
                    else
                    {
                        Task<IPAddress[]> dnsTask = Dns.GetHostAddressesAsync(host.Trim());
                        Task done = await Task.WhenAny(dnsTask, Task.Delay(Math.Min(550, timeoutMs), ct)).ConfigureAwait(false);
                        if (done != dnsTask) return;
                        addrs = await dnsTask.ConfigureAwait(false);
                    }
                }
                catch { return; }
                if (addrs == null || addrs.Length == 0 || ct.IsCancellationRequested) return;

                IPAddress addr4 = null;
                IPAddress addr6 = null;
                for (int i = 0; i < addrs.Length; i++)
                {
                    if (addrs[i].AddressFamily == AddressFamily.InterNetwork && addr4 == null) addr4 = addrs[i];
                    else if (canProbeV6 && addrs[i].AddressFamily == AddressFamily.InterNetworkV6 && addr6 == null) addr6 = addrs[i];
                }

                var subTasks = new List<Task>(2);
                if (addr4 != null)
                {
                    IPAddress cap4 = addr4;
                    subTasks.Add(Task.Run(async delegate
                    {
                        IPEndPoint mapped = await ProbeResolvedEndPointAsync(sock, new IPEndPoint(cap4, port), timeoutMs, ct).ConfigureAwait(false);
                        if (mapped != null && IsAllowedRemoteCandidateIp(mapped.Address))
                        {
                            IPAddress norm = mapped.Address.IsIPv4MappedToIPv6 ? mapped.Address.MapToIPv4() : mapped.Address;
                            var cleanEp = new IPEndPoint(norm, mapped.Port);
                            lock (resultGate)
                            {
                                if (norm.AddressFamily == AddressFamily.InterNetwork)
                                {
                                    if (result.PrimaryIPv4 == null) result.PrimaryIPv4 = cleanEp;
                                    else if (result.SecondaryIPv4 == null && !cleanEp.Equals(result.PrimaryIPv4))
                                        result.SecondaryIPv4 = cleanEp;
                                }
                                else if (result.MappedIPv6 == null)
                                {
                                    result.MappedIPv6 = cleanEp;
                                }
                            }
                        }
                    }));
                }
                if (addr6 != null)
                {
                    IPAddress cap6 = addr6;
                    subTasks.Add(Task.Run(async delegate
                    {
                        IPEndPoint mapped = await ProbeResolvedEndPointAsync(sock, new IPEndPoint(cap6, port), timeoutMs, ct).ConfigureAwait(false);
                        if (mapped != null && IsAllowedRemoteCandidateIp(mapped.Address))
                        {
                            IPAddress norm = mapped.Address.IsIPv4MappedToIPv6 ? mapped.Address.MapToIPv4() : mapped.Address;
                            var cleanEp = new IPEndPoint(norm, mapped.Port);
                            lock (resultGate)
                            {
                                if (norm.AddressFamily == AddressFamily.InterNetworkV6 && result.MappedIPv6 == null)
                                    result.MappedIPv6 = cleanEp;
                            }
                        }
                    }));
                }
                if (subTasks.Count > 0)
                {
                    try { await Task.WhenAll(subTasks).ConfigureAwait(false); } catch { }
                }
            }

            internal async Task<StunDualProbeResult> ProbeDualStackAsync(
                Socket sock,
                IList<Tuple<string, int>> servers,
                int timeoutMs,
                CancellationToken ct)
            {
                var result = new StunDualProbeResult();
                if (sock == null || servers == null || servers.Count == 0) return result;
                bool canProbeV6 = (sock.AddressFamily == AddressFamily.InterNetworkV6);
                var resultGate = new object();

                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    var hostTasks = new List<Task>(servers.Count);
                    for (int i = 0; i < servers.Count; i++)
                    {
                        Tuple<string, int> srv = servers[i];
                        if (srv == null) continue;
                        hostTasks.Add(ProbeHostTargetsAsync(sock, srv.Item1, srv.Item2, canProbeV6, timeoutMs, result, resultGate, linked.Token));
                    }
                    if (hostTasks.Count == 0) return result;

                    Task allTask = Task.WhenAll(hostTasks);
                    Task hardTimeout = Task.Delay(Math.Max(200, timeoutMs), linked.Token);
                    long firstV4Ticks = 0;
                    while (!allTask.IsCompleted && !hardTimeout.IsCompleted)
                    {
                        bool haveBoth;
                        bool haveOneV4;
                        lock (resultGate)
                        {
                            haveOneV4 = result.PrimaryIPv4 != null;
                            haveBoth = result.PrimaryIPv4 != null && result.SecondaryIPv4 != null &&
                                (!canProbeV6 || result.MappedIPv6 != null);
                        }
                        if (haveBoth) break;
                        if (haveOneV4 && firstV4Ticks == 0)
                        {
                            firstV4Ticks = Stopwatch.GetTimestamp();
                        }
                        if (firstV4Ticks != 0 &&
                            ((Stopwatch.GetTimestamp() - firstV4Ticks) * 1000.0 / Stopwatch.Frequency) >= 240.0)
                        {
                            break;
                        }
                        await Task.WhenAny(allTask, hardTimeout, Task.Delay(40, linked.Token)).ConfigureAwait(false);
                    }
                    try { linked.Cancel(); } catch { }
                }
                return result;
            }

            internal async Task<IPEndPoint> ProbeFirstValidAsync(
                Socket sock,
                IList<Tuple<string, int>> servers,
                int timeoutMs,
                CancellationToken ct)
            {
                if (sock == null || servers == null || servers.Count == 0) return null;
                var tasks = new List<Task<IPEndPoint>>(servers.Count);
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    for (int i = 0; i < servers.Count; i++)
                    {
                        Tuple<string, int> srv = servers[i];
                        if (srv == null) continue;
                        tasks.Add(ProbeServerAsync(sock, srv.Item1, srv.Item2, timeoutMs, linked.Token));
                    }
                    while (tasks.Count > 0)
                    {
                        Task<IPEndPoint> done = await Task.WhenAny(tasks).ConfigureAwait(false);
                        tasks.Remove(done);
                        IPEndPoint res = null;
                        try { res = await done.ConfigureAwait(false); } catch { }
                        if (res != null && IsAllowedRemoteCandidateIp(res.Address))
                        {
                            try { linked.Cancel(); } catch { }
                            return res;
                        }
                    }
                }
                return null;
            }
        }

        private static int elevatedFirewallConfigured;

        internal static void TryEnsureElevatedFirewallRules()
        {
            if (Interlocked.Exchange(ref elevatedFirewallConfigured, 1) != 0) return;
            Task.Run(delegate
            {
                try
                {
                    using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                    {
                        if (id == null) return;
                        var principal = new WindowsPrincipal(id);
                        if (!principal.IsInRole(WindowsBuiltInRole.Administrator)) return;
                    }
                    string exePath = Process.GetCurrentProcess().MainModule.FileName;
                    if (string.IsNullOrWhiteSpace(exePath)) return;
                    string quoted = "\"" + exePath + "\"";
                    RunHiddenNetsh("advfirewall firewall delete rule name=\"BoBo Remote (UDP-In)\"");
                    RunHiddenNetsh("advfirewall firewall delete rule name=\"BoBo Remote (TCP-In)\"");
                    RunHiddenNetsh("advfirewall firewall add rule name=\"BoBo Remote (UDP-In)\" dir=in action=allow program=" + quoted + " enable=yes profile=any protocol=UDP localport=49811,49812");
                    RunHiddenNetsh("advfirewall firewall add rule name=\"BoBo Remote (TCP-In)\" dir=in action=allow program=" + quoted + " enable=yes profile=any protocol=TCP localport=49810");
                }
                catch { }
            });
        }

        private static void RunHiddenNetsh(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("netsh", args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (Process p = Process.Start(psi))
                {
                    if (p != null) p.WaitForExit(2000);
                }
            }
            catch { }
        }
    }
}
