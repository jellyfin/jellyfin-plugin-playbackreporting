using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Plugin.Jellystics.Collectors;

/// <summary>Local vs remote from Jellyfin's RemoteEndPoint (same rules as Jellystics netutil).</summary>
public static class NetworkClassifier
{
    public static bool? IsRemote(string? remoteEndPoint)
    {
        if (string.IsNullOrWhiteSpace(remoteEndPoint)) return null;
        var s = remoteEndPoint.Trim();
        if (s.StartsWith('[')) { var end = s.IndexOf(']'); if (end > 0) s = s[1..end]; }
        else if (s.Count(ch => ch == ':') == 1) s = s[..s.IndexOf(':')];
        if (!IPAddress.TryParse(s, out var ip)) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return false;   // unspecified counts as local (netutil)
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] >= 224 && b[0] <= 239) return false;   // multicast
            var local = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
            return !local;
        }
        var v6 = ip.GetAddressBytes();
        if (v6[0] == 0xFF) return false;   // multicast
        var ula = (v6[0] & 0xFE) == 0xFC;
        // fec0::/10 (deprecated site-local) is kept local on purpose: some LANs still use it.
        return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ula);
    }
}
