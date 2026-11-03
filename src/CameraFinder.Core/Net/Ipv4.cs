using System.Net;
using System.Net.Sockets;

namespace CameraFinder.Core.Net;

/// <summary>Small helpers for IPv4 maths. Addresses are handled as host-order uint.</summary>
public static class Ipv4
{
    public static uint ToUInt(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("IPv4 only", nameof(ip));
        var b = ip.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    public static IPAddress FromUInt(uint v) =>
        new(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });

    public static uint MaskFromPrefix(int prefix) =>
        prefix <= 0 ? 0u : prefix >= 32 ? 0xFFFFFFFFu : 0xFFFFFFFFu << (32 - prefix);

    public static int PrefixFromMask(IPAddress mask)
    {
        uint m = ToUInt(mask);
        int p = 0;
        while ((m & 0x80000000u) != 0) { p++; m <<= 1; }
        return p;
    }

    public static bool IsLinkLocal(IPAddress ip) => (ToUInt(ip) & 0xFFFF0000u) == 0xA9FE0000u; // 169.254/16

    public static bool IsPrivate(IPAddress ip)
    {
        uint v = ToUInt(ip);
        return (v & 0xFF000000u) == 0x0A000000u      // 10/8
            || (v & 0xFFF00000u) == 0xAC100000u      // 172.16/12
            || (v & 0xFFFF0000u) == 0xC0A80000u;     // 192.168/16
    }

    /// <summary>True for addresses that should never be treated as a device: 0.0.0.0, broadcast, multicast, loopback.</summary>
    public static bool IsUsableHost(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        uint v = ToUInt(ip);
        if (v == 0 || v == 0xFFFFFFFFu) return false;
        if ((v & 0xF0000000u) == 0xE0000000u) return false;  // multicast 224/4
        if ((v & 0xFF000000u) == 0x7F000000u) return false;  // loopback
        if ((v & 0xFF000000u) == 0) return false;            // 0/8
        return true;
    }
}

/// <summary>An IPv4 subnet such as 10.0.5.0/24.</summary>
public readonly record struct Subnet(uint Network, int Prefix)
{
    public static Subnet Of(IPAddress ip, int prefix)
    {
        uint mask = Ipv4.MaskFromPrefix(prefix);
        return new Subnet(Ipv4.ToUInt(ip) & mask, prefix);
    }

    public static Subnet Parse(string cidr)
    {
        var parts = cidr.Split('/');
        return Of(IPAddress.Parse(parts[0]), parts.Length > 1 ? int.Parse(parts[1]) : 24);
    }

    public uint Mask => Ipv4.MaskFromPrefix(Prefix);
    public IPAddress NetworkAddress => Ipv4.FromUInt(Network);
    public IPAddress MaskAddress => Ipv4.FromUInt(Mask);
    public uint Broadcast => Network | ~Mask;
    public long HostCount => Prefix >= 31 ? (1L << (32 - Prefix)) : (1L << (32 - Prefix)) - 2;

    public bool Contains(IPAddress ip) => (Ipv4.ToUInt(ip) & Mask) == Network;

    /// <summary>All usable host addresses (excludes network and broadcast for prefixes up to /30).</summary>
    public IEnumerable<IPAddress> Hosts()
    {
        if (Prefix >= 31)
        {
            for (uint a = Network; a <= Broadcast && a >= Network; a++) yield return Ipv4.FromUInt(a);
            yield break;
        }
        for (uint a = Network + 1; a < Broadcast; a++) yield return Ipv4.FromUInt(a);
    }

    public override string ToString() => $"{NetworkAddress}/{Prefix}";
}
