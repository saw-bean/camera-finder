using System.Net;
using CameraFinder.Core.Net;

namespace CameraFinder.Core.Health;

// The camera health check runs in four steps, each with its own type:
//   1. collect   -> HealthSnapshot  (what the checks measured, plus the camera's own log as raw lines)
//   2. normalize -> HealthEvent     (every measurement and log line rewritten as one plain sentence)
//   3. detect    -> Finding         (known failure patterns matched across the events)
//   4. explain   -> Diagnosis text  (a short, readable explanation of what is wrong and what to try)

public enum Severity { Info, Notice, Warning, Problem }

/// <summary>ONVIF / RTSP login. Never logged or shown.</summary>
public sealed record Credentials(string User, string Password)
{
    public bool IsEmpty => string.IsNullOrEmpty(User);
    public override string ToString() => $"{User} / ****";
}

/// <summary>A short burst of pings (not the continuous ping window).</summary>
public sealed record PingSample(int Sent, int Received, IReadOnlyList<long> RoundTrips)
{
    public double LossPercent => Sent == 0 ? 0 : 100.0 * (Sent - Received) / Sent;
    public double AvgMs => RoundTrips.Count == 0 ? 0 : RoundTrips.Average();
    public long MaxMs => RoundTrips.Count == 0 ? 0 : RoundTrips.Max();
    public long MinMs => RoundTrips.Count == 0 ? 0 : RoundTrips.Min();

    /// <summary>Jitter as RFC 3550 uses it: the average change between consecutive round trips.</summary>
    public double JitterMs => RoundTrips.Count < 2 ? 0 : RoundTrips.Zip(RoundTrips.Skip(1), (a, b) => Math.Abs(a - b)).Average();
}

/// <summary>One video profile as the camera reports it over ONVIF.</summary>
/// <param name="GovLength">Configured I-frame (key frame) interval, in frames.</param>
public sealed record OnvifProfile(string Token, string? Name, string? Encoding, int? Width, int? Height, double? FrameRate, int? BitrateKbps, int? GovLength = null)
{
    /// <summary>Configured seconds between key frames, when both the interval and the frame rate are known.</summary>
    public double? KeyframeSeconds => GovLength is > 0 && FrameRate is > 0 ? GovLength / FrameRate : null;
}

/// <summary>The camera's own network settings, read over ONVIF (needs a login).</summary>
public sealed class OnvifNetwork
{
    public IPAddress? Address { get; set; }
    public int? Prefix { get; set; }
    /// <summary>The camera gets its address from DHCP.</summary>
    public bool? Dhcp { get; set; }
    public List<IPAddress> Gateways { get; } = new();
    /// <summary>NTP servers (addresses or names) the camera is set to use.</summary>
    public List<string> NtpServers { get; } = new();
    public bool? NtpFromDhcp { get; set; }
    /// <summary>Link the camera's own network port negotiated (OperSettings), Mbit/s.</summary>
    public int? LinkSpeedMbps { get; set; }
    /// <summary>"full" / "half".</summary>
    public string? Duplex { get; set; }
    /// <summary>False when speed/duplex is forced in the camera's settings.</summary>
    public bool? AutoNegotiation { get; set; }
    public int? Mtu { get; set; }

    public Subnet? Subnet => Address != null && Prefix is >= 1 and <= 32 ? Net.Subnet.Of(Address, Prefix.Value) : null;
}

/// <summary>What the camera answered over ONVIF. Fields stay null when the camera did not answer that request.</summary>
public sealed class OnvifReport
{
    public string ServiceUrl { get; init; } = "";
    /// <summary>The camera answered at least one ONVIF request (even an error).</summary>
    public bool Answered { get; set; }
    /// <summary>True: login accepted. False: refused. Null: no login was needed or tried.</summary>
    public bool? LoginAccepted { get; set; }
    public string? LoginError { get; set; }

    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? Firmware { get; set; }
    public string? Serial { get; set; }

    public DateTime? CameraUtc { get; set; }
    /// <summary>The laptop's clock when the camera's time was read, to compare the two.</summary>
    public DateTime? LaptopUtc { get; set; }
    /// <summary>"NTP" or "Manual".</summary>
    public string? TimeSource { get; set; }
    public string? TimeZone { get; set; }

    /// <summary>The camera's system log, as returned (null when the camera does not offer it).</summary>
    public string? SystemLog { get; set; }
    public List<OnvifProfile> Profiles { get; } = new();
    /// <summary>RTSP address of the first (main) profile.</summary>
    public string? StreamUri { get; set; }
    /// <summary>RTSP address per profile token (main, sub, third stream...).</summary>
    public Dictionary<string, string> StreamUris { get; } = new();

    /// <summary>The camera's network settings (null when not read).</summary>
    public OnvifNetwork? Network { get; set; }
    /// <summary>Protocols the camera says it serves: name (HTTP, HTTPS, RTSP) -> (enabled, first port).</summary>
    public Dictionary<string, (bool Enabled, int? Port)> Protocols { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The login works but its user level is too low for some requests (e.g. the system log).</summary>
    public bool LoginLevelTooLow { get; set; }

    /// <summary>Requests the camera did not support or refused, in plain words.</summary>
    public List<string> Unsupported { get; } = new();

    public TimeSpan? ClockOffset => CameraUtc is { } c && LaptopUtc is { } l ? c - l : null;
}

/// <summary>What ffprobe saw on the camera's RTSP stream over a few seconds.</summary>
public sealed class StreamReport
{
    /// <summary>Stream address with any password removed (safe to show).</summary>
    public string Url { get; init; } = "";
    public bool Opened { get; set; }
    public string? Error { get; set; }
    public string? Codec { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    /// <summary>Frame rate the stream says it has.</summary>
    public double? NominalFps { get; set; }
    /// <summary>Frames actually received per second.</summary>
    public double? MeasuredFps { get; set; }
    public double? BitrateKbps { get; set; }
    /// <summary>Average seconds between key frames (I-frames).</summary>
    public double? KeyframeInterval { get; set; }
    /// <summary>Longest gap between two frames, in seconds.</summary>
    public double? LongestGap { get; set; }
    public double SecondsWatched { get; set; }
    public int Frames { get; set; }
}

public sealed record Hop(int Ttl, IPAddress? Address, int Sent, int Received, long LastMs, long BestMs, double AvgMs, long WorstMs, double JitterMs)
{
    public double LossPercent => Sent == 0 ? 0 : 100.0 * (Sent - Received) / Sent;
}

/// <summary>mtr-style path result: every router between the laptop and the device, with loss and delay per hop.</summary>
public sealed record TraceReport(IPAddress Target, IReadOnlyList<Hop> Hops, bool ReachedTarget, int Rounds);

/// <summary>Everything one health check collected about one device.</summary>
public sealed class HealthSnapshot
{
    public required IPAddress Ip { get; init; }
    public string? Mac { get; init; }
    public string? Description { get; init; }
    public string? Vendor { get; init; }
    /// <summary>False for routers, PCs, door controllers...: no camera checks, and the wording says "device".</summary>
    public bool IsCamera { get; init; } = true;
    public IReadOnlyCollection<int> OpenPorts { get; init; } = Array.Empty<int>();
    public DateTime CheckedAt { get; init; } = DateTime.Now;

    public PingSample? Ping { get; set; }
    public TraceReport? Path { get; set; }
    public OnvifReport? Onvif { get; set; }
    public StreamReport? Stream { get; set; }
    /// <summary>The switch port the device is plugged into (SNMP), when a managed switch knows it.</summary>
    public SwitchPortReport? Port { get; set; }
    /// <summary>Live view delay measurements (from the live stream and the flash test).</summary>
    public DelayReport? Delay { get; set; }
    /// <summary>Whether the video stream (RTSP) accepted the login: true = it played with it, false = 401, null = not tried or no login.</summary>
    public bool? VideoLoginAccepted { get; set; }
    /// <summary>Whether the login used for video is the same one ONVIF was given.</summary>
    public bool VideoLoginSameAsOnvif { get; set; }

    /// <summary>Checks that could not run, in plain words ("Video stream not checked: ffprobe.exe is missing").</summary>
    public List<string> NotChecked { get; } = new();
}

/// <summary>One thing that happened or was measured, written as a plain sentence.</summary>
public sealed record HealthEvent(DateTime? Time, string Source, Severity Severity, EventKind Kind, string Text, string? Raw = null, string? From = null);

/// <summary>What a log line or measurement means, so patterns can count them without re-reading text.</summary>
public enum EventKind
{
    Other,
    Measurement,
    Restart,
    Crash,
    LoginFailed,
    LoginOk,
    AccountLocked,
    LinkDown,
    LinkUp,
    IpConflict,
    StorageError,
    TimeSyncFailed,
    TimeChanged,
    VideoLoss,
    Overheat,
    PowerProblem,
    Firmware,
    LowMemory,
    Tamper,
    Motion,
    ConfigChanged,
}

/// <summary>A known failure pattern that matched, with the evidence and what to do about it.</summary>
public sealed record Finding(string Code, Severity Severity, string Title, string Evidence, IReadOnlyList<string> Advice);
