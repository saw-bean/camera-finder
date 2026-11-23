using System.Net;
using System.Net.Sockets;

namespace CameraFinder.Core.Net;

/// <summary>
/// Connections sent from a chosen laptop address. Temporary addresses are added with skipassource, so Windows never
/// picks them by itself: a connection to a device in a joined range must bind to the temporary address explicitly,
/// or it leaves from the laptop's main address and the device's answer goes to its gateway instead of the laptop.
/// </summary>
public static class BoundSocket
{
    /// <summary>A TCP client bound to <paramref name="source"/> (any address when null).</summary>
    public static TcpClient Tcp(IPAddress? source) =>
        source == null ? new TcpClient(AddressFamily.InterNetwork) : new TcpClient(new IPEndPoint(source, 0));

    /// <summary>An HTTP handler whose connections leave from <paramref name="source"/> (normal when null).</summary>
    public static SocketsHttpHandler Http(IPAddress? source)
    {
        var handler = new SocketsHttpHandler();
        if (source != null)
            handler.ConnectCallback = async (ctx, ct) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    socket.Bind(new IPEndPoint(source, 0));
                    await socket.ConnectAsync(ctx.DnsEndPoint, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            };
        return handler;
    }
}
