using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CameraFinder.Core.Discovery;

/// <summary>
/// One manufacturer's own UDP discovery protocol (the one its "finder" tool uses, e.g. Hikvision SADP).
/// These work even when ONVIF is turned off, and most of them get answers from devices whose IP is in a
/// different range than the laptop's, because the device replies by broadcast or multicast.
/// </summary>
public sealed record VendorProtocol(
    string Name,
    int SendPort,
    int ListenPort,          // local port to bind; 0 = any. Some devices always reply to a fixed port.
    IPAddress? Multicast,    // group to send to (and join, to hear replies sent to the group)
    bool Broadcast,          // also send to 255.255.255.255
    Func<byte[]> Payload,
    Func<IPAddress, byte[], ProbeHit?> Parse);

/// <summary>
/// Discovery protocols of brands common in Canada. Written for Camera Finder; packet layouts were worked out
/// with the help of UniversalScanner by Julien Blitte (LGPL-3.0, https://github.com/julienblitte/UniversalScanner).
/// Every parser rejects our own requests (which we hear back by broadcast loopback) and anything malformed.
/// </summary>
public static class VendorProbes
{
    public static readonly IReadOnlyList<VendorProtocol> All = new[]
    {
        new VendorProtocol("Hikvision SADP", 37020, 37020, IPAddress.Parse("239.255.255.250"), false, HikvisionProbe, ParseHikvision),
        new VendorProtocol("Dahua", 5050, 5050, null, true, DahuaV1Probe, ParseDahuaV1),
        new VendorProtocol("Dahua DHIP", 37810, 37810, IPAddress.Parse("239.255.255.251"), true, DahuaV2Probe, ParseDahuaV2),
        new VendorProtocol("Hanwha", 7701, 7711, null, true, HanwhaProbe, ParseHanwha),
        new VendorProtocol("Bosch", 1757, 1758, null, true, BoschProbe, ParseBosch),
        new VendorProtocol("i-PRO/Panasonic", 10670, 10669, null, true, PanasonicProbe, ParsePanasonic),
        new VendorProtocol("Vivotek", 10000, 0, null, true, VivotekProbe, ParseVivotek),
        new VendorProtocol("Sony", 2380, 0, null, true, SonyProbe, ParseSony),
        new VendorProtocol("Ubiquiti", 10001, 0, IPAddress.Parse("233.89.188.1"), true, () => new byte[] { 1, 0, 0, 0 }, ParseUbiquiti),
        new VendorProtocol("mDNS (Axis/Arecont)", 5353, 0, IPAddress.Parse("224.0.0.251"), false, MdnsProbe, ParseMdns),
    };

    // ------------------------------------------------------------------ Hikvision (SADP) - also HiLook, many OEMs

    private static byte[] HikvisionProbe() => Encoding.UTF8.GetBytes(
        $"<?xml version=\"1.0\" encoding=\"utf-8\"?><Probe><Uuid>{Guid.NewGuid().ToString().ToUpperInvariant()}</Uuid><Types>inquiry</Types></Probe>");

    public static ProbeHit? ParseHikvision(IPAddress from, byte[] data)
    {
        string xml = Encoding.UTF8.GetString(data);
        if (!xml.Contains("<ProbeMatch>")) return null;
        var ip = ParseIp(XmlTag(xml, "IPv4Address")) ?? from;
        string? model = XmlTag(xml, "DeviceDescription") ?? XmlTag(xml, "DeviceType");
        if (string.Equals(XmlTag(xml, "Activated"), "false", StringComparison.OrdinalIgnoreCase))
            model = (model ?? "Hikvision") + " (NOT ACTIVATED - set password with SADP/web page)";
        return new ProbeHit(ip, model, null, "Hikvision", null, null, NormalizeMac(XmlTag(xml, "MAC")), true, "Hikvision SADP");
    }

    // ------------------------------------------------------------------ Dahua v1 (port 5050) - also Lorex, Amcrest, OEMs

    private static byte[] DahuaV1Probe() => new byte[]
    {
        0xa3, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    };

    private const int DahuaV1HeaderSize = 0x78;

    public static ProbeHit? ParseDahuaV1(IPAddress from, byte[] data)
    {
        if (data.Length < DahuaV1HeaderSize || data[0] != 0xb3) return null;
        int section2 = data[2];
        int section3 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x14));
        if (DahuaV1HeaderSize + section2 + section3 != data.Length) return null;

        string? model = AsciiZ(data, 0x28, 16);
        var ip = new IPAddress(data.AsSpan(0x38, 4));
        string? mac = null;
        if (section2 >= 17)
        {
            mac = NormalizeMac(AsciiZ(data, DahuaV1HeaderSize, 17));
            if (section2 > 17) model = AsciiZ(data, DahuaV1HeaderSize + 17, section2 - 17) ?? model;
        }
        return new ProbeHit(Usable(ip) ?? from, model, null, mac == null ? "Dahua" : null, null, null, mac, true, "Dahua");
    }

    // ------------------------------------------------------------------ Dahua v2 (DHIP, port 37810)

    private static byte[] DahuaV2Probe()
    {
        var body = Encoding.UTF8.GetBytes("{\"method\":\"DHDiscover.search\",\"params\":{\"mac\":\"\",\"uni\":0}}\n");
        var packet = new byte[0x20 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0x00), 0x20);
        "DHIP"u8.CopyTo(packet.AsSpan(0x04));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0x10), (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0x18), (uint)body.Length);
        body.CopyTo(packet, 0x20);
        return packet;
    }

    public static ProbeHit? ParseDahuaV2(IPAddress from, byte[] data)
    {
        if (data.Length <= 0x20 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x20) return null;
        if (!data.AsSpan(4, 4).SequenceEqual("DHIP"u8)) return null;
        int bodyLen = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x10)), (uint)(data.Length - 0x20));
        string body = Encoding.UTF8.GetString(data, 0x20, bodyLen).TrimEnd('\0', '\n', '\r', ' ');

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (JsonString(root, "method") != "client.notifyDevInfo") return null;
            IPAddress? ip = null;
            if (FindJson(root, "IPv4Address") is { } v4 && v4.ValueKind == JsonValueKind.Object)
                ip = ParseIp(JsonString(v4, "IPAddress"));
            string? model = JsonString(root, "DeviceType") ?? JsonString(root, "DeviceClass");
            string? mac = NormalizeMac(JsonString(root, "mac") ?? JsonString(root, "Mac"));
            string? vendor = JsonString(root, "Vendor");
            return new ProbeHit(Usable(ip) ?? from, model, JsonString(root, "MachineName"), vendor ?? "Dahua", null, null, mac, true, "Dahua DHIP");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ Hanwha / Samsung Wisenet (send 7701, reply 7711)

    private const int HanwhaPacketSize = 0x106;

    private static byte[] HanwhaProbe()
    {
        var p = new byte[HanwhaPacketSize];
        p[0] = 0x01;
        return p;
    }

    public static ProbeHit? ParseHanwha(IPAddress from, byte[] data)
    {
        if (data.Length < 0xAC || data[0] != 0x0b) return null;
        var ip = ParseIp(AsciiZ(data, 0x25, 16)) ?? from;
        string? model = AsciiZ(data, 0x6D, 10);
        string? mac = NormalizeMac(AsciiZ(data, 0x13, 18));
        return new ProbeHit(ip, model, null, mac == null ? "Hanwha" : null, null, null, mac, true, "Hanwha");
    }

    // ------------------------------------------------------------------ Bosch (send 1757, reply 1758)

    private const uint BoschMagic = 0x9939a427;

    private static byte[] BoschProbe()
    {
        var p = new byte[12];
        var now = DateTime.UtcNow;
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(0), BoschMagic);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), (uint)((now.Hour << 24) | (now.Minute << 16) | (now.Second << 8) | (now.Millisecond / 10)));
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(8), 0xff0006de);
        return p;
    }

    public static ProbeHit? ParseBosch(IPAddress from, byte[] data)
    {
        if (data.Length == 32)
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(data) != BoschMagic) return null;
            string mac = FormatMac(data.AsSpan(8, 6));
            var ip = new IPAddress(data.AsSpan(0x10, 4));
            return new ProbeHit(Usable(ip) ?? from, null, null, null, null, null, mac, true, "Bosch");
        }

        string xml = Encoding.UTF8.GetString(data);
        if (!xml.Contains("<unitIPAddress>") && !xml.Contains("<physAddress>")) return null;
        return new ProbeHit(ParseIp(XmlTag(xml, "unitIPAddress")) ?? from, XmlTag(xml, "friendlyName"), null, "Bosch", null, null,
            NormalizeMac(XmlTag(xml, "physAddress")), true, "Bosch");
    }

    // ------------------------------------------------------------------ i-PRO / Panasonic (send 10670, reply 10669)

    private static byte[] PanasonicProbe()
    {
        var p = new byte[0x34];
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(0x00), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(0x04), 0x000d0000);
        p.AsSpan(0x0C, 6).Fill(0xff);                       // MAC: all devices
        p.AsSpan(0x12, 4).Fill(0xff);                       // destination: broadcast
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(0x16), 0x00012011);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(0x1A), 0x1e11231f);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(0x1E), 0x1e191300);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(0x22), 0x00020000);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(0x2E), 0x0000ffff);
        return p;
    }

    public static ProbeHit? ParsePanasonic(IPAddress from, byte[] data)
    {
        if (data.Length <= 0x34) return null;
        IPAddress? ip = null;
        string? full = null, shortName = null;
        int i = 53;
        while (i + 4 <= data.Length)
        {
            int key = (data[i] << 8) | data[i + 1];
            int len = (data[i + 2] << 8) | data[i + 3];
            i += 4;
            if (i + len > data.Length) break;
            var value = data.AsSpan(i, len);
            if (key == 0x20 && len == 4) ip = new IPAddress(value);
            else if (key == 0xa7) full = AsciiZ(value);
            else if (key == 0xa8) shortName = AsciiZ(value);
            i += len;
        }
        string? model = full ?? shortName;
        if (model == null || ip == null) return null;
        return new ProbeHit(Usable(ip) ?? from, model, null, null, null, null, FormatMac(data.AsSpan(6, 6)), true, "i-PRO/Panasonic");
    }

    // ------------------------------------------------------------------ Vivotek (port 10000)

    private const uint VivotekMagic = 0x4a5d8f1c;

    private static byte[] VivotekProbe()
    {
        var p = new byte[5];
        p[0] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(1), VivotekMagic);
        return p;
    }

    public static ProbeHit? ParseVivotek(IPAddress from, byte[] data)
    {
        if (data.Length <= 5 || BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(1)) != VivotekMagic) return null;
        IPAddress? ip = null;
        string? model = null, name = null, mac = null;
        int i = 5;
        while (i + 2 <= data.Length)
        {
            byte type = data[i], size = data[i + 1];
            i += 2;
            if (type == 0x00) break;
            if (type == 0x04) continue;             // one-byte flag carried in the size field
            if (i + size > data.Length) break;
            var v = data.AsSpan(i, size);
            switch (type)
            {
                case 0x01: name = AsciiZ(v); break;
                case 0x02 when size == 6: mac = FormatMac(v); break;
                case 0x03 when size == 4: ip = new IPAddress(v); break;
                case 0x09: model = AsciiZ(v); break;
            }
            i += size;
        }
        if (ip == null) return null;
        return new ProbeHit(Usable(ip) ?? from, model, name, mac == null ? "Vivotek" : null, null, null, mac, true, "Vivotek");
    }

    // ------------------------------------------------------------------ Sony (port 2380)

    private static byte[] SonyProbe()
    {
        var p = new List<byte> { 0x02 };
        p.AddRange(Encoding.ASCII.GetBytes("ENQ:allinfo"));
        p.Add(0xff);
        p.Add(0x03);
        return p.ToArray();
    }

    public static ProbeHit? ParseSony(IPAddress from, byte[] data)
    {
        if (data.Length < 3 || data[0] != 0x02 || data[^1] != 0x03) return null;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int start = 1;
        for (int i = 1; i < data.Length - 1; i++)
        {
            if (data[i] != 0xff) continue;
            string line = Encoding.UTF8.GetString(data, start, i - start);
            int colon = line.IndexOf(':');
            if (colon > 0) values[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            start = i + 1;
        }
        if (!values.TryGetValue("ipadr", out var ipText) || !values.TryGetValue("model", out var model)) return null;
        values.TryGetValue("mac", out var mac);
        return new ProbeHit(ParseIp(ipText) ?? from, model, null, "Sony", null, null, NormalizeMac(mac), true, "Sony");
    }

    // ------------------------------------------------------------------ Ubiquiti / UniFi (port 10001)

    public static ProbeHit? ParseUbiquiti(IPAddress from, byte[] data)
    {
        if (data.Length <= 4 || data[0] != 0x01) return null;
        if (BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2)) != data.Length - 4) return null;
        IPAddress? ip = null;
        string? mac = null, model = null, shortModel = null, name = null;
        int i = 4;
        while (i + 3 <= data.Length)
        {
            byte type = data[i];
            int len = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i + 1));
            i += 3;
            if (i + len > data.Length) break;
            var v = data.AsSpan(i, len);
            switch (type)
            {
                case 0x01 when len == 6: mac ??= FormatMac(v); break;
                case 0x02 when len == 10: mac ??= FormatMac(v[..6]); ip ??= Usable(new IPAddress(v.Slice(6, 4))); break;
                case 0x0b: name = AsciiZ(v); break;
                case 0x0c: shortModel = AsciiZ(v); break;
                case 0x14: case 0x15: model ??= AsciiZ(v); break;
            }
            i += len;
        }
        if (mac == null) return null;
        model ??= shortModel;
        bool camera = (shortModel ?? model ?? "").StartsWith("UVC", StringComparison.OrdinalIgnoreCase);
        return new ProbeHit(ip ?? from, model, name, null, null, null, mac, camera, "Ubiquiti");
    }

    // ------------------------------------------------------------------ mDNS / Bonjour (Axis, Arecont)

    private static readonly (string Service, string Vendor)[] MdnsServices =
    {
        ("_axis-video._tcp.local", "Axis"),
        ("_axis-nvr._tcp.local", "Axis"),
        ("_arec._tcp.local", "Arecont Vision"),
    };

    private static byte[] MdnsProbe()
    {
        var p = new List<byte> { 0, 0, 0, 0, 0, (byte)MdnsServices.Length, 0, 0, 0, 0, 0, 0 };
        foreach (var (service, _) in MdnsServices)
        {
            foreach (var label in service.Split('.'))
            {
                p.Add((byte)label.Length);
                p.AddRange(Encoding.ASCII.GetBytes(label));
            }
            p.AddRange(new byte[] { 0, 0, 12, 0, 1 }); // end of name, type PTR, class IN
        }
        return p.ToArray();
    }

    public static ProbeHit? ParseMdns(IPAddress from, byte[] data)
    {
        if (data.Length < 12 || (data[2] & 0x80) == 0) return null;   // must be a response
        int qd = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
        int records = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6)) + BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(8))
                      + BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(10));
        try
        {
            int pos = 12;
            for (int q = 0; q < qd; q++) { ReadDnsName(data, ref pos); pos += 4; }
            for (int r = 0; r < records && pos + 10 <= data.Length; r++)
            {
                string owner = ReadDnsName(data, ref pos);
                int type = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
                int rdLen = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos + 8));
                int rd = pos + 10;
                pos = rd + rdLen;
                if (pos > data.Length) break;
                if (type != 12) continue;   // PTR
                foreach (var (service, vendor) in MdnsServices)
                {
                    if (!owner.Equals(service, StringComparison.OrdinalIgnoreCase)) continue;
                    int p2 = rd;
                    string instance = ReadDnsName(data, ref p2);
                    string label = instance.EndsWith("." + service, StringComparison.OrdinalIgnoreCase)
                        ? instance[..^(service.Length + 1)] : instance.Split('.')[0];
                    // Axis instance names look like "AXIS P3245-V - ACCC8E123456".
                    string? mac = null;
                    var m = Regex.Match(label, @"^(?<model>.+?)\s+-\s+(?<mac>[0-9A-Fa-f]{12})$");
                    if (m.Success) { mac = NormalizeMac(m.Groups["mac"].Value); label = m.Groups["model"].Value; }
                    return new ProbeHit(from, label, null, mac == null ? vendor : null, null, null, mac, true, "mDNS");
                }
            }
        }
        catch (IndexOutOfRangeException) { }
        catch (ArgumentOutOfRangeException) { }
        return null;
    }

    private static string ReadDnsName(byte[] data, ref int pos)
    {
        var labels = new List<string>();
        int p = pos, jumps = 0, total = 0;
        bool jumped = false;
        while (true)
        {
            byte len = data[p];
            if (len == 0) { p++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (++jumps > 16) throw new IndexOutOfRangeException("DNS name loop");
                int target = ((len & 0x3F) << 8) | data[p + 1];
                if (!jumped) pos = p + 2;
                jumped = true;
                p = target;
                continue;
            }
            // RFC 1035: labels are at most 63 bytes (0x40-0xBF are reserved types), names at most 255 bytes.
            if (len > 63) throw new IndexOutOfRangeException("bad DNS label");
            if ((total += len + 1) > 255) throw new IndexOutOfRangeException("DNS name too long");
            labels.Add(Encoding.UTF8.GetString(data, p + 1, len));
            p += 1 + len;
        }
        if (!jumped) pos = p;
        return string.Join(".", labels);
    }

    // ------------------------------------------------------------------ helpers

    private static string? XmlTag(string xml, string tag)
    {
        var m = Regex.Match(xml, $"<{tag}>([^<]*)</{tag}>");
        return m.Success && m.Groups[1].Value.Trim().Length > 0 ? WebUtility.HtmlDecode(m.Groups[1].Value.Trim()) : null;
    }

    private static string? JsonString(JsonElement root, string name) =>
        FindJson(root, name) is { ValueKind: JsonValueKind.String } e && e.GetString() is { Length: > 0 } s ? s : null;

    private static JsonElement? FindJson(JsonElement e, string name)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
                if (p.NameEquals(name)) return p.Value;
            foreach (var p in e.EnumerateObject())
                if (FindJson(p.Value, name) is { } hit) return hit;
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
                if (FindJson(item, name) is { } hit) return hit;
        }
        return null;
    }

    private static string? AsciiZ(byte[] data, int offset, int length) =>
        offset + length <= data.Length ? AsciiZ(data.AsSpan(offset, length)) : null;

    private static string? AsciiZ(ReadOnlySpan<byte> value)
    {
        int end = value.IndexOf((byte)0);
        if (end >= 0) value = value[..end];
        string s = Encoding.UTF8.GetString(value).Trim();
        return s.Length > 0 ? s : null;
    }

    private static IPAddress? ParseIp(string? text) =>
        IPAddress.TryParse(text?.Trim(), out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? Usable(ip) : null;

    private static IPAddress? Usable(IPAddress? ip) =>
        ip != null && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.Broadcast) ? ip : null;

    private static string FormatMac(ReadOnlySpan<byte> b) => string.Join(":", b.ToArray().Select(x => x.ToString("X2")));

    /// <summary>"44-19-b6-01-02-03", "4419b6010203" or "44:19:B6:01:02:03" to "44:19:B6:01:02:03".</summary>
    internal static string? NormalizeMac(string? text)
    {
        if (text == null) return null;
        string hex = new(text.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12 || hex.All(c => c == '0') || hex.All(c => c is 'f' or 'F')) return null;
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2).ToUpperInvariant()));
    }
}
