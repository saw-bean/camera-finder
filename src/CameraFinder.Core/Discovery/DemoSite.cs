using System.Net;
using CameraFinder.Core.Capture;
using CameraFinder.Core.Model;
using CameraFinder.Core.Net;

namespace CameraFinder.Core.Discovery;

/// <summary>
/// A made-up site for testing and training: cameras of several brands spread over several IP ranges, a door
/// controller, a camera on a factory-default address and one with a self-assigned address. It stands in for the
/// network card, Npcap, ARP and netsh, so a demo scan sends nothing and never changes the laptop's settings.
/// The laptop is 192.168.1.50/24.
/// </summary>
public sealed class DemoSite : IPassiveCapture, IArpResolver, IAddressJoiner, IPinger
{
    /// <param name="Heard">Transmits on its own (ARP, DHCP, broadcast), so passive listening hears it.</param>
    /// <param name="MentionedOnly">Never transmits, but other traffic asks for its address (an ARP request target).</param>
    public sealed record DemoDevice(string Ip, string Mac, string Model, string? Title, int[] Ports, bool Camera,
        bool Heard = false, bool MentionedOnly = false);

    public static readonly IReadOnlyList<DemoDevice> Devices = new[]
    {
        new DemoDevice("192.168.1.1", "24:5A:4C:10:00:01", "UniFi Dream Machine Pro", "UniFi OS", new[] { 80, 443 }, false, Heard: true),
        new DemoDevice("192.168.1.10", "44:19:B6:20:00:10", "DS-7616NI-I2/16P (NVR)", "Hikvision NVR", new[] { 80, 554, 8000 }, true),
        new DemoDevice("192.168.1.64", "44:19:B6:20:00:64", "DS-2CD2143G2-I", "Hikvision", new[] { 80, 443, 554, 8000 }, true, Heard: true),
        new DemoDevice("192.168.1.108", "38:AF:29:30:01:08", "IPC-HDW2431T-AS", "WEB SERVICE", new[] { 80, 554 }, true),
        new DemoDevice("192.168.1.120", "00:18:85:40:01:20", "H5A Bullet", "Avigilon", new[] { 443, 554 }, true),
        new DemoDevice("10.0.5.21", "00:40:8C:50:05:21", "AXIS P3245-V", "AXIS", new[] { 80, 443, 554 }, true, Heard: true),
        new DemoDevice("10.0.5.40", "D4:2D:C5:50:05:40", "WV-S2136", "WV-S2136 Network Camera", new[] { 80, 554 }, true),
        new DemoDevice("10.0.5.60", "00:0B:D6:50:05:60", "Net2 Plus door controller", null, Array.Empty<int>(), false, Heard: true),
        new DemoDevice("172.16.8.1", "00:04:63:60:08:01", "VIDEOJET decoder 7000", "Bosch VIDEOJET", new[] { 80, 443 }, false, MentionedOnly: true),
        new DemoDevice("172.16.8.30", "00:09:18:60:08:30", "XNV-6080R", "Wisenet", new[] { 80, 443, 554 }, true),
        new DemoDevice("192.0.0.64", "44:19:B6:70:00:64", "DS-2CD1043G0-I (NOT ACTIVATED)", "Hikvision - activate device", new[] { 80, 554, 8000 }, true, Heard: true),
        new DemoDevice("169.254.12.34", "00:40:8C:80:12:34", "AXIS M3106-L Mk II", "AXIS", new[] { 80 }, true, Heard: true),
    };

    public static readonly IPAddress LaptopAddress = IPAddress.Parse("192.168.1.50");

    private readonly Dictionary<string, DemoDevice> _byIp = Devices.ToDictionary(d => d.Ip);
    private readonly List<(IPAddress Address, int Prefix)> _joined = new();
    private readonly object _lock = new();

    /// <summary>Simulated delay of one ARP request, so the sweep progress bar moves like a real one.</summary>
    public TimeSpan ArpDelay { get; init; } = TimeSpan.FromMilliseconds(8);

    /// <summary>Simulated time to add a temporary address (Windows checks it is free first).</summary>
    public TimeSpan JoinDelay { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Temporary addresses "added" so far (nothing is really changed).</summary>
    public IReadOnlyList<(IPAddress Address, int Prefix)> Joined { get { lock (_lock) return _joined.ToList(); } }

    /// <summary>The demo laptop, including any temporary addresses it has "joined".</summary>
    public Adapter Laptop
    {
        get
        {
            var addresses = new List<(IPAddress, int)> { (LaptopAddress, 24) };
            addresses.AddRange(Joined);
            return new Adapter("demo", "Demo network", "Demo site (simulated)", 0, "02:00:00:00:00:50", true, true,
                addresses, new List<IPAddress> { IPAddress.Parse("192.168.1.1") });
        }
    }

    /// <summary>Scan settings for a demo scan: nothing is sent on the real network.</summary>
    public static ScanOptions Options(TimeSpan listen, bool safeMode) => new()
    {
        ListenTime = listen, ProbeTime = TimeSpan.FromMilliseconds(200), SafeMode = safeMode,
        SendProbes = false, VendorProtocols = false,
    };

    // ---- IPassiveCapture: plays the traffic the laptop would hear, spread over the listen time.

    public bool IsAvailable(out string? error) { error = null; return true; }
    public string? FindDevice(string adapterId, string adapterName) => "demo0";

    public void Listen(string device, TimeSpan duration, CancellationToken ct, Action<Observation> onObservation)
    {
        var events = new List<Observation>();
        foreach (var d in Devices)
        {
            if (d.Heard) events.Add(new Observation(IPAddress.Parse(d.Ip), d.Mac));
            if (d.MentionedOnly) events.Add(new Observation(IPAddress.Parse(d.Ip), null));
        }
        var gap = TimeSpan.FromTicks(Math.Max(0, duration.Ticks / (events.Count + 1)));
        foreach (var o in events)
        {
            if (ct.WaitHandle.WaitOne(gap)) return;
            onObservation(o);
        }
        ct.WaitHandle.WaitOne(gap);
    }

    // ---- IArpResolver: a device answers only when asked from an address in its own range, like a real one.

    public bool IsSupported => true;

    public async Task<string?> ResolveAsync(IPAddress target, IPAddress source, CancellationToken ct)
    {
        if (ArpDelay > TimeSpan.Zero) await Task.Delay(ArpDelay, ct);
        if (!Reach.RangeFor(target).Contains(source)) return null;
        return _byIp.TryGetValue(target.ToString(), out var d) ? d.Mac : null;
    }

    // ---- IAddressJoiner: "adds" the temporary address without touching the laptop.

    public async Task<IPAddress?> AddFreeAddressAsync(Adapter adapter, Subnet subnet, ISet<IPAddress> avoid, CancellationToken ct)
    {
        if (JoinDelay > TimeSpan.Zero) await Task.Delay(JoinDelay, ct);
        lock (_lock)
        {
            var existing = _joined.FirstOrDefault(j => subnet.Contains(j.Address));
            if (existing.Address != null) return existing.Address;
            var ip = TempAddressManager.Candidates(subnet).FirstOrDefault(c => !avoid.Contains(c) && !_byIp.ContainsKey(c.ToString()));
            if (ip != null) _joined.Add((ip, subnet.Prefix));
            return ip;
        }
    }

    // ---- IPinger: devices answer when the laptop is in their range. The Dahua camera "reboots" for 10 s of every
    // 60 s, so the ping window's DOWN / UP alerts can be tried out.

    public const string RebootingDevice = "192.168.1.108";
    private readonly Random _rnd = new();

    public async Task<PingResult> PingAsync(IPAddress target, IPAddress? source, TimeSpan timeout, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(5), ct);
        bool rebooting = target.ToString() == RebootingDevice && DateTime.UtcNow.Second is >= 30 and < 40;
        if (!_byIp.ContainsKey(target.ToString()) || !Reach.CanReach(Laptop.Addresses, target) || rebooting)
        {
            await Task.Delay(timeout, ct);
            return new PingResult(false, 0, "Request timed out");
        }
        int ms; lock (_rnd) ms = _rnd.Next(1, 5);
        return new PingResult(true, ms);
    }

    public void ForgetJoined() { lock (_lock) _joined.Clear(); }

    /// <summary>
    /// Demo version of "read details" (ports, web page title, model). Like the real one, it only works when the laptop
    /// has an address in the device's range.
    /// </summary>
    public Task ReadDetailsAsync(Device device, string? ssdpDescriptionUrl, CancellationToken ct) => ReadDetailsAsync(device, ct);

    public Task ReadDetailsAsync(Device device, CancellationToken ct)
    {
        if (!_byIp.TryGetValue(device.Ip.ToString(), out var d) || !Reach.CanReach(Laptop.Addresses, device.Ip)) return Task.CompletedTask;
        lock (device)
        {
            foreach (var p in d.Ports) device.OpenPorts.Add(p);
            device.Model ??= d.Model;
            device.WebTitle ??= d.Title;
            if (d.Camera) device.CameraProtocol = true;
            device.WebUrl ??= DeviceEnricher.PickWebUrl(device.Ip, d.Ports);
        }
        return Task.CompletedTask;
    }
}
