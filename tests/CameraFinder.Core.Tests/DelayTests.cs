using System.Net;
using CameraFinder.Core.Discovery;
using CameraFinder.Core.Health;

static partial class Tests
{
    // ---------- Live view delay ----------

    private static DelayTracker Synthetic(int seconds, Func<int, double> extraTransit, int fps = 25, int gopFrames = 50)
    {
        var d = new DelayTracker();
        d.Started(100.0);
        d.Playing(100.2);
        for (int i = 0; i < seconds * fps; i++)
        {
            double capture = i / (double)fps;
            bool key = i % gopFrames == 0;
            d.AddFrame(100.5 + capture + extraTransit(i), (uint)(i * 90000L / fps), 90000, key ? 60000 : 8000, key);
        }
        return d;
    }

    public static void TestDelayEvenBurstyAndFallingBehind()
    {
        var even = Synthetic(60, _ => 0.0).Report();
        Assert.True(even.BufferNeededMs < 5, "even: " + even.BufferNeededMs);
        Assert.True(even.FallingBehindPerMinute < 0.05, "even keeps up: " + even.FallingBehindPerMinute);
        Assert.Equal(0.5, Math.Round(even.TimeToPicture ?? 0, 2)); // first key frame 0.5 s after start
        Assert.Equal(0.2, Math.Round(even.ConnectSeconds ?? 0, 2));

        // Frames held back in bursts of up to 0.6 s (e.g. a key frame squeezing through a slow link).
        var bursty = Synthetic(60, i => (i % 25) < 15 ? 0.6 * (15 - i % 25) / 15 : 0).Report();
        Assert.True(bursty.BufferNeededMs > 400, "bursty: " + bursty.BufferNeededMs);

        // The path cannot keep up: 1 s more delay every minute.
        var behind = Synthetic(90, i => i / 25.0 / 60.0).Report();
        Assert.True(behind.FallingBehindPerMinute is > 0.8 and < 1.2, "behind: " + behind.FallingBehindPerMinute);

        var snap = new HealthSnapshot { Ip = IPAddress.Parse("10.0.5.21"), Delay = behind, Ping = new PingSample(5, 5, new long[] { 1, 1, 1, 1, 1 }) };
        Assert.True(HealthCheck.Analyse(snap).Findings.Any(f => f.Code == "falling-behind"), "falling-behind finding");
    }

    public static void TestFlashTestFindsTheBigFrameNotTheScheduledKeyFrame()
    {
        var d = new DelayTracker();
        var flips = new List<double>();
        const int fps = 25;
        // 20 s of video; key frames every 2 s on schedule; flips every 2.5 s; the camera answers each flip 0.35 s later.
        for (int f = 1; f <= 6; f++) flips.Add(10 + f * 2.5);
        for (int i = 0; i < 20 * fps; i++)
        {
            double at = 10 + i / (double)fps;
            bool key = i % (2 * fps) == 0;
            int bytes = key ? 60000 : 8000;
            if (flips.Any(fl => at >= fl + 0.35 && at < fl + 0.35 + 1.0 / fps)) bytes = 120000;
            d.AddFrame(at, (uint)(i * 90000L / fps), 90000, bytes, key);
        }
        foreach (var fl in flips) d.AddFlash(fl);
        var r = d.Report();
        Assert.Equal(6, r.FlashesShown);
        Assert.True(r.FlashesSeen >= 5, "seen " + r.FlashesSeen);
        Assert.True(r.CameraDelay is > 0.3 and < 0.42, "delay " + r.CameraDelay);

        var snap = new HealthSnapshot { Ip = IPAddress.Parse("10.0.5.21"), Delay = r };
        var fast = HealthCheck.Analyse(snap);
        Assert.True(fast.Findings.Any(f => f.Code == "delay-after-camera"), "fast camera: delay is after it");
        Assert.True(fast.Diagnosis.Contains("Camera + network delay (flash test)"), fast.Diagnosis);
        snap.Delay = new DelayReport { CameraDelay = 1.8, CameraDelayMin = 1.7, CameraDelayMax = 1.9, FlashesSeen = 6, FlashesShown = 6 };
        Assert.Equal("camera-slow", HealthCheck.Analyse(snap).Findings[0].Code);
    }

    public static async Task TestDemoFlashTestMeasuresTheSlowCamera()
    {
        var site = new DemoSite { ArpDelay = TimeSpan.Zero, JoinDelay = TimeSpan.Zero };
        await site.AddFreeAddressAsync(site.Laptop, CameraFinder.Core.Net.Subnet.Parse("10.0.5.0/24"), new HashSet<IPAddress>(), CancellationToken.None);
        var tick = TimeSpan.FromMilliseconds(100);
        var demo = new DemoHealth(site) { Tick = tick };
        var stats = new LiveStats();
        using var cts = new CancellationTokenSource();
        int n = 0;
        var run = demo.RunAsync($"rtsp://{DemoHealth.LossyDevice}:554/demo/main", new Credentials("admin", "x"), null, stats, _ =>
        {
            n++;
            if (n is 5 or 10 or 15 or 20) stats.Delay.AddFlash(DelayTracker.Now());
            if (n == 32) cts.Cancel();
        }, cts.Token);
        try { await run; } catch (OperationCanceledException) { }
        var r = stats.Delay.Report();
        Assert.True(r.FlashesSeen >= 3, $"flashes seen {r.FlashesSeen}/{r.FlashesShown}");
        double expected = DemoHealth.CameraDelayFor(DemoHealth.LossyDevice) * tick.TotalSeconds;
        Assert.True(r.CameraDelay is { } c && Math.Abs(c - expected) < 0.06, $"delay {r.CameraDelay} expected ~{expected}");
    }
}
