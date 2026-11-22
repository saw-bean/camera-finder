using System.Net;
using CameraFinder.Core.Net;

namespace CameraFinder.Core.Health;

/// <summary>
/// Step 3 of the health check: matches known failure patterns across the measurements and the camera's log.
/// Each pattern is something service technicians see again and again (restart loops from weak PoE, an NVR hammering
/// a camera with an old password, a clock that drifted because NTP is unreachable...). The most serious come first.
/// </summary>
public static class PatternDetector
{
    public static List<Finding> Detect(HealthSnapshot s, IReadOnlyList<HealthEvent> events)
    {
        var f = new List<Finding>();
        Reachability(s, f);
        PacketLoss(s, f);
        Latency(s, f);
        RestartLoop(events, f);
        LinkFlaps(events, f);
        Clock(s, events, f);
        Logins(s, events, f);
        Single(events, f, EventKind.IpConflict, "ip-conflict", Severity.Problem, "Another device is using this camera's IP address",
            "Find the other device (its MAC appears in the scan list under the same IP, or in the switch's MAC table) and give one of them a different address.",
            "Reserve camera addresses in the DHCP server, or keep static camera IPs outside the DHCP pool.");
        Single(events, f, EventKind.PowerProblem, "power", Severity.Problem, "The camera reported a power problem",
            "Check the PoE budget of the switch: heaters, IR and PTZ motors draw much more at night or in the cold.",
            "Try a different switch port or a PoE injector; replace long or thin (CCA) cable runs.");
        Single(events, f, EventKind.StorageError, "storage", Severity.Warning, "The camera's SD card or storage is failing",
            "Format the card in the camera's web page; if errors come back, replace it (use high-endurance cards rated for video).",
            "If recordings are on the NVR, this only affects the camera's local backup.");
        Single(events, f, EventKind.VideoLoss, "video-loss", Severity.Problem, "The camera lost its picture or reported a sensor fault",
            "Look at the live view: a black or frozen image after a restart points to a failing sensor board.",
            "Update the firmware; if it keeps happening the camera needs repair or replacement.");
        Single(events, f, EventKind.Overheat, "heat", Severity.Problem, "The camera is running too hot (or its fan failed)",
            "Check the housing is shaded and vents are clear; check the heater/blower setting on outdoor models.");
        Single(events, f, EventKind.LowMemory, "memory", Severity.Warning, "The camera ran low on memory",
            "Turn off analytics or extra streams you do not use, then restart the camera.",
            "Update the firmware: memory leaks are a common reason for firmware fixes.");
        Single(events, f, EventKind.Tamper, "tamper", Severity.Warning, "The camera reported tampering",
            "Check the view: the lens may be covered, sprayed, knocked out of position or out of focus.");
        Stream(s, f);
        Address(s, f);
        return f.OrderByDescending(x => x.Severity).ThenBy(x => Rank(x.Code)).ToList();
    }

    // Within the same severity: what stops the camera working, or gets worse by itself, comes first.
    private static readonly string[] Priority =
    {
        "offline", "services-down", "restart-loop", "locked", "ip-conflict", "power", "packet-loss", "stream-failed",
        "video-loss", "heat", "failed-logins", "onvif-login", "link-flap", "clock", "frame-drops", "stream-stall",
        "latency", "storage", "memory", "tamper", "not-activated", "link-local", "ntp", "long-gop", "ping-blocked",
    };

    private static int Rank(string code) { int i = Array.IndexOf(Priority, code); return i < 0 ? Priority.Length : i; }

    // ---- patterns

    private static void Reachability(HealthSnapshot s, List<Finding> f)
    {
        bool pingDead = s.Ping is { Received: 0, Sent: > 0 };
        bool onvifDead = s.Onvif is { Answered: false };
        bool streamDead = s.Stream is { Opened: false };
        if (pingDead && (s.Onvif == null || onvifDead) && (s.Stream == null || streamDead))
        {
            f.Add(new Finding("offline", Severity.Problem, "The camera is not answering at all",
                $"No reply to {s.Ping!.Sent} pings" + (onvifDead ? ", nor to ONVIF" : "") + (streamDead ? ", and the video stream would not open" : "") + ".",
                new[]
                {
                    "Check the switch port: is the link light on and is PoE being delivered?",
                    "Check the camera end: a loose RJ45, water in the connector or a failed PoE injector are the usual causes.",
                    "If the camera was reset or changed address, run a new scan: it may now be at its factory IP.",
                }));
            return;
        }
        if (pingDead && !onvifDead && s.Onvif != null)
            f.Add(new Finding("ping-blocked", Severity.Info, "The camera ignores ping but is otherwise reachable",
                "No reply to ping, but ONVIF answered. Many cameras have ping (ICMP) turned off.",
                new[] { "Nothing to fix. Use ONVIF or the web page to check it is up." }));
        if (!pingDead && s.Ping != null && onvifDead && (s.Stream == null || streamDead) && s.OpenPorts.Count == 0)
            f.Add(new Finding("services-down", Severity.Problem, "The network works, but the camera's services do not answer",
                "It answers ping, but not ONVIF" + (streamDead ? " or the video stream" : "") + ". The camera's software may be frozen.",
                new[]
                {
                    "Power-cycle the camera (unplug PoE for 10 seconds).",
                    "If this keeps happening, update the firmware.",
                }));
    }

    private static void PacketLoss(HealthSnapshot s, List<Finding> f)
    {
        if (s.Ping is not { Received: > 0 } p || p.LossPercent < 2) return;
        var sev = p.LossPercent >= 10 ? Severity.Problem : Severity.Warning;
        string where = "on the last link, between the switch and the camera";
        var advice = new List<string>
        {
            "Re-terminate or replace the patch cable and the camera's connector; test the run with a cable tester.",
            "Move the camera to another switch port to rule out a bad port.",
            "Check the switch port for errors (CRC / FCS) and that speed and duplex are both on auto.",
        };
        if (s.Path is { Hops.Count: > 1 } t)
        {
            var firstLossy = t.Hops.Take(t.Hops.Count - 1).FirstOrDefault(h => h.Sent > 0 && h.LossPercent >= Math.Max(2, p.LossPercent / 2));
            if (firstLossy != null)
            {
                where = $"from hop {firstLossy.Ttl} ({firstLossy.Address?.ToString() ?? "a router that did not reply"}) onwards, before the camera";
                advice.Insert(0, $"The loss starts at hop {firstLossy.Ttl}: check that router or the link into it before touching the camera.");
                advice.Insert(1, "Routers often drop probes addressed to themselves while still forwarding traffic; only loss that continues to the last hop is real.");
            }
        }
        f.Add(new Finding("packet-loss", sev, $"Packets are being lost ({p.LossPercent:0.#}%)",
            $"{p.Sent - p.Received} of {p.Sent} pings got no reply; the loss is {where}." +
            (s.IsCamera ? " Video uses a lot of bandwidth, so loss like this shows up as smearing, grey blocks or recordings with gaps." : ""),
            advice));
    }

    private static void Latency(HealthSnapshot s, List<Finding> f)
    {
        if (s.Ping is not { Received: >= 3 } p) return;
        bool local = s.Path is { Hops.Count: <= 1 } || s.Path == null;
        double slow = local ? 20 : 80, jittery = local ? 10 : 30;
        if (p.AvgMs < slow && p.JitterMs < jittery) return;
        f.Add(new Finding("latency", Severity.Warning,
            p.AvgMs >= slow ? "Replies are slow for a local network" : "Reply times jump around (jitter)",
            $"Average {LogNormalizer.Ms(p.AvgMs)}, slowest {LogNormalizer.Ms(p.MaxMs)}, jitter {LogNormalizer.Ms(p.JitterMs)}" +
            (local ? "; on the same network a camera normally answers in 1-2 ms." : "."),
            new[]
            {
                "The link may be overloaded: check the uplink between switches is not saturated by video from many cameras.",
                "A speed/duplex mismatch (one end forced to 100 Mbit full duplex, the other on auto) causes exactly this.",
                "A camera whose processor is busy (many streams, analytics) answers slowly too: check its CPU load in the web page.",
            }));
    }

    private static void RestartLoop(IReadOnlyList<HealthEvent> events, List<Finding> f)
    {
        var starts = events.Where(e => e.Kind is EventKind.Restart or EventKind.Crash && e.Time != null).Select(e => e.Time!.Value).OrderBy(t => t).ToList();
        int crashes = events.Count(e => e.Kind == EventKind.Crash);
        int firmware = events.Count(e => e.Kind == EventKind.Firmware);
        int inDay = MaxInWindow(starts, TimeSpan.FromHours(24));
        int inHour = MaxInWindow(starts, TimeSpan.FromHours(1));
        if (inDay < 3 && inHour < 2 && crashes == 0) return;
        if (crashes == 0 && inDay < 3 && firmware > 0) return; // restarts explained by a firmware update

        bool linkTrouble = events.Any(e => e.Kind is EventKind.LinkDown or EventKind.PowerProblem);
        var advice = new List<string>();
        string title, cause;
        if (crashes > 0 && crashes * 2 >= starts.Count)
        {
            title = "The camera keeps crashing and restarting itself";
            cause = $"Its log shows {crashes} crash or watchdog restart{(crashes == 1 ? "" : "s")}. That is a software fault, not the network.";
            advice.Add("Update the firmware to the latest version for this model.");
            advice.Add("Turn off features you do not use (extra streams, analytics, cloud/P2P), then watch whether it stays up.");
            advice.Add("If it still crashes after a factory reset and the latest firmware, it needs repair.");
        }
        else
        {
            title = "The camera keeps restarting - most likely it is losing power";
            cause = $"Its log shows {starts.Count} start-ups, {Math.Max(inDay, inHour)} of them within {(inHour >= 2 && inDay < 3 ? "one hour" : "one day")}" +
                    (linkTrouble ? ", together with network link drops or power warnings" : "") + ". " +
                    "When a camera starts up again and again without a crash in its log, the power is usually dropping out.";
            advice.Add("Check the switch's PoE budget and the port's power class: IR at night, heaters in the cold and PTZ motors all draw extra.");
            advice.Add("Try the camera on a PoE injector or another port; if it stays up, the switch port or budget is the problem.");
            advice.Add("Check the cable: long runs (near 100 m), copper-clad aluminium (CCA) cable or a corroded connector drop too much voltage.");
        }
        if (starts.Count > 0)
            cause += $" Last start-up: {starts[^1]:ddd d MMM HH:mm}.";
        f.Add(new Finding("restart-loop", Severity.Problem, title, cause, advice));
    }

    private static void LinkFlaps(IReadOnlyList<HealthEvent> events, List<Finding> f)
    {
        int downs = events.Count(e => e.Kind == EventKind.LinkDown);
        if (downs < 2) return;
        f.Add(new Finding("link-flap", Severity.Warning, "The network cable keeps dropping",
            $"The camera's log shows the link going down {downs} times.",
            new[]
            {
                "Re-terminate the connectors at both ends; outdoor connectors often let water in.",
                "Test the cable run with a cable tester, and try another switch port.",
                "If the drops line up with restarts, it is a power problem rather than the cable (see above).",
            }));
    }

    private static void Clock(HealthSnapshot s, IReadOnlyList<HealthEvent> events, List<Finding> f)
    {
        bool ntpErrors = events.Any(e => e.Kind == EventKind.TimeSyncFailed);
        if (s.Onvif?.ClockOffset is not { } off)
        {
            if (ntpErrors)
                f.Add(new Finding("ntp", Severity.Warning, "The camera cannot reach its time server",
                    "Its log shows NTP failures.",
                    new[] { "Point the camera's NTP setting at the NVR or a server it can reach on this network (cameras often have no internet access)." }));
            return;
        }
        double secs = Math.Abs(off.TotalSeconds);
        if (secs < 120) return;
        bool ntp = string.Equals(s.Onvif.TimeSource, "NTP", StringComparison.OrdinalIgnoreCase);
        var advice = new List<string>();
        if (ntp || ntpErrors)
            advice.Add("NTP is on but not working: point it at the NVR or a time server the camera can reach (cameras often have no internet).");
        else
            advice.Add("Turn on NTP in the camera, pointing at the NVR or the site's time server, so the clock stays right.");
        advice.Add("Wrong camera time makes recordings hard to find and video evidence questionable.");
        if (secs > 300)
            advice.Add("ONVIF logins check the time too: a clock this far off can make the NVR fail to log in to the camera.");
        advice.Add("Make sure the laptop's own clock is right before trusting this comparison.");
        f.Add(new Finding("clock", secs > 3600 ? Severity.Problem : Severity.Warning,
            $"The camera's clock is {LogNormalizer.Span(off.Duration())} {(off > TimeSpan.Zero ? "fast" : "slow")}",
            $"Camera time {s.Onvif.CameraUtc:yyyy-MM-dd HH:mm:ss} UTC vs laptop {s.Onvif.LaptopUtc:HH:mm:ss} UTC; time is set {(ntp ? "by NTP" : "by hand")}" +
            (ntpErrors ? ", and its log shows NTP failures." : "."),
            advice));
    }

    private static void Logins(HealthSnapshot s, IReadOnlyList<HealthEvent> events, List<Finding> f)
    {
        if (events.Any(e => e.Kind == EventKind.AccountLocked))
            f.Add(new Finding("locked", Severity.Problem, "A user account on the camera got locked",
                "The log shows the camera locking an account after repeated wrong passwords.",
                new[]
                {
                    "Find what is using the old password (usually the NVR/VMS, or a phone app) and update it there first.",
                    "Then unlock the account, or wait for the lock to time out (often 30 minutes), or restart the camera.",
                }));

        var failed = events.Where(e => e.Kind == EventKind.LoginFailed).ToList();
        if (failed.Count >= 5)
        {
            var bySource = failed.Where(e => e.From != null).GroupBy(e => e.From!).OrderByDescending(g => g.Count()).ToList();
            string who = bySource.Count == 0 ? ""
                : " Most came from " + string.Join(", ", bySource.Take(3).Select(g => $"{g.Key} ({g.Count()}x)")) + ".";
            bool external = bySource.Any(g => IPAddress.TryParse(g.Key, out var a) && !Ipv4.IsPrivate(a));
            var advice = new List<string>();
            if (bySource.Count == 1)
                advice.Add($"One address keeps trying: that is usually an NVR, VMS or phone app still using an old password. Update it on {bySource[0].Key}.");
            else
                advice.Add("Check the NVR/VMS and any phone apps for an old saved password.");
            if (external)
                advice.Add("Some attempts come from public (internet) addresses: the camera is exposed. Remove the port forward on the router and change the password.");
            advice.Add("Many cameras lock the account after a few failures, which then looks like the camera 'going offline' in the NVR.");
            f.Add(new Finding("failed-logins", external ? Severity.Problem : Severity.Warning,
                $"Wrong passwords are being tried ({failed.Count} refused logins)", "The camera's log shows repeated refused logins." + who, advice));
        }

        if (s.Onvif is { LoginAccepted: false } o)
        {
            var advice = new List<string>
            {
                "Check the user name and password (they are case-sensitive).",
                "Many brands need ONVIF turned on and a separate ONVIF user created in the web page (e.g. Hikvision: Integration Protocol; Axis: ONVIF users).",
            };
            if (o.ClockOffset is { } off && Math.Abs(off.TotalMinutes) > 5)
                advice.Insert(0, "The camera's clock is far off, which alone can make ONVIF logins fail. Fix the time first.");
            f.Add(new Finding("onvif-login", Severity.Warning, "The camera refused the ONVIF login",
                (o.LoginError ?? "Login refused") + ". Without it the camera's log and stream address cannot be read.", advice));
        }
    }

    private static void Stream(HealthSnapshot s, List<Finding> f)
    {
        if (s.Stream is not { } v) return;
        if (!v.Opened)
        {
            if (s.Ping is { Received: 0 }) return; // already covered by "not answering at all"
            string err = v.Error ?? "";
            var (title, advice) = err switch
            {
                _ when Has(err, "401", "Unauthorized", "authorization") => ("The video stream refused the login",
                    new[] { "Check the user name and password; the RTSP login is usually the camera's web login.", "Some cameras need RTSP authentication set to 'digest' or 'basic' to match the NVR." }),
                _ when Has(err, "404", "Not Found", "Invalid data") => ("The video stream address is wrong",
                    new[] { "The stream path differs by brand and firmware; use the one ONVIF reports, or the manufacturer's RTSP URL guide." }),
                _ when Has(err, "refused") => ("The camera refuses video connections (RTSP off or on another port)",
                    new[] { "Check RTSP is enabled and its port (554 by default) in the camera's network settings." }),
                _ when Has(err, "timed out", "timeout", "Connection timed") => ("The video stream timed out",
                    new[] { "A firewall or VLAN rule may block RTSP (554).", "The camera may be at its stream limit: close other viewers or NVR connections and try again." }),
                _ when Has(err, "453", "Not Enough Bandwidth", "503", "Service Unavailable") => ("The camera has no free video streams",
                    new[] { "Too many clients are pulling video: the NVR, VMS, phone apps. Close some, or use a sub-stream for extra viewers." }),
                _ => ("The video stream could not be opened",
                    new[] { "Open the live view in the web page: if that works too, check the RTSP settings; if not, restart the camera." }),
            };
            f.Add(new Finding("stream-failed", Severity.Problem, title, $"Opening {v.Url} failed: {Short(err)}.", advice));
            return;
        }

        if (v.Frames < 2) return;
        double? nominal = v.NominalFps ?? s.Onvif?.Profiles.FirstOrDefault()?.FrameRate;
        if (nominal is > 0 && v.MeasuredFps is { } fps && fps < nominal * 0.8 && v.SecondsWatched >= 3)
            f.Add(new Finding("frame-drops", Severity.Warning, "The video is dropping frames",
                $"Only {fps:0.#} of {nominal:0.#} frames per second arrived over {v.SecondsWatched:0} s.",
                new[]
                {
                    "If the ping also lost packets, fix the cable or port first.",
                    "Lower the bitrate or resolution, or turn off extra streams and analytics, if the camera is overloaded.",
                    "At night, many cameras lower the frame rate on purpose (slow shutter / WDR): check the exposure settings.",
                }));
        if (v.LongestGap is { } gap && gap >= 1.5)
            f.Add(new Finding("stream-stall", Severity.Warning, "The video freezes for moments",
                $"The longest pause between two frames was {gap:0.#} s.",
                new[] { "Short freezes usually mean packet loss or an overloaded link; check the ping result and the uplinks." }));
        if (v.KeyframeInterval is { } k && k > 4.5)
            f.Add(new Finding("long-gop", Severity.Notice, "Key frames are far apart",
                $"A key frame (I-frame) arrives only every {k:0.#} s. Live view is slow to start and the NVR can only jump to those points.",
                new[] { $"Set the I-frame interval to about twice the frame rate (e.g. {Math.Round((nominal ?? 25) * 2)}) unless you use smart codec on purpose to save storage." }));
    }

    private static void Address(HealthSnapshot s, List<Finding> f)
    {
        if (Ipv4.IsLinkLocal(s.Ip))
            f.Add(new Finding("link-local", Severity.Warning, "The camera has a self-assigned address (169.254.x.x)",
                "It looked for a DHCP server and found none, so the NVR will not find it at its normal address.",
                new[] { "Give it a static IP in the site's camera range, or check the DHCP server on this network." }));
        if (s.Description?.Contains("NOT ACTIVATED", StringComparison.OrdinalIgnoreCase) == true)
            f.Add(new Finding("not-activated", Severity.Warning, "The camera has not been activated yet",
                "Hikvision cameras do nothing until a password is set.",
                new[] { "Activate it with SADP or the web page (set a strong admin password), then add it to the NVR." }));
    }

    private static void Single(IReadOnlyList<HealthEvent> events, List<Finding> f, EventKind kind, string code, Severity sev, string title, params string[] advice)
    {
        var hits = events.Where(e => e.Kind == kind).ToList();
        if (hits.Count == 0) return;
        var last = hits.LastOrDefault(e => e.Time != null)?.Time;
        f.Add(new Finding(code, sev, title,
            $"The camera's log mentions it {(hits.Count == 1 ? "once" : $"{hits.Count} times")}" + (last != null ? $", last on {last:ddd d MMM HH:mm}." : "."),
            advice));
    }

    /// <summary>Most items within any window of the given length (items sorted ascending).</summary>
    public static int MaxInWindow(IReadOnlyList<DateTime> sorted, TimeSpan window)
    {
        int best = 0, start = 0;
        for (int end = 0; end < sorted.Count; end++)
        {
            while (sorted[end] - sorted[start] > window) start++;
            best = Math.Max(best, end - start + 1);
        }
        return best;
    }

    private static bool Has(string s, params string[] any) => any.Any(a => s.Contains(a, StringComparison.OrdinalIgnoreCase));
    private static string Short(string s) => string.IsNullOrWhiteSpace(s) ? "no reason given" : s.Length > 160 ? s[..160] + "…" : s;
}
