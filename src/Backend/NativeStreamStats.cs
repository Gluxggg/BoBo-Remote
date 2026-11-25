namespace DualRemote
{
    /// <summary>内置串流解码器与 HUD 共用的实时性能快照。</summary>
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
        internal string Transport = "TCP+TLS";
    }
}
