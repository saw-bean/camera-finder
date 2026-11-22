using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace CameraFinder.Core.Health;

/// <summary>One SNMP variable: its OID and value (BER tag + raw content bytes).</summary>
public sealed record SnmpVar(string Oid, byte Tag, byte[] Raw)
{
    public const byte Integer = 0x02, OctetString = 0x04, Null = 0x05, ObjectId = 0x06, IpAddr = 0x40,
        Counter32 = 0x41, Gauge32 = 0x42, TimeTicks = 0x43, Counter64 = 0x46,
        NoSuchObject = 0x80, NoSuchInstance = 0x81, EndOfMibView = 0x82;

    public bool Missing => Tag is Null or NoSuchObject or NoSuchInstance or EndOfMibView;

    /// <summary>Numeric value (INTEGER is signed; counters and gauges are unsigned).</summary>
    public long? Long => Tag switch
    {
        Integer => Raw.Length == 0 ? 0 : (long)new BigInteger(Raw, isUnsigned: false, isBigEndian: true),
        Counter32 or Gauge32 or TimeTicks or Counter64 => Raw.Length == 0 ? 0 : (long)(ulong)new BigInteger(Raw, isUnsigned: true, isBigEndian: true),
        _ => null,
    };

    public string? Text => Tag == OctetString ? Encoding.UTF8.GetString(Raw).TrimEnd('\0').Trim() : Tag == ObjectId ? Ber.DecodeOid(Raw) : null;
}

public interface ISnmpClient
{
    /// <summary>GET several OIDs. Null when the agent did not answer (wrong community, no SNMP, timeout).</summary>
    Task<IReadOnlyList<SnmpVar>?> GetAsync(IPAddress agent, string community, IReadOnlyList<string> oids, IPAddress? source, CancellationToken ct);
    /// <summary>All variables under <paramref name="rootOid"/> (GETBULK walk), at most <paramref name="max"/>.</summary>
    Task<IReadOnlyList<SnmpVar>> WalkAsync(IPAddress agent, string community, string rootOid, IPAddress? source, CancellationToken ct, int max = 5000);
}

/// <summary>
/// SNMP v2c client (read-only: GET, GETNEXT, GETBULK) over UDP, written against RFC 3416 with the minimal BER needed.
/// Used to read switch port counters; it never writes (no SET).
/// </summary>
public sealed class SnmpClient : ISnmpClient
{
    public int Port { get; init; } = 161;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(1500);
    public int Retries { get; init; } = 1;

    public async Task<IReadOnlyList<SnmpVar>?> GetAsync(IPAddress agent, string community, IReadOnlyList<string> oids, IPAddress? source, CancellationToken ct)
    {
        var resp = await RequestAsync(agent, community, 0xA0, oids, 0, 0, source, ct);
        return resp;
    }

    public async Task<IReadOnlyList<SnmpVar>> WalkAsync(IPAddress agent, string community, string rootOid, IPAddress? source, CancellationToken ct, int max = 5000)
    {
        var result = new List<SnmpVar>();
        string prefix = rootOid + ".";
        string next = rootOid;
        while (result.Count < max)
        {
            var batch = await RequestAsync(agent, community, 0xA5, new[] { next }, 0, 25, source, ct);
            if (batch == null || batch.Count == 0) break;
            bool done = false;
            foreach (var v in batch)
            {
                if (v.Tag == SnmpVar.EndOfMibView || !v.Oid.StartsWith(prefix, StringComparison.Ordinal) || OidCompare(v.Oid, next) <= 0) { done = true; break; }
                result.Add(v);
                next = v.Oid;
            }
            if (done) break;
        }
        return result;
    }

    private async Task<IReadOnlyList<SnmpVar>?> RequestAsync(IPAddress agent, string community, byte pdu, IReadOnlyList<string> oids,
        int nonRepeaters, int maxRepetitions, IPAddress? source, CancellationToken ct)
    {
        int requestId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        byte[] packet = Ber.Message(community, pdu, requestId, nonRepeaters, maxRepetitions, oids);
        using var udp = new UdpClient(new IPEndPoint(source ?? IPAddress.Any, 0));
        for (int attempt = 0; attempt <= Retries; attempt++)
        {
            await udp.SendAsync(packet, new IPEndPoint(agent, Port), ct);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(Timeout);
            try
            {
                while (true)
                {
                    var r = await udp.ReceiveAsync(limit.Token);
                    if (!r.RemoteEndPoint.Address.Equals(agent)) continue;
                    var parsed = Ber.ParseResponse(r.Buffer);
                    if (parsed == null || parsed.Value.RequestId != requestId) continue;
                    if (parsed.Value.ErrorStatus != 0) return pdu == 0xA0 ? oids.Select(o => new SnmpVar(o, SnmpVar.NoSuchObject, Array.Empty<byte>())).ToList() : null;
                    return parsed.Value.Vars;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* timeout: retry */ }
            catch (SocketException) { return null; } // e.g. ICMP port unreachable
        }
        return null;
    }

    /// <summary>Numeric OID order ("1.3.6.1.2" &lt; "1.3.6.1.10").</summary>
    public static int OidCompare(string a, string b)
    {
        var x = a.Split('.'); var y = b.Split('.');
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            int c = ulong.Parse(x[i]).CompareTo(ulong.Parse(y[i]));
            if (c != 0) return c;
        }
        return x.Length.CompareTo(y.Length);
    }
}

/// <summary>The BER encoding SNMP uses, just enough of it.</summary>
public static class Ber
{
    public static byte[] Message(string community, byte pdu, int requestId, int a, int b, IReadOnlyList<string> oids)
    {
        var varbinds = oids.Select(o => Tlv(0x30, Concat(Tlv(0x06, EncodeOid(o)), new byte[] { 0x05, 0x00 }))).ToArray();
        var pduBody = Concat(Int(requestId), Int(a), Int(b), Tlv(0x30, Concat(varbinds)));
        return Tlv(0x30, Concat(Int(1 /* v2c */), Tlv(0x04, Encoding.ASCII.GetBytes(community)), Tlv(pdu, pduBody)));
    }

    public readonly record struct Response(int RequestId, int ErrorStatus, IReadOnlyList<SnmpVar> Vars);

    /// <summary>Parses a GetResponse (or Report). Null if it is not one or is malformed.</summary>
    public static Response? ParseResponse(byte[] data)
    {
        try
        {
            int i = 0;
            var msg = Read(data, ref i, 0x30);
            int j = 0;
            Read(msg, ref j, 0x02);           // version
            Read(msg, ref j, 0x04);           // community
            if (j >= msg.Length || msg[j] != 0xA2) return null;
            var pdu = Read(msg, ref j, 0xA2);
            int k = 0;
            int id = (int)(long)new BigInteger(Read(pdu, ref k, 0x02), isUnsigned: false, isBigEndian: true);
            int status = (int)(long)new BigInteger(Read(pdu, ref k, 0x02), isUnsigned: false, isBigEndian: true);
            Read(pdu, ref k, 0x02);           // error index
            var list = Read(pdu, ref k, 0x30);
            var vars = new List<SnmpVar>();
            int m = 0;
            while (m < list.Length)
            {
                var vb = Read(list, ref m, 0x30);
                int n = 0;
                string oid = DecodeOid(Read(vb, ref n, 0x06));
                byte tag = vb[n];
                var raw = Read(vb, ref n, tag);
                vars.Add(new SnmpVar(oid, tag, raw));
            }
            return new Response(id, status, vars);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or FormatException or OverflowException) { return null; }
    }

    private static byte[] Read(byte[] d, ref int i, byte expectedTag)
    {
        if (i >= d.Length || d[i] != expectedTag) throw new FormatException("unexpected BER tag");
        i++;
        int len = d[i++];
        if ((len & 0x80) != 0)
        {
            int n = len & 0x7F;
            if (n is 0 or > 3) throw new FormatException("bad BER length");
            len = 0;
            for (int k = 0; k < n; k++) len = (len << 8) | d[i++];
        }
        if (len < 0 || i + len > d.Length) throw new FormatException("BER length past end");
        var v = d.AsSpan(i, len).ToArray();
        i += len;
        return v;
    }

    public static byte[] Tlv(byte tag, byte[] value)
    {
        byte[] len = value.Length < 0x80 ? new[] { (byte)value.Length }
            : value.Length < 0x100 ? new byte[] { 0x81, (byte)value.Length }
            : new byte[] { 0x82, (byte)(value.Length >> 8), (byte)value.Length };
        return Concat(new[] { tag }, len, value);
    }

    private static byte[] Int(long v) => Tlv(0x02, new BigInteger(v).ToByteArray(isUnsigned: false, isBigEndian: true));

    public static byte[] EncodeOid(string oid)
    {
        var parts = oid.Split('.').Select(ulong.Parse).ToArray();
        if (parts.Length < 2) throw new FormatException("OID too short");
        var bytes = new List<byte>();
        void Add(ulong v)
        {
            var stack = new Stack<byte>();
            stack.Push((byte)(v & 0x7F));
            while ((v >>= 7) > 0) stack.Push((byte)(0x80 | (v & 0x7F)));
            bytes.AddRange(stack);
        }
        Add(parts[0] * 40 + parts[1]);
        foreach (var p in parts.Skip(2)) Add(p);
        return bytes.ToArray();
    }

    public static string DecodeOid(byte[] b)
    {
        if (b.Length == 0) return "";
        var parts = new List<ulong>();
        ulong v = 0;
        bool first = true;
        foreach (var x in b)
        {
            v = (v << 7) | (ulong)(x & 0x7F);
            if ((x & 0x80) != 0) continue;
            if (first) { parts.Add(v < 80 ? v / 40 : 2); parts.Add(v < 80 ? v % 40 : v - 80); first = false; }
            else parts.Add(v);
            v = 0;
        }
        return string.Join('.', parts);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var r = new byte[parts.Sum(p => p.Length)];
        int o = 0;
        foreach (var p in parts) { p.CopyTo(r, o); o += p.Length; }
        return r;
    }
}
