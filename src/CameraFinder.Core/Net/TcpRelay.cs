using System.Net;
using System.Net.Sockets;

namespace CameraFinder.Core.Net;

/// <summary>
/// A local TCP relay for programs that cannot choose the address they connect from (ffprobe): they connect to
/// 127.0.0.1:<see cref="LocalPort"/>, and the relay connects on to the device from <c>source</c> - the temporary
/// address in the device's range - and copies bytes both ways. Only accepts connections from this machine.
/// RTSP over TCP (interleaved) uses a single connection, so the whole session goes through the relay.
/// </summary>
public sealed class TcpRelay : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly IPEndPoint _target;
    private readonly IPAddress? _source;

    public int LocalPort { get; }
    /// <summary>Connections relayed so far (for tests and the log).</summary>
    public int Connections { get; private set; }

    public TcpRelay(IPEndPoint target, IPAddress? source)
    {
        _target = target;
        _source = source;
        _listener.Start();
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoop);
    }

    /// <summary><paramref name="url"/> pointed at the relay (host and port replaced, path and query kept).</summary>
    public string Rewrite(string url) => new UriBuilder(url) { Host = "127.0.0.1", Port = LocalPort }.Uri.ToString();

    private async Task AcceptLoop()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            TcpClient inbound;
            try { inbound = await _listener.AcceptTcpClientAsync(ct); }
            catch { return; }
            Connections++;
            _ = Task.Run(() => PipeAsync(inbound, ct), ct);
        }
    }

    private async Task PipeAsync(TcpClient inbound, CancellationToken ct)
    {
        using (inbound)
        using (var outbound = BoundSocket.Tcp(_source))
        {
            try
            {
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connect.CancelAfter(TimeSpan.FromSeconds(6));
                await outbound.ConnectAsync(_target, connect.Token);
                inbound.NoDelay = outbound.NoDelay = true;
                var a = inbound.GetStream();
                var b = outbound.GetStream();
                using var done = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var up = a.CopyToAsync(b, done.Token);
                var down = b.CopyToAsync(a, done.Token);
                await Task.WhenAny(up, down); // either side closed: close both
                done.Cancel();
            }
            catch { /* device unreachable or connection reset: the program sees its connection close */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _loop; } catch { }
        _cts.Dispose();
    }
}
