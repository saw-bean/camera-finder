using System.Net;
using System.Net.Sockets;
using System.Text;
using CameraFinder.Core.Discovery;
using CameraFinder.Core.Health;
using CameraFinder.Core.Model;

static partial class Tests
{
    // ---------- Health: normalizing camera log lines ----------

    public static void TestLogLinesFromDifferentBrandsMeanTheSame()
    {
        (string Line, EventKind Kind)[] cases =
        {
            ("2026-03-01 10:00:00 Illegal login, user: admin, IP: 192.168.1.10", EventKind.LoginFailed),  // Hikvision
            ("2026/03/01 10:00:00 Login failed user=admin from 10.0.0.9", EventKind.LoginFailed),      // generic
            ("Mar  1 10:00:00 axis-cam httpd: authentication failure for root", EventKind.LoginFailed), // Axis syslog
            ("2026-03-01 10:00:00 User admin locked, IP: 192.168.1.10", EventKind.AccountLocked),
            ("2026-03-01 10:00:00 System Reboot", EventKind.Restart),                                  // Dahua
            ("2026-03-01 10:00:00 Power on", EventKind.Restart),
            ("01-03-2026 10:00:00 System Boot", EventKind.Restart),
            ("2026-03-01 10:00:00 watchdog reboot", EventKind.Crash),
            ("2026-03-01 10:00:00 Network Disconnected", EventKind.LinkDown),
            ("2026-03-01 10:00:00 eth0 link up", EventKind.LinkUp),
            ("2026-03-01 10:00:00 IP address conflict 192.168.1.64", EventKind.IpConflict),
            ("2026-03-01 10:00:00 SD card error: write fail", EventKind.StorageError),
            ("2026-03-01 10:00:00 NTP synchronization failed", EventKind.TimeSyncFailed),
            ("2026-03-01 10:00:00 Video loss", EventKind.VideoLoss),
            ("2026-03-01 10:00:00 Firmware upgrade success", EventKind.Firmware),
            ("2026-03-01 10:00:00 PoE power insufficient", EventKind.PowerProblem),
            ("2026-03-01 10:00:00 User Login admin 192.168.1.10", EventKind.LoginOk),
            ("2026-03-01 10:00:00 Motion Detect Start", EventKind.Motion),
            ("2026-03-01 10:00:00 something nobody has seen", EventKind.Other),
        };
        foreach (var (line, kind) in cases)
        {
            var e = LogNormalizer.NormalizeLine(line, 2026) ?? throw new Exception("no event for " + line);
            Assert.Equal(kind, e.Kind, line);
            Assert.Equal(new DateTime(2026, 3, 1, 10, 0, 0), e.Time, "time of " + line);
        }
        var failed = LogNormalizer.NormalizeLine("2026-03-01 10:00:00 Illegal login, user: admin, IP: 192.168.1.10", 2026)!;
        Assert.Equal("192.168.1.10", failed.From);
        Assert.True(failed.Text.Contains("wrong user name or password") && failed.Text.Contains("192.168.1.10"), failed.Text);
        Assert.Equal("Camera log: something nobody has seen", LogNormalizer.NormalizeLine("something nobody has seen", 2026)!.Text);
    }

    public static void TestLogTimestampsWithoutYearUseTheCamerasYear()
    {
        var (t, rest) = LogNormalizer.ExtractTime("Nov 19 23:59:01 cam kernel: eth0 down", 2026);
        Assert.Equal(new DateTime(2026, 11, 19, 23, 59, 1), t);
        Assert.True(!rest.Contains("23:59"), rest);
        Assert.Equal((DateTime?)null, LogNormalizer.ExtractTime("no time here", 2026).Time);
        Assert.Equal((DateTime?)null, LogNormalizer.ExtractTime("2026-13-45 99:99:99 bad", 2026).Time, "impossible date ignored");
    }

    // ---------- Health: failure patterns ----------

    private static HealthSnapshot Snap(string ip = "10.0.5.21", PingSample? ping = null, string? log = null, OnvifReport? onvif = null)
    {
        var s = new HealthSnapshot { Ip = IPAddress.Parse(ip), Description = "Test cam", OpenPorts = new[] { 80, 554 }, CheckedAt = new DateTime(2026, 3, 2, 12, 0, 0) };
        s.Ping = ping ?? new PingSample(20, 20, Enumerable.Repeat(1L, 20).ToList());
        s.Onvif = onvif ?? new OnvifReport { Answered = true, LoginAccepted = true, Model = "M1", Manufacturer = "Acme", SystemLog = log,
            CameraUtc = new DateTime(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc), LaptopUtc = new DateTime(2026, 3, 2, 12, 0, 1, DateTimeKind.Utc) };
        if (onvif == null && log != null) s.Onvif.SystemLog = log;
        return s;
    }

    private static string[] Codes(HealthSnapshot s) => HealthCheck.Analyse(s).Findings.Select(f => f.Code).ToArray();

    public static void TestHealthyCameraHasNoFindingsAndSaysSo()
    {
        var r = HealthCheck.Analyse(Snap(log: "2026-03-01 08:00:00 System start\n2026-03-02 09:00:00 Login success admin"));
        Assert.Equal(0, r.Findings.Count, "findings: " + string.Join(",", r.Findings.Select(f => f.Code)));
        Assert.True(r.Diagnosis.Contains("In short: no problems found"), r.Diagnosis);
        Assert.True(r.Diagnosis.Contains("What looked fine"), r.Diagnosis);
    }

    public static void TestRestartLoopPointsAtPower()
    {
        string log = string.Join("\n", new[] { "2026-03-01 13:00:00", "2026-03-01 20:00:00", "2026-03-02 02:00:00", "2026-03-02 09:00:00" }
            .Select(t => t + " System Reboot").Append("2026-03-02 01:59:00 Network Disconnected").Append("2026-03-02 08:59:00 Network Disconnected"));
        var r = HealthCheck.Analyse(Snap(log: log));
        var main = r.Findings[0];
        Assert.Equal("restart-loop", main.Code);
        Assert.True(main.Title.Contains("losing power"), main.Title);
        Assert.True(r.Findings.Any(f => f.Code == "link-flap"), "link flaps found too");
        Assert.True(r.Diagnosis.StartsWith("Test cam at 10.0.5.21"), r.Diagnosis);
        Assert.True(r.Diagnosis.Contains("In short: the camera keeps restarting"), r.Diagnosis);
        Assert.True(r.Diagnosis.Contains("PoE budget"), r.Diagnosis);
    }

    public static void TestWatchdogRestartsAreACrashNotPower()
    {
        string log = "2026-03-02 01:00:00 watchdog reboot\n2026-03-02 05:00:00 watchdog reboot\n2026-03-02 09:00:00 System Reboot";
        var f = HealthCheck.Analyse(Snap(log: log)).Findings.First(x => x.Code == "restart-loop");
        Assert.True(f.Title.Contains("crashing"), f.Title);
    }

    public static void TestOneRestartAfterFirmwareIsNotALoop()
    {
        var codes = Codes(Snap(log: "2026-03-02 01:00:00 Firmware upgrade success\n2026-03-02 01:01:00 System Reboot"));
        Assert.True(!codes.Contains("restart-loop"), string.Join(",", codes));
    }

    public static void TestNvrWithOldPasswordIsNamed()
    {
        string log = string.Join("\n", Enumerable.Range(0, 6).Select(i => $"2026-03-02 0{i}:00:00 Illegal login, user: admin, IP: 192.168.1.10"))
            + "\n2026-03-02 07:00:00 User admin locked, IP: 192.168.1.10";
        var r = HealthCheck.Analyse(Snap(log: log));
        var failed = r.Findings.First(f => f.Code == "failed-logins");
        Assert.True(failed.Evidence.Contains("192.168.1.10 (6x)"), failed.Evidence);
        Assert.True(failed.Advice[0].Contains("old password"), failed.Advice[0]);
        Assert.Equal(Severity.Warning, failed.Severity);
        Assert.True(r.Findings.Any(f => f.Code == "locked"), "account lock found");
    }

    public static void TestLoginsFromTheInternetAreAProblem()
    {
        string log = string.Join("\n", Enumerable.Range(0, 5).Select(i => $"2026-03-02 0{i}:00:00 Login failed from 45.33.12.{i + 1}"));
        Assert.Equal(Severity.Problem, HealthCheck.Analyse(Snap(log: log)).Findings.First(f => f.Code == "failed-logins").Severity);
    }

    public static void TestClockOffIsReportedWithNtpAdvice()
    {
        var o = new OnvifReport { Answered = true, LoginAccepted = true, TimeSource = "NTP",
            CameraUtc = new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc), LaptopUtc = new DateTime(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc) };
        var f = HealthCheck.Analyse(Snap(onvif: o)).Findings.First(x => x.Code == "clock");
        Assert.Equal(Severity.Problem, f.Severity);
        Assert.True(f.Title.Contains("3 hours slow"), f.Title);
        Assert.True(f.Advice[0].Contains("NTP is on but not working"), f.Advice[0]);
    }

    public static void TestPacketLossOnLastLinkVersusEarlierHop()
    {
        var lossy = new PingSample(20, 16, Enumerable.Repeat(2L, 16).ToList());
        var s = Snap(ping: lossy);
        s.Path = new TraceReport(s.Ip, new[] { new Hop(1, s.Ip, 5, 4, 1, 1, 1, 2, 0) }, true, 5);
        var f = HealthCheck.Analyse(s).Findings.First(x => x.Code == "packet-loss");
        Assert.Equal(Severity.Problem, f.Severity); // 20%
        Assert.True(f.Evidence.Contains("last link"), f.Evidence);

        s.Path = new TraceReport(s.Ip, new[]
        {
            new Hop(1, IPAddress.Parse("192.168.1.1"), 5, 5, 1, 1, 1, 1, 0),
            new Hop(2, IPAddress.Parse("10.0.0.1"), 5, 3, 3, 3, 3, 3, 0),
            new Hop(3, s.Ip, 5, 3, 3, 3, 3, 3, 0),
        }, true, 5);
        f = HealthCheck.Analyse(s).Findings.First(x => x.Code == "packet-loss");
        Assert.True(f.Evidence.Contains("hop 2 (10.0.0.1)"), f.Evidence);
    }

    public static void TestCameraThatAnswersNothingIsOffline()
    {
        var s = Snap(ping: new PingSample(10, 0, Array.Empty<long>()), onvif: new OnvifReport());
        s.Stream = new StreamReport { Url = "rtsp://x", Error = "Connection timed out" };
        var r = HealthCheck.Analyse(s);
        Assert.Equal("offline", r.Findings[0].Code);
        Assert.True(!r.Findings.Any(f => f.Code == "stream-failed"), "no separate stream finding when it is simply offline");
        Assert.True(r.Diagnosis.Contains("not answering at all"), r.Diagnosis);
    }

    public static void TestStreamProblemsAreRecognised()
    {
        var s = Snap();
        s.Stream = new StreamReport { Url = "rtsp://10.0.5.21/", Error = "method DESCRIBE failed: 401 Unauthorized" };
        Assert.True(HealthCheck.Analyse(s).Findings.First(f => f.Code == "stream-failed").Title.Contains("refused the login"), "401");

        s.Stream = new StreamReport { Url = "rtsp://10.0.5.21/", Opened = true, NominalFps = 25, MeasuredFps = 15, SecondsWatched = 8, Frames = 120, KeyframeInterval = 8, LongestGap = 2 };
        var codes = Codes(s);
        Assert.True(codes.Contains("frame-drops") && codes.Contains("stream-stall") && codes.Contains("long-gop"), string.Join(",", codes));
    }

    public static void TestOnvifLoginRefusedMentionsClockWhenOff()
    {
        var o = new OnvifReport { Answered = true, LoginAccepted = false, LoginError = "user name or password not accepted",
            CameraUtc = new DateTime(2026, 3, 2, 11, 0, 0, DateTimeKind.Utc), LaptopUtc = new DateTime(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc) };
        var f = HealthCheck.Analyse(Snap(onvif: o)).Findings.First(x => x.Code == "onvif-login");
        Assert.True(f.Advice[0].Contains("clock"), f.Advice[0]);
    }

    public static void TestMaxInWindow()
    {
        var t0 = new DateTime(2026, 1, 1);
        var times = new[] { 0, 1, 2, 30, 31 }.Select(h => t0.AddHours(h)).ToList();
        Assert.Equal(3, PatternDetector.MaxInWindow(times, TimeSpan.FromHours(24)));
        Assert.Equal(2, PatternDetector.MaxInWindow(times, TimeSpan.FromHours(1)));
        Assert.Equal(0, PatternDetector.MaxInWindow(new List<DateTime>(), TimeSpan.FromHours(1)));
    }

    // ---------- Health: ONVIF ----------

    public static void TestOnvifPasswordDigest()
    {
        var nonce = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        Assert.Equal("52mF2WrT+5MgommnOHdkwzty5nE=", OnvifClient.PasswordDigest(nonce, "2026-01-02T03:04:05Z", "pass123"));
        string env = OnvifClient.Envelope("<x/>", new Credentials("ad<min", "pw"), new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        Assert.True(env.Contains("<wsse:Username>ad&lt;min</wsse:Username>") && env.Contains("<wsu:Created>2026-01-02T03:04:05Z</wsu:Created>"), env);
        Assert.True(!env.Contains(">pw<"), "password never sent in clear");
        Assert.True(!OnvifClient.Envelope("<x/>", null, DateTime.UtcNow).Contains("Security"), "no header without login");
    }

    private const string TimeReply = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope" xmlns:tt="http://www.onvif.org/ver10/schema" xmlns:tds="http://www.onvif.org/ver10/device/wsdl"><s:Body>
        <tds:GetSystemDateAndTimeResponse><tds:SystemDateAndTime><tt:DateTimeType>NTP</tt:DateTimeType><tt:DaylightSavings>false</tt:DaylightSavings>
        <tt:TimeZone><tt:TZ>EST5EDT</tt:TZ></tt:TimeZone>
        <tt:UTCDateTime><tt:Time><tt:Hour>9</tt:Hour><tt:Minute>30</tt:Minute><tt:Second>0</tt:Second></tt:Time><tt:Date><tt:Year>2026</tt:Year><tt:Month>3</tt:Month><tt:Day>2</tt:Day></tt:Date></tt:UTCDateTime>
        </tds:SystemDateAndTime></tds:GetSystemDateAndTimeResponse></s:Body></s:Envelope>
        """;

    private const string ProfilesReply = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope" xmlns:tt="http://www.onvif.org/ver10/schema" xmlns:trt="http://www.onvif.org/ver10/media/wsdl"><s:Body>
        <trt:GetProfilesResponse>
          <trt:Profiles token="Profile_1" fixed="true"><tt:Name>mainStream</tt:Name>
            <tt:VideoEncoderConfiguration token="V1"><tt:Name>V1</tt:Name><tt:Encoding>H264</tt:Encoding>
              <tt:Resolution><tt:Width>2560</tt:Width><tt:Height>1440</tt:Height></tt:Resolution>
              <tt:RateControl><tt:FrameRateLimit>25</tt:FrameRateLimit><tt:EncodingInterval>1</tt:EncodingInterval><tt:BitrateLimit>4096</tt:BitrateLimit></tt:RateControl>
            </tt:VideoEncoderConfiguration></trt:Profiles>
          <trt:Profiles token="Profile_2"><tt:Name>subStream</tt:Name></trt:Profiles>
        </trt:GetProfilesResponse></s:Body></s:Envelope>
        """;

    public static void TestOnvifRepliesAreRead()
    {
        var r = new OnvifReport();
        OnvifClient.ReadTime(OnvifClient.Load(TimeReply)!, r);
        Assert.Equal(new DateTime(2026, 3, 2, 9, 30, 0, DateTimeKind.Utc), r.CameraUtc);
        Assert.Equal("NTP", r.TimeSource);
        Assert.Equal("EST5EDT", r.TimeZone);

        var p = OnvifClient.ReadProfiles(OnvifClient.Load(ProfilesReply)!).ToList();
        Assert.Equal(2, p.Count);
        Assert.Equal(new OnvifProfile("Profile_1", "mainStream", "H264", 2560, 1440, 25, 4096), p[0]);
        Assert.Equal("H264, 2560x1440, 25 fps, up to 4.1 Mbit/s", LogNormalizer.ProfileText(p[0]));

        var fault = OnvifClient.Load("""<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope" xmlns:ter="http://www.onvif.org/ver10/error"><s:Body><s:Fault><s:Code><s:Value>s:Sender</s:Value><s:Subcode><s:Value>ter:NotAuthorized</s:Value></s:Subcode></s:Code></s:Fault></s:Body></s:Envelope>""")!;
        Assert.True(OnvifClient.IsAuthFault(fault), "NotAuthorized fault");

        var caps = OnvifClient.Load("""<Envelope><Body><GetCapabilitiesResponse><Capabilities><Media><XAddr>http://10.99.0.5/onvif/Media</XAddr></Media></Capabilities></GetCapabilitiesResponse></Body></Envelope>""")!;
        Assert.Equal("http://10.0.5.21/onvif/Media", OnvifClient.MediaAddress(caps, IPAddress.Parse("10.0.5.21"), 80));

        Assert.Equal((System.Xml.Linq.XDocument?)null, OnvifClient.Load("<!DOCTYPE x [<!ENTITY a 'b'>]><x>&a;</x>"));
    }

    /// <summary>The whole ONVIF conversation against a fake camera on localhost: clock first, then logins with a digest.</summary>
    public static async Task TestOnvifClientTalksToAFakeCamera()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var seen = new List<string>();
        var server = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    using var c = await listener.AcceptTcpClientAsync(cts.Token);
                    var s = c.GetStream();
                    string req = await ReadHttpRequest(s, cts.Token);
                    lock (seen) seen.Add(req);
                    bool authed = req.Contains("PasswordDigest") && req.Contains("<wsse:Username>admin</wsse:Username>");
                    string body = req switch
                    {
                        _ when req.Contains("GetSystemDateAndTime") => TimeReply,
                        _ when !authed => "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body><s:Fault><s:Code><s:Value>s:Sender</s:Value><s:Subcode><s:Value>ter:NotAuthorized</s:Value></s:Subcode></s:Code></s:Fault></s:Body></s:Envelope>",
                        _ when req.Contains("GetDeviceInformation") => "<Envelope><Body><GetDeviceInformationResponse><Manufacturer>Acme</Manufacturer><Model>CAM-1</Model><FirmwareVersion>1.2.3</FirmwareVersion><SerialNumber>S1</SerialNumber></GetDeviceInformationResponse></Body></Envelope>",
                        _ when req.Contains("GetSystemLog") => "<Envelope><Body><GetSystemLogResponse><SystemLog><String>2026-03-02 08:00:00 System Reboot&#10;2026-03-02 08:05:00 Illegal login, IP: 10.1.1.1</String></SystemLog></GetSystemLogResponse></Body></Envelope>",
                        _ when req.Contains("GetCapabilities") => $"<Envelope><Body><GetCapabilitiesResponse><Capabilities><Media><XAddr>http://1.2.3.4/onvif/media</XAddr></Media></Capabilities></GetCapabilitiesResponse></Body></Envelope>",
                        _ when req.Contains("GetProfiles") => ProfilesReply,
                        _ when req.Contains("GetStreamUri") => "<Envelope><Body><GetStreamUriResponse><MediaUri><Uri>rtsp://1.2.3.4:554/main</Uri></MediaUri></GetStreamUriResponse></Body></Envelope>",
                        _ => null!,
                    };
                    string status = body == null ? "400 Bad Request" : body.Contains("Fault") ? "400 Bad Request" : "200 OK";
                    body ??= "";
                    string resp = $"HTTP/1.1 {status}\r\nContent-Type: application/soap+xml\r\nConnection: close\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
                    await s.WriteAsync(Encoding.UTF8.GetBytes(resp), cts.Token);
                }
            }
            catch (Exception) { }
        });

        var report = await new OnvifClient().CollectAsync(IPAddress.Loopback, port, new Credentials("admin", "secret"), null, cts.Token);
        Assert.True(report.Answered, "answered");
        Assert.Equal(true, report.LoginAccepted);
        Assert.Equal("CAM-1", report.Model);
        Assert.Equal("1.2.3", report.Firmware);
        Assert.True(report.SystemLog?.Contains("Illegal login") == true, "system log: " + report.SystemLog);
        Assert.Equal(2, report.Profiles.Count);
        Assert.Equal("rtsp://1.2.3.4:554/main", report.StreamUri);
        Assert.Equal(2, report.StreamUris.Count); // main and sub profile
        lock (seen)
        {
            Assert.True(seen.Count >= 6, "requests: " + seen.Count);
            Assert.True(seen.All(r => !r.Contains("secret")), "password never sent in clear");
            // The login was time-stamped with the camera's clock (2026-03-02 09:30 UTC), not the laptop's.
            Assert.True(seen[1].Contains("<wsu:Created>2026-03-02T09:3"), "Created uses camera time");
        }

        var refused = await new OnvifClient().CollectAsync(IPAddress.Loopback, port, new Credentials("other", "x"), null, cts.Token);
        Assert.Equal(false, refused.LoginAccepted);
        cts.Cancel();
        listener.Stop();
        try { await server; } catch { }

        var nobody = await new OnvifClient().CollectAsync(IPAddress.Loopback, port, null, null, CancellationToken.None);
        Assert.True(!nobody.Answered, "nothing listening");

        // A plain web server (404 page) on the camera's web port is not ONVIF.
        var web = new TcpListener(IPAddress.Loopback, 0);
        web.Start();
        int webPort = ((IPEndPoint)web.LocalEndpoint).Port;
        var webTask = Task.Run(async () =>
        {
            using var c = await web.AcceptTcpClientAsync();
            var st = c.GetStream();
            await ReadHttpRequest(st, CancellationToken.None);
            const string page = "<html><body>Not found</body></html>";
            await st.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 404 Not Found\r\nContent-Type: text/html\r\nConnection: close\r\nContent-Length: {page.Length}\r\n\r\n{page}"));
        });
        var notOnvif = await new OnvifClient().CollectAsync(IPAddress.Loopback, webPort, null, null, CancellationToken.None);
        web.Stop();
        try { await webTask; } catch { }
        Assert.True(!notOnvif.Answered, "a web page is not an ONVIF answer");
    }

    private static async Task<string> ReadHttpRequest(NetworkStream s, CancellationToken ct)
    {
        var buf = new byte[65536];
        int total = 0;
        while (true)
        {
            int n = await s.ReadAsync(buf.AsMemory(total), ct);
            if (n == 0) break;
            total += n;
            string text = Encoding.UTF8.GetString(buf, 0, total);
            int headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0) continue;
            var m = System.Text.RegularExpressions.Regex.Match(text, @"Content-Length:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            int len = m.Success ? int.Parse(m.Groups[1].Value) : 0;
            if (Encoding.UTF8.GetByteCount(text[(headerEnd + 4)..]) >= len) return text;
        }
        return Encoding.UTF8.GetString(buf, 0, total);
    }

    // ---------- Health: ffprobe ----------

    public static void TestFfprobeOutputIsMeasured()
    {
        var packets = new List<string>();
        // 25 fps for 8 s, key frame every 2 s, 20 kB per frame, one 1.2 s freeze at 4 s.
        double t = 0;
        for (int i = 0; i < 200; i++)
        {
            if (i == 100) t += 1.2;
            packets.Add($"{{\"pts_time\":\"{t.ToString(System.Globalization.CultureInfo.InvariantCulture)}\",\"size\":\"20000\",\"flags\":\"{(i % 50 == 0 ? "K__" : "___")}\"}}");
            t += 0.04;
        }
        string json = "{\"packets\":[" + string.Join(",", packets) + "],\"streams\":[{\"codec_name\":\"h264\",\"width\":1920,\"height\":1080,\"avg_frame_rate\":\"25/1\",\"r_frame_rate\":\"25/1\"}]}";
        var r = new StreamReport { Url = "rtsp://x" };
        FfprobeStreamProber.Parse(json, r);
        Assert.True(r.Opened, "opened");
        Assert.Equal("h264", r.Codec);
        Assert.Equal(1920, r.Width);
        Assert.Equal(25.0, r.NominalFps);
        Assert.Equal(200, r.Frames);
        Assert.True(Math.Abs(r.LongestGap!.Value - 1.24) < 0.01, "gap " + r.LongestGap);
        Assert.True(Math.Abs(r.KeyframeInterval!.Value - (150 * 0.04 + 1.2) / 3) < 0.01, "gop " + r.KeyframeInterval);
        Assert.True(r.MeasuredFps is > 20 and < 25, "fps " + r.MeasuredFps);
        Assert.True(r.BitrateKbps is > 3300 and < 3700, "bitrate " + r.BitrateKbps);

        Assert.Equal(29.97, Math.Round(FfprobeStreamProber.Rate("30000/1001")!.Value, 2));
        Assert.Equal((double?)null, FfprobeStreamProber.Rate("0/0"));
    }

    public static void TestRtspLoginIsAddedAndHidden()
    {
        string url = FfprobeStreamProber.WithLogin("rtsp://10.0.5.21:554/Streaming/Channels/101", new Credentials("admin", "p@ss:w/rd"));
        Assert.Equal("rtsp://admin:p%40ss%3Aw%2Frd@10.0.5.21:554/Streaming/Channels/101", url);
        Assert.Equal("rtsp://10.0.5.21:554/Streaming/Channels/101", FfprobeStreamProber.Redact(url));
        var err = FfprobeStreamProber.FirstError("[rtsp @ 000001] method DESCRIBE failed: 401 Unauthorized (pw=hunter2)\nmore", new Credentials("a", "hunter2"));
        Assert.Equal("method DESCRIBE failed: 401 Unauthorized (pw=****)", err);
        var args = FfprobeStreamProber.Arguments("rtsp://x/", TimeSpan.FromSeconds(8));
        Assert.True(args.Contains("%+8") && args[^1] == "rtsp://x/" && args.Contains("tcp"), string.Join(" ", args));
        Assert.Equal("rtsp://10.0.5.21:554/Streaming/Channels/101", RtspPaths.Guess("10.0.5.21", "Hangzhou Hikvision Digital Technology", null));
        Assert.Equal("rtsp://10.0.5.21/main", HealthCheck.Repoint("rtsp://192.168.0.2/main", IPAddress.Parse("10.0.5.21")));
    }

    // ---------- Health: iperf3 ----------

    private const string IperfTcp = """
        {"start":{"connected":[{"socket":5,"local_host":"10.0.5.251","local_port":5201,"remote_host":"10.0.5.77","remote_port":51000}],
          "test_start":{"protocol":"TCP","num_streams":1,"duration":10,"reverse":0}},
         "end":{"sum_sent":{"seconds":10.0,"bytes":117000000,"bits_per_second":93600000,"retransmits":12},
                "sum_received":{"seconds":10.0,"bytes":116000000,"bits_per_second":92800000}}}
        """;

    private const string IperfUdp = """
        {"start":{"connected":[{"remote_host":"10.0.5.77"}],"test_start":{"protocol":"UDP","duration":10,"reverse":0}},
         "end":{"sum":{"seconds":10,"bits_per_second":49000000,"jitter_ms":0.42,"lost_packets":120,"packets":42000,"lost_percent":3.1}}}
        """;

    public static void TestIperfResultsAreExplained()
    {
        var tcp = Iperf3Server.Parse(IperfTcp, DateTime.Now)!;
        Assert.Equal("10.0.5.77", tcp.Client);
        Assert.Equal("TCP", tcp.Protocol);
        Assert.True(Math.Abs(tcp.Mbps - 92.8) < 0.01, "mbps " + tcp.Mbps);
        var lines = Iperf3Server.Explain(tcp);
        Assert.True(lines.Any(l => l.Contains("100 Mbit/s link")), string.Join(" | ", lines));
        Assert.True(lines.Any(l => l.Contains("16 cameras at 4 Mbit/s")), string.Join(" | ", lines));

        var udp = Iperf3Server.Parse(IperfUdp, DateTime.Now)!;
        Assert.Equal(3.1, udp.LostPercent);
        Assert.True(Iperf3Server.Explain(udp).Any(l => l.Contains("video over this link will break up")), "udp loss");

        var err = Iperf3Server.Parse("""{"start":{},"error":"the client has terminated"}""", DateTime.Now)!;
        Assert.True(Iperf3Server.Explain(err)[0].Contains("terminated"), "error passed on");
        Assert.Equal((IperfResult?)null, Iperf3Server.Parse("not json", DateTime.Now));
        Assert.Equal("-s -1 -J -p 5201", string.Join(" ", Iperf3Server.Arguments(5201)));
    }

    // ---------- Health: mtr-style trace ----------

    private sealed class FakeHops : IHopProber
    {
        public int Calls;
        public Task<HopReply> ProbeAsync(IPAddress target, IPAddress? source, int ttl, TimeSpan timeout, CancellationToken ct)
        {
            int n = Interlocked.Increment(ref Calls);
            return Task.FromResult(ttl switch
            {
                1 => new HopReply(IPAddress.Parse("192.168.1.1"), false, 1, false),
                2 => new HopReply(null, false, 0, true), // router that never answers
                _ => n % 4 == 0 ? new HopReply(null, false, 0, true) : new HopReply(target, true, 5, false),
            });
        }
    }

    public static async Task TestPathTracerFindsHopsAndLoss()
    {
        var target = IPAddress.Parse("10.0.5.21");
        var rounds = new List<TraceReport>();
        var t = await new PathTracer(new FakeHops()) { Interval = TimeSpan.Zero }.RunAsync(target, null, 8, rounds.Add, CancellationToken.None);
        Assert.Equal(8, rounds.Count);
        Assert.True(t.ReachedTarget, "reached");
        Assert.Equal(3, t.Hops.Count);
        Assert.Equal("192.168.1.1", t.Hops[0].Address?.ToString());
        Assert.Equal((IPAddress?)null, t.Hops[1].Address);
        Assert.True(t.Hops[2].Received < t.Hops[2].Sent, "some loss at the target");
        Assert.True(PathTracer.Summary(t).Contains("2 routers"), PathTracer.Summary(t));
    }

    // ---------- Health: the whole check on the demo site ----------

    public static async Task TestDemoHealthCheckFindsEachDemoFault()
    {
        var site = new DemoSite { ArpDelay = TimeSpan.Zero, JoinDelay = TimeSpan.Zero };
        var demo = new DemoHealth(site);
        var check = new HealthCheck(demo, demo, demo, demo);
        var opts = new HealthCheckOptions { Login = new Credentials("admin", "x"), PingCount = 10, PingInterval = TimeSpan.Zero, TraceRounds = 2, StreamWatch = TimeSpan.FromSeconds(1) };

        async Task<HealthResult> Check(string ip) =>
            await check.RunAsync(new Device(IPAddress.Parse(ip)) { Model = DemoSite.Devices.First(d => d.Ip == ip).Model }, null, opts, null, CancellationToken.None);

        var dahua = await Check("192.168.1.108");
        Assert.Equal("restart-loop", dahua.Findings[0].Code);

        var hik = await Check("192.168.1.64");
        var codes = hik.Findings.Select(f => f.Code).ToList();
        Assert.True(codes.Contains("failed-logins") && codes.Contains("locked") && codes.Contains("clock"), string.Join(",", codes));

        var noLogin = await check.RunAsync(new Device(IPAddress.Parse("192.168.1.64")), null, new HealthCheckOptions { PingCount = 2, PingInterval = TimeSpan.Zero, TraceRounds = 1, StreamWatch = TimeSpan.FromSeconds(1) }, null, CancellationToken.None);
        Assert.True(noLogin.Findings.Any(f => f.Code == "onvif-login"), "login needed");

        // Not reachable from the demo laptop (192.168.1.50/24) until a temporary address is joined.
        var far = await Check("10.0.5.40");
        Assert.Equal("offline", far.Findings[0].Code);
        await site.AddFreeAddressAsync(site.Laptop, CameraFinder.Core.Net.Subnet.Parse("10.0.5.0/24"), new HashSet<IPAddress>(), CancellationToken.None);
        var axis = await Check("10.0.5.21");
        Assert.Equal(0, axis.Findings.Count(f => f.Severity >= Severity.Warning), string.Join(",", axis.Findings.Select(f => f.Code)));
        Assert.True(axis.Diagnosis.Contains("no problems found"), axis.Diagnosis);
    }
}
