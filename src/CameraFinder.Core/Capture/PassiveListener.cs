using System.Reflection;
using System.Runtime.InteropServices;

namespace CameraFinder.Core.Capture;

/// <summary>
/// Listens to raw traffic on one adapter using Npcap (Windows) or libpcap (Linux), without sending anything.
/// Npcap is optional: when it is not installed, <see cref="IsAvailable"/> is false and the rest of the app still works.
/// </summary>
public static class PassiveListener
{
    private const string Lib = "wpcap";
    private const int ErrBufSize = 256;
    private static bool _resolverSet;
    private static readonly object _lock = new();

    public static string NpcapDownloadUrl => "https://npcap.com/#download";

    /// <summary>True when the capture library can be loaded.</summary>
    public static bool IsAvailable(out string? error)
    {
        EnsureResolver();
        try
        {
            _ = pcap_lib_version();
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            error = OperatingSystem.IsWindows()
                ? "Npcap is not installed. Passive listening is off; install Npcap from npcap.com to enable it."
                : "libpcap is not installed.";
            return false;
        }
    }

    /// <summary>Lists capture device names, e.g. "\Device\NPF_{GUID}" on Windows or "eth0" on Linux.</summary>
    public static List<string> ListDevices()
    {
        EnsureResolver();
        var names = new List<string>();
        var err = new byte[ErrBufSize];
        if (pcap_findalldevs(out IntPtr all, err) != 0 || all == IntPtr.Zero) return names;
        try
        {
            for (IntPtr p = all; p != IntPtr.Zero;)
            {
                var dev = Marshal.PtrToStructure<PcapIf>(p);
                if (dev.name != IntPtr.Zero) names.Add(Marshal.PtrToStringAnsi(dev.name)!);
                p = dev.next;
            }
        }
        finally { pcap_freealldevs(all); }
        return names;
    }

    /// <summary>
    /// Finds the capture device for an adapter. On Windows the Npcap name contains the adapter GUID;
    /// on Linux it is the interface name.
    /// </summary>
    public static string? FindDevice(string adapterId, string adapterName)
    {
        var devices = ListDevices();
        return devices.FirstOrDefault(d => d.Contains(adapterId, StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(d => d.Equals(adapterName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Captures frames until <paramref name="duration"/> passes or cancellation, reporting each observation.</summary>
    public static void Listen(string device, TimeSpan duration, CancellationToken ct, Action<Observation> onObservation)
    {
        EnsureResolver();
        var err = new byte[ErrBufSize];
        // snaplen 128 is enough for ARP and IP headers; promiscuous off - we only need broadcast/multicast and our own traffic.
        IntPtr handle = pcap_open_live(device, 128, 0, 250, err);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("Could not open capture device: " + ErrorText(err));

        try
        {
            var until = DateTime.UtcNow + duration;
            // pcap_pkthdr = struct timeval + caplen + len. timeval is two 32-bit values on Windows, two longs on 64-bit Unix.
            int caplenOffset = OperatingSystem.IsWindows() ? 8 : IntPtr.Size * 2;

            while (!ct.IsCancellationRequested && DateTime.UtcNow < until)
            {
                int rc = pcap_next_ex(handle, out IntPtr header, out IntPtr data);
                if (rc == 0) continue;      // timeout, loop to check time/cancel
                if (rc < 0) break;          // error or end
                int caplen = Marshal.ReadInt32(header, caplenOffset);
                if (caplen <= 0 || caplen > 65535) continue;
                var buffer = new byte[caplen];
                Marshal.Copy(data, buffer, 0, caplen);
                foreach (var obs in FrameParser.Parse(buffer)) onObservation(obs);
            }
        }
        finally { pcap_close(handle); }
    }

    private static string ErrorText(byte[] err)
    {
        int n = Array.IndexOf(err, (byte)0);
        return System.Text.Encoding.ASCII.GetString(err, 0, n < 0 ? err.Length : n);
    }

    private static void EnsureResolver()
    {
        lock (_lock)
        {
            if (_resolverSet) return;
            NativeLibrary.SetDllImportResolver(typeof(PassiveListener).Assembly, Resolve);
            _resolverSet = true;
        }
    }

    private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;

        if (OperatingSystem.IsWindows())
        {
            // Npcap installs into System32\Npcap. wpcap.dll needs Packet.dll from the same folder.
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Npcap");
            string dll = Path.Combine(dir, "wpcap.dll");
            if (File.Exists(dll))
            {
                SetDllDirectory(dir);
                if (NativeLibrary.TryLoad(dll, out var h)) return h;
            }
            return NativeLibrary.TryLoad("wpcap.dll", out var h2) ? h2 : IntPtr.Zero;
        }

        foreach (var candidate in new[] { "libpcap.so.1", "libpcap.so.0.8", "libpcap.so", "libpcap.dylib" })
            if (NativeLibrary.TryLoad(candidate, out var h)) return h;
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PcapIf
    {
        public IntPtr next;
        public IntPtr name;
        public IntPtr description;
        public IntPtr addresses;
        public uint flags;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport(Lib)] private static extern IntPtr pcap_lib_version();
    [DllImport(Lib)] private static extern int pcap_findalldevs(out IntPtr alldevs, byte[] errbuf);
    [DllImport(Lib)] private static extern void pcap_freealldevs(IntPtr alldevs);
    [DllImport(Lib, CharSet = CharSet.Ansi)] private static extern IntPtr pcap_open_live(string device, int snaplen, int promisc, int toMs, byte[] errbuf);
    [DllImport(Lib)] private static extern int pcap_next_ex(IntPtr p, out IntPtr header, out IntPtr data);
    [DllImport(Lib)] private static extern void pcap_close(IntPtr p);
}
