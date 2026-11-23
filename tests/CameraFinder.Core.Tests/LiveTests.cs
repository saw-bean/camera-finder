using CameraFinder.Core.Net;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using CameraFinder.Core.Discovery;
using CameraFinder.Core.Health;
using CameraFinder.Core.Model;

static partial class Tests
{
    // ---------- Live bandwidth (RTSP) ----------

    public static void TestSdpAndKeyFrames()
    {
        const string sdp = "v=0\r\no=- 1 1 IN IP4 0.0.0.0\r\ns=Media\r\na=control:*\r\nm=audio 0 RTP/AVP 0\r\na=control:trackID=0\r\n" +
                           "m=video 0 RTP/AVP 96\r\na=rtpmap:96 H264/90000\r\na=control:trackID=1\r\nm=application 0 RTP/AVP 107\r\na=control:trackID=2\r\n";
        var p = Sdp.Parse(sdp);
        Assert.Equal("*", p.SessionControl);
        Assert.Equal("H264", p.Video?.Codec);
        Assert.Equal("trackID=1", p.Video?.Control);
        Assert.Equal("rtsp://cam/s/trackID=1", Sdp.Resolve("rtsp://cam/s/", "trackID=1"));
        Assert.Equal("rtsp://cam/s/trackID=1", Sdp.Resolve("rtsp://cam/s", "trackID=1"));
        Assert.Equal("rtsp://other/x", Sdp.Resolve("rtsp://cam/s", "rtsp://other/x"));
        Assert.Equal("rtsp://cam/s", Sdp.Resolve("rtsp://cam/s", "*"));
        Assert.Equal((Sdp.Media?)null, Sdp.Parse("v=0\r\nm=audio 0 RTP/AVP 0\r\n").Video);

        Assert.True(RtspLiveSource.IsKeyFrame(new byte[] { 0x65, 0x88 }, false), "H.264 IDR");
        Assert.True(!RtspLiveSource.IsKeyFrame(new byte[] { 0x41, 0x9a }, false), "H.264 P-slice");
        Assert.True(RtspLiveSource.IsKeyFrame(new byte[] { 0x7C, 0x85 }, false), "H.264 FU-A start of IDR");
        Assert.True(!RtspLiveSource.IsKeyFrame(new byte[] { 0x7C, 0x45 }, false), "FU-A continuation");
        Assert.True(RtspLiveSource.IsKeyFrame(new byte[] { 0x26, 0x01, 0xaf }, true), "H.265 IDR_W_RADL (19)");
        Assert.True(RtspLiveSource.IsKeyFrame(new byte[] { 0x62, 0x01, 0x93 }, true), "H.265 FU start of IDR");
        Assert.True(!RtspLiveSource.IsKeyFrame(new byte[] { 0x02, 0x01, 0xd0 }, true), "H.265 trailing picture");
        Assert.Equal(2, RtspLiveSource.InterleavedChannel("RTP/AVP/TCP;unicast;interleaved=2-3;ssrc=1"));
    }

    private static string Md5Hex(string s) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    /// <summary>A fake camera: RTSP with digest login, two-track SDP, then 25 fps H.264 over interleaved TCP.</summary>
    private static async Task FakeRtspCamera(TcpListener l, List<string> requests, CancellationToken ct)
    {
        using var c = await l.AcceptTcpClientAsync(ct);
        var s = c.GetStream();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        string baseUrl = $"rtsp://127.0.0.1:{port}/stream/";
        async Task Send(string text) => await s.WriteAsync(Encoding.ASCII.GetBytes(text), ct);
        while (!ct.IsCancellationRequested)
        {
            var sb = new StringBuilder();
            var one = new byte[1];
            while (!sb.ToString().EndsWith("\r\n\r\n"))
            {
                if (await s.ReadAsync(one, ct) == 0) return;
                sb.Append((char)one[0]);
            }
            string req = sb.ToString();
            lock (requests) requests.Add(req);
            string first = req.Split("\r\n")[0];
            string method = first.Split(' ')[0], uri = first.Split(' ')[1];
            string cseq = System.Text.RegularExpressions.Regex.Match(req, @"CSeq: (\d+)").Groups[1].Value;
            string expected = Md5Hex($"{Md5Hex("admin:cam:secret")}:abc:{Md5Hex($"{method}:{uri}")}");
            if (!req.Contains($"response=\"{expected}\""))
            {
                await Send($"RTSP/1.0 401 Unauthorized\r\nCSeq: {cseq}\r\nWWW-Authenticate: Digest realm=\"cam\", nonce=\"abc\"\r\n\r\n");
                continue;
            }
            switch (method)
            {
                case "DESCRIBE":
                    string sdp = "v=0\r\ns=x\r\na=control:*\r\nm=audio 0 RTP/AVP 0\r\na=control:trackID=0\r\nm=video 0 RTP/AVP 96\r\na=rtpmap:96 H264/90000\r\na=control:trackID=1\r\n";
                    await Send($"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nContent-Base: {baseUrl}\r\nContent-Type: application/sdp\r\nContent-Length: {sdp.Length}\r\n\r\n{sdp}");
                    break;
                case "SETUP":
                    await Send($"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nSession: 12345;timeout=60\r\nTransport: RTP/AVP/TCP;unicast;interleaved=2-3\r\n\r\n");
                    break;
                case "PLAY":
                    await Send($"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nSession: 12345\r\n\r\n");
                    ushort seq = 0;
                    for (int frame = 0; !ct.IsCancellationRequested; frame++)
                    {
                        for (int part = 0; part < 2; part++)
                        {
                            if (frame == 30 && part == 0) seq++; // one packet "lost" by the camera
                            var rtp = new byte[12 + 1000];
                            rtp[0] = 0x80;
                            rtp[1] = (byte)(96 | (part == 1 ? 0x80 : 0)); // marker on the frame's last packet
                            rtp[2] = (byte)(seq >> 8); rtp[3] = (byte)seq; seq++;
                            rtp[12] = (byte)(frame % 25 == 0 && part == 0 ? 0x65 : 0x41);
                            var head = new byte[] { (byte)'$', 2, (byte)(rtp.Length >> 8), (byte)rtp.Length };
                            await s.WriteAsync(head, ct);
                            await s.WriteAsync(rtp, ct);
                        }
                        if (frame == 10)
                        {
                            await s.WriteAsync(new byte[] { (byte)'$', 3, 0, 4, 1, 2, 3, 4 }, ct);            // RTCP on the other channel
                            await Send("RTSP/1.0 200 OK\r\nCSeq: 99\r\nContent-Length: 2\r\n\r\nok");     // a stray RTSP reply
                        }
                        await Task.Delay(40, ct);
                    }
                    return;
            }
        }
    }

    public static async Task TestLiveBandwidthFromAFakeRtspCamera()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var requests = new List<string>();
        var server = FakeRtspCamera(l, requests, cts.Token);

        var stats = new LiveStats();
        var samples = new List<LiveSample>();
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var client = new RtspLiveSource().RunAsync($"rtsp://127.0.0.1:{port}/stream", new Credentials("admin", "secret"), null, stats,
            s => { lock (samples) { samples.Add(s); if (samples.Count >= 3) run.Cancel(); } }, run.Token);
        try { await client; } catch (OperationCanceledException) { }
        cts.Cancel();
        l.Stop();
        try { await server; } catch { }

        Assert.Equal("H264", stats.Codec);
        Assert.True(samples.Count >= 3, "samples: " + samples.Count);
        var steady = samples[1];
        // 25 frames/s x 2 packets x (1012 bytes RTP + 4 interleave) x 8 bits = ~406 kbit/s (timer jitter allowed).
        Assert.True(steady.Kbps is > 250 and < 550, "kbps " + steady.Kbps);
        Assert.True(steady.Frames is >= 15 and <= 30, "fps " + steady.Frames);
        Assert.True(stats.KeyframeInterval is > 0.5 and < 1.6, "key frame interval " + stats.KeyframeInterval);
        Assert.True(stats.LossPercent > 0, "the skipped packet is counted as missing");
        lock (requests)
        {
            Assert.True(requests.Any(r => r.StartsWith($"SETUP rtsp://127.0.0.1:{port}/stream/trackID=1 ")), "SETUP the video track from Content-Base");
            Assert.True(requests.All(r => !r.Contains("secret")), "password never sent in clear");
        }
    }

    public static async Task TestLiveBandwidthWrongLoginIsReported()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = FakeRtspCamera(l, new List<string>(), cts.Token);
        try
        {
            await new RtspLiveSource().RunAsync($"rtsp://127.0.0.1:{port}/stream", new Credentials("admin", "wrong"), null, new LiveStats(), _ => { }, cts.Token);
            throw new Exception("should have failed");
        }
        catch (LiveStreamException ex) { Assert.True(ex.LoginRefused, ex.Message); }
        cts.Cancel();
        l.Stop();
        try { await server; } catch { }

        try
        {
            await new RtspLiveSource().RunAsync($"rtsp://127.0.0.1:{port}/stream", null, null, new LiveStats(), _ => { }, CancellationToken.None);
            throw new Exception("should have failed");
        }
        catch (LiveStreamException ex) { Assert.True(ex.Message.Contains("refused") || ex.Message.Contains("timed out"), ex.Message); }
    }

    public static void TestLiveStatsSummaries()
    {
        var st = new LiveStats();
        var t = DateTime.Now;
        st.Add(new LiveSample(t, 9000, 10, false, 0)); // partial first second: not the peak
        for (int i = 1; i <= 10; i++) st.Add(new LiveSample(t.AddSeconds(i), 4000 + (i == 5 ? 2000 : 0), 25, i % 2 == 0, 0));
        Assert.Equal(6000.0, st.PeakKbps);
        Assert.Equal(11, st.Seconds);
        Assert.Equal(25.0, st.Fps);
        var r = st.ToReport("rtsp://x", 25);
        Assert.True(r.Opened && r.Frames == 260 && r.NominalFps == 25, "report");
    }

    public static async Task TestDemoLiveBandwidthAndStreams()
    {
        var site = new DemoSite { ArpDelay = TimeSpan.Zero, JoinDelay = TimeSpan.Zero };
        var demo = new DemoHealth(site) { Tick = TimeSpan.FromMilliseconds(5) };
        var onvif = await demo.CollectAsync(IPAddress.Parse("192.168.1.64"), 80, new Credentials("admin", "x"), null, CancellationToken.None);
        Assert.Equal(2, onvif.StreamUris.Count);
        var stats = new LiveStats();
        using var cts = new CancellationTokenSource();
        int n = 0;
        try { await demo.RunAsync(onvif.StreamUris["Profile_1"], new Credentials("admin", "x"), null, stats, _ => { if (++n == 12) cts.Cancel(); }, cts.Token); }
        catch (OperationCanceledException) { }
        Assert.True(stats.AverageKbps is > 3000 and < 9000, "main stream kbps " + stats.AverageKbps);
        Assert.Equal(2.0, Math.Round(stats.KeyframeInterval ?? 0), "key frame every 2 s");
        try { await demo.RunAsync(onvif.StreamUris["Profile_1"], null, null, new LiveStats(), _ => { }, CancellationToken.None); throw new Exception("no login"); }
        catch (LiveStreamException ex) { Assert.True(ex.LoginRefused, "login needed"); }
    }
}

static partial class Tests
{
    public static async Task TestNonCamerasAreCalledDevicesAndGetNoOnvif()
    {
        var site = new DemoSite { ArpDelay = TimeSpan.Zero, JoinDelay = TimeSpan.Zero };
        var demo = new DemoHealth(site);
        var check = new HealthCheck(demo, demo, demo, demo);
        var router = new Device(IPAddress.Parse("192.168.1.1")) { Model = "UniFi Dream Machine Pro" };
        var r = await check.RunAsync(router, null,
            new HealthCheckOptions { CheckOnvif = false, PingCount = 3, PingInterval = TimeSpan.Zero, TraceRounds = 1 }, null, CancellationToken.None);
        Assert.True(r.Snapshot.Onvif == null, "no ONVIF for a router");
        Assert.True(!r.Diagnosis.Contains("camera", StringComparison.OrdinalIgnoreCase), r.Diagnosis);

        var dead = HealthCheck.Analyse(new HealthSnapshot { Ip = IPAddress.Parse("10.0.0.9"), IsCamera = false, Ping = new PingSample(5, 0, Array.Empty<long>()) });
        Assert.Equal("The device is not answering at all", dead.Findings[0].Title);
        Assert.True(!dead.Diagnosis.Contains("camera", StringComparison.OrdinalIgnoreCase), dead.Diagnosis);
    }

    /// <summary>
    /// The live bandwidth client against a real RTSP server (mediamtx, fed by ffmpeg) - an independent implementation,
    /// not our own fake. CI sets RTSP_TEST_URL (and RTSP_TEST_AUTH) after starting them; skipped elsewhere.
    /// Expected stream: H.264 1280x720, 25 fps, ~2 Mbit/s, key frame every 50 frames (2 s), login admin / secret.
    /// </summary>
    public static async Task TestLiveBandwidthAgainstARealRtspServer()
    {
        string url = Environment.GetEnvironmentVariable("RTSP_TEST_URL") ?? throw new SkipException("RTSP_TEST_URL not set (CI starts mediamtx)");
        string auth = Environment.GetEnvironmentVariable("RTSP_TEST_AUTH") ?? "?";
        var stats = new LiveStats();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int n = 0;
        try { await new RtspLiveSource().RunAsync(url, new Credentials("admin", "secret"), null, stats, _ => { if (++n == 8) cts.Cancel(); }, cts.Token); }
        catch (OperationCanceledException) { }
        Console.WriteLine($"     [{auth}] {stats.Codec} avg {stats.AverageKbps:0} kbit/s peak {stats.PeakKbps:0} fps {stats.Fps:0.#} key every {stats.KeyframeInterval:0.##} s loss {stats.LossPercent:0.##}%");
        Assert.True(n >= 8, $"[{auth}] got {n} seconds of video");
        Assert.Equal("H264", stats.Codec);
        Assert.True(stats.AverageKbps is > 800 and < 5000, $"[{auth}] bitrate {stats.AverageKbps}");
        Assert.True(stats.Fps is > 20 and < 30, $"[{auth}] fps {stats.Fps}");
        Assert.True(stats.KeyframeInterval is > 1.5 and < 2.6, $"[{auth}] key frame interval {stats.KeyframeInterval}");
        Assert.True(stats.LossPercent < 1, $"[{auth}] loss {stats.LossPercent}");
        var delay = stats.Delay.Report();
        Console.WriteLine($"     [{auth}] first picture {delay.TimeToPicture:0.##} s (connect {delay.ConnectSeconds:0.##} s), buffer needed {delay.BufferNeededMs:0} ms");
        Assert.True(delay.TimeToPicture is > 0 and < 3, $"[{auth}] time to picture {delay.TimeToPicture}");
        Assert.True(delay.BufferNeededMs is >= 0 and < 250, $"[{auth}] buffer needed {delay.BufferNeededMs}");

        try
        {
            await new RtspLiveSource().RunAsync(url, new Credentials("admin", "wrong"), null, new LiveStats(), _ => { }, CancellationToken.None);
            throw new Exception("wrong password accepted");
        }
        catch (LiveStreamException ex) { Assert.True(ex.LoginRefused, $"[{auth}] wrong password: {ex.Message}"); }

        // The login can also come inside the address, as a technician might paste it.
        var inline = new LiveStats();
        using var c2 = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int m = 0;
        var withLogin = new UriBuilder(url) { UserName = "admin", Password = "secret" }.Uri.AbsoluteUri;
        try { await new RtspLiveSource().RunAsync(withLogin, null, null, inline, _ => { if (++m == 2) c2.Cancel(); }, c2.Token); }
        catch (OperationCanceledException) { }
        Assert.True(m >= 2, $"[{auth}] login inside the address");

        // ffprobe (the same tool the app bundles) against the same stream.
        if (ExternalTool.Find(ExternalTool.FfprobeExe) is { } ffprobe)
        {
            var rep = await new FfprobeStreamProber(ffprobe).ProbeAsync(url, new Credentials("admin", "secret"), TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.True(rep.Opened, $"[{auth}] ffprobe: {rep.Error}");
            Assert.Equal(1280, rep.Width);
            Assert.True(rep.MeasuredFps is > 20 and < 30, $"[{auth}] ffprobe fps {rep.MeasuredFps}");

            // The same through the relay used for cameras reached from a temporary address (login digest and all).
            var relayed = await new FfprobeStreamProber(ffprobe).ProbeAsync(url, new Credentials("admin", "secret"), TimeSpan.FromSeconds(5), CancellationToken.None,
                source: IPAddress.Loopback);
            Assert.True(relayed.Opened, $"[{auth}] ffprobe through the relay: {relayed.Error}");
            Assert.Equal(1280, relayed.Width);
            Assert.True(relayed.Url.Contains("8554") && !relayed.Url.Contains("secret"), "the report shows the camera's address, without the password: " + relayed.Url);
        }
    }

    public static async Task TestTcpRelayCopiesBothWaysFromTheGivenAddress()
    {
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var target = (IPEndPoint)server.LocalEndpoint;
        IPEndPoint? seenFrom = null;
        var echo = Task.Run(async () =>
        {
            using var c = await server.AcceptTcpClientAsync();
            seenFrom = (IPEndPoint?)c.Client.RemoteEndPoint;
            var st = c.GetStream();
            var buf = new byte[64];
            int n = await st.ReadAsync(buf);
            await st.WriteAsync(buf.AsMemory(0, n).ToArray().Reverse().ToArray());
        });
        await using (var relay = new TcpRelay(target, IPAddress.Loopback))
        {
            Assert.Equal($"rtsp://127.0.0.1:{relay.LocalPort}/Streaming/Channels/101?x=1", relay.Rewrite("rtsp://10.0.5.21:554/Streaming/Channels/101?x=1"));
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, relay.LocalPort);
            var st = client.GetStream();
            await st.WriteAsync(Encoding.ASCII.GetBytes("abc"));
            var buf = new byte[3];
            int got = 0;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (got < 3) got += await st.ReadAsync(buf.AsMemory(got), cts.Token);
            Assert.Equal("cba", Encoding.ASCII.GetString(buf));
            Assert.Equal(1, relay.Connections);
        }
        await echo;
        server.Stop();
        Assert.True(seenFrom?.Address.Equals(IPAddress.Loopback) == true, "connected from the given address");
    }
}
