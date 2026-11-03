using System.Collections.Concurrent;
using System.Net;
using CameraFinder.Core.Capture;
using CameraFinder.Core.Model;
using CameraFinder.Core.Net;

namespace CameraFinder.Core.Discovery;

public sealed class ScanOptions
{
    /// <summary>How long to listen passively for devices announcing themselves.</summary>
    public TimeSpan ListenTime { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait for replies to ONVIF/SSDP probes.</summary>
    public TimeSpan ProbeTime { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Safe mode: only listen and send discovery probes. Never adds addresses to the laptop and never sweeps.
    /// Use on sensitive client networks.
    /// </summary>
    public bool SafeMode { get; init; }

    /// <summary>Maximum number of extra ranges to join automatically.</summary>
    public int MaxExtraRanges { get; init; } = 6;

    /// <summary>Hide the laptop itself (Windows answers its own SSDP searches). Only turned off in tests.</summary>
    public bool HideOwnAddresses { get; init; } = true;
}

/// <summary>
/// Runs the whole discovery: listen, probe, join ranges, ARP sweep, then read details.
///   1. Listen passively (Npcap) and send ONVIF + SSDP probes at the same time.
///   2. Work out which IP ranges are in use on the cable.
///   3. (not in safe mode) Add a temporary address in each new range and ARP-sweep every range.
///   4. Probe again from the new addresses, then check ports and read web page titles.
/// </summary>
public sealed class ScanEngine
{
    private readonly IArpResolver _arp;
    private readonly TempAddressManager? _temp;
    private readonly OuiDatabase _oui;
    private readonly ConcurrentDictionary<IPAddress, Device> _devices = new();
    private readonly ConcurrentDictionary<IPAddress, string> _ssdpDescriptions = new();
    private readonly ConcurrentDictionary<IPAddress, byte> _mentioned = new(); // IPs seen without a MAC (range hints)
    private HashSet<IPAddress> _own = new();
    private bool _hideOwn = true;

    public event Action<string>? Log;
    public event Action<Device>? DeviceChanged;
    public event Action<string, int>? Progress; // phase text, percent 0-100

    public ScanEngine(IArpResolver arp, TempAddressManager? temp, OuiDatabase? oui = null)
    {
        _arp = arp;
        _temp = temp;
        _oui = oui ?? OuiDatabase.Default;
    }

    public IReadOnlyCollection<Device> Devices => _devices.Values.ToList();

    public async Task RunAsync(Adapter adapter, ScanOptions options, CancellationToken ct)
    {
        _devices.Clear();
        _ssdpDescriptions.Clear();
        _mentioned.Clear();
        var ownAddresses = adapter.Addresses.Select(a => a.Address).ToHashSet();
        _own = ownAddresses;
        _hideOwn = options.HideOwnAddresses;
        Say($"Scanning on {adapter}.");

        // ---- 1. Listen + probe -------------------------------------------------------------------------
        Report("Listening and probing", 0);
        Task listen = Task.CompletedTask;
        if (PassiveListener.IsAvailable(out var why))
        {
            string? dev = PassiveListener.FindDevice(adapter.Id, adapter.Name);
            if (dev != null)
            {
                Say($"Listening passively for {options.ListenTime.TotalSeconds:0} s...");
                listen = Task.Run(() =>
                {
                    try { PassiveListener.Listen(dev, options.ListenTime, ct, o => OnObservation(o, ownAddresses)); }
                    catch (Exception ex) { Say("Passive listening stopped: " + ex.Message); }
                }, ct);
            }
            else Say("Npcap is installed but this adapter was not found for capture; skipping passive listening.");
        }
        else Say(why!);

        await ProbeAllAsync(adapter.Addresses.Select(a => a.Address), options, ct);

        // Show a countdown while the passive listener runs.
        var started = DateTime.UtcNow;
        while (!listen.IsCompleted)
        {
            int pct = (int)Math.Min(40, 40 * (DateTime.UtcNow - started) / options.ListenTime);
            Report("Listening for devices", pct);
            await Task.WhenAny(listen, Task.Delay(500, ct));
        }
        ct.ThrowIfCancellationRequested();

        // ---- 2. Which ranges are on this cable? ---------------------------------------------------------
        var ownSubnets = adapter.Addresses.Select(a => (Subnet: Subnet.Of(a.Address, a.Prefix), a.Address))
            .Where(s => !Ipv4.IsLinkLocal(s.Address) || adapter.Addresses.Count == 1).ToList();
        var seenIps = _devices.Keys.Concat(_mentioned.Keys).ToList();
        var newRanges = seenIps
            .Where(ip => !Ipv4.IsLinkLocal(ip) && Ipv4.IsPrivate(ip) && !adapter.Addresses.Any(a => Subnet.Of(a.Address, a.Prefix).Contains(ip)))
            .Select(ip => Subnet.Of(ip, 24))
            .Distinct()
            .OrderByDescending(s => seenIps.Count(ip => s.Contains(ip)))
            .ToList();
        var linkLocalDevices = _devices.Keys.Where(Ipv4.IsLinkLocal).ToList();

        if (newRanges.Count > 0) Say("Other ranges seen on this cable: " + string.Join(", ", newRanges));
        if (linkLocalDevices.Count > 0) Say($"{linkLocalDevices.Count} device(s) with a self-assigned 169.254.x.x address.");

        // ---- 3. Join ranges and sweep ---------------------------------------------------------------------
        var sweepPlan = new List<(Subnet Range, IPAddress Source, List<IPAddress> Only)>();
        foreach (var (subnet, addr) in ownSubnets.Where(s => !Ipv4.IsLinkLocal(s.Address)))
            sweepPlan.Add((ArpSweeper.SweepRange(subnet, addr), addr, new()));

        if (options.SafeMode)
        {
            Say("Safe mode: not adding addresses and not sweeping. Only listening and probe replies are shown.");
        }
        else if (!_arp.IsSupported || _temp == null)
        {
            Say("ARP sweep is only available on Windows.");
            sweepPlan.Clear();
        }
        else
        {
            var avoid = new HashSet<IPAddress>(seenIps);
            foreach (var range in newRanges.Take(options.MaxExtraRanges))
            {
                Report($"Joining {range}", 42);
                var ip = await _temp.AddFreeAddressAsync(adapter, range, avoid, ct);
                if (ip != null) { sweepPlan.Add((range, ip, new())); ownAddresses.Add(ip); }
            }
            if (newRanges.Count > options.MaxExtraRanges)
                Say($"Skipped {newRanges.Count - options.MaxExtraRanges} more range(s) (limit {options.MaxExtraRanges}).");

            if (linkLocalDevices.Count > 0)
            {
                var ll = adapter.Addresses.FirstOrDefault(a => Ipv4.IsLinkLocal(a.Address)).Address
                    ?? await _temp.AddFreeAddressAsync(adapter, Subnet.Parse("169.254.0.0/16"), avoid, ct);
                if (ll != null) { sweepPlan.Add((Subnet.Parse("169.254.0.0/16"), ll, linkLocalDevices)); ownAddresses.Add(ll); }
            }

            for (int i = 0; i < sweepPlan.Count; i++)
            {
                var (range, source, only) = sweepPlan[i];
                Say(only.Count > 0 ? $"Checking {only.Count} self-assigned device(s)..." : $"ARP sweep of {range} from {source}...");
                int baseline = 45 + 30 * i / sweepPlan.Count;
                var found = await ArpSweeper.SweepAsync(_arp, range, source, only, 48,
                    (done, total) => Report($"Sweeping {range}", baseline + 30 * done / Math.Max(1, total) / sweepPlan.Count), ct);
                foreach (var (ip, mac) in found)
                    if (!ownAddresses.Contains(ip)) Upsert(ip, mac, DiscoverySource.ArpSweep);
                Say($"  {found.Count} device(s) answered in {range}.");
            }

            // Probe again from the new addresses: devices in those ranges can now reply to us directly.
            var extra = sweepPlan.Select(p => p.Source).Where(s => !adapter.Addresses.Any(a => a.Address.Equals(s))).ToList();
            if (extra.Count > 0)
            {
                Report("Probing new ranges", 76);
                await ProbeAllAsync(extra, options, ct);
            }
        }

        // ---- 4. Details -------------------------------------------------------------------------------------
        var list = _devices.Values.ToList();
        Say($"Reading details from {list.Count} device(s)...");
        int doneCount = 0;
        using (var enricher = new DeviceEnricher())
        using (var gate = new SemaphoreSlim(16))
        {
            await Task.WhenAll(list.Select(async d =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    _ssdpDescriptions.TryGetValue(d.Ip, out var desc);
                    await enricher.EnrichAsync(d, desc, ct);
                    DeviceChanged?.Invoke(d);
                }
                finally
                {
                    gate.Release();
                    Report("Reading device details", 80 + 20 * Interlocked.Increment(ref doneCount) / Math.Max(1, list.Count));
                }
            }));
        }

        Report("Done", 100);
        Say($"Scan finished: {list.Count} device(s), {list.Count(d => d.Kind == DeviceKind.Camera)} camera(s).");
    }

    private async Task ProbeAllAsync(IEnumerable<IPAddress> sources, ScanOptions options, CancellationToken ct)
    {
        var tasks = new List<Task>();
        foreach (var src in sources)
        {
            tasks.Add(MulticastProbe.RunAsync(src, OnvifProbe.Group, OnvifProbe.Probes(), options.ProbeTime, (from, data) =>
            {
                var hit = OnvifProbe.Parse(from.Address, data);
                if (hit != null) Merge(hit, DiscoverySource.Onvif);
            }, ct));
            tasks.Add(MulticastProbe.RunAsync(src, SsdpProbe.Group, SsdpProbe.Probes(), options.ProbeTime, (from, data) =>
            {
                var hit = SsdpProbe.Parse(from.Address, data);
                if (hit != null) Merge(hit, DiscoverySource.Ssdp);
            }, ct));
        }
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Say("Probe error: " + ex.Message); }
    }

    private void OnObservation(Observation o, HashSet<IPAddress> own)
    {
        if (own.Contains(o.Ip)) return;
        if (o.Mac == null) { _mentioned.TryAdd(o.Ip, 0); return; }
        Upsert(o.Ip, o.Mac, DiscoverySource.Passive);
    }

    private void Merge(ProbeHit hit, DiscoverySource source)
    {
        if (!Ipv4.IsUsableHost(hit.Ip)) return;
        if (_hideOwn && _own.Contains(hit.Ip)) return;
        var d = _devices.GetOrAdd(hit.Ip, ip => new Device(ip));
        lock (d)
        {
            d.Sources |= source;
            d.Model ??= hit.Model;
            d.Name ??= hit.Name;
            d.Server ??= hit.Server;
            if (hit.Manufacturer != null && d.Vendor == null) d.Vendor = hit.Manufacturer;
        }
        if (hit.DescriptionUrl != null) _ssdpDescriptions.TryAdd(hit.Ip, hit.DescriptionUrl);
        DeviceChanged?.Invoke(d);
    }

    private void Upsert(IPAddress ip, string mac, DiscoverySource source)
    {
        var d = _devices.GetOrAdd(ip, i => new Device(i));
        bool changed;
        lock (d)
        {
            changed = d.Mac == null || !d.Sources.HasFlag(source);
            d.Mac ??= mac;
            d.Sources |= source;
            var vendor = _oui.Lookup(d.Mac);
            if (vendor != null && d.Vendor == null) d.Vendor = vendor;
        }
        if (changed) DeviceChanged?.Invoke(d);
    }

    private void Say(string message) => Log?.Invoke(message);
    private void Report(string phase, int percent) => Progress?.Invoke(phase, Math.Clamp(percent, 0, 100));
}
