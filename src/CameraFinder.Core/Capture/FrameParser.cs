using System.Buffers.Binary;
using System.Net;
using CameraFinder.Core.Net;

namespace CameraFinder.Core.Capture;

/// <summary>Something learned from one captured frame.</summary>
/// <param name="Ip">IPv4 address seen.</param>
/// <param name="Mac">MAC that owns <paramref name="Ip"/>, or null when the IP was only mentioned (e.g. the target of an ARP request).</param>
public readonly record struct Observation(IPAddress Ip, string? Mac);

/// <summary>
/// Parses raw Ethernet frames and pulls out addresses that are trustworthy for "who is on this cable":
/// ARP senders/targets, and IPv4 senders of broadcast/multicast frames (DHCP, SSDP, mDNS, ONVIF hello...).
/// Unicast IPv4 frames are ignored, because their source MAC may be a router rather than the device.
/// </summary>
public static class FrameParser
{
    private const ushort EtherTypeIPv4 = 0x0800;
    private const ushort EtherTypeArp = 0x0806;
    private const ushort EtherTypeVlan = 0x8100;

    public static List<Observation> Parse(ReadOnlySpan<byte> frame)
    {
        var result = new List<Observation>(2);
        if (frame.Length < 14) return result;

        var dstMac = frame[..6];
        int offset = 12;
        ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(frame[offset..]);
        offset += 2;
        while (etherType == EtherTypeVlan && frame.Length >= offset + 4)
        {
            etherType = BinaryPrimitives.ReadUInt16BigEndian(frame[(offset + 2)..]);
            offset += 4;
        }
        var payload = frame[offset..];

        if (etherType == EtherTypeArp) ParseArp(payload, result);
        else if (etherType == EtherTypeIPv4 && IsGroupAddress(dstMac)) ParseIPv4Source(frame[6..12], payload, result);

        return result;
    }

    private static void ParseArp(ReadOnlySpan<byte> arp, List<Observation> result)
    {
        // htype(2) ptype(2) hlen(1) plen(1) oper(2) sha(6) spa(4) tha(6) tpa(4)
        if (arp.Length < 28) return;
        if (BinaryPrimitives.ReadUInt16BigEndian(arp) != 1) return;            // Ethernet
        if (BinaryPrimitives.ReadUInt16BigEndian(arp[2..]) != 0x0800) return;  // IPv4
        if (arp[4] != 6 || arp[5] != 4) return;

        var senderMac = arp.Slice(8, 6);
        var senderIp = new IPAddress(arp.Slice(14, 4));
        var targetIp = new IPAddress(arp.Slice(24, 4));

        // ARP probes use sender 0.0.0.0 (a device checking its address is free): the target IP is the device's IP.
        if (senderIp.Equals(IPAddress.Any))
        {
            if (Ipv4.IsUsableHost(targetIp)) result.Add(new Observation(targetIp, OuiDatabase.FormatMac(senderMac)));
            return;
        }

        if (Ipv4.IsUsableHost(senderIp)) result.Add(new Observation(senderIp, OuiDatabase.FormatMac(senderMac)));
        // The target of a request tells us an address in use on this segment (often the gateway), without its MAC.
        if (Ipv4.IsUsableHost(targetIp) && !targetIp.Equals(senderIp)) result.Add(new Observation(targetIp, null));
    }

    private static void ParseIPv4Source(ReadOnlySpan<byte> srcMac, ReadOnlySpan<byte> ip, List<Observation> result)
    {
        if (ip.Length < 20 || (ip[0] >> 4) != 4) return;
        var src = new IPAddress(ip.Slice(12, 4));
        if (Ipv4.IsUsableHost(src)) result.Add(new Observation(src, OuiDatabase.FormatMac(srcMac)));
    }

    /// <summary>Broadcast or multicast destination MAC (group bit set).</summary>
    private static bool IsGroupAddress(ReadOnlySpan<byte> mac) => (mac[0] & 0x01) != 0;
}
