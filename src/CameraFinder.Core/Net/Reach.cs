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

    /// <summary>The laptop's address in the same subnet as <paramref name="ip"/> (the one to send from), or null.</summary>
    public static IPAddress? SourceFor(IEnumerable<(IPAddress Address, int Prefix)> addresses, IPAddress ip) =>
        addresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork && Subnet.Of(a.Address, a.Prefix).Contains(ip)).Address;

    /// <summary>Through <paramref name="adapter"/>: one of its addresses is in the device's subnet, or a one-device route leads to it.</summary>
    public static bool CanReach(Adapter adapter, IPAddress ip) => SourceFor(adapter, ip) != null;

    /// <summary>The laptop's address to send to <paramref name="ip"/> from, on <paramref name="adapter"/>, or null.</summary>
    public static IPAddress? SourceFor(Adapter adapter, IPAddress ip) =>
        SourceFor(adapter.Addresses, ip) ?? adapter.HostRoutes.FirstOrDefault(r => r.Device.Equals(ip)).Source;

    /// <summary>
    /// Ranges the app may add a whole-range temporary address in: private ranges, 192.0.0.0/24 and link-local.
    /// Devices elsewhere (public-looking addresses on a closed camera network) are reached through a one-device route
    /// instead, so the laptop's traffic to the rest of that range (the internet) is not touched.
    /// </summary>
    public static bool MayJoin(IPAddress ip) => Ipv4.IsUsableHost(ip) && (Ipv4.IsJoinable(ip) || Ipv4.IsLinkLocal(ip));

    /// <summary>A device the app can reach through a one-device route: any usable address outside <see cref="MayJoin"/>'s ranges.</summary>
    public static bool NeedsHostRoute(IPAddress ip) => Ipv4.IsUsableHost(ip) && !MayJoin(ip);
}
