using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using CameraFinder.Core.Capture;
using CameraFinder.Core.Discovery;
using CameraFinder.Core.Export;
using CameraFinder.Core.Model;
using CameraFinder.Core.Net;

// Minimal test runner: every public static method in Tests whose name starts with "Test" is a test.
// Methods marked [Live] need real network access / capture rights and are skipped with --unit-only.
bool unitOnly = args.Contains("--unit-only");
int passed = 0, failed = 0, skipped = 0;
foreach (var m in typeof(Tests).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name.StartsWith("Test")))
{
    bool live = m.GetCustomAttribute<LiveAttribute>() != null;
    if (live && unitOnly) { skipped++; continue; }
    try
    {
        var r = m.Invoke(null, null);
        if (r is Task t) await t;
        Console.WriteLine($"PASS {m.Name}");
        passed++;
    }
    catch (Exception ex)
    {
        var inner = ex is TargetInvocationException tie ? tie.InnerException! : ex;
        if (inner is SkipException) { Console.WriteLine($"SKIP {m.Name}: {inner.Message}"); skipped++; continue; }
        Console.WriteLine($"FAIL {m.Name}: {inner.GetType().Name}: {inner.Message}");
        failed++;
    }
}
Console.WriteLine($"\n{passed} passed, {failed} failed, {skipped} skipped");
return failed == 0 ? 0 : 1;

[AttributeUsage(AttributeTargets.Method)]
sealed class LiveAttribute : Attribute { }
sealed class SkipException(string message) : Exception(message) { }

static class Assert
{
    public static void Equal<T>(T expected, T actual, string? what = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what ?? "value"}: expected <{expected}> but got <{actual}>");
    }
    public static void True(bool condition, string message) { if (!condition) throw new Exception(message); }
}

static class Tests
{
    // ---------- IPv4 maths ----------
    public static void TestSubnetMaths()
    {
        var s = Subnet.Of(IPAddress.Parse("10.0.5.77"), 24);
        Assert.Equal("10.0.5.0/24", s.ToString());
        Assert.Equal(254L, s.HostCount);
        Assert.Equal(254, s.Hosts().Count());
        Assert.Equal("10.0.5.1", s.Hosts().First().ToString());
        Assert.Equal("10.0.5.254", s.Hosts().Last().ToString());
        Assert.True(s.Contains(IPAddress.Parse("10.0.5.200")), "contains");
        Assert.True(!s.Contains(IPAddress.Parse("10.0.6.1")), "not contains");
        Assert.Equal(20, Ipv4.PrefixFromMask(IPAddress.Parse("255.255.240.0")));
        Assert.Equal("255.255.255.0", s.MaskAddress.ToString());
        Assert.True(Ipv4.IsLinkLocal(IPAddress.Parse("169.254.3.4")), "link-local");
        Assert.True(Ipv4.IsPrivate(IPAddress.Parse("172.20.1.1")) && !Ipv4.IsPrivate(IPAddress.Parse("8.8.8.8")), "private");
        Assert.True(!Ipv4.IsUsableHost(IPAddress.Parse("239.255.255.250")) && !Ipv4.IsUsableHost(IPAddress.Any), "usable");
    }

    public static void TestSweepRangeLimitsLargeSubnets()
    {
        var big = Subnet.Parse("10.0.0.0/16");
        Assert.Equal("10.0.5.0/24", ArpSweeper.SweepRange(big, IPAddress.Parse("10.0.5.9")).ToString());
        var small = Subnet.Parse("192.168.8.0/23");
        Assert.Equal("192.168.8.0/23", ArpSweeper.SweepRange(small, IPAddress.Parse("192.168.9.9")).ToString());
    }

    public static void TestTempAddressCandidates()
    {
        var c = TempAddressManager.Candidates(Subnet.Parse("10.0.5.0/24")).Take(3).Select(a => a.ToString()).ToList();
        Assert.Equal("10.0.5.251,10.0.5.250,10.0.5.249", string.Join(",", c));
        Assert.True(TempAddressManager.Candidates(Subnet.Parse("169.254.0.0/16")).All(Ipv4.IsLinkLocal), "link-local candidates");
    }

    // ---------- OUI ----------
    public static void TestOuiLookup()
    {
        var db = OuiDatabase.Default;
        Assert.True(db.Count > 50000, "OUI database loaded: " + db.Count);
        Assert.Equal("Axis Communications AB", db.Lookup("00:40:8c:aa:bb:cc"));
        Assert.Equal("Avigilon (Motorola Solutions)", db.Lookup("00-18-85-01-02-03"));
        Assert.True(db.Lookup("d4:2d:c5:00:00:01")!.Contains("i-PRO"), "i-PRO (Panasonic)");
        Assert.Equal("(random / locally administered MAC)", db.Lookup("02:00:00:00:00:01"));
        Assert.Equal(null, db.Lookup(null));
    }

    public static void TestOuiPrefersLongestPrefix()
    {
        var db = OuiDatabase.Load(new StringReader("AABBCC\tBig Co\nAABBCC1\tMedium Co\nAABBCC123\tSmall Co\n"));
        Assert.Equal("Small Co", db.Lookup("AA:BB:CC:12:34:56"));
        Assert.Equal("Medium Co", db.Lookup("AA:BB:CC:19:99:99"));
        Assert.Equal("Big Co", db.Lookup("AA:BB:CC:F0:00:00"));
    }

    // ---------- Frame parsing ----------
    static byte[] Arp(int oper, string senderMac, string senderIp, string targetIp, bool vlan = false)
    {
        var f = new List<byte>();
        f.AddRange(Enumerable.Repeat((byte)0xFF, 6));                 // dst broadcast
        f.AddRange(Mac(senderMac));                                    // src
        if (vlan) f.AddRange(new byte[] { 0x81, 0x00, 0x00, 0x14 });  // VLAN 20
        f.AddRange(new byte[] { 0x08, 0x06, 0, 1, 0x08, 0x00, 6, 4, 0, (byte)oper });
        f.AddRange(Mac(senderMac));
        f.AddRange(IPAddress.Parse(senderIp).GetAddressBytes());
        f.AddRange(new byte[6]);
        f.AddRange(IPAddress.Parse(targetIp).GetAddressBytes());
        return f.ToArray();
    }

    static byte[] Mac(string m) => m.Split(':').Select(x => Convert.ToByte(x, 16)).ToArray();

    static byte[] Ipv4Frame(string dstMac, string srcMac, string srcIp, string dstIp)
    {
        var f = new List<byte>();
        f.AddRange(Mac(dstMac));
        f.AddRange(Mac(srcMac));
        f.AddRange(new byte[] { 0x08, 0x00 });
        var ip = new byte[20];
        ip[0] = 0x45; ip[9] = 17;
        IPAddress.Parse(srcIp).GetAddressBytes().CopyTo(ip, 12);
        IPAddress.Parse(dstIp).GetAddressBytes().CopyTo(ip, 16);
        f.AddRange(ip);
        return f.ToArray();
    }

    public static void TestParseArpRequest()
    {
        var obs = FrameParser.Parse(Arp(1, "00:18:85:11:22:33", "10.0.5.23", "10.0.5.1"));
        Assert.Equal(2, obs.Count);
        Assert.Equal("10.0.5.23", obs[0].Ip.ToString());
        Assert.Equal("00:18:85:11:22:33", obs[0].Mac);
        Assert.Equal("10.0.5.1", obs[1].Ip.ToString());
        Assert.Equal(null, obs[1].Mac, "target has no MAC");
    }

    public static void TestParseArpProbeAndVlan()
    {
        var probe = FrameParser.Parse(Arp(1, "00:40:8c:01:02:03", "0.0.0.0", "192.168.0.90"));
        Assert.Equal(1, probe.Count);
        Assert.Equal("192.168.0.90", probe[0].Ip.ToString());
        Assert.Equal("00:40:8C:01:02:03", probe[0].Mac);

        var tagged = FrameParser.Parse(Arp(2, "00:40:8c:01:02:03", "10.20.0.5", "10.20.0.1", vlan: true));
        Assert.Equal("10.20.0.5", tagged[0].Ip.ToString());
    }

    public static void TestParseIPv4OnlyFromBroadcastOrMulticast()
    {
        var bc = FrameParser.Parse(Ipv4Frame("ff:ff:ff:ff:ff:ff", "00:40:8c:01:02:03", "10.0.5.40", "255.255.255.255"));
        Assert.Equal(1, bc.Count);
        var mc = FrameParser.Parse(Ipv4Frame("01:00:5e:7f:ff:fa", "00:40:8c:01:02:03", "10.0.5.41", "239.255.255.250"));
        Assert.Equal("10.0.5.41", mc[0].Ip.ToString());
        var uni = FrameParser.Parse(Ipv4Frame("00:11:22:33:44:55", "00:40:8c:01:02:03", "8.8.8.8", "10.0.5.2"));
        Assert.Equal(0, uni.Count, "unicast ignored");
        Assert.Equal(0, FrameParser.Parse(new byte[10]).Count, "short frame");
    }

    // ---------- Probe parsing ----------
    public const string OnvifReply = """
        <?xml version="1.0" encoding="UTF-8"?>
        <SOAP-ENV:Envelope xmlns:SOAP-ENV="http://www.w3.org/2003/05/soap-envelope"
          xmlns:wsa="http://schemas.xmlsoap.org/ws/2004/08/addressing"
          xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery"
          xmlns:dn="http://www.onvif.org/ver10/network/wsdl">
          <SOAP-ENV:Header><wsa:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/ProbeMatches</wsa:Action></SOAP-ENV:Header>
          <SOAP-ENV:Body><d:ProbeMatches><d:ProbeMatch>
            <wsa:EndpointReference><wsa:Address>urn:uuid:1234</wsa:Address></wsa:EndpointReference>
            <d:Types>dn:NetworkVideoTransmitter</d:Types>
            <d:Scopes>onvif://www.onvif.org/type/video_encoder onvif://www.onvif.org/hardware/WV-S1136 onvif://www.onvif.org/name/Lobby%20Cam onvif://www.onvif.org/location/</d:Scopes>
            <d:XAddrs>http://[fe80::1]/onvif/device_service http://10.0.5.40/onvif/device_service</d:XAddrs>
            <d:MetadataVersion>1</d:MetadataVersion>
          </d:ProbeMatch></d:ProbeMatches></SOAP-ENV:Body>
        </SOAP-ENV:Envelope>
        """;

    public static void TestParseOnvifProbeMatch()
    {
        var hit = OnvifProbe.Parse(IPAddress.Parse("192.168.1.1"), Encoding.UTF8.GetBytes(OnvifReply.Trim()));
        Assert.True(hit != null, "parsed");
        Assert.Equal("10.0.5.40", hit!.Ip.ToString(), "IP from XAddrs");
        Assert.Equal("WV-S1136", hit.Model);
        Assert.Equal("Lobby Cam", hit.Name);
        Assert.Equal(null, OnvifProbe.Parse(IPAddress.Loopback, Encoding.UTF8.GetBytes("<x/>")), "not a match");
        Assert.Equal(null, OnvifProbe.Parse(IPAddress.Loopback, Encoding.UTF8.GetBytes("garbage")), "garbage");
        var probe = Encoding.UTF8.GetString(OnvifProbe.BuildProbe("dn:NetworkVideoTransmitter"));
        Assert.True(probe.Contains("discovery/Probe") && probe.Contains("NetworkVideoTransmitter"), "probe text");
    }

    public static void TestWsDiscoveryFromWindowsPcIsNotACamera()
    {
        // Real reply from a Windows 11 PC (WSDAPI, port 5357) captured on a test network: answered the untyped probe.
        const string windows = "<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://www.w3.org/2003/05/soap-envelope\" " +
            "xmlns:wsa=\"http://schemas.xmlsoap.org/ws/2004/08/addressing\" xmlns:wsd=\"http://schemas.xmlsoap.org/ws/2005/04/discovery\" " +
            "xmlns:wsdp=\"http://schemas.xmlsoap.org/ws/2006/02/devprof\" xmlns:pub=\"http://schemas.microsoft.com/windows/pub/2005/07\"><soap:Header>" +
            "<wsa:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/ProbeMatches</wsa:Action></soap:Header><soap:Body><wsd:ProbeMatches><wsd:ProbeMatch>" +
            "<wsa:EndpointReference><wsa:Address>urn:uuid:a6dbce08-f054-45c2-8088-100fe9e6ebf6</wsa:Address></wsa:EndpointReference>" +
            "<wsd:Types>wsdp:Device pub:Computer</wsd:Types><wsd:XAddrs>http://192.168.2.20:5357/a6dbce08-f054-45c2-8088-100fe9e6ebf6/</wsd:XAddrs>" +
            "<wsd:MetadataVersion>16</wsd:MetadataVersion></wsd:ProbeMatch></wsd:ProbeMatches></soap:Body></soap:Envelope>";
        var pc = OnvifProbe.Parse(IPAddress.Parse("192.168.2.20"), Encoding.UTF8.GetBytes(windows))!;
        Assert.True(!pc.Camera, "Windows PC is not ONVIF");
        Assert.True(OnvifProbe.Parse(IPAddress.Loopback, Encoding.UTF8.GetBytes(OnvifReply.Trim()))!.Camera, "real camera is ONVIF");
        Assert.True(OnvifProbe.IsOnvif("tds:Device", "onvif://www.onvif.org/type/NetworkVideoStorage"), "NVR via scopes");
        Assert.True(!OnvifProbe.IsOnvif("wsdp:Device wprt:PrintDeviceType", null), "WSD printer");

        var d = new Device(IPAddress.Parse("192.168.2.20")) { Sources = DiscoverySource.WsDiscovery, Mac = "80:B6:55:EC:B1:84" };
        Assert.Equal(DeviceKind.Other, d.Kind, "PC listed as Other, not Camera");
        Assert.Equal("WsDiscovery", d.FoundBy);
    }

    public static void TestOnvifRejectsDtd()
    {
        string evil = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><ProbeMatch>&e;</ProbeMatch>";
        Assert.Equal(null, OnvifProbe.Parse(IPAddress.Loopback, Encoding.UTF8.GetBytes(evil)), "DTD rejected");
    }

    public static void TestParseSsdp()
    {
        string reply = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=1800\r\nLOCATION: http://10.0.5.50:49152/desc.xml\r\nSERVER: Linux/3.10 UPnP/1.0 IPCamera/1.0\r\nST: upnp:rootdevice\r\n\r\n";
        var hit = SsdpProbe.Parse(IPAddress.Parse("10.0.5.50"), Encoding.ASCII.GetBytes(reply));
        Assert.Equal("http://10.0.5.50:49152/desc.xml", hit!.DescriptionUrl);
        Assert.Equal("Linux/3.10 UPnP/1.0 IPCamera/1.0", hit.Server);
        Assert.Equal(null, SsdpProbe.Parse(IPAddress.Loopback, Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\n\r\n")), "own search ignored");

        var (name, mfr, model) = SsdpProbe.ParseDescription(
            "<root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device><friendlyName>Dock Door</friendlyName>" +
            "<manufacturer>Panasonic</manufacturer><modelName>WV-X1571</modelName><modelNumber>LN</modelNumber></device></root>");
        Assert.Equal("Dock Door", name);
        Assert.Equal("Panasonic", mfr);
        Assert.Equal("WV-X1571 LN", model);
    }

    // ---------- Manufacturer discovery protocols ----------
    static VendorProtocol Proto(string name) => VendorProbes.All.First(p => p.Name == name);
    static readonly IPAddress From = IPAddress.Parse("192.168.1.99");

    public static void TestVendorParsersIgnoreOwnRequestsAndGarbage()
    {
        var rnd = new Random(42);
        foreach (var p in VendorProbes.All)
        {
            Assert.Equal(null, p.Parse(From, p.Payload()), $"{p.Name} ignores its own request");
            for (int i = 0; i < 300; i++)
            {
                var junk = new byte[rnd.Next(0, 400)];
                rnd.NextBytes(junk);
                if (junk.Length > 0 && rnd.Next(3) == 0) junk[0] = p.Payload()[0]; // look a bit like the real thing
                try { p.Parse(From, junk); }
                catch (Exception ex) { throw new Exception($"{p.Name} threw on junk: {ex.GetType().Name} {ex.Message}"); }
            }
        }
    }

    public static void TestParseHikvisionSadp()
    {
        string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><ProbeMatch><Uuid>X</Uuid><Types>inquiry</Types>" +
                     "<DeviceType>139</DeviceType><DeviceDescription>DS-2CD2143G2-I</DeviceDescription>" +
                     "<DeviceSN>DS-2CD2143G2-I20220101AAWRJ12345678</DeviceSN><MAC>44-19-b6-01-02-03</MAC>" +
                     "<IPv4Address>192.0.0.64</IPv4Address><Activated>false</Activated></ProbeMatch>";
        var hit = Proto("Hikvision SADP").Parse(From, Encoding.UTF8.GetBytes(xml))!;
        Assert.Equal("192.0.0.64", hit.Ip.ToString(), "IP from payload, not sender");
        Assert.Equal("44:19:B6:01:02:03", hit.Mac);
        Assert.True(hit.Model!.StartsWith("DS-2CD2143G2-I") && hit.Model.Contains("NOT ACTIVATED"), "model + activation: " + hit.Model);
        Assert.True(hit.Camera, "camera");
        Assert.Equal("Hikvision SADP", hit.Protocol);
    }

    public static void TestParseDahuaV1()
    {
        var mac = Encoding.ASCII.GetBytes("38:af:29:11:22:33");
        var model = Encoding.ASCII.GetBytes("IPC-HDW2431T");
        var extra = Encoding.ASCII.GetBytes("SerialNo:5J0123PAZ\r\n");
        var d = new byte[0x78 + mac.Length + model.Length + extra.Length];
        d[0] = 0xb3;
        d[2] = (byte)(mac.Length + model.Length);
        BitConverter.GetBytes((ushort)extra.Length).CopyTo(d, 0x14);
        new byte[] { 192, 168, 1, 108 }.CopyTo(d, 0x38);
        mac.CopyTo(d, 0x78); model.CopyTo(d, 0x78 + mac.Length); extra.CopyTo(d, 0x78 + mac.Length + model.Length);
        var hit = Proto("Dahua").Parse(From, d)!;
        Assert.Equal("192.168.1.108", hit.Ip.ToString());
        Assert.Equal("38:AF:29:11:22:33", hit.Mac);
        Assert.Equal("IPC-HDW2431T", hit.Model);
    }

    public static void TestParseDahuaV2()
    {
        var body = Encoding.UTF8.GetBytes("{\"method\":\"client.notifyDevInfo\",\"params\":{\"deviceInfo\":{\"DeviceType\":\"N842B3\"," +
            "\"Vendor\":\"Lorex\",\"IPv4Address\":{\"IPAddress\":\"10.20.0.7\",\"SubnetMask\":\"255.255.255.0\"},\"mac\":\"9c:8e:cd:aa:bb:cc\"}}}\n");
        var d = new byte[0x20 + body.Length];
        BitConverter.GetBytes(0x20u).CopyTo(d, 0);
        Encoding.ASCII.GetBytes("DHIP").CopyTo(d, 4);
        BitConverter.GetBytes((uint)body.Length).CopyTo(d, 0x10);
        BitConverter.GetBytes((uint)body.Length).CopyTo(d, 0x18);
        body.CopyTo(d, 0x20);
        var hit = Proto("Dahua DHIP").Parse(From, d)!;
        Assert.Equal("10.20.0.7", hit.Ip.ToString());
        Assert.Equal("N842B3", hit.Model);
        Assert.Equal("Lorex", hit.Manufacturer);
        Assert.Equal("9C:8E:CD:AA:BB:CC", hit.Mac);
    }

    public static void TestParseHanwha()
    {
        var d = new byte[0x106];
        d[0] = 0x0b;
        Encoding.ASCII.GetBytes("00:09:18:4A:5B:6C").CopyTo(d, 0x13);
        Encoding.ASCII.GetBytes("192.168.1.100").CopyTo(d, 0x25);
        Encoding.ASCII.GetBytes("XNV-6080R").CopyTo(d, 0x6D);
        var hit = Proto("Hanwha").Parse(From, d)!;
        Assert.Equal("192.168.1.100", hit.Ip.ToString());
        Assert.Equal("XNV-6080R", hit.Model);
        Assert.Equal("00:09:18:4A:5B:6C", hit.Mac);
    }

    public static void TestParseBosch()
    {
        var d = new byte[32];
        new byte[] { 0x99, 0x39, 0xa4, 0x27 }.CopyTo(d, 0);
        new byte[] { 0x00, 0x07, 0x5f, 0x01, 0x02, 0x03 }.CopyTo(d, 8);
        new byte[] { 192, 168, 0, 1 }.CopyTo(d, 0x10);
        var hit = Proto("Bosch").Parse(From, d)!;
        Assert.Equal("192.168.0.1", hit.Ip.ToString());
        Assert.Equal("00:07:5F:01:02:03", hit.Mac);

        var xml = "<unit><friendlyName>FLEXIDOME IP 5000i</friendlyName><unitIPAddress>10.1.1.20</unitIPAddress><physAddress>00-07-5f-aa-bb-cc</physAddress></unit>";
        var x = Proto("Bosch").Parse(From, Encoding.UTF8.GetBytes(xml))!;
        Assert.Equal("10.1.1.20", x.Ip.ToString());
        Assert.Equal("FLEXIDOME IP 5000i", x.Model);
    }

    public static void TestParsePanasonic()
    {
        var d = new List<byte>(new byte[53]);
        for (int i = 0; i < 6; i++) d[6 + i] = new byte[] { 0x00, 0x80, 0x45, 0x11, 0x22, 0x33 }[i];
        void Tlv(int key, byte[] v) { d.Add((byte)(key >> 8)); d.Add((byte)key); d.Add((byte)(v.Length >> 8)); d.Add((byte)v.Length); d.AddRange(v); }
        Tlv(0x20, new byte[] { 192, 168, 0, 10 });
        Tlv(0xa7, Encoding.ASCII.GetBytes("WV-S2136"));
        d.AddRange(new byte[4]);
        var hit = Proto("i-PRO/Panasonic").Parse(From, d.ToArray())!;
        Assert.Equal("192.168.0.10", hit.Ip.ToString());
        Assert.Equal("WV-S2136", hit.Model);
        Assert.Equal("00:80:45:11:22:33", hit.Mac);
    }

    public static void TestParseVivotek()
    {
        var d = new List<byte> { 0x01, 0x4a, 0x5d, 0x8f, 0x1c };
        d.AddRange(new byte[] { 0x02, 6, 0x00, 0x02, 0xd1, 0x01, 0x02, 0x03 });
        d.AddRange(new byte[] { 0x03, 4, 192, 168, 0, 99 });
        d.AddRange(new byte[] { 0x04, 1 });
        var m = Encoding.ASCII.GetBytes("FD9187-HT");
        d.Add(0x09); d.Add((byte)m.Length); d.AddRange(m);
        var hit = Proto("Vivotek").Parse(From, d.ToArray())!;
        Assert.Equal("192.168.0.99", hit.Ip.ToString());
        Assert.Equal("FD9187-HT", hit.Model);
        Assert.Equal("00:02:D1:01:02:03", hit.Mac);
    }

    public static void TestParseSony()
    {
        var d = new List<byte> { 0x02 };
        foreach (var line in new[] { "MODEL:SNC-VB770", "MAC:08:00:46:aa:bb:cc", "IPADR:192.168.0.100" })
        { d.AddRange(Encoding.ASCII.GetBytes(line)); d.Add(0xff); }
        d.Add(0x03);
        var hit = Proto("Sony").Parse(From, d.ToArray())!;
        Assert.Equal("192.168.0.100", hit.Ip.ToString());
        Assert.Equal("SNC-VB770", hit.Model);
        Assert.Equal("08:00:46:AA:BB:CC", hit.Mac, "MAC with colons kept whole");
    }

    public static void TestParseUbiquiti()
    {
        var body = new List<byte>();
        void Tlv(byte t, byte[] v) { body.Add(t); body.Add((byte)(v.Length >> 8)); body.Add((byte)v.Length); body.AddRange(v); }
        Tlv(0x02, new byte[] { 0x24, 0x5a, 0x4c, 0x01, 0x02, 0x03, 10, 0, 0, 50 });
        Tlv(0x0b, Encoding.ASCII.GetBytes("Front-Door-Cam"));
        Tlv(0x0c, Encoding.ASCII.GetBytes("UVC G4 Bullet"));
        var d = new List<byte> { 0x01, 0x00, (byte)(body.Count >> 8), (byte)body.Count };
        d.AddRange(body);
        var hit = Proto("Ubiquiti").Parse(From, d.ToArray())!;
        Assert.Equal("10.0.0.50", hit.Ip.ToString());
        Assert.Equal("24:5A:4C:01:02:03", hit.Mac);
        Assert.Equal("UVC G4 Bullet", hit.Model);
        Assert.Equal("Front-Door-Cam", hit.Name);
        Assert.True(hit.Camera, "UVC is a camera");
    }

    public static void TestParseMdnsAxis()
    {
        var d = new List<byte> { 0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0 }; // response, 1 answer
        int serviceAt = d.Count;
        foreach (var label in "_axis-video._tcp.local".Split('.')) { d.Add((byte)label.Length); d.AddRange(Encoding.ASCII.GetBytes(label)); }
        d.Add(0);
        d.AddRange(new byte[] { 0, 12, 0, 1, 0, 0, 0x11, 0x94 }); // PTR IN ttl
        var inst = Encoding.UTF8.GetBytes("AXIS P3245-V - ACCC8E123456");
        var rdata = new List<byte> { (byte)inst.Length };
        rdata.AddRange(inst);
        rdata.Add((byte)(0xC0 | (serviceAt >> 8))); rdata.Add((byte)serviceAt); // compressed pointer to the service name
        d.Add((byte)(rdata.Count >> 8)); d.Add((byte)rdata.Count);
        d.AddRange(rdata);
        var hit = Proto("mDNS (Axis/Arecont)").Parse(IPAddress.Parse("192.168.0.90"), d.ToArray())!;
        Assert.Equal("192.168.0.90", hit.Ip.ToString());
        Assert.Equal("AXIS P3245-V", hit.Model);
        Assert.Equal("AC:CC:8E:12:34:56", hit.Mac);
    }

    public static void TestFoundByListsProtocols()
    {
        var d = new Device(IPAddress.Parse("10.0.0.5")) { Sources = DiscoverySource.ArpSweep | DiscoverySource.Vendor, CameraProtocol = true };
        d.Protocols.Add("Hikvision SADP");
        Assert.Equal("ArpSweep, Hikvision SADP", d.FoundBy);
        Assert.Equal(DeviceKind.Camera, d.Kind);
    }

    public static void TestExtractTitle()
    {
        Assert.Equal("Network Camera &", DeviceEnricher.ExtractTitle("<html><head><TITLE>\n Network   Camera &amp; </TITLE>"));
        Assert.Equal(null, DeviceEnricher.ExtractTitle("<html>no title</html>"));
        Assert.Equal("https://10.0.0.5/", DeviceEnricher.PickWebUrl(IPAddress.Parse("10.0.0.5"), new[] { 443, 554 }));
        Assert.Equal(null, DeviceEnricher.PickWebUrl(IPAddress.Parse("10.0.0.5"), new[] { 554 }));
    }

    // ---------- ARP sweep logic ----------
    sealed class FakeArp(Dictionary<string, string> table) : IArpResolver
    {
        public bool IsSupported => true;
        public int Calls;
        public Task<string?> ResolveAsync(IPAddress target, IPAddress source, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(table.TryGetValue(target.ToString(), out var m) ? m : null);
        }
    }

    public static async Task TestArpSweepFindsDevices()
    {
        var fake = new FakeArp(new() { ["10.0.5.21"] = "00:18:85:00:00:01", ["10.0.5.40"] = "D4:2D:C5:00:00:02" });
        var found = await ArpSweeper.SweepAsync(fake, Subnet.Parse("10.0.5.0/24"), IPAddress.Parse("10.0.5.251"),
            Array.Empty<IPAddress>(), 32, null, CancellationToken.None);
        Assert.Equal(2, found.Count);
        Assert.Equal("10.0.5.21", found[0].Ip.ToString());
        Assert.Equal(253, fake.Calls, "skips own address");
    }

    public static void TestProxyArpRemoved()
    {
        var list = Enumerable.Range(1, 30).Select(i => (IPAddress.Parse($"10.0.9.{i}"), "AA:AA:AA:AA:AA:AA")).ToList();
        list.Add((IPAddress.Parse("10.0.9.200"), "00:40:8C:00:00:01"));
        var cleaned = ArpSweeper.RemoveProxyArp(list);
        Assert.Equal(1, cleaned.Count);
    }

    // ---------- Export ----------
    public static void TestCsvEscapesAndBlocksFormulas()
    {
        var d = new Device(IPAddress.Parse("10.0.5.40")) { Mac = "D4:2D:C5:00:00:02", Vendor = "i-PRO Co., Ltd.", WebTitle = "=HYPERLINK(\"x\")" };
        d.OpenPorts.Add(80); d.OpenPorts.Add(554);
        d.Sources = DiscoverySource.ArpSweep | DiscoverySource.Onvif;
        string csv = CsvExporter.ToCsv(new[] { d }, "Test site");
        Assert.True(csv.Contains("\"i-PRO Co., Ltd.\""), "comma quoted");
        Assert.True(csv.Contains("\"'=HYPERLINK(\"\"x\"\")\""), "formula neutralised: " + csv);
        Assert.True(csv.Contains(",Camera,"), "camera kind");
        Assert.True(csv.Contains("80 554"), "ports");
    }

    // ---------- Robustness regressions (found by stress testing) ----------
    static void Within(int ms, string what, Action a)
    {
        var t = Task.Run(a);
        if (!t.Wait(ms)) throw new Exception($"{what}: took longer than {ms} ms");
    }

    public static void TestOnvifSurvivesHostileXml()
    {
        // Deep nesting used to overflow the stack (kills the process); bad XML declarations threw ArgumentException.
        string deep = string.Concat(Enumerable.Repeat("<a>", 20000)) + "<ProbeMatch/>" + string.Concat(Enumerable.Repeat("</a>", 20000));
        Within(2000, "deep XML", () => Assert.Equal(null, OnvifProbe.Parse(IPAddress.Loopback, Encoding.UTF8.GetBytes(deep)), "deep XML rejected"));
        Assert.Equal(null, OnvifProbe.Parse(IPAddress.Loopback, Encoding.UTF8.GetBytes("<?xml version=\"9\"?><ProbeMatch/>")), "bad version");
        Assert.Equal(null, OnvifProbe.Parse(IPAddress.Loopback, Array.Empty<byte>()), "empty");
        var rnd = new Random(7);
        var seed = Encoding.UTF8.GetBytes(OnvifReply.Trim());
        for (int i = 0; i < 3000; i++)
        {
            var b = (byte[])seed.Clone();
            for (int k = rnd.Next(1, 6); k > 0; k--) b[rnd.Next(b.Length)] = (byte)rnd.Next(256);
            OnvifProbe.Parse(IPAddress.Loopback, b); // must not throw
        }
    }

    public static void TestOnvifAdjacentElementsAndEmptyOnes()
    {
        // XAddrs directly followed by Scopes (no whitespace) and an empty element first: the streaming reader must not skip any.
        string xml = "<Envelope><Body><ProbeMatches><ProbeMatch><Types/><XAddrs>http://10.1.2.3/onvif</XAddrs><Scopes>onvif://www.onvif.org/hardware/M3106</Scopes></ProbeMatch></ProbeMatches></Body></Envelope>";
        var hit = OnvifProbe.Parse(IPAddress.Loopback, Encoding.UTF8.GetBytes(xml))!;
        Assert.Equal("10.1.2.3", hit.Ip.ToString());
        Assert.Equal("M3106", hit.Model);
        Assert.Equal(null, OnvifProbe.Parse(IPAddress.Loopback, Encoding.UTF8.GetBytes("<Probe><XAddrs>http://10.1.2.3/</XAddrs></Probe>")), "no ProbeMatch");
    }

    public static void TestSsdpDescriptionHostileAndNested()
    {
        var (name, _, model) = SsdpProbe.ParseDescription("<root><device><friendlyName>  </friendlyName><friendlyName>Gate <b>2</b></friendlyName><modelName>NVR</modelName></device></root>");
        Assert.Equal("Gate 2", name, "first non-blank, child text included");
        Assert.Equal("NVR", model);
        string deep = "<root>" + string.Concat(Enumerable.Repeat("<x>", 20000)) + "</root>";
        Within(2000, "deep description", () => Assert.Equal(null, SsdpProbe.ParseDescription(deep).Name));
    }

    public static void TestExtractTitleIsFastOnHostilePages()
    {
        // The old regex backtracked for minutes on these 64 KB inputs.
        Within(1000, "whitespace", () => Assert.Equal(null, DeviceEnricher.ExtractTitle("<title>" + new string(' ', 64 * 1024))));
        Within(1000, "repeated", () => Assert.Equal(null, DeviceEnricher.ExtractTitle(string.Concat(Enumerable.Repeat("<title> a ", 6500)))));
        Assert.Equal("Cam", DeviceEnricher.ExtractTitle("<titlebar>x</titlebar><title lang=\"en\">Cam</title>"), "skips <titlebar>");
        Assert.Equal(new string('x', 80), DeviceEnricher.ExtractTitle("<title>" + new string('x', 500) + "</title>"), "capped at 80");
    }

    public static void TestCsvBlocksTabAndCrFormulas()
    {
        var d = new Device(IPAddress.Parse("10.0.0.1")) { Vendor = "\t=cmd|' /C calc'!A0", WebTitle = "\r=1+1" };
        string csv = CsvExporter.ToCsv(new[] { d });
        Assert.True(!csv.Contains(",\t="), "tab formula neutralised: " + csv);
        Assert.True(csv.Contains("\"'\r=1+1\""), "CR formula neutralised: " + csv);
    }

    public static void TestSubnetParseRejectsBadInput()
    {
        foreach (var bad in new[] { "10.0.0.0/33", "10.0.0.0/-1", "10.0.0.0/x", "::1/64", "10.0.0.0/24/1" })
        {
            bool threw = false;
            try { Subnet.Parse(bad); } catch (FormatException) { threw = true; }
            Assert.True(threw, "rejected " + bad);
        }
        Assert.Equal("0.0.0.0/0", Subnet.Parse("10.0.0.0/0").ToString());
        Assert.Equal(1, Subnet.Parse("10.0.0.5/32").Hosts().Count());
        Assert.Equal(2, Subnet.Parse("255.255.255.254/31").Hosts().Count(), "no wrap at top of address space");
    }

    public static void TestTempCandidatesStayInsideSubnet()
    {
        foreach (var s in new[] { "10.0.0.0/30", "10.0.0.0/29", "0.0.0.0/30", "255.255.255.0/24", "192.0.0.0/24" })
        {
            var sub = Subnet.Parse(s);
            var c = TempAddressManager.Candidates(sub).ToList();
            Assert.True(c.All(a => sub.Contains(a) && !Ipv4.ToUInt(a).Equals(sub.Network) && Ipv4.ToUInt(a) != sub.Broadcast), $"{s}: {string.Join(",", c)}");
        }
    }

    public static void TestHikvisionDefaultRangeIsJoinable()
    {
        Assert.True(Ipv4.IsJoinable(IPAddress.Parse("192.0.0.64")), "Hikvision default 192.0.0.64");
        Assert.True(Ipv4.IsJoinable(IPAddress.Parse("10.1.1.1")), "private");
        Assert.True(!Ipv4.IsJoinable(IPAddress.Parse("192.0.2.1")) && !Ipv4.IsJoinable(IPAddress.Parse("8.8.8.8")), "public / doc ranges");
    }

    public static async Task TestDeviceReadsAreThreadSafe()
    {
        // The UI reads FoundBy/OpenPorts while the engine adds to them; this used to throw "Collection was modified".
        var d = new Device(IPAddress.Parse("10.0.0.9"));
        using var cts = new CancellationTokenSource(700);
        var writer = Task.Run(() => { int i = 0; while (!cts.IsCancellationRequested) lock (d) { d.Protocols.Add("P" + (i++ % 300)); d.OpenPorts.Add(i % 9000); if (i % 500 == 0) { d.Protocols.Clear(); d.OpenPorts.Clear(); } } });
        var reader = Task.Run(() => { while (!cts.IsCancellationRequested) { _ = d.FoundBy; _ = d.OpenPortsText; _ = d.Kind; } });
        await Task.WhenAll(writer, reader);
    }

    public static async Task TestBlockingArpCallsRunAtFullConcurrency()
    {
        // SendARP blocks; running it with Task.Run starved the thread pool (12 of 48 concurrent in testing).
        int inFlight = 0, peak = 0;
        var r = new BlockingArp(() => { int n = Interlocked.Increment(ref inFlight); lock (typeof(Tests)) peak = Math.Max(peak, n); Thread.Sleep(400); Interlocked.Decrement(ref inFlight); });
        await ArpSweeper.SweepAsync(r, Subnet.Parse("10.0.0.0/26"), IPAddress.Parse("10.0.0.1"), Array.Empty<IPAddress>(), 48, null, CancellationToken.None);
        Assert.True(peak >= 40, $"peak concurrency {peak} of 48");
    }

    sealed class BlockingArp(Action block) : IArpResolver
    {
        public bool IsSupported => true;
        public Task<string?> ResolveAsync(IPAddress target, IPAddress source, CancellationToken ct) =>
            WindowsArpResolver.RunBlocking<string?>(() => { block(); return null; }, ct);
    }

    public static async Task TestProbeListenerSurvivesThrowingHandler()
    {
        // A reply that makes the handler throw must not stop the listener (it used to end the whole ONVIF/SSDP probe).
        using var echo = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var ep = (IPEndPoint)echo.Client.LocalEndPoint!;
        var server = Task.Run(async () => { var r = await echo.ReceiveAsync(); for (int i = 0; i < 3; i++) await echo.SendAsync(new byte[] { 1 }, 1, r.RemoteEndPoint); });
        int calls = 0;
        await MulticastProbe.RunAsync(IPAddress.Loopback, ep, new[] { new byte[] { 0 } }, TimeSpan.FromSeconds(1),
            (_, _) => { if (Interlocked.Increment(ref calls) == 1) throw new FormatException("bad reply"); }, CancellationToken.None);
        await server;
        Assert.Equal(3, calls, "replies handled after a bad one");
    }

    public static void TestMdnsRejectsOverlongNames()
    {
        var mdns = Proto("mDNS (Axis/Arecont)");
        // Reserved label type 0x40-0xBF and a 300-byte name must be refused, not fed to the instance-name regex.
        var d = new List<byte> { 0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0x7F };
        d.AddRange(new byte[200]);
        Assert.Equal(null, mdns.Parse(From, d.ToArray()), "reserved label length");
        var e = new List<byte> { 0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0 };
        for (int i = 0; i < 6; i++) { e.Add(60); e.AddRange(Enumerable.Repeat((byte)'a', 60)); }
        e.Add(0); e.AddRange(new byte[10]);
        Assert.Equal(null, mdns.Parse(From, e.ToArray()), "name over 255 bytes");
    }

    // ---------- Whole scan across several IP ranges on one cable (simulated site, no real network) ----------
    // Laptop 192.168.1.50/24. Devices sit in other ranges too; only Layer 2 connects them. The fake capture plays the
    // traffic the laptop would hear; the fake ARP answers only when we ask from an address in the device's own range,
    // like a real camera does; the fake joiner stands in for netsh.

    sealed class SimCapture(IEnumerable<Observation> heard) : IPassiveCapture
    {
        public bool IsAvailable(out string? error) { error = null; return true; }
        public string? FindDevice(string adapterId, string adapterName) => "sim0";
        public void Listen(string device, TimeSpan duration, CancellationToken ct, Action<Observation> on) { foreach (var o in heard) on(o); }
    }

    sealed class SimJoiner : IAddressJoiner
    {
        public readonly List<Subnet> Joined = new();
        public Task<IPAddress?> AddFreeAddressAsync(Adapter adapter, Subnet subnet, ISet<IPAddress> avoid, CancellationToken ct)
        {
            lock (Joined) Joined.Add(subnet);
            var ip = TempAddressManager.Candidates(subnet).First(c => !avoid.Contains(c));
            return Task.FromResult<IPAddress?>(ip);
        }
    }

    sealed class SimArp(Dictionary<string, string> devices, Subnet? proxyRange = null) : IArpResolver
    {
        public bool IsSupported => true;
        public Task<string?> ResolveAsync(IPAddress target, IPAddress source, CancellationToken ct)
        {
            int prefix = Ipv4.IsLinkLocal(target) ? 16 : 24;
            if (!Subnet.Of(source, prefix).Contains(target)) return Task.FromResult<string?>(null); // not in our range: no reply
            if (proxyRange is { } p && p.Contains(target)) return Task.FromResult<string?>("00:00:5E:00:01:01"); // router answers for all
            return Task.FromResult(devices.TryGetValue(target.ToString(), out var m) ? m : null);
        }
    }

    static Adapter SimLaptop() => new("sim", "Ethernet", "sim adapter", 1, "00:11:22:33:44:55", true, true,
        new List<(IPAddress, int)> { (IPAddress.Parse("192.168.1.50"), 24) }, new List<IPAddress> { IPAddress.Parse("192.168.1.1") });

    static Observation Heard(string ip, string? mac) => new(IPAddress.Parse(ip), mac);

    static readonly ScanOptions SimOptions = new()
    {
        ListenTime = TimeSpan.FromMilliseconds(50), ProbeTime = TimeSpan.FromMilliseconds(100),
        VendorProtocols = false, ReadDetails = false,
    };

    public static async Task TestScanFindsDevicesInOtherRanges()
    {
        var world = new Dictionary<string, string>
        {
            ["192.168.1.10"] = "44:19:B6:00:00:10",   // NVR in the laptop's own range (quiet)
            ["10.0.5.21"] = "00:40:8C:00:00:21",      // Axis camera, heard on the wire
            ["10.0.5.40"] = "D4:2D:C5:00:00:40",      // i-PRO camera, never transmits: only the sweep finds it
            ["172.16.8.1"] = "00:07:5F:00:08:01",     // Bosch encoder, only seen as the target of an ARP request
            ["172.16.8.30"] = "00:09:18:00:08:30",    // Hanwha camera, quiet
            ["192.0.0.64"] = "44:19:B6:00:00:64",     // new Hikvision camera on its factory default address
            ["169.254.12.34"] = "00:40:8C:00:12:34",  // camera that gave itself a link-local address
        };
        var heard = new[]
        {
            Heard("10.0.5.21", "00:40:8C:00:00:21"),
            Heard("172.16.8.1", null),                       // someone ARPed for it
            Heard("0.0.0.0", "44:19:B6:00:00:64"),           // filtered: not a usable host
            Heard("192.0.0.64", "44:19:B6:00:00:64"),        // ARP probe from the Hikvision
            Heard("169.254.12.34", "00:40:8C:00:12:34"),
            Heard("10.0.9.5", "00:40:8C:00:09:05"),          // device behind a proxy-ARP router
            Heard("8.8.8.8", "00:00:5E:00:01:01"),           // public address seen on the wire: never joined
            Heard("192.168.1.50", "00:11:22:33:44:55"),      // the laptop itself: ignored
        };
        var joiner = new SimJoiner();
        var engine = new ScanEngine(new SimArp(world, Subnet.Parse("10.0.9.0/24")), joiner, capture: new SimCapture(heard));
        var log = new List<string>();
        engine.Log += m => { lock (log) log.Add(m); };
        await engine.RunAsync(SimLaptop(), SimOptions, CancellationToken.None);

        string Dump() => string.Join("\n", engine.Devices.OrderBy(d => Ipv4.ToUInt(d.Ip)).Select(d => $"  {d.Ip} {d.Mac} [{d.FoundBy}]")) + "\nlog:\n  " + string.Join("\n  ", log);
        var joined = joiner.Joined.Select(s => s.ToString()).OrderBy(s => s).ToList();
        Assert.Equal("10.0.5.0/24,10.0.9.0/24,169.254.0.0/16,172.16.8.0/24,192.0.0.0/24", string.Join(",", joined), "ranges joined");

        Device Find(string ip) => engine.Devices.FirstOrDefault(d => d.Ip.ToString() == ip) ?? throw new Exception($"{ip} not found:\n" + Dump());
        Assert.True(Find("10.0.5.40").Sources.HasFlag(DiscoverySource.ArpSweep), "quiet camera in another range found by sweep");
        Assert.True(Find("10.0.5.21").Sources.HasFlag(DiscoverySource.Passive), "heard camera");
        Assert.Equal("00:07:5F:00:08:01", Find("172.16.8.1").Mac, "range known only from an ARP target, device then swept");
        Assert.True(Find("172.16.8.30").Sources.HasFlag(DiscoverySource.ArpSweep), "quiet Hanwha");
        Assert.Equal("44:19:B6:00:00:10", Find("192.168.1.10").Mac, "own-range NVR swept");
        Assert.True(Find("192.0.0.64").Vendor!.Contains("Hikvision", StringComparison.OrdinalIgnoreCase), "Hikvision default IP, vendor " + Find("192.0.0.64").Vendor);
        Assert.True(Find("169.254.12.34").Sources.HasFlag(DiscoverySource.ArpSweep), "link-local device confirmed by ARP");
        Find("10.0.9.5");
        Assert.True(!engine.Devices.Any(d => d.Mac == "00:00:5E:00:01:01" && d.Ip.ToString().StartsWith("10.0.9.")), "proxy-ARP answers dropped:\n" + Dump());
        Assert.True(!engine.Devices.Any(d => d.Ip.ToString() is "192.168.1.50" or "0.0.0.0" || d.Ip.ToString().EndsWith(".251")), "laptop's own / temporary addresses hidden:\n" + Dump());
        Assert.Equal(9, engine.Devices.Count, "device count:\n" + Dump());
    }

    public static async Task TestScanJoinsAtMostMaxExtraRanges()
    {
        var heard = Enumerable.Range(1, 9).Select(i => Heard($"10.{i}.0.5", $"00:40:8C:00:00:{i:X2}")).ToList();
        heard.AddRange(Enumerable.Range(0, 3).Select(i => Heard($"10.9.0.{10 + i}", $"00:40:8C:00:09:{i:X2}"))); // busiest range
        var joiner = new SimJoiner();
        var engine = new ScanEngine(new SimArp(new()), joiner, capture: new SimCapture(heard));
        var log = new List<string>();
        engine.Log += m => { lock (log) log.Add(m); };
        await engine.RunAsync(SimLaptop(), SimOptions, CancellationToken.None);
        Assert.Equal(6, joiner.Joined.Count, "joined ranges");
        Assert.Equal("10.9.0.0/24", joiner.Joined[0].ToString(), "busiest range first");
        Assert.True(log.Any(l => l.Contains("Skipped 3 more range")), "skip logged: " + string.Join(" | ", log));
    }

    public static async Task TestSafeModeNeverJoinsOrSweeps()
    {
        var joiner = new SimJoiner();
        var arp = new SimArp(new() { ["192.168.1.10"] = "44:19:B6:00:00:10" });
        var engine = new ScanEngine(arp, joiner, capture: new SimCapture(new[] { Heard("10.0.5.21", "00:40:8C:00:00:21") }));
        await engine.RunAsync(SimLaptop(), new ScanOptions
        {
            SafeMode = true, ListenTime = TimeSpan.FromMilliseconds(50), ProbeTime = TimeSpan.FromMilliseconds(100), VendorProtocols = false, ReadDetails = false,
        }, CancellationToken.None);
        Assert.Equal(0, joiner.Joined.Count, "no addresses added");
        Assert.True(!engine.Devices.Any(d => d.Sources.HasFlag(DiscoverySource.ArpSweep)), "no sweep");
        Assert.True(engine.Devices.Any(d => d.Ip.ToString() == "10.0.5.21"), "heard device still listed");
    }

    // ---------- Demo site (virtual scanning) and "Open web page" through a temporary IP ----------

    static DemoSite FastDemo() => new() { ArpDelay = TimeSpan.Zero, JoinDelay = TimeSpan.Zero };

    static async Task<(ScanEngine Engine, List<string> Log)> RunDemo(DemoSite site, bool safe)
    {
        var engine = new ScanEngine(site, site, capture: site, readDetails: site.ReadDetailsAsync);
        var log = new List<string>();
        engine.Log += m => { lock (log) log.Add(m); };
        await engine.RunAsync(site.Laptop, DemoSite.Options(TimeSpan.FromMilliseconds(100), safe), CancellationToken.None);
        return (engine, log);
    }

    public static async Task TestDemoScanFindsWholeSite()
    {
        var site = FastDemo();
        var (engine, log) = await RunDemo(site, safe: false);
        string Dump() => string.Join("\n", engine.Devices.OrderBy(d => Ipv4.ToUInt(d.Ip)).Select(d => $"  {d.Ip} {d.Vendor} | {d.Description} [{d.FoundBy}]")) + "\n" + string.Join("\n", log);
        Assert.Equal(DemoSite.Devices.Count, engine.Devices.Count, "every demo device found:\n" + Dump());
        foreach (var dd in DemoSite.Devices)
        {
            var d = engine.Devices.First(x => x.Ip.ToString() == dd.Ip);
            Assert.Equal(dd.Mac, d.Mac, dd.Ip + " MAC");
            Assert.True(d.Vendor != null, $"{dd.Ip} has a manufacturer from its MAC:\n" + Dump());
            Assert.Equal(dd.Camera ? DeviceKind.Camera : dd.Ports.Length > 0 ? DeviceKind.WebDevice : DeviceKind.Other, d.Kind, dd.Ip + " type");
        }
        Assert.True(engine.Devices.First(x => x.Ip.ToString() == "10.0.5.40").Sources.HasFlag(DiscoverySource.ArpSweep), "quiet camera found by sweep");
        Assert.Equal(4, site.Joined.Count, "joined 10.0.5/24, 172.16.8/24, 192.0.0/24 and 169.254/16");
        Assert.True(!engine.Devices.Any(d => site.Joined.Any(j => j.Address.Equals(d.Ip))), "temporary IPs not listed as devices");
        Assert.True(log.Any(l => l.Contains("Other ranges seen")), "range discovery logged");
        Assert.True(log.Any(l => l.Contains($"{DemoSite.Devices.Count(x => x.Camera)} camera(s)")), "camera count in final log:\n" + Dump());
    }

    public static async Task TestDemoSafeModeChangesNothing()
    {
        var site = FastDemo();
        var (engine, _) = await RunDemo(site, safe: true);
        Assert.Equal(0, site.Joined.Count, "no temporary IPs in safe mode");
        Assert.Equal(DemoSite.Devices.Count(d => d.Heard), engine.Devices.Count, "only devices that transmit are listed");
        var other = engine.Devices.First(d => d.Ip.ToString() == "10.0.5.21");
        Assert.Equal(null, other.WebUrl, "other-range device has no web page yet (not reachable)");
        var own = engine.Devices.First(d => d.Ip.ToString() == "192.168.1.64");
        Assert.Equal("http://192.168.1.64/", own.WebUrl, "own-range device reachable");
    }

    public static async Task TestOpenWebPageAddsVirtualIpInDeviceRange()
    {
        var site = FastDemo();
        var (engine, _) = await RunDemo(site, safe: true); // safe scan: nothing joined yet
        var web = new WebPageAccess(site, () => site.Laptop, site.ReadDetailsAsync);
        var avoid = engine.Devices.Select(d => d.Ip).ToHashSet();
        var axis = engine.Devices.First(d => d.Ip.ToString() == "10.0.5.21");

        // Safe mode: asks first, changes nothing.
        var ask = await web.PrepareAsync(axis, mayJoin: false, avoid, CancellationToken.None);
        Assert.Equal(WebPageStatus.NeedsTemporaryAddress, ask.Status);
        Assert.Equal("10.0.5.0/24", ask.Range.ToString());
        Assert.Equal(0, site.Joined.Count, "nothing added before the user agrees");

        // User agrees: virtual IP added in the camera's range, ports re-checked, page ready.
        var ok = await web.PrepareAsync(axis, mayJoin: true, avoid, CancellationToken.None);
        Assert.Equal(WebPageStatus.Ready, ok.Status);
        Assert.Equal("http://10.0.5.21/", ok.Url);
        Assert.Equal("10.0.5.251", ok.TemporaryAddress?.ToString(), "temporary IP at the top of the camera's range");
        Assert.True(Reach.CanReach(site.Laptop.Addresses, axis.Ip), "laptop now in the camera's range");
        Assert.Equal(DeviceKind.Camera, axis.Kind, "details read after joining");

        // Second camera in the same range: no second IP.
        var ipro = new Device(IPAddress.Parse("10.0.5.40"));
        var again = await web.PrepareAsync(ipro, mayJoin: true, avoid, CancellationToken.None);
        Assert.Equal(WebPageStatus.Ready, again.Status);
        Assert.Equal(null, again.TemporaryAddress, "range already joined");
        Assert.Equal(1, site.Joined.Count);

        // Hikvision on its factory address and a self-assigned camera: both joinable.
        Assert.Equal(WebPageStatus.Ready, (await web.PrepareAsync(engine.Devices.First(d => d.Ip.ToString() == "192.0.0.64"), true, avoid, CancellationToken.None)).Status);
        var ll = await web.PrepareAsync(engine.Devices.First(d => d.Ip.ToString() == "169.254.12.34"), true, avoid, CancellationToken.None);
        Assert.Equal(WebPageStatus.Ready, ll.Status);
        Assert.Equal("169.254.0.0/16", ll.Range.ToString(), "link-local joins the whole /16");

        // Door controller: reachable but no web page.
        var door = engine.Devices.First(d => d.Ip.ToString() == "10.0.5.60");
        Assert.Equal(WebPageStatus.NoWebPage, (await web.PrepareAsync(door, true, avoid, CancellationToken.None)).Status);

        // Public address: never joined.
        Assert.Equal(WebPageStatus.NotJoinable, (await web.PrepareAsync(new Device(IPAddress.Parse("8.8.8.8")), true, avoid, CancellationToken.None)).Status);
        Assert.Equal(3, site.Joined.Count, "10.0.5/24, 192.0.0/24, 169.254/16");
    }

    public static async Task TestOpenWebPageReportsJoinFailure()
    {
        var web = new WebPageAccess(new NoJoin(), () => SimLaptop(), (_, _) => Task.CompletedTask);
        var r = await web.PrepareAsync(new Device(IPAddress.Parse("10.9.9.9")), true, new HashSet<IPAddress>(), CancellationToken.None);
        Assert.Equal(WebPageStatus.JoinFailed, r.Status);
        var noJoiner = new WebPageAccess(null, () => SimLaptop(), (_, _) => Task.CompletedTask);
        Assert.Equal(WebPageStatus.JoinFailed, (await noJoiner.PrepareAsync(new Device(IPAddress.Parse("10.9.9.9")), true, new HashSet<IPAddress>(), CancellationToken.None)).Status);
    }

    // ---------- Continuous ping ----------

    public static void TestPingStatsAndDownUpAlerts()
    {
        var s = new PingStats();
        var t = new DateTime(2026, 1, 1, 12, 0, 0);
        Assert.Equal(null, s.Add(new PingResult(true, 2), t));
        Assert.Equal(null, s.Add(new PingResult(true, 6), t));
        Assert.Equal(null, s.Add(new PingResult(false, 0, "Request timed out"), t.AddSeconds(2)));
        Assert.Equal(null, s.Add(new PingResult(false, 0, "Request timed out"), t.AddSeconds(3)));
        Assert.Equal("DOWN", s.Add(new PingResult(false, 0, "Request timed out"), t.AddSeconds(4)), "down after 3 lost in a row");
        Assert.Equal(null, s.Add(new PingResult(false, 0, "Request timed out"), t.AddSeconds(5)), "only announced once");
        Assert.Equal(t.AddSeconds(2), s.DownSince, "down since first lost");
        Assert.Equal("UP", s.Add(new PingResult(true, 1), t.AddSeconds(6)));
        Assert.Equal(7, s.Sent);
        Assert.Equal(3, s.Received);
        Assert.Equal(1L, s.MinMs);
        Assert.Equal(6L, s.MaxMs);
        Assert.Equal(3.0, s.AvgMs);
        Assert.True(s.ToString().Contains("Lost 4 (57.1%)"), s.ToString());

        var never = new PingStats();
        for (int i = 0; i < 5; i++) Assert.Equal(null, never.Add(new PingResult(false, 0, "x"), t), "no DOWN for a device that never answered");
        Assert.True(!never.ToString().Contains("Min"), "no times without replies");
    }

    sealed class FakePinger(Func<int, PingResult> answer) : IPinger
    {
        public int Calls;
        public IPAddress? LastSource;
        public Task<PingResult> PingAsync(IPAddress target, IPAddress? source, TimeSpan timeout, CancellationToken ct)
        {
            LastSource = source;
            return Task.FromResult(answer(Interlocked.Increment(ref Calls)));
        }
    }

    public static async Task TestContinuousPingRunsUntilStopped()
    {
        var pinger = new FakePinger(n => n % 4 == 0 ? new PingResult(false, 0, "Request timed out") : new PingResult(true, 3));
        var results = new List<PingResult>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(550));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await ContinuousPing.RunAsync(pinger, IPAddress.Parse("10.0.5.21"), IPAddress.Parse("10.0.5.251"), TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(50), (r, _, _) => results.Add(r), cts.Token);
        Assert.True(sw.ElapsedMilliseconds < 1500, "stops promptly when cancelled");
        Assert.True(results.Count is >= 6 and <= 14, $"one request per interval ({results.Count} in 550 ms at 50 ms)");
        Assert.Equal("10.0.5.251", pinger.LastSource?.ToString(), "sent from the address in the device's range");

        var throwing = new FakePinger(_ => throw new InvalidOperationException("adapter gone"));
        var seen = new List<PingResult>();
        using var cts2 = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await ContinuousPing.RunAsync(throwing, IPAddress.Loopback, null, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), (r, _, _) => seen.Add(r), cts2.Token);
        Assert.True(seen.Count > 1 && seen.All(r => !r.Success && r.Status == "adapter gone"), "errors are reported, ping keeps going");
    }

    public static async Task TestIcmpPingLoopback()
    {
        // Real ICMP. With a source address this uses IcmpSendEcho2Ex on Windows (checked by the Windows CI job).
        var pinger = new IcmpPinger();
        var r = await pinger.PingAsync(IPAddress.Loopback, null, TimeSpan.FromSeconds(2), CancellationToken.None);
        if (!r.Success && r.Status != null && r.Status.Contains("permi", StringComparison.OrdinalIgnoreCase)) throw new SkipException("ICMP not permitted: " + r.Status);
        Assert.True(r.Success, "loopback answers: " + r.Status);
        var withSource = await pinger.PingAsync(IPAddress.Loopback, IPAddress.Loopback, TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.True(withSource.Success, "loopback answers with explicit source: " + withSource.Status);
    }

    public static async Task TestDemoPingNeedsTheDevicesRange()
    {
        var site = FastDemo();
        var web = new WebPageAccess(site, () => site.Laptop, site.ReadDetailsAsync);
        var axis = IPAddress.Parse("10.0.5.21");
        var before = await site.PingAsync(axis, null, TimeSpan.FromMilliseconds(20), CancellationToken.None);
        Assert.True(!before.Success, "no reply from another range");

        var reach = await web.EnsureReachableAsync(axis, mayJoin: true, new HashSet<IPAddress>(), CancellationToken.None);
        Assert.Equal(WebPageStatus.Ready, reach.Status);
        Assert.Equal("10.0.5.251", reach.Source?.ToString(), "ping source is the temporary IP");
        var after = await site.PingAsync(axis, reach.Source, TimeSpan.FromMilliseconds(20), CancellationToken.None);
        Assert.True(after.Success && after.RoundTripMs is >= 1 and <= 4, "reply after joining");

        var own = await web.EnsureReachableAsync(IPAddress.Parse("192.168.1.64"), mayJoin: false, new HashSet<IPAddress>(), CancellationToken.None);
        Assert.Equal("192.168.1.50", own.Source?.ToString(), "own range: laptop's normal address, nothing added");
        Assert.Equal(null, own.TemporaryAddress);
    }

    sealed class NoJoin : IAddressJoiner
    {
        public Task<IPAddress?> AddFreeAddressAsync(Adapter adapter, Subnet subnet, ISet<IPAddress> avoid, CancellationToken ct) => Task.FromResult<IPAddress?>(null);
    }

    // ---------- Live tests (real sockets / capture) ----------
    static IPAddress LocalIPv4()
    {
        var a = Adapter.List().SelectMany(x => x.Addresses).FirstOrDefault(x => !IPAddress.IsLoopback(x.Address)).Address;
        return a ?? throw new SkipException("no IPv4 adapter");
    }

    [Live]
    public static async Task TestOnvifAndSsdpOverRealSockets()
    {
        var local = LocalIPv4();
        // Fake camera: answers ONVIF and SSDP multicast on this host.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        async Task FakeResponder(int port, Func<byte[], byte[]?> answer)
        {
            using var u = new UdpClient();
            u.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            u.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            u.JoinMulticastGroup(IPAddress.Parse("239.255.255.250"), local);
            u.MulticastLoopback = true;
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var r = await u.ReceiveAsync(cts.Token);
                    var reply = answer(r.Buffer);
                    if (reply != null) await u.SendAsync(reply, reply.Length, r.RemoteEndPoint);
                }
            }
            catch (OperationCanceledException) { }
        }
        string onvif = OnvifReply.Trim().Replace("10.0.5.40", local.ToString());
        var t1 = FakeResponder(3702, b => Encoding.UTF8.GetString(b).Contains("discovery/Probe") ? Encoding.UTF8.GetBytes(onvif) : null);
        var t2 = FakeResponder(1900, b => Encoding.ASCII.GetString(b).StartsWith("M-SEARCH")
            ? Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nLOCATION: http://{local}:1/d.xml\r\nSERVER: FakeCam/1.0\r\n\r\n") : null);
        await Task.Delay(300);

        var hits = new List<ProbeHit>();
        await MulticastProbe.RunAsync(local, OnvifProbe.Group, OnvifProbe.Probes(), TimeSpan.FromSeconds(2),
            (from, data) => { var h = OnvifProbe.Parse(from.Address, data); if (h != null) lock (hits) hits.Add(h); }, CancellationToken.None);
        await MulticastProbe.RunAsync(local, SsdpProbe.Group, SsdpProbe.Probes(), TimeSpan.FromSeconds(2),
            (from, data) => { var h = SsdpProbe.Parse(from.Address, data); if (h != null) lock (hits) hits.Add(h); }, CancellationToken.None);
        cts.Cancel();
        await Task.WhenAll(t1, t2);

        Assert.True(hits.Any(h => h.Model == "WV-S1136" && h.Ip.Equals(local)), $"ONVIF reply received ({hits.Count} hits)");
        Assert.True(hits.Any(h => h.Server == "FakeCam/1.0"), "SSDP reply received");
    }

    [Live]
    public static async Task TestPassiveCaptureSeesBroadcast()
    {
        if (!PassiveListener.IsAvailable(out var why)) throw new SkipException(why ?? "no pcap");
        var adapter = Adapter.List().FirstOrDefault(a => a.Addresses.Count > 0) ?? throw new SkipException("no adapter");
        var dev = PassiveListener.FindDevice(adapter.Id, adapter.Name) ?? throw new SkipException("capture device not found");
        var local = adapter.Addresses[0].Address;

        var seen = new List<Observation>();
        var listen = Task.Run(() => PassiveListener.Listen(dev, TimeSpan.FromSeconds(3), CancellationToken.None, o => { lock (seen) seen.Add(o); }));
        await Task.Delay(800);
        using (var u = new UdpClient(new IPEndPoint(local, 0)) { EnableBroadcast = true })
            for (int i = 0; i < 3; i++) await u.SendAsync(new byte[] { 1, 2, 3 }, 3, new IPEndPoint(IPAddress.Broadcast, 9));
        try { await listen; }
        catch (InvalidOperationException ex) when (ex.Message.Contains("permission", StringComparison.OrdinalIgnoreCase))
        {
            throw new SkipException("no capture permission (run as root/admin): " + ex.Message);
        }
        Assert.True(seen.Any(o => o.Ip.Equals(local) && o.Mac != null), $"own broadcast captured ({seen.Count} observations)");
    }

    [Live]
    public static async Task TestVendorProbeOverRealSockets()
    {
        var local = LocalIPv4();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var fake = new UdpClient();
        fake.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try { fake.Client.Bind(new IPEndPoint(IPAddress.Any, 2380)); } catch (SocketException) { throw new SkipException("port 2380 busy"); }
        var reply = new List<byte> { 0x02 };
        foreach (var line in new[] { "MODEL:SNC-FAKE", $"IPADR:{local}" }) { reply.AddRange(Encoding.ASCII.GetBytes(line)); reply.Add(0xff); }
        reply.Add(0x03);
        var responder = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var r = await fake.ReceiveAsync(cts.Token);
                    if (r.Buffer.Length > 0 && r.Buffer[0] == 0x02 && Encoding.ASCII.GetString(r.Buffer).Contains("ENQ:allinfo"))
                        await fake.SendAsync(reply.ToArray(), reply.Count, r.RemoteEndPoint);
                }
            }
            catch (OperationCanceledException) { }
        });

        var hits = new List<ProbeHit>();
        var sony = VendorProbes.All.First(p => p.Name == "Sony");
        // Broadcast may not loop back on every host; send the same request unicast to ourselves as well.
        var unicast = sony with { Broadcast = false, Multicast = null };
        await Task.WhenAll(
            MulticastProbe.RunVendorAsync(local, sony, TimeSpan.FromSeconds(2), (f, data) => { var h = sony.Parse(f.Address, data); if (h != null) lock (hits) hits.Add(h); }, cts.Token),
            Task.Run(async () =>
            {
                using var u = new UdpClient(new IPEndPoint(local, 0));
                var req = unicast.Payload();
                await u.SendAsync(req, req.Length, new IPEndPoint(local, 2380));
                var r = await u.ReceiveAsync(cts.Token);
                var h = sony.Parse(r.RemoteEndPoint.Address, r.Buffer);
                if (h != null) lock (hits) hits.Add(h);
            }));
        cts.Cancel();
        try { await responder; } catch { }
        Assert.True(hits.Any(h => h.Model == "SNC-FAKE" && h.Ip.Equals(local)), $"fake Sony camera answered ({hits.Count} hits)");
    }

    [Live]
    public static async Task TestScanEngineSafeModeEndToEnd()
    {
        var adapter = Adapter.List().FirstOrDefault(a => a.Addresses.Count > 0) ?? throw new SkipException("no adapter");
        var local = adapter.Addresses[0].Address;

        // Fake camera web page on port 80 + ONVIF responder, all on this host.
        var http = new TcpListener(IPAddress.Any, 80);
        try { http.Start(); } catch (SocketException) { throw new SkipException("port 80 not available"); }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var web = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    using var c = await http.AcceptTcpClientAsync(cts.Token);
                    var s = c.GetStream();
                    var buf = new byte[2048];
                    try { await s.ReadAsync(buf, cts.Token); } catch { }
                    var body = "<html><head><title>WV-S1136 Network Camera</title></head></html>";
                    var resp = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nServer: FakeCamHttp\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
                    try { await s.WriteAsync(resp, cts.Token); } catch { }
                }
            }
            catch (OperationCanceledException) { }
        });
        var onvif = Task.Run(async () =>
        {
            using var u = new UdpClient();
            u.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            u.Client.Bind(new IPEndPoint(IPAddress.Any, 3702));
            u.JoinMulticastGroup(IPAddress.Parse("239.255.255.250"), local);
            var reply = Encoding.UTF8.GetBytes(OnvifReply.Trim().Replace("10.0.5.40", local.ToString()));
            try { while (true) { var r = await u.ReceiveAsync(cts.Token); await u.SendAsync(reply, reply.Length, r.RemoteEndPoint); } }
            catch (OperationCanceledException) { }
        });
        await Task.Delay(300);

        var engine = new ScanEngine(new WindowsArpResolver(), null);
        var log = new List<string>();
        engine.Log += m => { lock (log) log.Add(m); };
        await engine.RunAsync(adapter, new ScanOptions { SafeMode = true, HideOwnAddresses = false, ListenTime = TimeSpan.FromSeconds(3), ProbeTime = TimeSpan.FromSeconds(2) }, CancellationToken.None);
        cts.Cancel();
        http.Stop();
        try { await Task.WhenAll(web, onvif); } catch { }

        var cam = engine.Devices.FirstOrDefault(d => d.Ip.Equals(local))
            ?? throw new Exception("fake camera not found. Log:\n" + string.Join("\n", log));
        Assert.Equal(DeviceKind.Camera, cam.Kind);
        Assert.Equal("WV-S1136", cam.Model);
        Assert.Equal($"http://{local}/", cam.WebUrl);
        Assert.Equal("WV-S1136 Network Camera", cam.WebTitle);
        Assert.True(cam.OpenPorts.Contains(80), "port 80 open");
        Console.WriteLine("     engine log: " + string.Join(" | ", log));
    }
}
