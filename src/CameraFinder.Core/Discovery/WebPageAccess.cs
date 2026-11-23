using System.Net;
using CameraFinder.Core.Model;
using CameraFinder.Core.Net;

namespace CameraFinder.Core.Discovery;

public enum WebPageStatus
{
    /// <summary><see cref="WebPageResult.Url"/> can be opened now.</summary>
    Ready,
    /// <summary>The laptop is not in the device's range; asking first (safe mode) was requested.</summary>
    NeedsTemporaryAddress,
    /// <summary>Adding a temporary address in the device's range failed (e.g. no admin rights, all candidates in use).</summary>
    JoinFailed,
    /// <summary>Not a usable device address (0.0.0.0, broadcast, multicast, loopback).</summary>
    NotJoinable,
    /// <summary>Reachable, but nothing answers on 80/443/8080.</summary>
    NoWebPage,
}

/// <param name="Source">The laptop's address in the device's range (to send from).</param>
public sealed record WebPageResult(WebPageStatus Status, string? Url = null, Subnet? Range = null, IPAddress? TemporaryAddress = null,
    IPAddress? Source = null);

/// <summary>
/// "Open web page" for any device in the list, whatever range it is in: if needed, adds a temporary (virtual) address
/// on the scan adapter - in the device's range for private and link-local ranges, or an address plus a route for that
/// one device in public-looking ranges, so the laptop's internet traffic is left alone - re-checks its ports, and
/// returns the page address.
/// </summary>
public sealed class WebPageAccess
{
    private readonly IAddressJoiner? _joiner;
    private readonly Func<Adapter?> _adapter;
    private readonly Func<Device, IPAddress?, CancellationToken, Task> _readDetails;

    /// <param name="joiner">Adds temporary addresses (null = not possible, e.g. not Windows).</param>
    /// <param name="currentAdapter">The scan adapter as it is now (including temporary addresses already added).</param>
    /// <param name="readDetails">Checks ports and reads the page title (DeviceEnricher in the app), connecting from the given laptop address.</param>
    public WebPageAccess(IAddressJoiner? joiner, Func<Adapter?> currentAdapter, Func<Device, IPAddress?, CancellationToken, Task> readDetails)
    {
        _joiner = joiner;
        _adapter = currentAdapter;
        _readDetails = readDetails;
    }

    /// <summary>
    /// Makes sure the laptop has an address in the device's range, adding a temporary one if needed and allowed.
    /// Returns <see cref="WebPageStatus.Ready"/> with <see cref="WebPageResult.Source"/> = the laptop's address in that range.
    /// </summary>
    public async Task<WebPageResult> EnsureReachableAsync(IPAddress ip, bool mayJoin, ISet<IPAddress> avoid, CancellationToken ct)
    {
        var adapter = _adapter();
        var range = Reach.RangeFor(ip);
        var source = adapter == null ? null : Reach.SourceFor(adapter, ip);
        if (source != null) return new WebPageResult(WebPageStatus.Ready, Range: range, Source: source);

        if (!Ipv4.IsUsableHost(ip)) return new WebPageResult(WebPageStatus.NotJoinable, Range: range);
        if (!mayJoin) return new WebPageResult(WebPageStatus.NeedsTemporaryAddress, Range: range);
        if (_joiner == null || adapter == null) return new WebPageResult(WebPageStatus.JoinFailed, Range: range);
        var added = Reach.NeedsHostRoute(ip)
            ? await _joiner.AddHostRouteAsync(adapter, ip, avoid, ct)
            : await _joiner.AddFreeAddressAsync(adapter, range, avoid, ct, near: ip);
        return added == null
            ? new WebPageResult(WebPageStatus.JoinFailed, Range: range)
            : new WebPageResult(WebPageStatus.Ready, Range: range, TemporaryAddress: added, Source: added);
    }

    /// <param name="mayJoin">False in safe mode: report <see cref="WebPageStatus.NeedsTemporaryAddress"/> instead of adding one.</param>
    /// <param name="avoid">Addresses known to be in use (other devices), never picked as the temporary address.</param>
    public async Task<WebPageResult> PrepareAsync(Device device, bool mayJoin, ISet<IPAddress> avoid, CancellationToken ct)
    {
        var reach = await EnsureReachableAsync(device.Ip, mayJoin, avoid, ct);
        if (reach.Status != WebPageStatus.Ready) return reach;
        var range = reach.Range;
        var added = reach.TemporaryAddress;

        // Check again even when a URL is known: the page may only now be reachable, or may have moved port.
        if (added != null || device.WebUrl == null) await _readDetails(device, reach.Source, ct);
        return device.WebUrl != null
            ? new WebPageResult(WebPageStatus.Ready, device.WebUrl, range, added, reach.Source)
            : new WebPageResult(WebPageStatus.NoWebPage, Range: range, TemporaryAddress: added, Source: reach.Source);
    }
}
