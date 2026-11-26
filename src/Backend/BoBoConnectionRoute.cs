using System;
using System.Net;
using System.Net.NetworkInformation;

namespace DualRemote
{
    internal enum BoBoRoutePreference
    {
        Auto,
        LanOnly,
        Tailnet
    }

    internal enum BoBoRouteKind
    {
        Address,
        Lan,
        TailnetUnknown,
        TailnetDirect,
        TailnetDerp,
        TailnetPeerRelay
    }

    internal sealed class BoBoConnectionRoute
    {
        internal readonly IPAddress RemoteAddress;
        internal readonly double InitialRttMs;
        internal BoBoRouteKind Kind { get; private set; }

        internal bool IsTailnet { get { return Kind >= BoBoRouteKind.TailnetUnknown; } }
        internal bool PreferWeakNetworkProfile
        {
            get { return InitialRttMs >= 75 || Kind == BoBoRouteKind.TailnetDerp || Kind == BoBoRouteKind.TailnetPeerRelay; }
        }

        internal string Label
        {
            get
            {
                switch (Kind)
                {
                    case BoBoRouteKind.Lan: return "局域网直连";
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
            if (RemoteAddress == null) return;
            if (Tailscale.IsTailnetAddress(RemoteAddress.ToString()))
                Kind = BoBoRouteKind.TailnetUnknown;
            else if (IsOnLocalSubnet(RemoteAddress, local))
                Kind = BoBoRouteKind.Lan;
        }

        internal void UpdateTailnetPath(PingPong pong)
        {
            if (!IsTailnet) return;
            Kind = BoBoRouteKind.TailnetUnknown;
            if (pong == null) return;
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
