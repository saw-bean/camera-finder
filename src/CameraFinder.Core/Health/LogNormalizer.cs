using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace CameraFinder.Core.Health;

/// <summary>
/// Step 2 of the health check: turns every measurement and every line of the camera's own log into one plain
/// sentence (<see cref="HealthEvent"/>). Camera logs differ by brand ("Illegal login", "auth failed", "login error");
/// the rules below map the common wordings onto one meaning, so pattern detection does not depend on the brand.
/// </summary>
public static class LogNormalizer
{
    private sealed record Rule(Regex Match, EventKind Kind, Severity Severity, string Text);

    private static Rule R(string pattern, EventKind kind, Severity severity, string text) =>
        new(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100)),
            kind, severity, text);

    // Order matters: the first rule that matches wins (e.g. "watchdog reboot" is a crash, not a plain restart).
    private static readonly Rule[] Rules =
    {
        R(@"watchdog|kernel panic|segfault|exception reboot|abnormal (reboot|restart)|crash", EventKind.Crash, Severity.Problem,
            "The camera crashed or hung and restarted itself"),
        R(@"(account|user)\b.{0,30}\block(ed)?\b|locked out|too many (login|failed)", EventKind.AccountLocked, Severity.Problem,
            "A user account was locked after too many wrong passwords"),
        R(@"(illegal|invalid|fail(ed|ure)?|wrong|bad|denied|refused|error)\W{0,3}(user\s*)?(login|logon|log in|auth\w*|password)|(login|logon|auth\w*|password)\W{0,3}(fail(ed|ure)?|error|denied|refused|invalid|incorrect|wrong)",
            EventKind.LoginFailed, Severity.Warning, "A login was refused (wrong user name or password)"),
        R(@"\b(login|logon|logged in|log in)\b.{0,15}(success|ok\b)|user login\b|\blogged in\b", EventKind.LoginOk, Severity.Info,
            "Someone logged in"),
        R(@"ip.{0,10}(address )?conflict|address conflict|duplicate ip", EventKind.IpConflict, Severity.Problem,
            "Another device is using the camera's IP address (IP conflict)"),
        R(@"(poe|power).{0,20}(low|insufficient|fail|lost|under|drop)|under-?voltage|brown-?out", EventKind.PowerProblem, Severity.Problem,
            "The camera reported a power problem"),
        R(@"(link|network|eth\d*|lan|cable|interface).{0,15}(down|disconnect(ed)?|lost|unplug(ged)?|broken)|network (is )?unreachable",
            EventKind.LinkDown, Severity.Warning, "The network cable link went down"),
        R(@"(link|network|eth\d*|lan|interface).{0,15}\b(up|connected|recover(ed)?|restored)\b", EventKind.LinkUp, Severity.Info,
            "The network cable link came back up"),
        R(@"(sd|hdd|disk|storage|card|nas|memory card).{0,25}(error|fail\w*|full|abnormal|not found|unformatted|exception|uninitiali[sz]ed)|storage full",
            EventKind.StorageError, Severity.Warning, "The camera's storage (SD card / disk) reported an error"),
        R(@"(ntp|time ?sync).{0,25}(fail\w*|error|timeout|timed out|unreachable)", EventKind.TimeSyncFailed, Severity.Warning,
            "The camera could not get the time from its time server (NTP)"),
        R(@"(time|clock|date).{0,15}(changed|set|adjust\w*|modif\w*|correct\w*)|ntp.{0,15}(success|sync\w*)", EventKind.TimeChanged, Severity.Info,
            "The camera's clock was set or corrected"),
        R(@"video (signal )?loss|signal lost|no video|sensor (error|fail\w*)|image sensor", EventKind.VideoLoss, Severity.Problem,
            "The camera lost its video signal or reported a sensor fault"),
        R(@"temperature|overheat|too hot|fan (fail\w*|error|stop)", EventKind.Overheat, Severity.Problem,
            "The camera reported a temperature or fan problem"),
        R(@"out of memory|\boom\b|memory (low|insufficient|exhaust\w*)", EventKind.LowMemory, Severity.Warning,
            "The camera ran low on memory"),
        R(@"(firmware|upgrade|update)\b.{0,25}(success\w*|complete\w*|start\w*|finish\w*|fail\w*)", EventKind.Firmware, Severity.Notice,
            "A firmware update ran"),
        R(@"\b(re-?boot\w*|restart\w*|power(ed)? ?(on|up)|cold start|warm start|system start\w*|start ?up|boot(ed|ing)?\b)",
            EventKind.Restart, Severity.Notice, "The camera started up (it was restarted or lost power)"),
        R(@"tamper|lens (covered|blocked)|scene change|defocus", EventKind.Tamper, Severity.Warning,
            "The camera reported tampering (lens covered, moved or out of focus)"),
        R(@"(config\w*|parameter|setting)s?.{0,15}(changed|modif\w*|saved|import\w*|restor\w*)", EventKind.ConfigChanged, Severity.Info,
            "A setting was changed"),
        R(@"motion|vmd|line ?cross|intrusion", EventKind.Motion, Severity.Info, "Motion or an analytics event was detected"),
    };

    private static readonly Regex IpInLine = new(@"\b((?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3})\b", RegexOptions.Compiled);

    // Timestamps seen in camera logs: "2026-05-01 10:22:33", "2026/05/01T10:22:33Z", "01-05-2026 10:22:33", "May  1 10:22:33".
    private static readonly Regex IsoTime = new(@"(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})[ T_]+(\d{1,2}):(\d{2})(?::(\d{2}))?", RegexOptions.Compiled);
    private static readonly Regex DayFirstTime = new(@"\b(\d{1,2})[-/.](\d{1,2})[-/.](\d{4})[ T_]+(\d{1,2}):(\d{2})(?::(\d{2}))?", RegexOptions.Compiled);
    private static readonly Regex SyslogTime = new(@"\b(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\w*\s+(\d{1,2})\s+(\d{1,2}):(\d{2}):(\d{2})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>All events for a snapshot: measurements first, then the camera's log, oldest first.</summary>
    public static List<HealthEvent> Normalize(HealthSnapshot s)
    {
        var events = new List<HealthEvent>();
        events.AddRange(Measurements(s));
        if (s.Onvif?.SystemLog is { } log)
        {
            int year = (s.Onvif.CameraUtc ?? s.CheckedAt).Year;
            events.AddRange(ParseLog(log, year).OrderBy(e => e.Time ?? DateTime.MinValue));
        }
        return events;
    }

    /// <summary>Parses a camera system log (one event per line). Lines that match no rule are kept as "Camera log: ...".</summary>
    public static IEnumerable<HealthEvent> ParseLog(string log, int assumedYear)
    {
        foreach (var raw in log.Split('\n'))
        {
            string line = raw.Trim().TrimEnd('\r');
            if (line.Length < 3) continue;
            if (line.Length > 400) line = line[..400];
            var ev = NormalizeLine(line, assumedYear);
            if (ev != null) yield return ev;
        }
    }

    public static HealthEvent? NormalizeLine(string line, int assumedYear)
    {
        var (time, rest) = ExtractTime(line, assumedYear);
        if (rest.Trim(' ', '-', ':', '|', '[', ']').Length == 0) return null;
        string? from = IpInLine.Match(rest) is { Success: true } m ? m.Groups[1].Value : null;
        foreach (var rule in Rules)
        {
            bool hit;
            try { hit = rule.Match.IsMatch(rest); }
            catch (RegexMatchTimeoutException) { hit = false; }
            if (!hit) continue;
            string text = rule.Text;
            if (from != null && rule.Kind is EventKind.LoginFailed or EventKind.LoginOk or EventKind.AccountLocked)
                text += $" from {from}";
            else if (from != null && rule.Kind == EventKind.IpConflict)
                text += $" ({from})";
            return new HealthEvent(time, "Camera log", rule.Severity, rule.Kind, text, line, from);
        }
        return new HealthEvent(time, "Camera log", Severity.Info, EventKind.Other, "Camera log: " + rest.Trim(), line, from);
    }

    /// <summary>Finds a timestamp anywhere in the line; returns it and the line without it.</summary>
    public static (DateTime? Time, string Remainder) ExtractTime(string line, int assumedYear)
    {
        var m = IsoTime.Match(line);
        if (m.Success && TryDate(Int(m, 1), Int(m, 2), Int(m, 3), Int(m, 4), Int(m, 5), Int(m, 6)) is { } iso)
            return (iso, Cut(line, m));
        m = DayFirstTime.Match(line);
        if (m.Success && TryDate(Int(m, 3), Int(m, 2), Int(m, 1), Int(m, 4), Int(m, 5), Int(m, 6)) is { } dmy)
            return (dmy, Cut(line, m));
        m = SyslogTime.Match(line);
        if (m.Success)
        {
            int month = DateTime.ParseExact(m.Groups[1].Value[..3], "MMM", CultureInfo.InvariantCulture).Month;
            if (TryDate(assumedYear, month, Int(m, 2), Int(m, 3), Int(m, 4), Int(m, 5)) is { } sys) return (sys, Cut(line, m));
        }
        return (null, line);
    }

    private static int Int(Match m, int group) => m.Groups[group].Success ? int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture) : 0;

    private static DateTime? TryDate(int y, int mo, int d, int h, int mi, int s)
    {
        if (y < 1990 || y > 2100 || mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo) || h > 23 || mi > 59 || s > 59) return null;
        return new DateTime(y, mo, d, h, mi, s, DateTimeKind.Unspecified);
    }

    private static string Cut(string line, Match m) => (line[..m.Index] + " " + line[(m.Index + m.Length)..]).Trim();

    // ---- measurements as sentences

    private static IEnumerable<HealthEvent> Measurements(HealthSnapshot s)
    {
        var now = s.CheckedAt;
        HealthEvent M(string source, Severity sev, string text) => new(now, source, sev, EventKind.Measurement, text);

        if (s.Ping is { } p)
        {
            if (p.Received == 0)
                yield return M("Ping", Severity.Problem, $"The camera did not answer any of {p.Sent} pings");
            else
                yield return M("Ping", p.LossPercent >= 2 ? Severity.Warning : Severity.Info,
                    $"The camera answered {p.Received} of {p.Sent} pings ({p.LossPercent:0.#}% lost), " +
                    $"average {Ms(p.AvgMs)}, slowest {Ms(p.MaxMs)}, jitter {Ms(p.JitterMs)}");
        }

        if (s.Path is { } t)
        {
            int n = t.Hops.Count;
            if (n <= 1 && t.ReachedTarget)
                yield return M("Path", Severity.Info, "The camera is on the same network as the laptop (no routers in between)");
            else
                yield return M("Path", t.ReachedTarget ? Severity.Info : Severity.Warning,
                    t.ReachedTarget ? $"The path to the camera passes {n - 1} router{(n - 1 == 1 ? "" : "s")}"
                                    : $"The path trace stopped after {n} hop{(n == 1 ? "" : "s")} without reaching the camera");
            foreach (var h in t.Hops.Where(h => h.Sent > 0 && h.LossPercent >= 2))
                yield return M("Path", Severity.Warning,
                    $"Hop {h.Ttl} ({h.Address?.ToString() ?? "no reply"}) lost {h.LossPercent:0.#}% of probes");
        }

        if (s.Onvif is { } o)
        {
            if (!o.Answered)
                yield return M("ONVIF", Severity.Warning, "The camera did not answer ONVIF requests");
            else
            {
                if (o.LoginAccepted == false)
                    yield return M("ONVIF", Severity.Warning, "The camera refused the ONVIF login" + (o.LoginError is { } e ? $" ({e})" : ""));
                if (o.Model != null)
                    yield return M("ONVIF", Severity.Info,
                        $"The camera says it is a {DiagnosisWriter.MakeAndModel(o.Manufacturer, o.Model)}" +
                        (o.Firmware != null ? $", firmware {o.Firmware}" : ""));
                if (o.ClockOffset is { } off)
                    yield return M("ONVIF", Math.Abs(off.TotalSeconds) > 120 ? Severity.Warning : Severity.Info,
                        Math.Abs(off.TotalSeconds) < 5 ? "The camera's clock matches the laptop's"
                            : $"The camera's clock is {Span(off.Duration())} {(off > TimeSpan.Zero ? "ahead of" : "behind")} the laptop's" +
                              (o.TimeSource != null ? $" (time set by {(o.TimeSource.Equals("NTP", StringComparison.OrdinalIgnoreCase) ? "NTP" : "hand")})" : ""));
                foreach (var prof in o.Profiles.Take(3))
                    yield return M("ONVIF", Severity.Info, $"Video profile \"{prof.Name ?? prof.Token}\": {ProfileText(prof)}");
                foreach (var u in o.Unsupported)
                    yield return M("ONVIF", Severity.Info, u);
            }
        }

        if (s.Stream is { } v)
        {
            if (!v.Opened)
                yield return M("Video", Severity.Problem, "The video stream could not be opened" + (v.Error is { } e ? $": {e}" : ""));
            else
                yield return M("Video", Severity.Info,
                    $"The video stream played for {v.SecondsWatched:0} s: {v.Codec?.ToUpperInvariant()} {v.Width}x{v.Height}, " +
                    $"{v.MeasuredFps:0.#} frames per second received" + (v.NominalFps is { } nf ? $" (stream says {nf:0.#})" : "") +
                    (v.BitrateKbps is { } br ? $", {Mbps(br)}" : "") +
                    (v.KeyframeInterval is { } k ? $", a key frame every {k:0.#} s" : "") +
                    (v.LongestGap is { } g && g > 0.5 ? $", longest pause {g:0.#} s" : ""));
        }

        if (s.Port is { } port)
        {
            yield return M("Switch", Severity.Info,
                $"Plugged into {port.Describe()}" +
                (port.SpeedMbps is { } sp ? $", link {(sp >= 1000 ? $"{sp / 1000.0:0.#} Gbit/s" : $"{sp} Mbit/s")}" : "") +
                (port.Duplex != null ? $" {port.Duplex} duplex" : "") +
                (port.MacsOnPort > 1 ? $", {port.MacsOnPort} devices learned on that port" : ""));
            if (port.FromDeviceKbps is { } from && port.ToDeviceKbps is { } to)
                yield return M("Switch", Severity.Info, $"The port carries {Mbps(from)} from the device and {Mbps(to)} to it");
            if (port.ErrorsDuringCheck is > 0 and var e)
                yield return M("Switch", Severity.Warning, $"The switch counted {e} damaged frames (CRC errors) from the device while it was watched");
        }

        foreach (var n in s.NotChecked)
            yield return M("Check", Severity.Info, n);
    }

    public static string ProfileText(OnvifProfile p) =>
        string.Join(", ", new[]
        {
            p.Encoding,
            p.Width is { } w && p.Height is { } h ? $"{w}x{h}" : null,
            p.FrameRate is { } f ? $"{f:0.#} fps" : null,
            p.BitrateKbps is { } b ? $"up to {Mbps(b)}" : null,
        }.Where(x => x != null));

    public static string Ms(double ms) => ms < 1 ? "under 1 ms" : $"{ms:0} ms";
    public static string Mbps(double kbps) => kbps >= 1000 ? $"{kbps / 1000:0.#} Mbit/s" : $"{kbps:0} kbit/s";

    public static string Span(TimeSpan t) =>
        t.TotalDays >= 2 ? $"{t.TotalDays:0} days"
        : t.TotalHours >= 2 ? $"{t.TotalHours:0} hours"
        : t.TotalMinutes >= 2 ? $"{t.TotalMinutes:0} minutes"
        : $"{t.TotalSeconds:0} seconds";

    public static bool IsAddress(string? s) => s != null && IPAddress.TryParse(s, out _);
}
