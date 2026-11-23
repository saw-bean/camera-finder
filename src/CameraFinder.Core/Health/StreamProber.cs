using System.Globalization;
using System.Net;
using System.Text.Json;
using CameraFinder.Core.Net;

namespace CameraFinder.Core.Health;

public interface IStreamProber
{
    /// <param name="url">RTSP address, without the login (it is added here).</param>
    /// <param name="source">Laptop address to connect from (the temporary address in the camera's range), or null.</param>
    Task<StreamReport> ProbeAsync(string url, Credentials? login, TimeSpan watch, CancellationToken ct, IPAddress? source = null);
}

/// <summary>
/// Watches a camera's RTSP stream for a few seconds with ffprobe and measures what really arrives: frames per second,
/// bitrate, key-frame spacing and pauses. RTSP is pulled over TCP so a firewall that blocks UDP does not look like a
/// camera fault.
/// </summary>
public sealed class FfprobeStreamProber : IStreamProber
{
    private readonly string _ffprobe;
    public FfprobeStreamProber(string ffprobePath) => _ffprobe = ffprobePath;

    public async Task<StreamReport> ProbeAsync(string url, Credentials? login, TimeSpan watch, CancellationToken ct, IPAddress? source = null)
    {
        var report = new StreamReport { Url = Redact(url) };
        // ffprobe cannot choose the address it connects from; through a temporary address it goes via a local relay
        // that connects from that address.
        await using var relay = source != null && Uri.TryCreate(url, UriKind.Absolute, out var u) && IPAddress.TryParse(u.Host, out var host)
            ? new TcpRelay(new IPEndPoint(host, u.Port > 0 ? u.Port : 554), source)
            : null;
        var args = Arguments(WithLogin(relay?.Rewrite(url) ?? url, login), watch);
        ProcessResult r;
        try { r = await ProcessRunner.RunAsync(_ffprobe, args, watch + TimeSpan.FromSeconds(15), ct); }
        catch (System.ComponentModel.Win32Exception ex) { report.Error = "ffprobe could not start: " + ex.Message; return report; }
        if (r.TimedOut) { report.Error = "Connection timed out"; return report; }
        Parse(r.StdOut, report);
        if (!report.Opened)
            report.Error = FirstError(r.StdErr, login) ?? (r.ExitCode != 0 ? $"ffprobe exit code {r.ExitCode}" : "no video in the stream");
        return report;
    }

    public static List<string> Arguments(string url, TimeSpan watch) => new()
    {
        "-v", "error", "-hide_banner",
        "-rtsp_transport", "tcp",
        "-timeout", "5000000",            // socket I/O timeout, microseconds
        "-select_streams", "v:0",
        "-read_intervals", $"%+{(int)Math.Max(1, watch.TotalSeconds)}",
        "-show_entries", "stream=codec_name,width,height,avg_frame_rate,r_frame_rate:packet=pts_time,dts_time,size,flags",
        "-of", "json",
        url,
    };

    /// <summary>Puts the login into the URL the way RTSP expects (rtsp://user:pass@host/...), percent-encoded.</summary>
    public static string WithLogin(string url, Credentials? login)
    {
        if (login is not { IsEmpty: false } || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        var b = new UriBuilder(u) { UserName = Uri.EscapeDataString(login.User), Password = Uri.EscapeDataString(login.Password) };
        return b.Uri.AbsoluteUri;
    }

    /// <summary>The URL without user name and password, for display.</summary>
    public static string Redact(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.UserInfo)) return url;
        return new UriBuilder(u) { UserName = "", Password = "" }.Uri.AbsoluteUri;
    }

    /// <summary>Reads ffprobe's JSON (stream info plus one entry per video packet) into the report.</summary>
    public static void Parse(string json, StreamReport report)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array && streams.GetArrayLength() > 0)
            {
                var st = streams[0];
                report.Opened = true;
                report.Codec = Str(st, "codec_name");
                report.Width = Int(st, "width");
                report.Height = Int(st, "height");
                report.NominalFps = Rate(Str(st, "avg_frame_rate")) ?? Rate(Str(st, "r_frame_rate"));
            }

            if (!root.TryGetProperty("packets", out var packets) || packets.ValueKind != JsonValueKind.Array) return;
            var times = new List<double>();
            var keys = new List<double>();
            long bytes = 0;
            foreach (var p in packets.EnumerateArray())
            {
                double? t = Dbl(Str(p, "pts_time")) ?? Dbl(Str(p, "dts_time"));
                if (t == null) continue;
                times.Add(t.Value);
                bytes += Int(p, "size") ?? 0;
                if (Str(p, "flags")?.Contains('K') == true) keys.Add(t.Value);
            }
            report.Opened |= times.Count > 0;
            report.Frames = times.Count;
            if (times.Count < 2) return;
            times.Sort();
            double span = times[^1] - times[0];
            if (span <= 0) return;
            report.SecondsWatched = span;
            report.MeasuredFps = (times.Count - 1) / span;
            report.BitrateKbps = bytes * 8 / span / 1000;
            report.LongestGap = times.Zip(times.Skip(1), (a, b) => b - a).Max();
            if (keys.Count >= 2) report.KeyframeInterval = (keys[^1] - keys[0]) / (keys.Count - 1);
            else if (keys.Count == 1 && span > 4) report.KeyframeInterval = span; // at most one key frame seen: at least this far apart
            // An RTSP stream's first packets often carry the stream's own start time; drop silly frame rates.
            if (report.MeasuredFps > 240) report.MeasuredFps = null;
        }
    }

    /// <summary>ffprobe's first meaningful error line, with the password taken out.</summary>
    public static string? FirstError(string stderr, Credentials? login)
    {
        foreach (var raw in stderr.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (login is { IsEmpty: false } && login.Password.Length > 0)
                line = line.Replace(login.Password, "****").Replace(Uri.EscapeDataString(login.Password), "****");
            // "[rtsp @ 0000021a] method DESCRIBE failed: 401 Unauthorized" -> keep the part after the tag.
            int tag = line.IndexOf("] ", StringComparison.Ordinal);
            if (line.StartsWith('[') && tag > 0) line = line[(tag + 2)..];
            return Redact(line);
        }
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static int? Int(JsonElement e, string name) =>
        int.TryParse(Str(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double? Dbl(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

    /// <summary>"25/1" -> 25, "30000/1001" -> 29.97, "0/0" -> null.</summary>
    public static double? Rate(string? s)
    {
        if (s == null) return null;
        var parts = s.Split('/');
        if (parts.Length == 2 && Dbl(parts[0]) is { } n && Dbl(parts[1]) is { } d && d > 0 && n > 0) return n / d;
        return parts.Length == 1 && Dbl(s) is { } x && x > 0 ? x : null;
    }
}

/// <summary>Usual RTSP addresses by brand, for cameras that do not report one over ONVIF.</summary>
public static class RtspPaths
{
    public static string? Guess(string ip, string? vendor, string? description, int port = 554)
    {
        string v = ((vendor ?? "") + " " + (description ?? "")).ToLowerInvariant();
        string path =
            v.Contains("hikvision") || v.Contains("hilook") || v.Contains("ds-2") ? "/Streaming/Channels/101"
            : v.Contains("dahua") || v.Contains("lorex") || v.Contains("amcrest") || v.Contains("ipc-") ? "/cam/realmonitor?channel=1&subtype=0"
            : v.Contains("axis") ? "/axis-media/media.amp"
            : v.Contains("hanwha") || v.Contains("samsung") || v.Contains("wisenet") ? "/profile2/media.smp"
            : v.Contains("bosch") ? "/?inst=1"
            : v.Contains("panasonic") || v.Contains("i-pro") || v.Contains("wv-") ? "/MediaInput/h264"
            : v.Contains("vivotek") ? "/live.sdp"
            : v.Contains("uniview") ? "/media/video1"
            : v.Contains("avigilon") ? "/defaultPrimary?streamType=u"
            : "";
        return path.Length == 0 ? null : $"rtsp://{ip}:{port}{path}";
    }
}
