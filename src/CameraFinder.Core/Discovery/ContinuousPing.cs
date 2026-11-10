using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace CameraFinder.Core.Discovery;

/// <summary>Result of one echo request. <see cref="Status"/> is null on success, otherwise e.g. "Request timed out".</summary>
public readonly record struct PingResult(bool Success, long RoundTripMs, string? Status = null);

public interface IPinger
{
    /// <param name="source">Local address to send from, or null to let the OS choose.</param>
    Task<PingResult> PingAsync(IPAddress target, IPAddress? source, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// ICMP echo. On Windows with a <c>source</c> address it uses IcmpSendEcho2Ex, so the request leaves from that address:
/// temporary addresses are added with skipassource, which Windows would otherwise never pick as a source, and a device
/// in that range only answers a request from its own range.
/// </summary>
public sealed class IcmpPinger : IPinger
{
    private static readonly byte[] Payload = "CameraFinder ping"u8.ToArray();

    public async Task<PingResult> PingAsync(IPAddress target, IPAddress? source, TimeSpan timeout, CancellationToken ct)
    {
        if (source != null && OperatingSystem.IsWindows())
            return await WindowsArpResolver.RunBlocking(() => PingFromWindows(target, source, timeout), ct);

        using var ping = new Ping();
        try
        {
            // Linux/macOS only allow a custom payload to privileged users; the default payload works unprivileged.
            var reply = OperatingSystem.IsWindows()
                ? await ping.SendPingAsync(target, timeout, Payload, null, ct)
                : await ping.SendPingAsync(target, timeout, cancellationToken: ct);
            return reply.Status == IPStatus.Success
                ? new PingResult(true, reply.RoundtripTime)
                : new PingResult(false, 0, Describe(reply.Status));
        }
        catch (PingException ex) { return new PingResult(false, 0, ex.InnerException?.Message ?? ex.Message); }
        catch (PlatformNotSupportedException ex) { return new PingResult(false, 0, ex.Message); }
    }

    private static string Describe(IPStatus s) => s switch
    {
        IPStatus.TimedOut => "Request timed out",
        IPStatus.DestinationHostUnreachable => "Destination host unreachable",
        IPStatus.DestinationNetworkUnreachable => "Destination network unreachable",
        IPStatus.TtlExpired => "TTL expired in transit",
        _ => s.ToString(),
    };

    // ---- Windows: IcmpSendEcho2Ex with an explicit source address

    private const uint IpReqTimedOut = 11010;

    private static PingResult PingFromWindows(IPAddress target, IPAddress source, TimeSpan timeout)
    {
        IntPtr handle = IcmpCreateFile();
        if (handle == new IntPtr(-1)) return new PingResult(false, 0, "ICMP not available");
        int replySize = 64 + Payload.Length + 8 + 16; // ICMP_ECHO_REPLY + data + ICMP error + IO_STATUS_BLOCK
        IntPtr reply = Marshal.AllocHGlobal(replySize);
        try
        {
            uint src = BitConverter.ToUInt32(source.GetAddressBytes(), 0);   // network byte order
            uint dst = BitConverter.ToUInt32(target.GetAddressBytes(), 0);
            uint n = IcmpSendEcho2Ex(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, src, dst, Payload, (ushort)Payload.Length,
                IntPtr.Zero, reply, (uint)replySize, (uint)Math.Clamp(timeout.TotalMilliseconds, 1, 60000));
            if (n == 0)
            {
                int err = Marshal.GetLastWin32Error();
                return new PingResult(false, 0, err == IpReqTimedOut ? "Request timed out" : $"Ping failed (error {err})");
            }
            // ICMP_ECHO_REPLY: Address (4), Status (4), RoundTripTime (4), ...
            uint status = (uint)Marshal.ReadInt32(reply, 4);
            uint rtt = (uint)Marshal.ReadInt32(reply, 8);
            return status == 0 ? new PingResult(true, rtt) : new PingResult(false, 0, Describe((IPStatus)status));
        }
        finally
        {
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

/// <summary>Running totals for a continuous ping, plus up/down tracking for watching a device reboot.</summary>
public sealed class PingStats
{
    public int Sent { get; private set; }
    public int Received { get; private set; }
    public int Lost => Sent - Received;
    public double LossPercent => Sent == 0 ? 0 : 100.0 * Lost / Sent;
    public long MinMs { get; private set; } = long.MaxValue;
    public long MaxMs { get; private set; }
    public double AvgMs => Received == 0 ? 0 : _totalMs / (double)Received;
    /// <summary>Requests lost in a row right now.</summary>
    public int LostInARow { get; private set; }
    /// <summary>True while the device is considered down (<see cref="DownAfter"/> or more lost in a row).</summary>
    public bool IsDown { get; private set; }
    public DateTime? DownSince { get; private set; }
    public const int DownAfter = 3;

    private long _totalMs;

    /// <summary>Adds one result. Returns "DOWN" or "UP" when the device changes state, otherwise null.</summary>
    public string? Add(PingResult r, DateTime now)
    {
        Sent++;
        if (r.Success)
        {
            Received++;
            _totalMs += r.RoundTripMs;
            MinMs = Math.Min(MinMs, r.RoundTripMs);
            MaxMs = Math.Max(MaxMs, r.RoundTripMs);
            LostInARow = 0;
            if (IsDown) { IsDown = false; return "UP"; }
            return null;
        }
        if (LostInARow++ == 0) DownSince = now;
        if (!IsDown && LostInARow >= DownAfter && Received > 0) { IsDown = true; return "DOWN"; }
        return null;
    }

    public override string ToString() =>
        $"Sent {Sent}   Received {Received}   Lost {Lost} ({LossPercent:0.#}%)" +
        (Received > 0 ? $"   Min {MinMs} ms   Avg {AvgMs:0} ms   Max {MaxMs} ms" : "");
}

/// <summary>Pings one address every <c>interval</c> until cancelled, like "ping -t".</summary>
public static class ContinuousPing
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(1);

    /// <param name="onResult">Called after every request with the result, the totals and a state change ("DOWN"/"UP") or null.</param>
    public static async Task RunAsync(IPinger pinger, IPAddress target, IPAddress? source, TimeSpan interval, TimeSpan timeout,
        Action<PingResult, PingStats, string?> onResult, CancellationToken ct, Func<DateTime>? clock = null)
    {
        clock ??= () => DateTime.Now;
        var stats = new PingStats();
        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            PingResult r;
            try { r = await pinger.PingAsync(target, source, timeout, ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { r = new PingResult(false, 0, ex.Message); }
            if (ct.IsCancellationRequested) return;
            string? change = stats.Add(r, clock());
            onResult(r, stats, change);

            var wait = interval - (DateTime.UtcNow - started);
            if (wait > TimeSpan.Zero)
            {
                try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }
}
