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

    public DeviceKind Kind
    {
        get
        {
            if (Sources.HasFlag(DiscoverySource.Onvif) || OpenPorts.Contains(554)) return DeviceKind.Camera;
            if (WebUrl != null) return DeviceKind.WebDevice;
            return Mac != null ? DeviceKind.Other : DeviceKind.Unknown;
        }
    }

    /// <summary>Best human-readable description: model from ONVIF/SSDP, else web page title, else server header.</summary>
    public string Description =>
        FirstNonEmpty(Model, Name, WebTitle, Server) ?? "";

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    public override string ToString() => $"{Ip} {Mac} {Vendor} {Description}";
}
