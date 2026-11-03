using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CameraFinder.Core.Net;

/// <summary>A network adapter on this laptop that can be used for scanning.</summary>
public sealed record Adapter(
    string Id,
    string Name,
    string Description,
    int IfIndex,
    string Mac,
    bool IsWired,
    bool DhcpEnabled,
    IReadOnlyList<(IPAddress Address, int Prefix)> Addresses,
    IReadOnlyList<IPAddress> Gateways)
{
    public IEnumerable<Subnet> Subnets => Addresses.Select(a => Subnet.Of(a.Address, a.Prefix));

    public bool HasAddressIn(Subnet subnet) => Addresses.Any(a => subnet.Contains(a.Address));

    public override string ToString()
    {
        string ips = Addresses.Count == 0 ? "no IPv4" : string.Join(", ", Addresses.Select(a => $"{a.Address}/{a.Prefix}"));
        return $"{Name} ({ips})";
    }

    /// <summary>Lists adapters that are up and are Ethernet or Wi-Fi. Wired adapters first.</summary>
    public static List<Adapter> List()
    {
        var result = new List<Adapter>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            bool wired = nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit;
            bool wifi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            if (!wired && !wifi) continue;

            // Skip common virtual adapters (Hyper-V, VirtualBox, VMware, VPNs) - they are not on the site network.
            string d = nic.Description.ToLowerInvariant();
            if (d.Contains("virtual") || d.Contains("hyper-v") || d.Contains("vmware") || d.Contains("vpn")
                || d.Contains("loopback") || d.Contains("tap-") || d.Contains("wireguard")) continue;

            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); } catch { continue; }

            var addrs = props.UnicastAddresses
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(u => (u.Address, u.PrefixLength))
                .ToList();

            int index = 0;
            bool dhcp = false;
            try
            {
                var v4 = props.GetIPv4Properties();
                if (v4 != null) { index = v4.Index; dhcp = OperatingSystem.IsWindows() && v4.IsDhcpEnabled; }
            }
            catch { /* not supported on every OS */ }

            var gws = props.GatewayAddresses.Select(g => g.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any)).ToList();

            result.Add(new Adapter(nic.Id, nic.Name, nic.Description, index,
                OuiDatabase.FormatMac(nic.GetPhysicalAddress().GetAddressBytes()), wired, dhcp, addrs, gws));
        }
        return result.OrderByDescending(a => a.IsWired).ThenBy(a => a.Name).ToList();
    }
}
