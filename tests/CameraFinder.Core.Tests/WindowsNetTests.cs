using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CameraFinder.Core.Health;
using CameraFinder.Core.Net;

static partial class Tests
{
    // ---------- The real Windows network changes (netsh / PowerShell), run by CI on a Windows machine ----------

    /// <summary>
    /// Adds a virtual IP on the machine's real adapter, checks Windows keeps it out of normal traffic, switches the
    /// network over for a web session (Windows then sends to the device from the virtual IP), puts it back, reaches a
    /// public-looking address through a one-device route only, and removes everything - checking each step with
    /// PowerShell. Also checks that changes left by a crash are undone on the next start. No device is needed: the
    /// checks ask Windows which address and route it would use. Needs admin rights; only runs with NETSH_TEST=1.
    /// </summary>
    public static async Task TestRealWindowsVirtualIpAndWebSession()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("NETSH_TEST") != "1")
            throw new SkipException("NETSH_TEST=1 not set (CI sets it on its Windows machine)");

        var nic = NetworkInterface.GetAllNetworkInterfaces().First(n => n.OperationalStatus == OperationalStatus.Up
            && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
            && n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork));
        var props = nic.GetIPProperties();
        var adapter = new Adapter(nic.Id, nic.Name, nic.Description, props.GetIPv4Properties().Index, "", true, props.GetIPv4Properties().IsDhcpEnabled,
            props.UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork).Select(u => (u.Address, u.PrefixLength)).ToList(),
            props.GatewayAddresses.Select(g => g.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList());
        var main = adapter.Addresses.First().Address;
        Console.WriteLine($"     adapter {adapter.Name} ({adapter.Description}) {main}, DHCP {adapter.DhcpEnabled}");

        string dir = Path.Combine(Path.GetTempPath(), "cf-netsh-" + Guid.NewGuid().ToString("N")[..8]);
        var log = new List<string>();
        var mgr = new TempAddressManager(l => { lock (log) log.Add(l); Console.WriteLine("     | " + l); }, dir);
        var device = IPAddress.Parse("10.250.77.10");
        var publicDevice = IPAddress.Parse("11.250.0.64");
        try
        {
            // 1. Virtual IP near the device, kept out of normal traffic (skipassource).
            var ip = await mgr.AddFreeAddressAsync(adapter, Subnet.Parse("10.250.77.0/24"), new HashSet<IPAddress>(), CancellationToken.None, near: device);
            Assert.Equal("10.250.77.62", ip?.ToString(), "virtual IP added near the device: " + string.Join(" / ", log));
            Assert.Equal("True", await Ps($"(Get-NetIPAddress -IPAddress {ip}).SkipAsSource"), "skipassource");
            Assert.Equal("24", await Ps($"(Get-NetIPAddress -IPAddress {ip}).PrefixLength"));
            Assert.True(await Ps($"(Find-NetRoute -RemoteIPAddress {device} | Select-Object -First 1).IPAddress") != ip!.ToString(),
                "Windows does not pick the virtual IP by itself (that is why every check binds to it)");

            // 2. A connection bound to the virtual IP really leaves from it.
            var listener = new TcpListener(ip, 0);
            listener.Start();
            using (var client = BoundSocket.Tcp(ip))
            {
                var accept = listener.AcceptTcpClientAsync();
                await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
                using var server = await accept;
                Assert.Equal(ip.ToString(), ((IPEndPoint)server.Client.RemoteEndPoint!).Address.ToString(), "bound connection comes from the virtual IP");
            }
            listener.Stop();

            // 3. Web session: the browser now sends to the device from the virtual IP, over this adapter.
            var session = await mgr.StartWebSessionAsync(adapter, ip, device, CancellationToken.None);
            Assert.True(session is { SourceSwitched: true, RouteAdded: true }, "web session started: " + string.Join(" / ", log));
            Assert.Equal("False", await Ps($"(Get-NetIPAddress -IPAddress {ip}).SkipAsSource"), "virtual IP is a main address during the session");
            Assert.Equal(ip.ToString(), await Ps($"(Find-NetRoute -RemoteIPAddress {device} | Select-Object -First 1).IPAddress"), "browser would send from the virtual IP");
            Assert.Equal(nic.Name, await Ps($"(Find-NetRoute -RemoteIPAddress {device} | Select-Object -Last 1).InterfaceAlias"), "over the scan adapter");
            mgr.EndWebSession();
            Assert.Equal("True", await Ps($"(Get-NetIPAddress -IPAddress {ip}).SkipAsSource"), "back to a temporary address");
            Assert.Equal("", await Ps($"Get-NetRoute -DestinationPrefix {device}/32 -ErrorAction SilentlyContinue | Select-Object -ExpandProperty DestinationPrefix"), "session route removed");

            // 4. Public-looking device: a /32 address and a route for that one device; the rest of the range untouched.
            var src = await mgr.AddHostRouteAsync(adapter, publicDevice, new HashSet<IPAddress>(), CancellationToken.None);
            Assert.True(src != null && Subnet.Parse("11.250.0.0/24").Contains(src), "address for the public-looking device: " + string.Join(" / ", log));
            Assert.Equal("32", await Ps($"(Get-NetIPAddress -IPAddress {src}).PrefixLength"), "covers only itself");
            Assert.Equal($"{publicDevice}/32", await Ps($"Get-NetRoute -DestinationPrefix {publicDevice}/32 | Select-Object -First 1 -ExpandProperty DestinationPrefix"), "route to that device");
            string neighbour = await Ps("(Find-NetRoute -RemoteIPAddress 11.250.0.65 | Select-Object -First 1).IPAddress");
            Assert.True(neighbour != src!.ToString(), "the rest of 11.250.0.0/24 (the internet) is not sent from the new address: " + neighbour);
            Assert.True(Reach.SourceFor(mgr.WithHostRoutes(adapter), publicDevice)?.Equals(src) == true, "the app knows the address to use");
            var publicSession = await mgr.StartWebSessionAsync(adapter, src, publicDevice, CancellationToken.None);
            Assert.True(publicSession != null, "web session for the public-looking device");
            Assert.Equal(src.ToString(), await Ps($"(Find-NetRoute -RemoteIPAddress {publicDevice} | Select-Object -First 1).IPAddress"), "browser would send from it");
            mgr.EndWebSession();
            Assert.Equal($"{publicDevice}/32", await Ps($"Get-NetRoute -DestinationPrefix {publicDevice}/32 | Select-Object -First 1 -ExpandProperty DestinationPrefix"),
                "the device's own route stays after the session (it is removed with the addresses)");

            // 5. Everything removed.
            mgr.RemoveAll();
            foreach (var a in new[] { ip, src })
                Assert.Equal("", await Ps($"Get-NetIPAddress -IPAddress {a} -ErrorAction SilentlyContinue | Select-Object -ExpandProperty IPAddress"), $"{a} removed");
            Assert.Equal("", await Ps($"Get-NetRoute -DestinationPrefix {publicDevice}/32 -ErrorAction SilentlyContinue | Select-Object -ExpandProperty DestinationPrefix"), "route removed");
            Assert.True(!mgr.HasChanges, "nothing left");

            // 6. A crash leaves an address behind: the next start removes it.
            var left = await mgr.AddFreeAddressAsync(adapter, Subnet.Parse("10.250.78.0/24"), new HashSet<IPAddress>(), CancellationToken.None);
            Assert.True(left != null, "second address");
            var restarted = new TempAddressManager(_ => { }, dir);
            Assert.True(restarted.CleanupLeftovers() >= 1, "leftover found after the 'crash'"); // + the DHCP coexistence switch on DHCP adapters
            Assert.Equal("", await Ps($"Get-NetIPAddress -IPAddress {left} -ErrorAction SilentlyContinue | Select-Object -ExpandProperty IPAddress"), "leftover removed");
        }
        finally
        {
            mgr.RemoveAll();
            new TempAddressManager(_ => { }, dir).CleanupLeftovers();
        }
        // The machine's own network still works.
        Assert.True(adapter.Addresses.Any(), "adapter still has its address");
        Assert.True(await Ps($"(Get-NetIPAddress -IPAddress {main}).IPAddress") == main.ToString(), "main address untouched");
    }

    private static async Task<string> Ps(string command)
    {
        var r = await ProcessRunner.RunAsync("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", command }, TimeSpan.FromSeconds(60), CancellationToken.None);
        return r.StdOut.Trim();
    }
}
