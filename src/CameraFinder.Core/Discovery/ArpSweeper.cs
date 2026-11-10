using System.Net;
using System.Runtime.InteropServices;
using CameraFinder.Core.Net;

namespace CameraFinder.Core.Discovery;

/// <summary>Asks "who has this IP?" on the local segment and returns the MAC, or null if nobody answered.</summary>
public interface IArpResolver
{
    bool IsSupported { get; }
    Task<string?> ResolveAsync(IPAddress target, IPAddress source, CancellationToken ct);
}

/// <summary>Uses the Windows SendARP API (iphlpapi.dll). No driver needed.</summary>
public sealed class WindowsArpResolver : IArpResolver
{
    public bool IsSupported => OperatingSystem.IsWindows();

    public Task<string?> ResolveAsync(IPAddress target, IPAddress source, CancellationToken ct) =>
        RunBlocking(() =>
        {
            ct.ThrowIfCancellationRequested();
            var mac = new byte[6];
            uint len = (uint)mac.Length;
            // SendARP expects addresses in network byte order, which is the in-memory order of the address bytes.
            uint dst = BitConverter.ToUInt32(target.GetAddressBytes(), 0);
            uint src = BitConverter.ToUInt32(source.GetAddressBytes(), 0);
            int rc = SendARP(dst, src, mac, ref len);
            return rc == 0 && len == 6 ? OuiDatabase.FormatMac(mac) : null;
        }, ct);

    /// <summary>
    /// Runs a blocking call (SendARP waits up to ~3 s for a missing host) on its own thread. Task.Run would park
    /// dozens of thread-pool threads, and the pool adds threads slowly, so the sweep would run far below its
    /// intended concurrency and starve the rest of the app's async work.
    /// </summary>
    public static Task<T> RunBlocking<T>(Func<T> call, CancellationToken ct) =>
        Task.Factory.StartNew(call, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint physAddrLen);
}

public static class ArpSweeper
{
    /// <summary>Largest range swept in one go. Bigger subnets are limited to the /24 around our own address.</summary>
    public const int MaxPrefixToSweepWhole = 22;

    /// <summary>The addresses that will be swept for a subnet we have <paramref name="ourAddress"/> in.</summary>
    public static Subnet SweepRange(Subnet subnet, IPAddress ourAddress) =>
        subnet.Prefix >= MaxPrefixToSweepWhole ? subnet : Subnet.Of(ourAddress, 24);

    /// <summary>
    /// ARPs every host in <paramref name="range"/> from <paramref name="source"/>. Every IPv4 device on the
    /// segment must answer ARP, even if it ignores ping, so this finds quiet devices.
    /// </summary>
    public static async Task<List<(IPAddress Ip, string Mac)>> SweepAsync(
        IArpResolver resolver,
        Subnet range,
        IPAddress source,
        IEnumerable<IPAddress> only,
        int concurrency,
        Action<int, int>? progress,
        CancellationToken ct)
    {
        var targets = only.Any() ? only.ToList() : range.Hosts().Where(h => !h.Equals(source)).ToList();
        var found = new List<(IPAddress, string)>();
        int done = 0;
        using var gate = new SemaphoreSlim(concurrency);

        var tasks = targets.Select(async ip =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var mac = await resolver.ResolveAsync(ip, source, ct);
                if (mac != null) lock (found) found.Add((ip, mac));
            }
            catch (OperationCanceledException) { throw; }
            catch { /* unreachable host */ }
            finally
            {
                gate.Release();
                progress?.Invoke(Interlocked.Increment(ref done), targets.Count);
            }
        });
        await Task.WhenAll(tasks);

        return RemoveProxyArp(found);
    }

    /// <summary>
    /// If one MAC answers for many different IPs, a router is answering on their behalf (proxy ARP),
    /// so those answers do not prove a device exists. Drop them.
    /// </summary>
    public static List<(IPAddress Ip, string Mac)> RemoveProxyArp(List<(IPAddress Ip, string Mac)> found, int threshold = 16)
    {
        var noisy = found.GroupBy(f => f.Mac).Where(g => g.Count() >= threshold).Select(g => g.Key).ToHashSet();
        return found.Where(f => !noisy.Contains(f.Mac)).OrderBy(f => Ipv4.ToUInt(f.Ip)).ToList();
    }
}
