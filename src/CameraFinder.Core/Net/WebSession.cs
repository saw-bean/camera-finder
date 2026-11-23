using System.Net;

namespace CameraFinder.Core.Net;

/// <summary>
/// While a device's web page is open, the laptop's network is switched over to the device's network, so the
/// browser (and anything else on the laptop) reaches the device exactly as if the laptop had been given an IP in
/// that range by hand. Ending the session puts everything back.
///
/// Two changes are made (both recorded in the state file first, so a crash is undone on the next start):
/// <list type="bullet">
/// <item>The temporary address in the device's range becomes a normal ("main") address. Temporary addresses are
/// normally added with skipassource, which stops Windows from ever sending from them, so the browser would send from
/// the laptop's own address and the device could not answer.</item>
/// <item>A route to the device (a /32, active only, never saved) is added on the scan adapter with the best metric,
/// so the device is reached over that cable even if Wi-Fi or a VPN uses the same range.</item>
/// </list>
/// </summary>
/// <param name="AdapterName">The adapter the session is on.</param>
/// <param name="Source">The laptop's address in the device's range (what the browser now sends from).</param>
/// <param name="Device">The device whose page is open.</param>
/// <param name="Range">The device's range.</param>
/// <param name="SourceSwitched">True when <paramref name="Source"/> was a temporary address that was made a main address.</param>
/// <param name="RouteAdded">True when the route to the device was added.</param>
public sealed record WebSession(string AdapterName, IPAddress Source, IPAddress Device, Subnet Range, bool SourceSwitched, bool RouteAdded)
{
    /// <summary>
    /// Whether opening <paramref name="device"/> needs the network switched over. Not when the laptop already has a
    /// normal address in the device's range and no other adapter competes for that range.
    /// </summary>
    /// <param name="sourceIsTemporary">The address to reach the device from is a temporary (skipassource) one.</param>
    /// <param name="otherAdapters">The laptop's other adapters (Wi-Fi, VPN ...), not the scan adapter.</param>
    public static bool Needed(bool sourceIsTemporary, IEnumerable<Adapter> otherAdapters, IPAddress device) =>
        sourceIsTemporary || otherAdapters.Any(a => Reach.CanReach(a.Addresses, device));

    public override string ToString() => $"{Device} via {AdapterName} from {Source}";
}
