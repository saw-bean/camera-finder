using System.Net;
using System.Net.Sockets;

namespace CameraFinder.Core.Discovery;

/// <summary>Sends a UDP multicast/broadcast probe from one local address and collects the replies.</summary>
public static class MulticastProbe
{
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
