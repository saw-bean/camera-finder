using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using CameraFinder.Core.Discovery;

namespace CameraFinder.Core.Health;

/// <summary>Reply to one probe sent with a limited TTL: from the target itself, or from the router where the TTL ran out.</summary>
public readonly record struct HopReply(IPAddress? From, bool ReachedTarget, long RoundTripMs, bool TimedOut);

public interface IHopProber
{
    Task<HopReply> ProbeAsync(IPAddress target, IPAddress? source, int ttl, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// ICMP echo with a TTL, like tracert / mtr. With a source address on Windows it uses IcmpSendEcho2Ex so the probe
/// leaves from a temporary address (added with skipassource) in the device's range.
/// </summary>
public sealed class IcmpHopProber : IHopProber
{
    private static readonly byte[] Payload = new byte[32];

    public async Task<HopReply> ProbeAsync(IPAddress target, IPAddress? source, int ttl, TimeSpan timeout, CancellationToken ct)
    {
        if (source != null && OperatingSystem.IsWindows())
            return await WindowsArpResolver.RunBlocking(() => FromWindows(target, source, ttl, timeout), ct);

        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(target, timeout, Payload, new PingOptions(ttl, dontFragment: false), ct);
            return reply.Status switch
            {
                IPStatus.Success => new HopReply(reply.Address, true, reply.RoundtripTime, false),
                IPStatus.TtlExpired or IPStatus.TimeExceeded => new HopReply(reply.Address, false, reply.RoundtripTime, false),
                _ => new HopReply(null, false, 0, true),
            };
        }
        catch (PingException) { return new HopReply(null, false, 0, true); }
        catch (PlatformNotSupportedException) { return new HopReply(null, false, 0, true); }
    }

    private const uint IpSuccess = 0, IpTtlExpiredTransit = 11013, IpTtlExpiredReassem = 11014;

    private static HopReply FromWindows(IPAddress target, IPAddress source, int ttl, TimeSpan timeout)
    {
        IntPtr handle = IcmpCreateFile();
        if (handle == new IntPtr(-1)) return new HopReply(null, false, 0, true);
        // ICMP_ECHO_REPLY (x64: 40 bytes incl. IP_OPTION_INFORMATION) + echoed data + room for an ICMP error + IO_STATUS_BLOCK.
        int replySize = 64 + Payload.Length + 8 + 16 + 64;
        IntPtr reply = Marshal.AllocHGlobal(replySize);
        IntPtr options = Marshal.AllocHGlobal(IntPtr.Size * 2);
        try
        {
            // IP_OPTION_INFORMATION: Ttl, Tos, Flags, OptionsSize (one byte each), then OptionsData (pointer).
            for (int i = 0; i < IntPtr.Size * 2; i++) Marshal.WriteByte(options, i, 0);
            Marshal.WriteByte(options, 0, (byte)Math.Clamp(ttl, 1, 255));
            uint src = BitConverter.ToUInt32(source.GetAddressBytes(), 0);
            uint dst = BitConverter.ToUInt32(target.GetAddressBytes(), 0);
            uint n = IcmpSendEcho2Ex(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, src, dst, Payload, (ushort)Payload.Length,
                options, reply, (uint)replySize, (uint)Math.Clamp(timeout.TotalMilliseconds, 1, 60000));
            if (n == 0)
            {
                // Some Windows versions report "TTL expired" as a failed call while still filling in the reply.
                int err = Marshal.GetLastWin32Error();
                if (err != IpTtlExpiredTransit && err != IpTtlExpiredReassem) return new HopReply(null, false, 0, true);
            }
            uint from = (uint)Marshal.ReadInt32(reply, 0);
            uint status = (uint)Marshal.ReadInt32(reply, 4);
            uint rtt = (uint)Marshal.ReadInt32(reply, 8);
            var addr = new IPAddress(from);
            return status switch
            {
                IpSuccess => new HopReply(addr, true, rtt, false),
                IpTtlExpiredTransit or IpTtlExpiredReassem => new HopReply(addr, false, rtt, false),
                _ => new HopReply(from == 0 ? null : addr, false, 0, from == 0),
            };
        }
        finally
        {
            Marshal.FreeHGlobal(options);
            Marshal.FreeHGlobal(reply);
            IcmpCloseHandle(handle);
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)] private static extern IntPtr IcmpCreateFile();
    [DllImport("iphlpapi.dll", SetLastError = true)] private static extern bool IcmpCloseHandle(IntPtr handle);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint IcmpSendEcho2Ex(IntPtr icmpHandle, IntPtr @event, IntPtr apcRoutine, IntPtr apcContext,
        uint sourceAddress, uint destinationAddress, byte[] requestData, ushort requestSize, IntPtr requestOptions,
        IntPtr replyBuffer, uint replySize, uint timeout);
}

/// <summary>Running per-hop totals, like one line of mtr.</summary>
public sealed class HopStats
{
    public HopStats(int ttl) => Ttl = ttl;
    public int Ttl { get; }
    public IPAddress? Address { get; private set; }
    public int Sent { get; private set; }
    public int Received { get; private set; }
    public long LastMs { get; private set; }
    public long BestMs { get; private set; } = long.MaxValue;
    public long WorstMs { get; private set; }
    public double AvgMs => Received == 0 ? 0 : _total / (double)Received;
    public double JitterMs => _jitterCount == 0 ? 0 : _jitterTotal / _jitterCount;
    private long _total;
    private double _jitterTotal;
    private int _jitterCount;

    public void Add(HopReply r)
    {
        Sent++;
        if (r.TimedOut || r.From == null) return;
        if (Received > 0) { _jitterTotal += Math.Abs(r.RoundTripMs - LastMs); _jitterCount++; }
        Received++;
        Address = r.From;
        LastMs = r.RoundTripMs;
        _total += r.RoundTripMs;
        BestMs = Math.Min(BestMs, r.RoundTripMs);
        WorstMs = Math.Max(WorstMs, r.RoundTripMs);
    }

    public Hop ToHop() => new(Ttl, Address, Sent, Received, LastMs, Received == 0 ? 0 : BestMs, AvgMs, WorstMs, JitterMs);
}

/// <summary>
/// mtr for Windows: finds every router on the way to a device, then keeps probing all of them each round, so loss and
/// delay can be pinned to the hop where they start. On a flat camera network the path is one hop (the device itself).
/// </summary>
public sealed class PathTracer
{
    public const int MaxHops = 30;
    private readonly IHopProber _prober;
    public PathTracer(IHopProber prober) => _prober = prober;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Runs rounds until <paramref name="rounds"/> are done (0 = until cancelled), reporting after each round.</summary>
    public async Task<TraceReport> RunAsync(IPAddress target, IPAddress? source, int rounds, Action<TraceReport>? onRound, CancellationToken ct)
    {
        var hops = new List<HopStats>();
        int length = MaxHops; // shrinks to the target's hop once it answers
        bool reached = false;
        int done = 0;
        while (!ct.IsCancellationRequested && (rounds == 0 || done < rounds))
        {
            var started = DateTime.UtcNow;
            int probeTo = Math.Min(length, MaxHops);
            var tasks = Enumerable.Range(1, probeTo).Select(ttl => SafeProbe(target, source, ttl, ct)).ToArray();
            HopReply[] replies;
            try { replies = await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { break; }

            int targetAt = Array.FindIndex(replies, r => r.ReachedTarget);
            if (targetAt >= 0) { reached = true; length = Math.Min(length, targetAt + 1); }
            else if (!reached && done == 0)
            {
                // First round and no answer from the target: stop at the last hop that replied (+1 so a silent last hop shows).
                int lastReply = Array.FindLastIndex(replies, r => r.From != null);
                length = Math.Min(MaxHops, Math.Max(1, lastReply + 2));
            }
            while (hops.Count < length) hops.Add(new HopStats(hops.Count + 1));
            if (hops.Count > length) hops.RemoveRange(length, hops.Count - length);
            for (int i = 0; i < length && i < replies.Length; i++) hops[i].Add(replies[i]);
            done++;
            var report = new TraceReport(target, hops.Select(h => h.ToHop()).ToList(), reached, done);
            onRound?.Invoke(report);

            if (rounds != 0 && done >= rounds) break;
            var wait = Interval - (DateTime.UtcNow - started);
            if (wait > TimeSpan.Zero)
            {
                try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { break; }
            }
        }
        return new TraceReport(target, hops.Select(h => h.ToHop()).ToList(), reached, done);
    }

    private async Task<HopReply> SafeProbe(IPAddress target, IPAddress? source, int ttl, CancellationToken ct)
    {
        try { return await _prober.ProbeAsync(target, source, ttl, Timeout, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new HopReply(null, false, 0, true); }
    }

    /// <summary>One plain sentence about a trace, for the bottom of the trace window.</summary>
    public static string Summary(TraceReport t)
    {
        if (t.Hops.Count == 0) return "No replies yet.";
        var last = t.Hops[^1];
        if (!t.ReachedTarget)
            return $"The device has not answered yet; the path was followed for {t.Hops.Count} hop{(t.Hops.Count == 1 ? "" : "s")}.";
        string path = t.Hops.Count == 1 ? "Same network as the laptop (no routers in between)." : $"{t.Hops.Count - 1} router{(t.Hops.Count == 2 ? "" : "s")} on the way.";
        if (last.Sent < 3) return path;
        if (last.LossPercent < 1)
            return $"{path} No loss to the device; average {LogNormalizer.Ms(last.AvgMs)}, jitter {LogNormalizer.Ms(last.JitterMs)}.";
        var start = t.Hops.Take(t.Hops.Count - 1).FirstOrDefault(h => h.Sent >= 3 && h.LossPercent >= last.LossPercent / 2 && t.Hops.SkipWhile(x => x != h).All(x => x.LossPercent >= 1));
        return start != null
            ? $"{path} {last.LossPercent:0.#}% loss to the device, starting at hop {start.Ttl} ({start.Address?.ToString() ?? "?"}): look there first."
            : $"{path} {last.LossPercent:0.#}% loss on the last link only: the device's cable, switch port or the device itself.";
    }
}
