using System.Net;
using System.Net.Sockets;
using CameraFinder.Core.Discovery;
using CameraFinder.Core.Health;

static partial class Tests
{
    // ---------- SNMP ----------

    public static void TestSnmpGetRequestBytes()
    {
        // The classic example: GET sysDescr.0, community "public", SNMPv2c, request-id 1.
        var bytes = Ber.Message("public", 0xA0, 1, 0, 0, new[] { "1.3.6.1.2.1.1.1.0" });
        Assert.Equal("302602010104067075626C6963A019020101020100020100300E300C06082B060102010101000500", Convert.ToHexString(bytes));
        Assert.Equal("1.3.6.1.4.1.9.1.2134", Ber.DecodeOid(Ber.EncodeOid("1.3.6.1.4.1.9.1.2134")));
        Assert.Equal("1.3.6.1.2.1.17.7.1.2.2.1.2.1.0.64.140.80.5.40", Ber.DecodeOid(Ber.EncodeOid("1.3.6.1.2.1.17.7.1.2.2.1.2.1.0.64.140.80.5.40")));
        Assert.True(SnmpClient.OidCompare("1.3.6.1.2", "1.3.6.1.10") < 0, "numeric OID order");
    }

    public static void TestSnmpResponseParsing()
    {
        // GetResponse: sysName = "sw1", ifHCInOctets = Counter64 2^40, a noSuchInstance.
        byte[] Vb(string oid, byte tag, byte[] v) => Ber.Tlv(0x30, Ber.Tlv(0x06, Ber.EncodeOid(oid)).Concat(Ber.Tlv(tag, v)).ToArray());
        var list = Ber.Tlv(0x30, Vb("1.3.6.1.2.1.1.5.0", 0x04, "sw1"u8.ToArray())
            .Concat(Vb("1.3.6.1.2.1.31.1.1.1.6.3", 0x46, new byte[] { 0x01, 0, 0, 0, 0, 0 }))
            .Concat(Vb("1.3.6.1.2.1.31.1.1.1.18.3", 0x81, Array.Empty<byte>())).ToArray());
        var pdu = Ber.Tlv(0xA2, new byte[] { 2, 1, 7, 2, 1, 0, 2, 1, 0 }.Concat(list).ToArray());
        var msg = Ber.Tlv(0x30, new byte[] { 2, 1, 1 }.Concat(Ber.Tlv(0x04, "public"u8.ToArray())).Concat(pdu).ToArray());
        var r = Ber.ParseResponse(msg) ?? throw new Exception("not parsed");
        Assert.Equal(7, r.RequestId);
        Assert.Equal("sw1", r.Vars[0].Text);
        Assert.Equal(1L << 40, r.Vars[1].Long);
        Assert.True(r.Vars[2].Missing, "noSuchInstance");
        Assert.True(Ber.ParseResponse(new byte[] { 0x30, 0x05, 1, 2 }) == null, "truncated packet rejected");
        Assert.Equal(10L, SwitchDirectory.Delta(uint.MaxValue - 4, 5, is64: false));
    }

    public static async Task TestSwitchPortFoundAndWatchedOnTheDemoSwitch()
    {
        var dir = new SwitchDirectory(new DemoSwitch());
        var ips = DemoSite.Devices.Select(d => IPAddress.Parse(d.Ip));
        await dir.DiscoverAsync(ips, new[] { "public" }, _ => null, CancellationToken.None);
        Assert.Equal(1, dir.Switches.Count);
        Assert.Equal("UDM-Pro", dir.Switches[0].Name);

        string mac = DemoSite.Devices.First(d => d.Ip == DemoHealth.LossyDevice).Mac;
        var port = await dir.LocateAsync(mac, CancellationToken.None) ?? throw new Exception("port not found");
        Assert.True(port.IfIndex > 1000, "bridge port mapped to ifIndex " + port.IfIndex);
        Assert.Equal(1, port.MacsOnPort);
        Assert.Equal(100L, port.SpeedMbps);
        Assert.Equal("half", port.Duplex);
        Assert.True(port.PortName?.StartsWith("Port ") == true, port.PortName ?? "");

        var samples = new List<PortSample>();
        using var cts = new CancellationTokenSource();
        try { await dir.MonitorAsync(port, TimeSpan.FromMilliseconds(300), s => { samples.Add(s); if (samples.Count == 3) cts.Cancel(); }, cts.Token); }
        catch (OperationCanceledException) { }
        Assert.True(samples.Count >= 3, "samples");
        Assert.True(samples[^1].FromDeviceKbps is > 2000 and < 5000, "from device " + samples[^1].FromDeviceKbps);
        Assert.True(port.ErrorsDuringCheck > 0, "CRC errors counted");

        var snap = new HealthSnapshot { Ip = IPAddress.Parse(DemoHealth.LossyDevice), Port = port, Ping = new PingSample(5, 5, new long[] { 1, 1, 1, 1, 1 }) };
        var codes = HealthCheck.Analyse(snap).Findings.Select(f => f.Code).ToList();
        Assert.True(codes.Contains("half-duplex") && codes.Contains("crc-errors"), string.Join(",", codes));

        Assert.True(await new SwitchDirectory(new DemoSwitch()).LocateAsync("00:11:22:33:44:55", CancellationToken.None) == null, "unknown MAC");
        var wrong = new SwitchDirectory(new DemoSwitch());
        await wrong.DiscoverAsync(ips, new[] { "private" }, _ => null, CancellationToken.None);
        Assert.Equal(0, wrong.Switches.Count);
    }

    /// <summary>
    /// The SNMP client against a real agent (net-snmp's snmpd, which CI starts on 127.0.0.1:1161 with community
    /// "public"). Reads the loopback interface's 64-bit counters while sending traffic over it. Skipped elsewhere.
    /// </summary>
    public static async Task TestSnmpAgainstARealAgent()
    {
        string agent = Environment.GetEnvironmentVariable("SNMP_TEST_AGENT") ?? throw new SkipException("SNMP_TEST_AGENT not set (CI starts snmpd)");
        var client = new SnmpClient { Port = int.Parse(agent.Split(':')[1]) };
        var ip = IPAddress.Parse(agent.Split(':')[0]);
        var sys = await client.GetAsync(ip, "public", new[] { SwitchDirectory.SysDescr, SwitchDirectory.SysName }, null, CancellationToken.None)
                  ?? throw new Exception("snmpd did not answer");
        Assert.True(sys[0].Text?.Contains("Linux") == true, "sysDescr: " + sys[0].Text);
        Assert.True(await client.GetAsync(ip, "wrong", new[] { SwitchDirectory.SysDescr }, null, CancellationToken.None) == null, "wrong community ignored");

        var names = await client.WalkAsync(ip, "public", SwitchDirectory.IfDescr, null, CancellationToken.None);
        var lo = names.FirstOrDefault(v => v.Text == "lo") ?? throw new Exception("no lo in ifDescr: " + string.Join(",", names.Select(n => n.Text)));
        int ifIndex = int.Parse(lo.Oid[(lo.Oid.LastIndexOf('.') + 1)..]);

        var port = new SwitchPortReport { Switch = new SwitchInfo(ip, "public", null, "test", null), IfIndex = ifIndex };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var traffic = Task.Run(async () =>
        {
            using var u = new UdpClient();
            var payload = new byte[1200];
            while (!cts.IsCancellationRequested) { await u.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, 9)); await Task.Delay(1); }
        });
        var samples = new List<PortSample>();
        try { await new SwitchDirectory(client).MonitorAsync(port, TimeSpan.FromSeconds(3), s => { samples.Add(s); if (samples.Count == 2) cts.Cancel(); }, cts.Token); }
        catch (OperationCanceledException) { }
        try { await traffic; } catch { }
        Console.WriteLine($"     lo (ifIndex {ifIndex}): " + string.Join(" | ", samples.Select(s => $"in {s.FromDeviceKbps:0} kbit/s out {s.ToDeviceKbps:0} kbit/s")));
        Assert.True(samples.Count >= 2 && samples[^1].FromDeviceKbps > 100, "counters moved with the traffic");
    }
}
