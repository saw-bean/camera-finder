using System.Globalization;
using System.Text.Json;

namespace CameraFinder.Core.Health;

/// <summary>One finished iperf3 test, as the laptop (the server) saw it.</summary>
public sealed record IperfResult(
    DateTime Finished, string? Client, string Protocol, bool Reverse, double Seconds, double Mbps,
    int? Retransmits, double? JitterMs, double? LostPercent, string? Error = null);

/// <summary>
/// Bandwidth test server: runs the bundled iperf3 in server mode so a second laptop (or an iperf3 app on a phone)
/// at the far end of the cable run - the camera's location, another switch, the NVR rack - can measure the link
/// to this laptop. Each test is run as a one-off server (-1) with JSON output, then the server is started again.
/// </summary>
public sealed class Iperf3Server
{
    public const int DefaultPort = 5201;
    private readonly string _exe;
    public Iperf3Server(string iperf3Path) => _exe = iperf3Path;

    public static List<string> Arguments(int port) => new() { "-s", "-1", "-J", "-p", port.ToString(CultureInfo.InvariantCulture) };

    /// <summary>Serves tests until cancelled. <paramref name="onResult"/> is called after each one.</summary>
    public async Task RunAsync(int port, Action<IperfResult> onResult, Action<string> onStatus, CancellationToken ct)
    {
        int failuresInARow = 0;
        while (!ct.IsCancellationRequested)
        {
            onStatus($"Waiting for a client on port {port}…");
            ProcessResult r;
            try { r = await ProcessRunner.RunAsync(_exe, Arguments(port), TimeSpan.FromDays(1), ct); }
            catch (OperationCanceledException) { return; }
            catch (System.ComponentModel.Win32Exception ex) { onStatus("iperf3 could not start: " + ex.Message); return; }

            var result = Parse(r.StdOut, DateTime.Now);
            if (result == null)
            {
                string err = r.StdErr.Trim();
                if (++failuresInARow >= 3)
                {
                    onStatus("iperf3 keeps stopping" + (err.Length > 0 ? ": " + err : "") + ". Is another iperf3 server already using the port?");
                    return;
                }
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct); } catch (OperationCanceledException) { return; }
                continue;
            }
            failuresInARow = 0;
            onResult(result);
        }
    }

    /// <summary>Reads iperf3's -J output for one test. Null if there is no test in it.</summary>
    public static IperfResult? Parse(string json, DateTime finished)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            if (!root.TryGetProperty("start", out var start)) return error == null ? null : new IperfResult(finished, null, "?", false, 0, 0, null, null, null, error);

            string? client = null;
            if (start.TryGetProperty("connected", out var conn) && conn.ValueKind == JsonValueKind.Array && conn.GetArrayLength() > 0)
                client = Str(conn[0], "remote_host");
            string protocol = "TCP";
            bool reverse = false;
            double seconds = 0;
            if (start.TryGetProperty("test_start", out var ts))
            {
                protocol = Str(ts, "protocol") ?? "TCP";
                reverse = Num(ts, "reverse") == 1;
                seconds = Num(ts, "duration") ?? 0;
            }
            if (!root.TryGetProperty("end", out var end)) return error == null ? null : new IperfResult(finished, client, protocol, reverse, seconds, 0, null, null, null, error);

            double bps; int? retr = null; double? jitter = null, lost = null;
            if (protocol.Equals("UDP", StringComparison.OrdinalIgnoreCase) && end.TryGetProperty("sum", out var sum))
            {
                bps = Num(sum, "bits_per_second") ?? 0;
                jitter = Num(sum, "jitter_ms");
                lost = Num(sum, "lost_percent");
                seconds = Num(sum, "seconds") ?? seconds;
            }
            else
            {
                // sum_sent / sum_received describe the sender and receiver of the test data (either direction).
                // What arrived at the receiver is the real throughput; older servers leave it empty, so fall back.
                bool hasRecv = end.TryGetProperty("sum_received", out var recv) && (Num(recv, "bits_per_second") ?? 0) > 0;
                if (!hasRecv && !end.TryGetProperty("sum_sent", out recv)) return null;
                bps = Num(recv, "bits_per_second") ?? 0;
                seconds = Num(recv, "seconds") ?? seconds;
                if (end.TryGetProperty("sum_sent", out var sent) && Num(sent, "retransmits") is { } rt) retr = (int)rt;
            }
            return new IperfResult(finished, client, protocol.ToUpperInvariant(), reverse, seconds, bps / 1e6, retr, jitter, lost, error);
        }
    }

    /// <summary>
    /// What the number means for a camera system, in plain words: which link speed it matches (100 Mbit/s instead of
    /// gigabit is a classic sign of a damaged pair) and how many cameras of a typical bitrate the link can carry.
    /// </summary>
    public static IReadOnlyList<string> Explain(IperfResult r)
    {
        var lines = new List<string>();
        if (r.Error != null) { lines.Add("The test failed: " + r.Error); return lines; }
        string dir = r.Reverse ? "from this laptop to " + (r.Client ?? "the client") : "from " + (r.Client ?? "the client") + " to this laptop";
        lines.Add($"{r.Protocol} test {dir}: {r.Mbps:0.#} Mbit/s over {r.Seconds:0} s.");

        if (r.Protocol == "TCP")
        {
            if (r.Mbps >= 850) lines.Add("That is a healthy gigabit link.");
            else if (r.Mbps >= 400) lines.Add("Faster than 100 Mbit/s but well short of gigabit: something on the path is busy or slow (an uplink shared with camera traffic, a slow laptop adapter, Wi-Fi).");
            else if (r.Mbps >= 80) lines.Add("This matches a 100 Mbit/s link. Fine for a camera port, but if this should be gigabit (an uplink, NVR port), a damaged pair or a coupler has dropped it to 100 Mbit/s.");
            else if (r.Mbps >= 40) lines.Add("Below what even a 100 Mbit/s link should give: look for errors on the switch ports or a half-duplex mismatch.");
            else if (r.Mbps >= 7) lines.Add("This looks like a 10 Mbit/s link or a badly damaged cable: re-terminate or replace it.");
            else lines.Add("The link is barely working: check the cable and both ports.");
            if (r.Retransmits is > 0 && r.Seconds > 0)
                lines.Add(r.Retransmits > 50 * r.Seconds
                    ? $"{r.Retransmits} retransmissions: packets are being lost on the way (cable, port errors or congestion)."
                    : $"{r.Retransmits} retransmissions: normal for a fast TCP test.");
        }
        else
        {
            if (r.LostPercent is { } lost)
                lines.Add(lost < 0.5 ? "No meaningful UDP loss - camera video (which uses UDP or RTP) will be clean."
                    : lost < 2 ? $"{lost:0.##}% UDP loss: video may show the odd smeared or grey frame."
                    : $"{lost:0.#}% UDP loss: video over this link will break up. Find the bad cable, port or overloaded uplink.");
            if (r.JitterMs is { } j)
                lines.Add(j < 5 ? $"Jitter {j:0.##} ms: steady." : $"Jitter {j:0.#} ms: delivery is uneven; the link is congested.");
        }

        double usable = r.Mbps * 0.7; // leave headroom: cameras burst on key frames and at night with noise
        if (usable >= 1)
            lines.Add($"Room for about {Math.Floor(usable / 4)} cameras at 4 Mbit/s (1080p H.264) or {Math.Floor(usable / 8)} at 8 Mbit/s (4K H.265), keeping 30% headroom.");
        return lines;
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static double? Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
