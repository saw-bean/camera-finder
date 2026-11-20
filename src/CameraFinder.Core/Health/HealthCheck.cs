using System.Net;
using CameraFinder.Core.Discovery;
using CameraFinder.Core.Model;

namespace CameraFinder.Core.Health;

public sealed class HealthCheckOptions
{
    public Credentials? Login { get; init; }
    public int PingCount { get; init; } = 20;
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public int TraceRounds { get; init; } = 5;
    public bool CheckStream { get; init; } = true;
    public TimeSpan StreamWatch { get; init; } = TimeSpan.FromSeconds(8);
    /// <summary>
    /// The source address is a temporary one (added with skipassource). ffprobe cannot be told to connect from it, and
    /// Windows never picks it, so the stream check is skipped instead of reporting a false failure.
    /// </summary>
    public bool SourceIsTemporary { get; init; }
}

/// <summary>The result of one health check: what was collected, the plain-language events, the matched patterns and the diagnosis.</summary>
public sealed record HealthResult(HealthSnapshot Snapshot, IReadOnlyList<HealthEvent> Events, IReadOnlyList<Finding> Findings, string Diagnosis);

/// <summary>
/// Runs a camera health check: collects (ping, path, ONVIF incl. the camera's system log, video stream), then
/// normalizes, detects known failure patterns and writes the diagnosis. Each collector may fail on its own; whatever
/// could not be checked is said so in the diagnosis instead of failing the whole check.
/// </summary>
public sealed class HealthCheck
{
    private readonly IPinger _pinger;
    private readonly IHopProber _hops;
    private readonly IOnvifClient _onvif;
    private readonly IStreamProber? _stream;
    private readonly string? _streamMissingReason;

    /// <param name="stream">Null when ffprobe is not available; <paramref name="streamMissingReason"/> says why.</param>
    public HealthCheck(IPinger pinger, IHopProber hops, IOnvifClient onvif, IStreamProber? stream, string? streamMissingReason = null)
    {
        _pinger = pinger;
        _hops = hops;
        _onvif = onvif;
        _stream = stream;
        _streamMissingReason = streamMissingReason;
    }

    /// <summary>The usual set-up: real ICMP and ONVIF, ffprobe from the tools folder if it is there.</summary>
    public static HealthCheck CreateDefault()
    {
        string? ffprobe = ExternalTool.Find(ExternalTool.FfprobeExe);
        return new HealthCheck(new IcmpPinger(), new IcmpHopProber(), new OnvifClient(),
            ffprobe == null ? null : new FfprobeStreamProber(ffprobe),
            ffprobe == null ? "Video stream not checked: ffprobe.exe was not found in the tools folder (reinstall Camera Finder)." : null);
    }

    /// <param name="source">Laptop address in the device's range (temporary or not), or null to let Windows choose.</param>
    /// <param name="progress">Short status lines while collecting ("Reading the camera's log…").</param>
    public async Task<HealthResult> RunAsync(Device device, IPAddress? source, HealthCheckOptions options, IProgress<string>? progress, CancellationToken ct)
    {
        HealthSnapshot snap;
        lock (device)
        {
            snap = new HealthSnapshot
            {
                Ip = device.Ip, Mac = device.Mac, Vendor = device.Vendor, Description = device.Description,
                OpenPorts = device.OpenPorts.ToArray(), CheckedAt = DateTime.Now,
            };
        }

        // Ping and path run side by side; ONVIF after (it needs the camera to be reachable anyway).
        progress?.Report($"Pinging {device.Ip} and tracing the path…");
        var ping = PingAsync(device.Ip, source, options, ct);
        var path = new PathTracer(_hops) { Interval = TimeSpan.FromMilliseconds(500) }.RunAsync(device.Ip, source, options.TraceRounds, null, ct);
        snap.Ping = await ping;
        try { snap.Path = await path; }
        catch (Exception ex) when (ex is not OperationCanceledException) { snap.NotChecked.Add("Path not traced: " + ex.Message); }

        progress?.Report("Asking the camera over ONVIF (identity, clock, log, video settings)…");
        int port = OnvifPort(snap.OpenPorts);
        try { snap.Onvif = await _onvif.CollectAsync(device.Ip, port, options.Login, source, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { snap.NotChecked.Add("ONVIF not checked: " + ex.Message); }
        if (snap.Onvif is { Answered: true, LoginAccepted: null } o && o.Model == null && (options.Login == null || options.Login.IsEmpty))
            snap.NotChecked.Add("Camera log not read: enter the camera's user name and password to include it.");

        if (!options.CheckStream) snap.NotChecked.Add("Video stream not checked (turned off).");
        else if (_stream == null) snap.NotChecked.Add(_streamMissingReason ?? "Video stream not checked.");
        else
        {
            string? url = snap.Onvif?.StreamUri ?? RtspPaths.Guess(device.Ip.ToString(), snap.Vendor, snap.Description ?? snap.Onvif?.Model);
            if (url == null)
                snap.NotChecked.Add("Video stream not checked: the camera did not report its stream address over ONVIF and its brand is not known.");
            else if (options.SourceIsTemporary)
                snap.NotChecked.Add("Video stream not checked: the camera is in another IP range, and ffprobe can only connect from the laptop's main address. Set an IP in that range on the laptop to check video.");
            else
            {
                progress?.Report($"Watching the video stream for {options.StreamWatch.TotalSeconds:0} seconds…");
                try { snap.Stream = await _stream.ProbeAsync(Repoint(url, device.Ip), options.Login, options.StreamWatch, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { snap.NotChecked.Add("Video stream not checked: " + ex.Message); }
            }
        }

        progress?.Report("Working out the diagnosis…");
        return Analyse(snap);
    }

    /// <summary>Steps 2-4 on an already collected snapshot (also used by tests and the demo).</summary>
    public static HealthResult Analyse(HealthSnapshot snap)
    {
        var events = LogNormalizer.Normalize(snap);
        var findings = PatternDetector.Detect(snap, events);
        return new HealthResult(snap, events, findings, DiagnosisWriter.Write(snap, events, findings));
    }

    private async Task<PingSample> PingAsync(IPAddress ip, IPAddress? source, HealthCheckOptions o, CancellationToken ct)
    {
        var rtts = new List<long>();
        int sent = 0;
        for (int i = 0; i < o.PingCount && !ct.IsCancellationRequested; i++)
        {
            var started = DateTime.UtcNow;
            PingResult r;
            try { r = await _pinger.PingAsync(ip, source, TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { r = new PingResult(false, 0, ex.Message); }
            sent++;
            if (r.Success) rtts.Add(r.RoundTripMs);
            var wait = o.PingInterval - (DateTime.UtcNow - started);
            if (wait > TimeSpan.Zero)
            {
                try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { break; }
            }
        }
        ct.ThrowIfCancellationRequested();
        return new PingSample(sent, rtts.Count, rtts);
    }

    /// <summary>ONVIF normally lives on the web port: 80, else 8080/8000 if only those are open.</summary>
    public static int OnvifPort(IReadOnlyCollection<int> open) =>
        open.Contains(80) || open.Count == 0 ? 80 : open.Contains(8080) ? 8080 : open.Contains(8000) && !open.Contains(554) ? 8000 : 80;

    /// <summary>Cameras behind NAT or with two addresses often report a stream URL with the wrong host: use the one we reached.</summary>
    public static string Repoint(string url, IPAddress ip) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host != ip.ToString()
            ? new UriBuilder(u) { Host = ip.ToString() }.Uri.AbsoluteUri
            : url;
}
