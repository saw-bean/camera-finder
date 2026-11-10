using System.Net;

namespace CameraFinder.Core.Model;

/// <summary>How a device was found. A device can be found several ways.</summary>
[Flags]
public enum DiscoverySource
{
    None = 0,
    Passive = 1,   // heard on the wire (ARP / broadcast) without asking
    ArpSweep = 2,  // answered an ARP request during the range sweep
    Onvif = 4,     // answered an ONVIF WS-Discovery probe
    Ssdp = 8,      // answered a UPnP / SSDP search
    Vendor = 16,   // answered a manufacturer discovery protocol (Hikvision SADP, Dahua, Hanwha, Axis, ...)
    WsDiscovery = 32, // answered WS-Discovery but is not ONVIF (Windows PCs, WSD printers)
}

public enum DeviceKind
{
    Unknown,
    Camera,        // ONVIF video device or RTSP port open
    WebDevice,     // has a web page, type not known
    Other,
}

/// <summary>One device found on the network. Updated in place as more information arrives.</summary>
public sealed class Device
{
    public Device(IPAddress ip) => Ip = ip;

    public IPAddress Ip { get; }
    public string? Mac { get; set; }
    public string? Vendor { get; set; }
    public string? Model { get; set; }
    public string? Name { get; set; }
    public string? WebTitle { get; set; }
    public string? Server { get; set; }
    public DiscoverySource Sources { get; set; }
    public SortedSet<int> OpenPorts { get; } = new();
    public string? WebUrl { get; set; }

    /// <summary>Manufacturer discovery protocols that found the device, e.g. "Hikvision SADP".</summary>
    public SortedSet<string> Protocols { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set when a camera/recorder-only protocol (ONVIF or a camera maker's own) found the device.</summary>
    public bool CameraProtocol { get; set; }

    // The scan engine updates a device from several threads while holding lock(device); the readers below take the
    // same lock so the UI can show a device mid-scan without "collection was modified" errors.

    /// <summary>How it was found, for the list and CSV: "ArpSweep, Onvif, Hikvision SADP".</summary>
    public string FoundBy
    {
        get
        {
            lock (this)
            {
                var parts = Enum.GetValues<DiscoverySource>()
                    .Where(s => s != DiscoverySource.None && s != DiscoverySource.Vendor && Sources.HasFlag(s))
                    .Select(s => s.ToString())
                    .Concat(Protocols);
                return string.Join(", ", parts);
            }
        }
    }

    /// <summary>Open ports as text, e.g. "80 443 554".</summary>
    public string OpenPortsText
    {
        get { lock (this) return string.Join(" ", OpenPorts); }
    }

    public DeviceKind Kind
    {
        get
        {
            lock (this)
            {
                if (Sources.HasFlag(DiscoverySource.Onvif) || CameraProtocol || OpenPorts.Contains(554)) return DeviceKind.Camera;
                if (WebUrl != null) return DeviceKind.WebDevice;
                return Mac != null ? DeviceKind.Other : DeviceKind.Unknown;
            }
        }
    }

    /// <summary>Best human-readable description: model from ONVIF/SSDP, else web page title, else server header.</summary>
    public string Description =>
        FirstNonEmpty(Model, Name, WebTitle, Server) ?? "";

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>
    /// True when <paramref name="query"/> identifies this device: its IP ("10.0.5.21"), its full MAC in any format
    /// ("00:40:8C:12:34:56", "00408c123456"), or the end of its MAC as printed on a camera label (at least 4 hex digits, "3456").
    /// </summary>
    public bool Matches(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        string q = query.Trim();
        if (q.Count(c => c == '.') == 3)
            return IPAddress.TryParse(q, out var ip) && ip.Equals(Ip);
        if (!q.All(c => Uri.IsHexDigit(c) || c is ':' or '-' or ' ')) return false;
        string hex = new string(q.Where(Uri.IsHexDigit).ToArray());
        string? mac = Mac == null ? null : new string(Mac.Where(Uri.IsHexDigit).ToArray());
        if (mac == null || hex.Length < 4 || hex.Length > 12) return false;
        return mac.EndsWith(hex, StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => $"{Ip} {Mac} {Vendor} {Description}";
}
