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
        internal IPAddress RemoteAddress { get; private set; }
        internal readonly double InitialRttMs;
        internal double LatestPathRttMs { get; private set; }
        internal BoBoRouteKind Kind { get; private set; }
        internal int TargetPort { get; private set; }
        internal string CustomRelayId { get; private set; }
        internal string CustomRelayName { get; private set; }
        internal string CustomRelayEndpoint { get; private set; }
        internal bool CustomRelayHasLoad { get; private set; }
        internal double CustomRelayLoad { get; private set; }
        internal bool CustomRelayAccepting { get; private set; }
        internal bool CustomRelayAutoScheduled { get; private set; }
        internal BoBoRelaySelectionMode CustomRelaySelectionMode { get; private set; }
        internal int CustomRelayUdpPort { get; private set; }
        internal int CustomRelayStunPort { get; private set; }

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
                    ((Kind == BoBoRouteKind.TailnetPeerRelay || Kind == BoBoRouteKind.CustomRelay)
                        && (effectiveRtt >= 55 || (CustomRelayHasLoad && CustomRelayLoad >= 0.80)));
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
                        string baseLabel;
                        if (!string.IsNullOrEmpty(CustomRelayName)) baseLabel = "自建兜底中转 · " + CustomRelayName;
                        else if (!string.IsNullOrEmpty(CustomRelayEndpoint)) baseLabel = "自建兜底中转 · " + CustomRelayEndpoint;
                        else baseLabel = "自建兜底中转";

                        if (!CustomRelayHasLoad && !CustomRelayAutoScheduled)
                            return baseLabel;

                        string schedTag = "";
                        if (CustomRelayAutoScheduled)
                        {
                            if (CustomRelaySelectionMode == BoBoRelaySelectionMode.LowestLatency)
                                schedTag = "延迟优选";
                            else if (CustomRelaySelectionMode == BoBoRelaySelectionMode.DynamicLoadBalanced)
                                schedTag = "动态优选";
                        }

                        string loadTag = "";
                        if (CustomRelayHasLoad)
                        {
                            if (!CustomRelayAccepting)
                                loadTag = "维护中";
                            else
                                loadTag = "负载 " + (int)Math.Round(Math.Max(0.0, Math.Min(1.0, CustomRelayLoad)) * 100.0) + "%";
                        }

                        if (!string.IsNullOrEmpty(schedTag) && !string.IsNullOrEmpty(loadTag))
                            return baseLabel + "（" + schedTag + " · " + loadTag + "）";
                        if (!string.IsNullOrEmpty(schedTag))
                            return baseLabel + "（" + schedTag + "）";
                        if (!string.IsNullOrEmpty(loadTag))
                            return baseLabel + "（" + loadTag + "）";
                        return baseLabel;
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
            CustomRelayId = "";
            CustomRelayName = "";
            CustomRelayEndpoint = "";
            CustomRelayAccepting = true;
            if (RemoteAddress == null) return;
            if (Tailscale.IsTailnetAddress(RemoteAddress.ToString()))
                Kind = BoBoRouteKind.TailnetUnknown;
            else if (IsOnLocalSubnet(RemoteAddress, local))
                Kind = BoBoRouteKind.Lan;
        }

        internal void UpdateCustomRelayPorts(int udpPort, int stunPort)
        {
            if (Kind != BoBoRouteKind.CustomRelay) return;
            if (udpPort > 0 && udpPort <= 65535) CustomRelayUdpPort = udpPort;
            if (stunPort > 0 && stunPort <= 65535) CustomRelayStunPort = stunPort;
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

        internal static BoBoConnectionRoute CreateScheduledCustomRelay(
            IPAddress remote,
            IPAddress local,
            double rttMs,
            BoBoCustomRelay relay,
            BoBoRelaySelectionMode mode,
            bool hasLoad,
            double load,
            bool accepting)
        {
            int port = relay != null && relay.Port > 0 && relay.Port <= 65535 ? relay.Port : BoBoProtocol.DefaultTcpPort;
            string name = relay != null ? relay.Name : "";
            string endpoint = relay != null ? relay.EndpointText : "";
            var route = CreateCustomRelay(remote, local, rttMs, port, name, endpoint);
            route.CustomRelayId = relay != null ? (relay.Id ?? "") : "";
            route.CustomRelaySelectionMode = mode;
            route.CustomRelayAutoScheduled = mode != BoBoRelaySelectionMode.Manual;
            route.CustomRelayHasLoad = hasLoad;
            route.CustomRelayLoad = Math.Max(0.0, Math.Min(1.0, load));
            route.CustomRelayAccepting = accepting;
            return route;
        }

        internal void UpdateCustomRelayMetrics(double rttMs, bool hasLoad, double load, bool accepting)
        {
            if (Kind != BoBoRouteKind.CustomRelay) return;
            if (rttMs > 0.0) LatestPathRttMs = rttMs;
            CustomRelayHasLoad = hasLoad;
            CustomRelayLoad = hasLoad ? Math.Max(0.0, Math.Min(1.0, load)) : 0.0;
            CustomRelayAccepting = accepting;
        }

        internal void SwitchCustomRelay(
            IPAddress remote,
            double rttMs,
            BoBoCustomRelay relay,
            BoBoRelaySelectionMode mode,
            bool hasLoad,
            double load,
            bool accepting)
        {
            IPAddress norm = NormalizeAddress(remote);
            if (norm != null) RemoteAddress = norm;
            Kind = BoBoRouteKind.CustomRelay;
            TargetPort = relay != null && relay.Port > 0 && relay.Port <= 65535 ? relay.Port : BoBoProtocol.DefaultTcpPort;
            CustomRelayId = relay != null ? (relay.Id ?? "") : "";
            CustomRelayName = relay != null ? (relay.Name ?? "").Trim() : "";
            CustomRelayEndpoint = relay != null ? (relay.EndpointText ?? "").Trim() : "";
            if (rttMs > 0.0) LatestPathRttMs = rttMs;
            CustomRelaySelectionMode = mode;
            CustomRelayAutoScheduled = mode != BoBoRelaySelectionMode.Manual;
            CustomRelayHasLoad = hasLoad;
            CustomRelayLoad = hasLoad ? Math.Max(0.0, Math.Min(1.0, load)) : 0.0;
            CustomRelayAccepting = accepting;
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
