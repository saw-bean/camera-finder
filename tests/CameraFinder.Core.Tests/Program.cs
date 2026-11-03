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
        await listen;
        Assert.True(seen.Any(o => o.Ip.Equals(local) && o.Mac != null), $"own broadcast captured ({seen.Count} observations)");
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
