using System.Net;
using CameraFinder.Core.Discovery;
using CameraFinder.Core.Net;

namespace CameraFinder.Core.Health;

/// <summary>
/// Health check data for the demo site, so every part of the health check (and the trace window) can be tried and
/// taught without real cameras. Each demo camera has a typical fault:
///   192.168.1.108 Dahua     - restarts again and again with link drops (weak PoE)
///   192.168.1.64  Hikvision - the NVR keeps trying an old password, account locked; clock 3 hours slow
///   10.0.5.40     Panasonic - packet loss on its cable, so the video drops frames
///   172.16.8.30   Wisenet   - SD card failing; key frames 8 s apart
///   others                  - healthy
/// </summary>
public sealed class DemoHealth : IPinger, IHopProber, IOnvifClient, IStreamProber
{
    private readonly DemoSite _site;
    private readonly Random _rnd = new(7);
    public DemoHealth(DemoSite site) => _site = site;

    public const string LossyDevice = "10.0.5.40";

    private bool Reachable(IPAddress ip) =>
        DemoSite.Devices.Any(d => d.Ip == ip.ToString()) && Reach.CanReach(_site.Laptop.Addresses, ip);

    private int Next(int min, int max) { lock (_rnd) return _rnd.Next(min, max); }

    // ---- ping / trace

    public async Task<PingResult> PingAsync(IPAddress target, IPAddress? source, TimeSpan timeout, CancellationToken ct)
    {
        if (target.ToString() == LossyDevice && Reachable(target))
        {
            await Task.Delay(5, ct);
            return Next(0, 100) < 12 ? new PingResult(false, 0, "Request timed out") : new PingResult(true, Next(1, 9));
        }
        return await _site.PingAsync(target, source, timeout, ct);
    }

    public async Task<HopReply> ProbeAsync(IPAddress target, IPAddress? source, int ttl, TimeSpan timeout, CancellationToken ct)
    {
        var r = await PingAsync(target, source, timeout, ct);
        return r.Success ? new HopReply(target, true, r.RoundTripMs, false) : new HopReply(null, false, 0, true);
    }

    // ---- ONVIF

    public async Task<OnvifReport> CollectAsync(IPAddress camera, int port, Credentials? login, IPAddress? source, CancellationToken ct)
    {
        await Task.Delay(600, ct);
        string ip = camera.ToString();
        var report = new OnvifReport { ServiceUrl = $"http://{ip}/onvif/device_service" };
        var dev = DemoSite.Devices.FirstOrDefault(d => d.Ip == ip);
        if (dev == null || !dev.Camera || !Reachable(camera) || dev.Model.Contains("NOT ACTIVATED")) return report;

        report.Answered = true;
        var now = DateTime.UtcNow;
        report.LaptopUtc = now;
        report.CameraUtc = ip == "192.168.1.64" ? now.AddHours(-3).AddMinutes(-4) : now.AddSeconds(1);
        report.TimeSource = ip == "192.168.1.64" ? "NTP" : "Manual";
        if (login is not { IsEmpty: false })
        {
            report.LoginAccepted = false;
            report.LoginError = "the camera needs a user name and password";
            return report;
        }
        report.LoginAccepted = true;
        report.Manufacturer = dev.Mac[..8] switch
        {
            "44:19:B6" => "Hikvision", "38:AF:29" => "Dahua", "00:18:85" => "Avigilon", "00:40:8C" => "AXIS",
            "D4:2D:C5" => "Panasonic", "00:09:18" => "Hanwha Vision", _ => null,
        };
        report.Model = dev.Model;
        report.Firmware = ip switch { "192.168.1.108" => "V2.800.0000000.25.R", "192.168.1.64" => "V5.7.3 build 220112", _ => "10.12.240" };
        report.SystemLog = Log(ip, DateTime.Now);
        if (report.SystemLog == null) report.Unsupported.Add("The camera does not share its system log over ONVIF");
        double fps = ip == "172.16.8.30" ? 30 : 25;
        report.Profiles.Add(new OnvifProfile("Profile_1", "MainStream", "H264", 2688, 1520, fps, 6144));
        report.Profiles.Add(new OnvifProfile("Profile_2", "SubStream", "H264", 640, 360, 15, 512));
        report.StreamUri = $"rtsp://{ip}:554/demo/main";
        return report;
    }

    /// <summary>The demo cameras' own logs, written the way each brand words them.</summary>
    public static string? Log(string ip, DateTime now)
    {
        string T(double hoursAgo) => now.AddHours(-hoursAgo).ToString("yyyy-MM-dd HH:mm:ss");
        return ip switch
        {
            "192.168.1.108" => string.Join("\n",
                $"{T(30)} System Reboot", $"{T(22.5)} Network Disconnected", $"{T(22.4)} System Reboot",
                $"{T(14.1)} Network Disconnected", $"{T(14)} System Reboot", $"{T(9.2)} User Login admin 192.168.1.10",
                $"{T(6.3)} Network Disconnected", $"{T(6.2)} System Reboot", $"{T(1.1)} System Reboot", $"{T(0.5)} Motion Detect Start"),
            "192.168.1.64" => string.Join("\n",
                Enumerable.Range(0, 14).Select(i => $"{T(5 - i * 0.2)} Illegal login, user: admin, IP: 192.168.1.10")
                    .Append($"{T(2.1)} User admin locked, IP: 192.168.1.10")
                    .Append($"{T(1.5)} NTP synchronization failed")
                    .Prepend($"{T(48)} Power on")),
            "172.16.8.30" => string.Join("\n",
                $"{T(70)} System Boot", $"{T(40)} SD card error: write fail", $"{T(20)} SD card error: write fail",
                $"{T(3)} SD card abnormal", $"{T(2)} Config changed by admin"),
            "10.0.5.40" => string.Join("\n", $"{T(200)} Power on", $"{T(12)} Login success admin"),
            "10.0.5.21" => string.Join("\n", $"{T(400)} System start", $"{T(30)} Time set via NTP"),
            _ => null,
        };
    }

    // ---- video

    public async Task<StreamReport> ProbeAsync(string url, Credentials? login, TimeSpan watch, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(3, watch.TotalSeconds)), ct);
        string ip = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";
        var r = new StreamReport { Url = FfprobeStreamProber.Redact(url) };
        if (!IPAddress.TryParse(ip, out var addr) || !Reachable(addr)) { r.Error = "Connection timed out"; return r; }
        r.Opened = true;
        r.Codec = "h264";
        r.Width = 2688; r.Height = 1520;
        r.NominalFps = ip == "172.16.8.30" ? 30 : 25;
        r.SecondsWatched = watch.TotalSeconds;
        (r.MeasuredFps, r.BitrateKbps, r.KeyframeInterval, r.LongestGap) = ip switch
        {
            LossyDevice => (17.4, 3900d, 2.0, 1.9),
            "172.16.8.30" => (29.9, 5200d, 8.0, 0.07),
            _ => (25.0, 5800d, 2.0, 0.08),
        };
        r.Frames = (int)(r.MeasuredFps.Value * r.SecondsWatched);
        return r;
    }
}
