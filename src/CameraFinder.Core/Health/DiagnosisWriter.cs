using System.Text;

namespace CameraFinder.Core.Health;

/// <summary>
/// Step 4 of the health check: explains the result the way a colleague would on the phone - the most likely problem
/// first, why we think so, what to try - instead of a table of every value measured.
/// </summary>
public static class DiagnosisWriter
{
    /// <summary>How many camera log events to retell at the end.</summary>
    public const int RecentEvents = 8;

    public static string Write(HealthSnapshot s, IReadOnlyList<HealthEvent> events, IReadOnlyList<Finding> findings)
    {
        var sb = new StringBuilder();
        string name = string.IsNullOrWhiteSpace(s.Description) ? s.Ip.ToString() : $"{s.Description} at {s.Ip}";
        sb.AppendLine($"{name} - checked {s.CheckedAt:ddd d MMM, HH:mm}");
        sb.AppendLine();

        var serious = findings.Where(x => x.Severity >= Severity.Warning).ToList();
        var minor = findings.Where(x => x.Severity < Severity.Warning).ToList();

        if (serious.Count == 0)
        {
            sb.AppendLine(HealthyLine(s));
        }
        else
        {
            var main = serious[0];
            sb.AppendLine(serious.Count == 1 ? $"In short: {Lower(main.Title)}." : $"In short: {Lower(main.Title)}, and {serious.Count - 1} more thing{(serious.Count == 2 ? "" : "s")} to look at.");
            sb.AppendLine();
            sb.AppendLine(main.Evidence);
            sb.AppendLine();
            sb.AppendLine("What to try first:");
            for (int i = 0; i < main.Advice.Count; i++) sb.AppendLine($"  {i + 1}. {main.Advice[i]}");

            foreach (var other in serious.Skip(1))
            {
                sb.AppendLine();
                sb.AppendLine($"Also: {other.Title}.");
                sb.AppendLine(other.Evidence);
                foreach (var a in other.Advice) sb.AppendLine($"  - {a}");
            }
        }

        foreach (var n in minor)
        {
            sb.AppendLine();
            sb.AppendLine($"Note: {n.Title}. {n.Evidence}");
            foreach (var a in n.Advice) sb.AppendLine($"  - {a}");
        }

        if (s.Delay is { } d && (d.TimeToPicture != null || d.BufferNeededMs != null || d.CameraDelay != null))
        {
            sb.AppendLine();
            sb.AppendLine("Live view delay, measured straight from the camera (no NVR in between):");
            foreach (var line in DelayLines(d)) sb.AppendLine("  - " + line);
        }

        var fine = WhatLooksFine(s, findings);
        if (fine.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("What looked fine: " + JoinSentence(fine) + ".");
        }

        var recent = events
            .Where(e => e.Source == "Camera log" && e.Kind is not (EventKind.Motion or EventKind.Other or EventKind.LoginOk))
            .Where(e => e.Time != null)
            .OrderByDescending(e => e.Time)
            .Take(RecentEvents)
            .ToList();
        if (recent.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Recently, according to the camera's own log:");
            foreach (var e in recent) sb.AppendLine($"  {e.Time:ddd d MMM HH:mm}  {e.Text}.");
        }

        if (s.NotChecked.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Not checked:");
            foreach (var n in s.NotChecked) sb.AppendLine($"  - {n}");
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    public static IEnumerable<string> DelayLines(DelayReport d)
    {
        if (d.TimeToPicture is { } ttp)
            yield return $"First picture after {ttp:0.#} s" + (d.ConnectSeconds is { } c ? $" (connecting {c:0.#} s, then waiting {ttp - c:0.#} s for a key frame)" : "") + ".";
        if (d.BufferNeededMs is { } b)
            yield return b < 150 ? $"Frames arrive evenly: a player needs only about {b:0} ms of buffer."
                                 : $"Frames arrive in bursts: a player needs about {b / 1000:0.##} s of buffer to play smoothly.";
        if (d.FallingBehindPerMinute is { } fb)
            yield return fb < 0.2 ? $"Keeping up with real time (watched {d.SecondsWatched / 60:0.#} min)." : $"Falling behind by {fb:0.#} s per minute.";
        else if (d.SecondsWatched > 0)
            yield return $"Whether delay builds up over time shows after 40 s of watching (now {d.SecondsWatched:0} s).";
        if (d.CameraDelay is { } cam)
            yield return $"Camera + network delay (flash test): {cam:0.##} s ({d.FlashesSeen} of {d.FlashesShown} flashes seen, {d.CameraDelayMin:0.##}-{d.CameraDelayMax:0.##} s).";
        else if (d.FlashesShown > 0)
            yield return $"The flash test did not see the flashes ({d.FlashesShown} shown): point the camera at the screen so the pattern fills most of the picture, and try again.";
        else
            yield return "Camera + network delay: not measured yet - press \"Measure camera delay\" and point the camera at the laptop screen.";
    }

    /// <summary>The events as a timeline, one plain sentence per line.</summary>
    public static string Timeline(IReadOnlyList<HealthEvent> events)
    {
        var sb = new StringBuilder();
        foreach (var e in events)
        {
            string mark = e.Severity switch { Severity.Problem => "!!", Severity.Warning => "! ", _ => "  " };
            sb.AppendLine($"{mark} {(e.Time is { } t ? t.ToString("yyyy-MM-dd HH:mm:ss") : "                   ")}  {e.Source,-10}  {e.Text}");
        }
        return sb.ToString();
    }

    /// <summary>Everything collected, close to how the device sent it (for support, or to check a diagnosis).</summary>
    public static string CollectedData(HealthSnapshot s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Device      {s.Ip}  {s.Mac}  {s.Vendor}  {s.Description}");
        sb.AppendLine($"Open ports  {string.Join(" ", s.OpenPorts)}");
        sb.AppendLine($"Checked     {s.CheckedAt:yyyy-MM-dd HH:mm:ss}");
        if (s.Ping is { } p)
        {
            sb.AppendLine();
            sb.AppendLine($"[Ping] sent {p.Sent}, received {p.Received}, loss {p.LossPercent:0.#}%, min/avg/max {p.MinMs}/{p.AvgMs:0.#}/{p.MaxMs} ms, jitter {p.JitterMs:0.#} ms");
            sb.AppendLine("  round trips (ms): " + string.Join(" ", p.RoundTrips));
        }
        if (s.Path is { } t)
        {
            sb.AppendLine();
            sb.AppendLine($"[Path] {t.Rounds} rounds, reached target: {(t.ReachedTarget ? "yes" : "no")}");
            sb.AppendLine("  hop  address          loss%  sent  last   avg  best  worst  jitter");
            foreach (var h in t.Hops)
                sb.AppendLine($"  {h.Ttl,3}  {h.Address?.ToString() ?? "???",-15}  {h.LossPercent,5:0.#}  {h.Sent,4}  {h.LastMs,4}  {h.AvgMs,4:0}  {h.BestMs,4}  {h.WorstMs,5}  {h.JitterMs,6:0.#}");
        }
        if (s.VideoLoginAccepted is { } vl)
        {
            sb.AppendLine();
            sb.AppendLine($"[Video login] {(vl ? "accepted" : "refused")} by the RTSP stream" +
                (s.Onvif?.LoginAccepted != null ? (s.VideoLoginSameAsOnvif ? " (same login as ONVIF)" : " (a different login from ONVIF)") : ""));
        }
        if (s.Onvif is { } o)
        {
            sb.AppendLine();
            sb.AppendLine($"[ONVIF] {o.ServiceUrl}  answered: {(o.Answered ? "yes" : "no")}  login: {o.LoginAccepted switch { true => "accepted", false => "refused - " + o.LoginError, null => "not used" }}");
            if (o.Model != null) sb.AppendLine($"  {o.Manufacturer} {o.Model}  firmware {o.Firmware}  serial {o.Serial}");
            if (o.CameraUtc != null) sb.AppendLine($"  camera time {o.CameraUtc:yyyy-MM-dd HH:mm:ss} UTC (laptop {o.LaptopUtc:HH:mm:ss} UTC), set by {o.TimeSource}, TZ {o.TimeZone}");
            foreach (var pr in o.Profiles) sb.AppendLine($"  profile {pr.Token} \"{pr.Name}\": {LogNormalizer.ProfileText(pr)}");
            if (o.StreamUri != null) sb.AppendLine($"  stream {o.StreamUri}");
            foreach (var u in o.Unsupported) sb.AppendLine($"  ({u})");
            if (o.SystemLog != null)
            {
                sb.AppendLine("  system log:");
                foreach (var line in o.SystemLog.Split('\n')) sb.AppendLine("    " + line.TrimEnd());
            }
        }
        if (s.Stream is { } v)
        {
            sb.AppendLine();
            sb.AppendLine($"[Video] {v.Url}  opened: {(v.Opened ? "yes" : "no - " + v.Error)}");
            if (v.Opened)
                sb.AppendLine($"  {v.Codec} {v.Width}x{v.Height}, nominal {v.NominalFps:0.##} fps, measured {v.MeasuredFps:0.##} fps over {v.SecondsWatched:0.#} s ({v.Frames} frames), " +
                              $"{v.BitrateKbps:0} kbit/s, key frame every {v.KeyframeInterval:0.##} s, longest gap {v.LongestGap:0.##} s");
        }
        if (s.Port is { } port)
        {
            sb.AppendLine();
            sb.AppendLine($"[Switch port] {port.Describe()}  ifIndex {port.IfIndex}");
            sb.AppendLine($"  link {(port.Up switch { true => "up", false => "down", null => "?" })}, speed {port.SpeedMbps} Mbit/s, duplex {port.Duplex ?? "?"}, " +
                          $"MACs on port {port.MacsOnPort}, from device {port.FromDeviceKbps:0} kbit/s, to device {port.ToDeviceKbps:0} kbit/s, errors while watched {port.ErrorsDuringCheck}");
            sb.AppendLine($"  switch: {port.Switch.Description}");
        }
        foreach (var n in s.NotChecked) sb.AppendLine("(" + n + ")");
        return sb.ToString();
    }

    private static string HealthyLine(HealthSnapshot s)
    {
        var checkedParts = new List<string>();
        if (s.Ping != null) checkedParts.Add("ping");
        if (s.Path != null) checkedParts.Add("the network path");
        if (s.Onvif is { Answered: true }) checkedParts.Add(s.Onvif.SystemLog != null ? "ONVIF and the camera's own log" : "ONVIF");
        if (s.Stream is { Opened: true }) checkedParts.Add("the video stream");
        return checkedParts.Count == 0
            ? "In short: nothing could be checked, so there is no verdict."
            : $"In short: no problems found. Checked {JoinSentence(checkedParts)}.";
    }

    private static List<string> WhatLooksFine(HealthSnapshot s, IReadOnlyList<Finding> findings)
    {
        bool Has(string code) => findings.Any(x => x.Code == code);
        var fine = new List<string>();
        if (s.Ping is { Received: > 0 } p && !Has("packet-loss") && !Has("latency"))
            fine.Add($"it answers ping in {LogNormalizer.Ms(p.AvgMs)} with {(p.LossPercent == 0 ? "no" : $"{p.LossPercent:0.#}%")} loss");
        if (s.Onvif is { Answered: true } o && o.LoginAccepted != false)
            fine.Add("ONVIF answers" + (o.Model != null ? $" ({MakeAndModel(o.Manufacturer, o.Model)}" + (o.Firmware != null ? $", firmware {o.Firmware})" : ")") : ""));
        if (s.Onvif?.ClockOffset is { } off && Math.Abs(off.TotalSeconds) < 120)
            fine.Add("its clock is right");
        if (s.Stream is { Opened: true } v && !Has("frame-drops") && !Has("stream-stall"))
            fine.Add($"video plays smoothly at {v.MeasuredFps:0.#} fps" + (v.Width != null ? $", {v.Width}x{v.Height}" : ""));
        if (s.Port is { Up: not false } port && !Has("half-duplex") && !Has("link-10m") && !Has("crc-errors"))
            fine.Add($"its switch port ({port.PortName ?? port.IfIndex.ToString()}) is clean" +
                     (port.SpeedMbps is { } sp ? $" at {(sp >= 1000 ? $"{sp / 1000.0:0.#} Gbit/s" : $"{sp} Mbit/s")}" : "") +
                     (port.Duplex == "full" ? " full duplex" : ""));
        if (s.Onvif?.SystemLog != null && !findings.Any(x => x.Code is "restart-loop" or "link-flap" or "failed-logins" or "locked"))
            fine.Add("the camera's log shows no restarts, link drops or login trouble worth worrying about");
        return fine;
    }

    /// <summary>"Axis" + "AXIS P3245-V" -> "AXIS P3245-V" (many cameras repeat the maker in the model).</summary>
    public static string MakeAndModel(string? make, string model) =>
        string.IsNullOrWhiteSpace(make) || model.StartsWith(make.Trim(), StringComparison.OrdinalIgnoreCase) ? model : $"{make.Trim()} {model}";

    private static string Lower(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    private static string JoinSentence(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => "",
        1 => parts[0],
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
    };
}
