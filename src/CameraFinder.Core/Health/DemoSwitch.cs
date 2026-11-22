using System.Net;
using System.Text;
using CameraFinder.Core.Discovery;

namespace CameraFinder.Core.Health;

/// <summary>
/// The demo site's router (192.168.1.1, a UniFi Dream Machine Pro) answers SNMP like a managed switch: every other
/// demo device is learned on its own port, and the port counters move at believable rates - each camera sends its
/// video, the NVR receives everyone's. The lossy demo camera (10.0.5.40) also produces CRC errors, on a half-duplex
/// 100 Mbit/s link. Only community "public" is accepted.
/// </summary>
public sealed class DemoSwitch : ISnmpClient
{
    public static readonly IPAddress Address = IPAddress.Parse("192.168.1.1");
    private readonly DateTime _start = DateTime.UtcNow;

    // bridge port n -> ifIndex 1000+n, like many real switches (so the mapping table matters).
    private static readonly List<(DemoSite.DemoDevice Device, int Port)> Ports =
        DemoSite.Devices.Where(d => d.Ip != "192.168.1.1").Select((d, i) => (d, i + 1)).ToList();

    private static (double FromKbps, double ToKbps) Rates(DemoSite.DemoDevice d) => d.Ip switch
    {
        "192.168.1.10" => (900, 38000),                 // NVR: receives every camera
        DemoHealth.LossyDevice => (3300, 60),
        _ when d.Camera => (4600, 45),                  // main + sub stream to the NVR
        _ => (12, 8),
    };

    public Task<IReadOnlyList<SnmpVar>?> GetAsync(IPAddress agent, string community, IReadOnlyList<string> oids, IPAddress? source, CancellationToken ct)
    {
        if (!agent.Equals(Address) || community != "public") return Task.FromResult<IReadOnlyList<SnmpVar>?>(null);
        return Task.FromResult<IReadOnlyList<SnmpVar>?>(oids.Select(Get).ToList());
    }

    private SnmpVar Get(string oid)
    {
        switch (oid)
        {
            case SwitchDirectory.SysName: return Str(oid, "UDM-Pro");
            case SwitchDirectory.SysDescr: return Str(oid, "UniFi Dream Machine Pro (demo)");
            case SwitchDirectory.BaseNumPorts: return Num(oid, SnmpVar.Integer, Ports.Count + 1);
        }
        int dot = oid.LastIndexOf('.');
        string column = oid[..dot];
        if (!int.TryParse(oid[(dot + 1)..], out int ifIndex) || ifIndex <= 1000 || ifIndex > 1000 + Ports.Count) return Missing(oid);
        var (dev, port) = Ports[ifIndex - 1001];
        bool lossy = dev.Ip == DemoHealth.LossyDevice;
        double secs = (DateTime.UtcNow - _start).TotalSeconds + 1000;
        var (from, to) = Rates(dev);
        // Smooth wobble so the graph moves; integral of rate*(1 + 0.15 sin) over time.
        long Octets(double kbps) => (long)(kbps * 1000 / 8 * (secs - 0.15 * 6 * Math.Cos(secs / 6) + 1000));
        return column switch
        {
            SwitchDirectory.IfName => Str(oid, $"Port {port}"),
            SwitchDirectory.IfDescr => Str(oid, $"eth{port}"),
            SwitchDirectory.IfAlias => Str(oid, dev.Model.Split(' ')[0]),
            SwitchDirectory.IfHighSpeed => Num(oid, SnmpVar.Gauge32, lossy ? 100 : 1000),
            SwitchDirectory.IfOperStatus => Num(oid, SnmpVar.Integer, 1),
            SwitchDirectory.Dot3Duplex => Num(oid, SnmpVar.Integer, lossy ? 2 : 3),
            SwitchDirectory.IfHCInOctets => Num(oid, SnmpVar.Counter64, Octets(from)),
            SwitchDirectory.IfHCOutOctets => Num(oid, SnmpVar.Counter64, Octets(to)),
            SwitchDirectory.IfInErrors => Num(oid, SnmpVar.Counter32, lossy ? (long)(secs * 1.5) : 0),
            _ => Missing(oid),
        };
    }

    public Task<IReadOnlyList<SnmpVar>> WalkAsync(IPAddress agent, string community, string rootOid, IPAddress? source, CancellationToken ct, int max = 5000)
    {
        IReadOnlyList<SnmpVar> rows = Array.Empty<SnmpVar>();
        if (agent.Equals(Address) && community == "public")
        {
            if (rootOid == SwitchDirectory.BasePortIfIndex)
                rows = Ports.Select(p => Num($"{rootOid}.{p.Port}", SnmpVar.Integer, 1000 + p.Port)).ToList();
            else if (rootOid == SwitchDirectory.Dot1qTpFdbPort)
                rows = Ports.Select(p => Num($"{rootOid}.1.{MacIndex(p.Device.Mac)}", SnmpVar.Integer, p.Port)).ToList();
        }
        return Task.FromResult(rows);
    }

    private static string MacIndex(string mac) =>
        string.Join('.', mac.Split(':').Select(h => Convert.ToInt32(h, 16)));

    private static SnmpVar Str(string oid, string s) => new(oid, SnmpVar.OctetString, Encoding.UTF8.GetBytes(s));
    private static SnmpVar Missing(string oid) => new(oid, SnmpVar.NoSuchInstance, Array.Empty<byte>());
    private static SnmpVar Num(string oid, byte tag, long v)
    {
        var bytes = BitConverter.GetBytes(v).Reverse().SkipWhile(b => b == 0).ToArray();
        if (bytes.Length == 0) bytes = new byte[] { 0 };
        if (tag == SnmpVar.Integer && (bytes[0] & 0x80) != 0) bytes = new byte[] { 0 }.Concat(bytes).ToArray();
        return new SnmpVar(oid, tag, bytes);
    }
}
