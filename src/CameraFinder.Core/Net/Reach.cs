using System.Net;
using System.Net.Sockets;

namespace CameraFinder.Core.Net;

/// <summary>Works out whether the laptop can reach a device directly, or must first join the device's range.</summary>
public static class Reach
{
    /// <summary>The subnet the laptop needs an address in to reach <paramref name="ip"/>: its /24, or 169.254/16.</summary>
    public static Subnet RangeFor(IPAddress ip) => Ipv4.IsLinkLocal(ip) ? Subnet.Of(ip, 16) : Subnet.Of(ip, 24);

    /// <summary>True when one of <paramref name="addresses"/> is in the same subnet as <paramref name="ip"/>.</summary>
    public static bool CanReach(IEnumerable<(IPAddress Address, int Prefix)> addresses, IPAddress ip) =>
        addresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork && Subnet.Of(a.Address, a.Prefix).Contains(ip));

    /// <summary>Ranges the app may add a temporary address in: private ranges, 192.0.0.0/24 and link-local.</summary>
    public static bool MayJoin(IPAddress ip) => Ipv4.IsUsableHost(ip) && (Ipv4.IsJoinable(ip) || Ipv4.IsLinkLocal(ip));
}
