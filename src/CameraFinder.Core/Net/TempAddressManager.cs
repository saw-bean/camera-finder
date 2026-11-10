using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace CameraFinder.Core.Net;

/// <summary>
/// Adds temporary extra IPv4 addresses to an adapter so the laptop can talk to devices in other ranges on the
/// same cable, and removes them again. Every change is written to a state file BEFORE it is made, so if the app
/// crashes the changes are undone on the next start (<see cref="CleanupLeftovers"/>).
///
/// Uses Windows' built-in netsh. Addresses are added with skipassource=true so normal traffic keeps using the
/// laptop's own address. On DHCP adapters, Windows' "DHCP/static coexistence" is switched on for the duration.
/// Windows checks every new address for conflicts (duplicate address detection); if another device already
/// uses the address, it is removed straight away and another one is tried.
/// </summary>
public sealed class TempAddressManager
{
    private readonly string _stateFile;
    private readonly Action<string> _log;
    private State _state;

    public TempAddressManager(Action<string> log, string? stateDir = null)
    {
        _log = log;
        stateDir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CameraFinder");
        Directory.CreateDirectory(stateDir);
        _stateFile = Path.Combine(stateDir, "temporary-addresses.json");
        _state = LoadState();
    }

    public IReadOnlyList<AddedAddress> Added => _state.Addresses;

    public sealed record AddedAddress(string AdapterName, string Ip, string Mask);

    private sealed class State
    {
        public List<AddedAddress> Addresses { get; set; } = new();
        /// <summary>Adapters where we turned on DHCP/static coexistence and must turn it off again.</summary>
        public List<string> CoexistenceEnabledOn { get; set; } = new();
    }

    /// <summary>
    /// Adds a free address in <paramref name="subnet"/> to the adapter. Tries addresses from the top of the
    /// range downwards (.250, .249 ...), skipping any in <paramref name="avoid"/>. Returns the address, or null.
    /// </summary>
    public async Task<IPAddress?> AddFreeAddressAsync(Adapter adapter, Subnet subnet, ISet<IPAddress> avoid, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) { _log("Adding addresses is only supported on Windows."); return null; }

        if (adapter.DhcpEnabled && !_state.CoexistenceEnabledOn.Contains(adapter.Name))
        {
            _state.CoexistenceEnabledOn.Add(adapter.Name);
            SaveState();
            var (ok, output) = Netsh($"interface ipv4 set interface interface=\"{adapter.Name}\" dhcpstaticipcoexistence=enabled");
            if (!ok)
            {
                _state.CoexistenceEnabledOn.Remove(adapter.Name);
                SaveState();
                _log($"Could not enable DHCP/static coexistence on {adapter.Name} (needs Windows 10 2004 or later): {output}");
                return null;
            }
        }

        foreach (var candidate in Candidates(subnet).Where(c => !avoid.Contains(c)).Take(6))
        {
            ct.ThrowIfCancellationRequested();
            var mask = subnet.MaskAddress.ToString();
            var entry = new AddedAddress(adapter.Name, candidate.ToString(), mask);
            _state.Addresses.Add(entry);
            SaveState();

            var (ok, output) = Netsh(
                $"interface ipv4 add address name=\"{adapter.Name}\" address={candidate} mask={mask} skipassource=true");
            if (!ok)
            {
                _state.Addresses.Remove(entry);
                SaveState();
                _log($"Could not add {candidate}: {output}");
                return null;
            }

            var dad = await WaitForAddressAsync(adapter.Id, candidate, ct);
            if (dad == DuplicateAddressDetectionState.Duplicate)
            {
                _log($"{candidate} is already used by another device - trying another address.");
                Remove(entry);
                continue;
            }
            _log($"Joined {subnet} as {candidate} (temporary).");
            return candidate;
        }
        return null;
    }

    /// <summary>Removes everything this app added. Safe to call more than once.</summary>
    public void RemoveAll()
    {
        foreach (var a in _state.Addresses.ToList()) Remove(a);
        foreach (var name in _state.CoexistenceEnabledOn.ToList())
        {
            Netsh($"interface ipv4 set interface interface=\"{name}\" dhcpstaticipcoexistence=disabled");
            _state.CoexistenceEnabledOn.Remove(name);
        }
        SaveState();
        try { if (_state.Addresses.Count == 0 && File.Exists(_stateFile)) File.Delete(_stateFile); } catch { }
    }

    /// <summary>Call at start-up: undoes changes left behind by a crash or power loss.</summary>
    public int CleanupLeftovers()
    {
        int n = _state.Addresses.Count + _state.CoexistenceEnabledOn.Count;
        if (n > 0)
        {
            _log($"Removing {n} leftover network change(s) from a previous run.");
            RemoveAll();
        }
        return n;
    }

    private void Remove(AddedAddress a)
    {
        var (ok, output) = Netsh($"interface ipv4 delete address name=\"{a.AdapterName}\" address={a.Ip}");
        if (!ok && !output.Contains("not found", StringComparison.OrdinalIgnoreCase)
            && !output.Contains("Element not found", StringComparison.OrdinalIgnoreCase))
            _log($"Could not remove {a.Ip} from {a.AdapterName}: {output}");
        _state.Addresses.Remove(a);
        SaveState();
    }

    /// <summary>Candidate addresses from the top of the subnet down, avoiding the very last address (.254 is often a gateway).</summary>
    public static IEnumerable<IPAddress> Candidates(Subnet subnet)
    {
        if (Ipv4.IsLinkLocal(subnet.NetworkAddress))
        {
            // 169.254.0.0/16: pick from the middle of the range, away from the reserved first/last /24.
            var rnd = new Random();
            for (int i = 0; i < 20; i++)
                yield return Ipv4.FromUInt(0xA9FE0000u | (uint)rnd.Next(1, 254) << 8 | (uint)rnd.Next(10, 250));
            yield break;
        }
        if (subnet.Broadcast - subnet.Network < 6) yield break; // too small to leave room for devices
        uint top = subnet.Broadcast - 4; // .251 in a /24
        for (uint a = top; a > subnet.Network && top - a < 40; a--) yield return Ipv4.FromUInt(a);
    }

    private static async Task<DuplicateAddressDetectionState?> WaitForAddressAsync(string adapterId, IPAddress ip, CancellationToken ct)
    {
        DuplicateAddressDetectionState? last = null;
        for (int i = 0; i < 25; i++)
        {
            await Task.Delay(200, ct);
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id == adapterId);
            var ua = nic?.GetIPProperties().UnicastAddresses.FirstOrDefault(u => u.Address.Equals(ip));
            if (ua == null) continue;
            if (!OperatingSystem.IsWindows()) return DuplicateAddressDetectionState.Preferred;
            last = ua.DuplicateAddressDetectionState;
            if (last is DuplicateAddressDetectionState.Preferred or DuplicateAddressDetectionState.Duplicate) return last;
        }
        return last;
    }

    private static (bool Ok, string Output) Netsh(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            string output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
            p.WaitForExit(15000);
            return (p.ExitCode == 0, output);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private State LoadState()
    {
        try
        {
            if (File.Exists(_stateFile))
                return JsonSerializer.Deserialize<State>(File.ReadAllText(_stateFile)) ?? new State();
        }
        catch { }
        return new State();
    }

    private void SaveState()
    {
        try { File.WriteAllText(_stateFile, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { _log("Could not save state file: " + ex.Message); }
    }
}
