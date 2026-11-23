using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace CameraFinder.Core.Net;

/// <summary>Gives the laptop an address in another range on the same cable, so it can talk to devices there.</summary>
public interface IAddressJoiner
{
    /// <summary>Adds a free address in <paramref name="subnet"/> to the adapter and returns it, or null.</summary>
    Task<IPAddress?> AddFreeAddressAsync(Adapter adapter, Subnet subnet, ISet<IPAddress> avoid, CancellationToken ct);
}

/// <summary>
/// Adds temporary extra IPv4 addresses to an adapter so the laptop can talk to devices in other ranges on the
/// same cable, and removes them again. Every change is written to a state file BEFORE it is made, so if the app
/// crashes the changes are undone on the next start (<see cref="CleanupLeftovers"/>).
///
/// Uses Windows' built-in netsh. Addresses are added with skipassource=true so normal traffic keeps using the
/// laptop's own address. On DHCP adapters, Windows' "DHCP/static coexistence" is switched on for the duration.
/// While a device's web page is open, one of them is made a main address for a <see cref="WebSession"/>.
/// Windows checks every new address for conflicts (duplicate address detection); if another device already
/// uses the address, it is removed straight away and another one is tried.
/// </summary>
public sealed class TempAddressManager : IAddressJoiner
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

    /// <summary>True while anything this app changed is still in place (addresses, coexistence, a web session).</summary>
    public bool HasChanges => _state.Addresses.Count + _state.CoexistenceEnabledOn.Count + _state.MainAddresses.Count + _state.Routes.Count > 0;

    /// <summary>The web page session in progress (network switched over to one device), or null.</summary>
    public WebSession? Session { get; private set; }

    public sealed record AddedAddress(string AdapterName, string Ip, string Mask);

    /// <summary>A route to one device, added for a web session (active only: never survives a restart).</summary>
    public sealed record AddedRoute(string AdapterName, string Target);

    private sealed class State
    {
        public List<AddedAddress> Addresses { get; set; } = new();
        /// <summary>Adapters where we turned on DHCP/static coexistence and must turn it off again.</summary>
        public List<string> CoexistenceEnabledOn { get; set; } = new();
        /// <summary>Temporary addresses switched to normal (main) addresses for a web session: switch back afterwards.</summary>
        public List<AddedAddress> MainAddresses { get; set; } = new();
        /// <summary>Routes added for a web session.</summary>
        public List<AddedRoute> Routes { get; set; } = new();
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

    /// <summary>
    /// Switches the laptop's network over to the device's network for its web page (see <see cref="WebSession"/>):
    /// the temporary address <paramref name="source"/> becomes a main address and a route to the device is added on
    /// <paramref name="adapter"/>. Ends any previous session first. Returns null if the address could not be switched.
    /// </summary>
    public async Task<WebSession?> StartWebSessionAsync(Adapter adapter, IPAddress source, IPAddress device, CancellationToken ct)
    {
        EndWebSession();
        if (!OperatingSystem.IsWindows()) { _log("Switching the network is only supported on Windows."); return null; }

        var temporary = _state.Addresses.FirstOrDefault(a => a.AdapterName == adapter.Name && a.Ip == source.ToString());
        bool switched = false;
        if (temporary != null)
        {
            _state.MainAddresses.Add(temporary);
            SaveState();
            if (!await Task.Run(() => SetSkipAsSource(temporary, skip: false), ct))
            {
                _state.MainAddresses.Remove(temporary);
                SaveState();
                _log($"Could not make {source} a main address on {adapter.Name}.");
                return null;
            }
            switched = true;
        }

        var route = new AddedRoute(adapter.Name, device.ToString());
        _state.Routes.Add(route);
        SaveState();
        var (ok, output) = await Task.Run(() => Netsh(
            $"interface ipv4 add route prefix={device}/32 interface=\"{adapter.Name}\" nexthop=0.0.0.0 metric=1 store=active"), ct);
        bool existed = !ok && output.Contains("already exists", StringComparison.OrdinalIgnoreCase);
        if (!ok)
        {
            // Not ours (it already existed) or not added: never remove it later. Without the route the session still
            // works unless another adapter uses the same range.
            _state.Routes.Remove(route);
            SaveState();
            if (!existed) _log($"Could not add a route to {device} on {adapter.Name}: {output}");
        }
        bool routed = ok || existed;

        Session = new WebSession(adapter.Name, source, device, Reach.RangeFor(device), switched, routed);
        _log($"Network switched to {Session.Range} for {device}: this laptop now uses {source} on {adapter.Name}" +
             (routed ? " and sends everything for the device through that adapter." : "."));
        return Session;
    }

    /// <summary>Puts the network back as it was before <see cref="StartWebSessionAsync"/>. Safe to call more than once.</summary>
    public void EndWebSession() => EndWebSession(addressesGoing: false);

    private void EndWebSession(bool addressesGoing)
    {
        bool any = _state.MainAddresses.Count + _state.Routes.Count > 0;
        foreach (var r in _state.Routes.ToList())
        {
            var (ok, output) = Netsh($"interface ipv4 delete route prefix={r.Target}/32 interface=\"{r.AdapterName}\" nexthop=0.0.0.0 store=active");
            if (!ok && !NotFound(output)) _log($"Could not remove the route to {r.Target}: {output}");
            _state.Routes.Remove(r);
            SaveState();
        }
        foreach (var a in _state.MainAddresses.ToList())
        {
            // An address that is about to be removed anyway does not need switching back first.
            bool stillThere = _state.Addresses.Contains(a);
            if (stillThere && !addressesGoing && !SetSkipAsSource(a, skip: true))
                _log($"Could not switch {a.Ip} back to a temporary address; it is removed when you close the app.");
            _state.MainAddresses.Remove(a);
            SaveState();
        }
        if (any && Session is { } s) _log($"Network back to normal (web session for {s.Device} ended).");
        Session = null;
    }

    /// <summary>Removes everything this app added. Safe to call more than once.</summary>
    public void RemoveAll()
    {
        EndWebSession(addressesGoing: true);
        foreach (var a in _state.Addresses.ToList()) Remove(a);
        foreach (var name in _state.CoexistenceEnabledOn.ToList())
        {
            Netsh($"interface ipv4 set interface interface=\"{name}\" dhcpstaticipcoexistence=disabled");
            _state.CoexistenceEnabledOn.Remove(name);
        }
        SaveState();
        try { if (!HasChanges && File.Exists(_stateFile)) File.Delete(_stateFile); } catch { }
    }

    /// <summary>Call at start-up: undoes changes left behind by a crash or power loss.</summary>
    public int CleanupLeftovers()
    {
        int n = _state.Addresses.Count + _state.CoexistenceEnabledOn.Count + _state.MainAddresses.Count + _state.Routes.Count;
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
        if (!ok && !NotFound(output))
            _log($"Could not remove {a.Ip} from {a.AdapterName}: {output}");
        _state.Addresses.Remove(a);
        SaveState();
    }

    private static bool NotFound(string output) => output.Contains("not found", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Turns Windows' skipassource flag of an address on or off without removing it (so connections from it stay up).
    /// netsh cannot change the flag in place, so PowerShell's Set-NetIPAddress is used; if that fails, the address is
    /// removed and added again with the new flag.
    /// </summary>
    private bool SetSkipAsSource(AddedAddress a, bool skip)
    {
        string alias = a.AdapterName.Replace("'", "''");
        var (ok, output) = Run("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " +
            $"\"Set-NetIPAddress -InterfaceAlias '{alias}' -IPAddress {a.Ip} -SkipAsSource ${(skip ? "true" : "false")} -ErrorAction Stop\"");
        if (ok) return true;
        _log($"PowerShell could not change {a.Ip} ({output}); adding it again instead.");
        Netsh($"interface ipv4 delete address name=\"{a.AdapterName}\" address={a.Ip}");
        (ok, output) = Netsh($"interface ipv4 add address name=\"{a.AdapterName}\" address={a.Ip} mask={a.Mask} skipassource={(skip ? "true" : "false")}");
        if (!ok) _log($"Could not add {a.Ip} again: {output}");
        return ok;
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

    private static (bool Ok, string Output) Netsh(string args) => Run("netsh", args);

    private static (bool Ok, string Output) Run(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
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
