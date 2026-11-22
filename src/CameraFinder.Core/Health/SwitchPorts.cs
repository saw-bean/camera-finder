using System.Net;

namespace CameraFinder.Core.Health;

/// <summary>A managed switch that answered SNMP and has a bridge (MAC address) table.</summary>
public sealed record SwitchInfo(IPAddress Ip, string Community, IPAddress? Source, string? Name, string? Description);

/// <summary>Where a device is plugged in, and how that port is doing.</summary>
public sealed class SwitchPortReport
{
    public required SwitchInfo Switch { get; init; }
    public required int IfIndex { get; init; }
    public string? PortName { get; set; }
    public string? PortAlias { get; set; }
    /// <summary>Negotiated link speed in Mbit/s (0 = no link).</summary>
    public long? SpeedMbps { get; set; }
    /// <summary>"full", "half" or null when the switch does not say.</summary>
    public string? Duplex { get; set; }
    public bool? Up { get; set; }
    /// <summary>How many MAC addresses the switch has learned on this port: 1 is a device's own port, many is an uplink or another switch.</summary>
    public int MacsOnPort { get; set; }
    /// <summary>Input errors (CRC/FCS, alignment) seen while the device was watched; null until a second reading.</summary>
    public long? ErrorsDuringCheck { get; set; }
    public double? FromDeviceKbps { get; set; }
    public double? ToDeviceKbps { get; set; }

    public string Describe() =>
        $"{Switch.Name ?? Switch.Ip.ToString()} ({Switch.Ip}) port {PortName ?? IfIndex.ToString()}" +
        (string.IsNullOrWhiteSpace(PortAlias) ? "" : $" \"{PortAlias}\"");
}

/// <summary>One reading of a port's traffic, from the device's point of view.</summary>
public readonly record struct PortSample(DateTime Time, double FromDeviceKbps, double ToDeviceKbps, long NewErrors);

/// <summary>
/// Finds the managed switches on the site and the port each device is plugged into, using only standard MIBs
/// every managed switch has: SNMPv2-MIB (name), BRIDGE-MIB / Q-BRIDGE-MIB (which MAC is learned on which port),
/// IF-MIB (port name, speed, traffic counters, errors) and EtherLike-MIB (duplex). Read-only.
/// A device can show up on several switches (on their uplinks); the port with the fewest MACs is where it is plugged in.
/// </summary>
public sealed class SwitchDirectory
{
    public const string SysDescr = "1.3.6.1.2.1.1.1.0", SysName = "1.3.6.1.2.1.1.5.0", BaseNumPorts = "1.3.6.1.2.1.17.1.2.0";
    public const string Dot1dTpFdbPort = "1.3.6.1.2.1.17.4.3.1.2", Dot1qTpFdbPort = "1.3.6.1.2.1.17.7.1.2.2.1.2", BasePortIfIndex = "1.3.6.1.2.1.17.1.4.1.2";
    public const string IfName = "1.3.6.1.2.1.31.1.1.1.1", IfAlias = "1.3.6.1.2.1.31.1.1.1.18", IfHighSpeed = "1.3.6.1.2.1.31.1.1.1.15",
        IfOperStatus = "1.3.6.1.2.1.2.2.1.8", IfDescr = "1.3.6.1.2.1.2.2.1.2",
        IfHCInOctets = "1.3.6.1.2.1.31.1.1.1.6", IfHCOutOctets = "1.3.6.1.2.1.31.1.1.1.10",
        IfInOctets = "1.3.6.1.2.1.2.2.1.10", IfOutOctets = "1.3.6.1.2.1.2.2.1.16", IfInErrors = "1.3.6.1.2.1.2.2.1.14",
        Dot3Duplex = "1.3.6.1.2.1.10.7.2.1.19";

    private readonly ISnmpClient _snmp;
    private readonly List<SwitchInfo> _switches = new();
    private readonly Dictionary<IPAddress, (DateTime At, List<(string Mac, int IfIndex)> Table)> _fdb = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Task? _discovery;

    public SwitchDirectory(ISnmpClient snmp) => _snmp = snmp;

    public IReadOnlyList<SwitchInfo> Switches { get { lock (_switches) return _switches.ToList(); } }
    public bool Discovered => _discovery?.IsCompleted == true;

    /// <summary>
    /// Asks every candidate address (once per scan) whether it is a managed switch. Read communities are tried in order;
    /// <paramref name="sourceFor"/> gives the laptop address to ask from (null = skip, not reachable).
    /// </summary>
    public Task DiscoverAsync(IEnumerable<IPAddress> candidates, IReadOnlyList<string> communities, Func<IPAddress, IPAddress?> sourceFor, CancellationToken ct)
    {
        lock (_switches)
            return _discovery ??= Task.Run(() => RunDiscovery(candidates.ToList(), communities, sourceFor, ct), ct);
    }

    /// <summary>Forget the result so the next <see cref="DiscoverAsync"/> asks again (e.g. a community was entered).</summary>
    public void Reset()
    {
        lock (_switches) { _discovery = null; _switches.Clear(); }
        lock (_fdb) _fdb.Clear();
    }

    private async Task RunDiscovery(List<IPAddress> candidates, IReadOnlyList<string> communities, Func<IPAddress, IPAddress?> sourceFor, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(16);
        await Task.WhenAll(candidates.Select(async ip =>
        {
            var source = sourceFor(ip);
            await gate.WaitAsync(ct);
            try
            {
                foreach (var community in communities)
                {
                    var r = await _snmp.GetAsync(ip, community, new[] { SysName, SysDescr, BaseNumPorts }, source, ct);
                    if (r == null) continue; // wrong community or no SNMP
                    if (r.Count == 3 && !r[2].Missing && r[2].Long > 0)
                        lock (_switches) _switches.Add(new SwitchInfo(ip, community, source, r[0].Text, r[1].Text));
                    break; // it answered: right community, switch or not
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* unreachable: skip */ }
            finally { gate.Release(); }
        }));
    }

    /// <summary>The port <paramref name="mac"/> is plugged into, or null if no switch has learned it.</summary>
    public async Task<SwitchPortReport?> LocateAsync(string mac, CancellationToken ct)
    {
        string want = NormalizeMac(mac);
        SwitchPortReport? best = null;
        foreach (var sw in Switches)
        {
            var table = await FdbAsync(sw, ct);
            var hit = table.FirstOrDefault(e => e.Mac == want);
            if (hit.Mac == null) continue;
            int count = table.Where(e => e.IfIndex == hit.IfIndex).Select(e => e.Mac).Distinct().Count();
            if (best == null || count < best.MacsOnPort)
                best = new SwitchPortReport { Switch = sw, IfIndex = hit.IfIndex, MacsOnPort = count };
        }
        if (best != null) await ReadPortAsync(best, ct);
        return best;
    }

    /// <summary>MAC -> ifIndex table of one switch, cached for 30 s (it changes slowly).</summary>
    public async Task<List<(string Mac, int IfIndex)>> FdbAsync(SwitchInfo sw, CancellationToken ct)
    {
        lock (_fdb)
            if (_fdb.TryGetValue(sw.Ip, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(30)) return c.Table;
        await _lock.WaitAsync(ct);
        try
        {
            var portToIf = new Dictionary<int, int>();
            foreach (var v in await _snmp.WalkAsync(sw.Ip, sw.Community, BasePortIfIndex, sw.Source, ct))
                if (v.Long is { } ifIndex && LastIndex(v.Oid) is { } bp) portToIf[bp] = (int)ifIndex;

            var table = new List<(string, int)>();
            // VLAN-aware table first (index: vlan . 6 MAC bytes), then the classic one (index: 6 MAC bytes).
            foreach (var root in new[] { Dot1qTpFdbPort, Dot1dTpFdbPort })
            {
                foreach (var v in await _snmp.WalkAsync(sw.Ip, sw.Community, root, sw.Source, ct))
                {
                    if (v.Long is not { } bport || bport <= 0) continue; // 0 = the switch's own MAC
                    var idx = v.Oid[(root.Length + 1)..].Split('.');
                    if (idx.Length < 6) continue;
                    string mac = string.Concat(idx[^6..].Select(b => int.Parse(b).ToString("X2")));
                    table.Add((mac, portToIf.TryGetValue((int)bport, out var ifi) ? ifi : (int)bport));
                }
                if (table.Count > 0) break;
            }
            lock (_fdb) _fdb[sw.Ip] = (DateTime.UtcNow, table);
            return table;
        }
        finally { _lock.Release(); }
    }

    private async Task ReadPortAsync(SwitchPortReport p, CancellationToken ct)
    {
        string i = "." + p.IfIndex;
        var r = await _snmp.GetAsync(p.Switch.Ip, p.Switch.Community,
            new[] { IfName + i, IfDescr + i, IfAlias + i, IfHighSpeed + i, IfOperStatus + i, Dot3Duplex + i }, p.Switch.Source, ct);
        if (r == null || r.Count < 6) return;
        p.PortName = !r[0].Missing ? r[0].Text : !r[1].Missing ? r[1].Text : null;
        p.PortAlias = r[2].Missing ? null : r[2].Text;
        p.SpeedMbps = r[3].Missing ? null : r[3].Long;
        p.Up = r[4].Missing ? null : r[4].Long == 1;
        p.Duplex = r[5].Missing ? null : r[5].Long switch { 2 => "half", 3 => "full", _ => null };
    }

    /// <summary>
    /// Watches a port's counters until cancelled (every <paramref name="interval"/>). On the switch, "in" is traffic
    /// arriving from the device and "out" is traffic going to it. Uses 64-bit counters when the switch has them.
    /// </summary>
    public async Task MonitorAsync(SwitchPortReport p, TimeSpan interval, Action<PortSample> onSample, CancellationToken ct)
    {
        string i = "." + p.IfIndex;
        var hc = new[] { IfHCInOctets + i, IfHCOutOctets + i, IfInErrors + i };
        var legacy = new[] { IfInOctets + i, IfOutOctets + i, IfInErrors + i };
        bool use64 = true;
        (long In, long Out, long Err, DateTime At)? last = null;
        long errorsTotal = 0;
        while (!ct.IsCancellationRequested)
        {
            var r = await _snmp.GetAsync(p.Switch.Ip, p.Switch.Community, use64 ? hc : legacy, p.Switch.Source, ct);
            if (r != null && use64 && (r.Count < 2 || r[0].Missing)) { use64 = false; continue; }
            var now = DateTime.UtcNow;
            if (r is { Count: 3 } && r[0].Long is { } inO && r[1].Long is { } outO)
            {
                long err = r[2].Long ?? 0;
                if (last is { } l)
                {
                    double secs = (now - l.At).TotalSeconds;
                    long dIn = Delta(l.In, inO, use64), dOut = Delta(l.Out, outO, use64), dErr = Math.Max(0, err - l.Err);
                    errorsTotal += dErr;
                    p.ErrorsDuringCheck = errorsTotal;
                    if (secs > 0)
                    {
                        p.FromDeviceKbps = dIn * 8 / secs / 1000;
                        p.ToDeviceKbps = dOut * 8 / secs / 1000;
                        onSample(new PortSample(DateTime.Now, p.FromDeviceKbps.Value, p.ToDeviceKbps.Value, dErr));
                    }
                }
                last = (inO, outO, err, now);
            }
            await Task.Delay(interval, ct);
        }
    }

    /// <summary>Counter difference, allowing for a 32-bit counter wrapping once.</summary>
    public static long Delta(long before, long after, bool is64) =>
        after >= before ? after - before : is64 ? 0 : (long)(uint.MaxValue - (ulong)before) + after + 1;

    public static string NormalizeMac(string mac) => new string(mac.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    private static int? LastIndex(string oid) => int.TryParse(oid[(oid.LastIndexOf('.') + 1)..], out var n) ? n : null;
}
