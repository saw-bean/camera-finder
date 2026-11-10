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
    /// <summary>The device is in a public range; the app never adds addresses there.</summary>
    NotJoinable,
    /// <summary>Reachable, but nothing answers on 80/443/8080.</summary>
    NoWebPage,
}

public sealed record WebPageResult(WebPageStatus Status, string? Url = null, Subnet? Range = null, IPAddress? TemporaryAddress = null);

/// <summary>
/// "Open web page" for any device in the list, even one in a range the laptop is not in: if needed, adds a temporary
/// (virtual) address in the device's range, re-checks its ports, and returns the page address.
/// </summary>
public sealed class WebPageAccess
{
    private readonly IAddressJoiner? _joiner;
    private readonly Func<Adapter?> _adapter;
    private readonly Func<Device, CancellationToken, Task> _readDetails;

    /// <param name="joiner">Adds temporary addresses (null = not possible, e.g. not Windows).</param>
    /// <param name="currentAdapter">The scan adapter as it is now (including temporary addresses already added).</param>
    /// <param name="readDetails">Checks ports and reads the page title (DeviceEnricher in the app).</param>
    public WebPageAccess(IAddressJoiner? joiner, Func<Adapter?> currentAdapter, Func<Device, CancellationToken, Task> readDetails)
    {
        _joiner = joiner;
        _adapter = currentAdapter;
        _readDetails = readDetails;
    }

    /// <param name="mayJoin">False in safe mode: report <see cref="WebPageStatus.NeedsTemporaryAddress"/> instead of adding one.</param>
    /// <param name="avoid">Addresses known to be in use (other devices), never picked as the temporary address.</param>
    public async Task<WebPageResult> PrepareAsync(Device device, bool mayJoin, ISet<IPAddress> avoid, CancellationToken ct)
    {
        var adapter = _adapter();
        var range = Reach.RangeFor(device.Ip);
        IPAddress? added = null;

        if (adapter == null || !Reach.CanReach(adapter.Addresses, device.Ip))
        {
            if (!Reach.MayJoin(device.Ip)) return new WebPageResult(WebPageStatus.NotJoinable, Range: range);
            if (!mayJoin) return new WebPageResult(WebPageStatus.NeedsTemporaryAddress, Range: range);
            if (_joiner == null || adapter == null) return new WebPageResult(WebPageStatus.JoinFailed, Range: range);
            added = await _joiner.AddFreeAddressAsync(adapter, range, avoid, ct);
            if (added == null) return new WebPageResult(WebPageStatus.JoinFailed, Range: range);
        }

        // Check again even when a URL is known: the page may only now be reachable, or may have moved port.
        if (added != null || device.WebUrl == null) await _readDetails(device, ct);
        return device.WebUrl != null
            ? new WebPageResult(WebPageStatus.Ready, device.WebUrl, range, added)
            : new WebPageResult(WebPageStatus.NoWebPage, Range: range, TemporaryAddress: added);
    }
}
