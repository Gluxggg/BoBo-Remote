using System;
using System.Net;
using System.Net.NetworkInformation;

namespace DualRemote
{
    internal enum BoBoRoutePreference
    {
        Auto = 0,
        LanOnly = 1,
        Tailnet = 2,
        CustomRelayFallback = 3
    }

    internal enum BoBoRouteKind
    {
        Address,
        Lan,
        CustomRelay,
        TailnetUnknown,
        TailnetDirect,
        TailnetDerp,
        TailnetPeerRelay
    }

    internal sealed class BoBoConnectionRoute
    {
        internal readonly IPAddress RemoteAddress;
        internal readonly double InitialRttMs;
        internal double LatestPathRttMs { get; private set; }
        internal BoBoRouteKind Kind { get; private set; }
        internal int TargetPort { get; private set; }
        internal string CustomRelayName { get; private set; }
        internal string CustomRelayEndpoint { get; private set; }

        internal bool IsTailnet
        {
            get
            {
                return Kind == BoBoRouteKind.TailnetUnknown
                    || Kind == BoBoRouteKind.TailnetDirect
                    || Kind == BoBoRouteKind.TailnetDerp
                    || Kind == BoBoRouteKind.TailnetPeerRelay;
            }
        }

        internal bool PreferWeakNetworkProfile
        {
            get
            {
                double effectiveRtt = LatestPathRttMs > 0.1 ? LatestPathRttMs : InitialRttMs;
                return effectiveRtt >= 75 || Kind == BoBoRouteKind.TailnetDerp ||
                    ((Kind == BoBoRouteKind.TailnetPeerRelay || Kind == BoBoRouteKind.CustomRelay) && effectiveRtt >= 55);
            }
        }

        internal string Label
        {
            get
            {
                switch (Kind)
                {
                    case BoBoRouteKind.Lan: return "局域网直连";
                    case BoBoRouteKind.CustomRelay:
                        if (!string.IsNullOrEmpty(CustomRelayName)) return "自建兜底中转 · " + CustomRelayName;
                        if (!string.IsNullOrEmpty(CustomRelayEndpoint)) return "自建兜底中转 · " + CustomRelayEndpoint;
                        return "自建兜底中转";
                    case BoBoRouteKind.TailnetDirect: return "Tailscale P2P 直连";
                    case BoBoRouteKind.TailnetDerp: return "Tailscale DERP 中继";
                    case BoBoRouteKind.TailnetPeerRelay: return "Tailscale 节点中继";
                    case BoBoRouteKind.TailnetUnknown: return "Tailscale · 路径待确认";
                    default: return "指定地址 · 路径待确认";
                }
            }
        }

        internal BoBoConnectionRoute(IPAddress remote, IPAddress local, double rttMs)
        {
            RemoteAddress = NormalizeAddress(remote);
            InitialRttMs = rttMs;
            LatestPathRttMs = rttMs;
            TargetPort = BoBoProtocol.DefaultTcpPort;
            CustomRelayName = "";
            CustomRelayEndpoint = "";
            if (RemoteAddress == null) return;
            if (Tailscale.IsTailnetAddress(RemoteAddress.ToString()))
                Kind = BoBoRouteKind.TailnetUnknown;
            else if (IsOnLocalSubnet(RemoteAddress, local))
                Kind = BoBoRouteKind.Lan;
        }

        internal static BoBoConnectionRoute CreateCustomRelay(
            IPAddress remote, IPAddress local, double rttMs, int port, string relayName, string relayEndpoint)
        {
            var route = new BoBoConnectionRoute(remote, local, rttMs);
            route.Kind = BoBoRouteKind.CustomRelay;
            route.TargetPort = port > 0 && port <= 65535 ? port : BoBoProtocol.DefaultTcpPort;
            route.CustomRelayName = (relayName ?? "").Trim();
            route.CustomRelayEndpoint = (relayEndpoint ?? "").Trim();
            return route;
        }

        internal void UpdateTailnetPath(PingPong pong)
        {
            if (!IsTailnet) return;
            Kind = BoBoRouteKind.TailnetUnknown;
            if (pong == null)
            {
                LatestPathRttMs = InitialRttMs;
                return;
            }
            if (pong.LatencyMs > 0.0) LatestPathRttMs = pong.LatencyMs;
            if (pong.Direct) Kind = BoBoRouteKind.TailnetDirect;
            else if (pong.Route.StartsWith("DERP(", StringComparison.OrdinalIgnoreCase)) Kind = BoBoRouteKind.TailnetDerp;
            else if (pong.Route.StartsWith("peer-relay(", StringComparison.OrdinalIgnoreCase)) Kind = BoBoRouteKind.TailnetPeerRelay;
        }

        private static IPAddress NormalizeAddress(IPAddress address)
        {
            return address != null && address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        }

        internal static bool IsOnLocalSubnet(IPAddress remote, IPAddress local)
        {
            remote = NormalizeAddress(remote);
            local = NormalizeAddress(local);
            if (remote == null || IPAddress.IsLoopback(remote) || Tailscale.IsTailnetAddress(remote.ToString())) return false;
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!BoBoProtocol.IsLanInterface(nic)) continue;
                    foreach (UnicastIPAddressInformation entry in nic.GetIPProperties().UnicastAddresses)
                    {
                        IPAddress address = entry.Address;
                        if (address == null || address.AddressFamily != remote.AddressFamily ||
                            (local != null && !local.Equals(address)) || address.Equals(remote)) continue;
                        if (remote.IsIPv6LinkLocal && remote.ScopeId != address.ScopeId) continue;
                        if (HasSamePrefix(address, remote, entry.PrefixLength)) return true;
                    }
                }
            }
            catch (NetworkInformationException) { }
            catch (PlatformNotSupportedException) { }
            return false;
        }

        internal static bool HasSamePrefix(IPAddress first, IPAddress second, int prefixLength)
        {
            byte[] a = first.GetAddressBytes();
            byte[] b = second.GetAddressBytes();
            if (a.Length != b.Length || prefixLength <= 0 || prefixLength > a.Length * 8) return false;
            int wholeBytes = prefixLength / 8;
            for (int i = 0; i < wholeBytes; i++)
                if (a[i] != b[i]) return false;
            int remaining = prefixLength % 8;
            return remaining == 0 || ((a[wholeBytes] ^ b[wholeBytes]) >> (8 - remaining)) == 0;
        }
    }
}
