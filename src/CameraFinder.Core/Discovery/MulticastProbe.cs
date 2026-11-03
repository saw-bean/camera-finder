using System.Net;
using System.Net.Sockets;

namespace CameraFinder.Core.Discovery;

/// <summary>Sends a UDP multicast/broadcast probe from one local address and collects the replies.</summary>
public static class MulticastProbe
{
    /// <summary>
    /// Runs one manufacturer protocol: binds its reply port when it has a fixed one (falls back to any port if the
    /// port is taken, e.g. by the vendor's own tool), joins its multicast group, sends by broadcast and/or multicast.
    /// </summary>
    public static async Task RunVendorAsync(
        IPAddress localAddress,
        VendorProtocol protocol,
        TimeSpan listenFor,
        Action<IPEndPoint, byte[]> onReply,
        CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.EnableBroadcast = true;
        try { udp.Client.Bind(new IPEndPoint(localAddress, protocol.ListenPort)); }
        catch (SocketException) when (protocol.ListenPort != 0) { udp.Client.Bind(new IPEndPoint(localAddress, 0)); }

        var destinations = new List<IPEndPoint>();
        if (protocol.Multicast != null)
        {
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, localAddress.GetAddressBytes());
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
            try { udp.JoinMulticastGroup(protocol.Multicast, localAddress); } catch (SocketException) { }
            destinations.Add(new IPEndPoint(protocol.Multicast, protocol.SendPort));
        }
        if (protocol.Broadcast) destinations.Add(new IPEndPoint(IPAddress.Broadcast, protocol.SendPort));

        var payload = protocol.Payload();
        foreach (var dest in destinations)
        {
            try { await udp.SendAsync(payload, payload.Length, dest); }
            catch (SocketException) { }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(listenFor);
        while (!timeout.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(timeout.Token); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }
            onReply(r.RemoteEndPoint, r.Buffer);
        }
    }

    public static async Task RunAsync(
        IPAddress localAddress,
        IPEndPoint destination,
        IEnumerable<byte[]> payloads,
        TimeSpan listenFor,
        Action<IPEndPoint, byte[]> onReply,
        CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.EnableBroadcast = true;
        udp.Client.Bind(new IPEndPoint(localAddress, 0));
        // Make sure multicast leaves through this adapter, not the default route.
        udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, localAddress.GetAddressBytes());
        udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);

        foreach (var payload in payloads)
        {
            try { await udp.SendAsync(payload, payload.Length, destination); }
            catch (SocketException) { return; } // adapter cannot send (e.g. no link)
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(listenFor);
        while (!timeout.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(timeout.Token); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; } // e.g. ICMP port unreachable on Windows
            onReply(r.RemoteEndPoint, r.Buffer);
        }
    }
}
