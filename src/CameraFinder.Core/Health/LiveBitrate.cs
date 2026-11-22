using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace CameraFinder.Core.Health;

/// <summary>One second of a camera's video stream, as it arrived.</summary>
public readonly record struct LiveSample(DateTime Time, double Kbps, int Frames, bool KeyFrame, int LostPackets);

/// <summary>Running totals of a live stream measurement.</summary>
public sealed class LiveStats
{
    public const int HistorySeconds = 120;
    private readonly List<LiveSample> _history = new();
    private readonly List<DateTime> _keyFrames = new();
    private long _packets, _lost;
    private double _totalKbps;
    private int _seconds, _totalFrames, _longestGapMs;

    public string? Codec { get; set; }
    /// <summary>Frame-by-frame timing for the live view delay checks.</summary>
    public DelayTracker Delay { get; } = new();
    public IReadOnlyList<LiveSample> History { get { lock (_history) return _history.ToList(); } }
    public double CurrentKbps { get { lock (_history) return _history.Count == 0 ? 0 : _history[^1].Kbps; } }
    public double AverageKbps => _seconds == 0 ? 0 : _totalKbps / _seconds;
    public double PeakKbps { get; private set; }
    public double Fps { get { lock (_history) return _history.Count == 0 ? 0 : _history.TakeLast(5).Average(s => s.Frames); } }
    public int Seconds => _seconds;
    public int TotalFrames => _totalFrames;
    public double LossPercent => _packets == 0 ? 0 : 100.0 * _lost / (_packets + _lost);
    public double LongestGap => _longestGapMs / 1000.0;

    /// <summary>Average seconds between key frames, once two have been seen.</summary>
    public double? KeyframeInterval
    {
        get
        {
            lock (_keyFrames)
                return _keyFrames.Count < 2 ? null : (_keyFrames[^1] - _keyFrames[0]).TotalSeconds / (_keyFrames.Count - 1);
        }
    }

    internal void AddKeyFrame(DateTime t) { lock (_keyFrames) { _keyFrames.Add(t); if (_keyFrames.Count > 60) _keyFrames.RemoveAt(0); } }
    internal void AddPackets(int received, int lost) { _packets += received; _lost += lost; }
    internal void AddGap(int ms) { if (ms > _longestGapMs) _longestGapMs = ms; }

    public void Add(LiveSample s)
    {
        lock (_history)
        {
            _history.Add(s);
            if (_history.Count > HistorySeconds) _history.RemoveAt(0);
        }
        // The first second is partial (the stream started mid-second); do not let it set the peak.
        _seconds++;
        _totalKbps += s.Kbps;
        _totalFrames += s.Frames;
        if (_seconds > 1) PeakKbps = Math.Max(PeakKbps, s.Kbps);
    }

    /// <summary>The measurement so far, in the form the health check's patterns understand.</summary>
    public StreamReport ToReport(string url, double? nominalFps) => new StreamReport
    {
        Url = url, Opened = true, Codec = Codec, NominalFps = nominalFps, SecondsWatched = _seconds,
        Frames = _totalFrames, MeasuredFps = _seconds < 2 ? null : (double)_totalFrames / _seconds,
        BitrateKbps = AverageKbps, KeyframeInterval = KeyframeInterval, LongestGap = LongestGap,
    };
}

public interface ILiveStreamSource
{
    /// <summary>Plays the stream until cancelled, reporting every second. Throws <see cref="LiveStreamException"/> when it cannot play.</summary>
    Task RunAsync(string url, Credentials? login, IPAddress? source, LiveStats stats, Action<LiveSample> onSecond, CancellationToken ct);
}

public sealed class LiveStreamException(string message, bool loginRefused = false) : Exception(message)
{
    public bool LoginRefused { get; } = loginRefused;
}

/// <summary>
/// Measures the bandwidth a camera's video stream really uses, second by second. A small RTSP client of its own
/// (DESCRIBE, SETUP with RTP over the RTSP TCP connection, PLAY) reads the packets as they arrive - no buffering, so
/// the number is live - and can connect from a temporary address in the camera's range. Counts bytes on the wire,
/// frames (RTP marker bit), key frames (H.264 / H.265 IDR) and packets the camera skipped (RTP sequence gaps).
/// </summary>
public sealed class RtspLiveSource : ILiveStreamSource
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    public async Task RunAsync(string url, Credentials? login, IPAddress? source, LiveStats stats, Action<LiveSample> onSecond, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "rtsp") throw new LiveStreamException("Not an RTSP address");
        // A typed-in address may carry its own login: rtsp://user:pass@host/path.
        if (login is not { IsEmpty: false } && uri.UserInfo.Length > 0)
        {
            var parts = uri.UserInfo.Split(':', 2);
            login = new Credentials(Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
        }
        string plainUrl = FfprobeStreamProber.Redact(url);
        stats.Delay.Started(DelayTracker.Now());
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true, ReceiveBufferSize = 1 << 20 };
        if (source != null) socket.Bind(new IPEndPoint(source, 0));
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(ConnectTimeout);
            try { await socket.ConnectAsync(uri.Host, uri.Port > 0 ? uri.Port : 554, connect.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new LiveStreamException("Connection timed out"); }
            catch (SocketException ex) { throw new LiveStreamException(ex.SocketErrorCode == SocketError.ConnectionRefused ? "Connection refused (RTSP is off or on another port)" : ex.Message); }
        }
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        var rtsp = new RtspSession(stream, login);

        var describe = await rtsp.RequestAsync("DESCRIBE", plainUrl, new() { ["Accept"] = "application/sdp" }, ct);
        var sdp = Sdp.Parse(describe.Body);
        var video = sdp.Video ?? throw new LiveStreamException("The stream has no video");
        stats.Codec = video.Codec;
        string baseUrl = describe.Headers.GetValueOrDefault("content-base") ?? describe.Headers.GetValueOrDefault("content-location") ?? plainUrl;
        string control = Sdp.Resolve(baseUrl, video.Control);

        var setup = await rtsp.RequestAsync("SETUP", control, new() { ["Transport"] = "RTP/AVP/TCP;unicast;interleaved=0-1" }, ct);
        string session = (setup.Headers.GetValueOrDefault("session") ?? "").Split(';')[0].Trim();
        int channel = InterleavedChannel(setup.Headers.GetValueOrDefault("transport") ?? "") ?? 0;
        var withSession = new Dictionary<string, string> { ["Session"] = session };
        string playUrl = Sdp.Resolve(baseUrl, sdp.SessionControl);
        await rtsp.RequestAsync("PLAY", playUrl, new() { ["Session"] = session, ["Range"] = "npt=0.000-" }, ct);
        stats.Delay.Playing(DelayTracker.Now());

        try { await ReadLoopAsync(stream, rtsp, channel, video.Codec, video.ClockRate ?? 90000, baseUrl, withSession, stats, onSecond, ct); }
        finally
        {
            try { await rtsp.SendOnlyAsync("TEARDOWN", baseUrl, withSession); } catch { /* best effort */ }
        }
    }

    private static async Task ReadLoopAsync(NetworkStream stream, RtspSession rtsp, int channel, string? codec, int clockRate, string url,
        Dictionary<string, string> session, LiveStats stats, Action<LiveSample> onSecond, CancellationToken ct)
    {
        var reader = new FrameReader(stream);
        var second = DateTime.UtcNow;
        var lastKeepAlive = DateTime.UtcNow;
        long bytes = 0;
        int frames = 0, lost = 0, received = 0;
        bool key = false;
        int frameBytes = 0;
        bool frameKey = false;
        int? lastSeq = null;
        DateTime? lastFrameAt = null;
        bool hevc = codec?.StartsWith("H265", StringComparison.OrdinalIgnoreCase) == true || codec?.StartsWith("HEVC", StringComparison.OrdinalIgnoreCase) == true;

        while (!ct.IsCancellationRequested)
        {
            var (ch, payload, length) = await reader.NextAsync(ReadTimeout, ct);
            var now = DateTime.UtcNow;
            if (ch == channel && length >= 12)
            {
                bytes += length + 4; // RTP packet + 4-byte interleave header: what crossed the wire
                received++;
                int seq = (payload[2] << 8) | payload[3];
                if (lastSeq is { } prev)
                {
                    int gap = (seq - prev - 1) & 0xFFFF;
                    if (gap is > 0 and < 1000) lost += gap;
                }
                lastSeq = seq;
                int header = 12 + 4 * (payload[0] & 0x0F);
                if ((payload[0] & 0x10) != 0 && length >= header + 4) header += 4 + 4 * ((payload[header + 2] << 8) | payload[header + 3]);
                frameBytes += length;
                if (length > header && IsKeyFrame(payload.AsSpan(header, length - header), hevc)) key = frameKey = true;
                if ((payload[1] & 0x80) != 0) // marker: last packet of a frame
                {
                    uint rtpTime = (uint)(payload[4] << 24 | payload[5] << 16 | payload[6] << 8 | payload[7]);
                    stats.Delay.AddFrame(DelayTracker.Now(), rtpTime, clockRate, frameBytes, frameKey);
                    frameBytes = 0;
                    frameKey = false;
                    frames++;
                    if (lastFrameAt is { } lf) stats.AddGap((int)(now - lf).TotalMilliseconds);
                    lastFrameAt = now;
                }
            }

            if (now - second >= TimeSpan.FromSeconds(1))
            {
                double elapsed = (now - second).TotalSeconds;
                if (key) stats.AddKeyFrame(now);
                stats.AddPackets(received, lost);
                var sample = new LiveSample(DateTime.Now, bytes * 8 / elapsed / 1000, (int)Math.Round(frames / elapsed), key, lost);
                stats.Add(sample);
                onSecond(sample);
                bytes = 0; frames = 0; lost = 0; received = 0; key = false;
                second = now;
            }
            if (now - lastKeepAlive >= TimeSpan.FromSeconds(25))
            {
                // Cameras end the session after ~60 s without RTSP traffic; RTCP alone is not enough for all of them.
                await rtsp.SendOnlyAsync("GET_PARAMETER", url, session);
                lastKeepAlive = now;
            }
        }
    }

    /// <summary>Does this RTP payload start (or contain) a key frame? Handles single NAL units, STAP-A and FU-A/FU.</summary>
    public static bool IsKeyFrame(ReadOnlySpan<byte> p, bool hevc)
    {
        if (p.Length < 2) return false;
        if (!hevc)
        {
            int type = p[0] & 0x1F;
            if (type is 5 or 7) return true;                                    // IDR slice / SPS
            if (type == 28 && (p[1] & 0x80) != 0) return (p[1] & 0x1F) == 5;  // FU-A start of an IDR
            if (type == 24)                                                    // STAP-A: walk the units
            {
                int i = 1;
                while (i + 2 < p.Length)
                {
                    int len = (p[i] << 8) | p[i + 1];
                    if (i + 2 < p.Length && (p[i + 2] & 0x1F) is 5 or 7) return true;
                    i += 2 + len;
                }
            }
            return false;
        }
        int t = (p[0] >> 1) & 0x3F;
        if (t is >= 16 and <= 21 or 32 or 33) return true;                     // IRAP pictures / VPS / SPS
        if (t == 49 && p.Length >= 3 && (p[2] & 0x80) != 0) return ((p[2] & 0x3F) is >= 16 and <= 21);
        if (t == 48) return true;                                              // aggregation packet: VPS/SPS/PPS bundle precedes IDR
        return false;
    }

    public static int? InterleavedChannel(string transport)
    {
        var m = System.Text.RegularExpressions.Regex.Match(transport, @"interleaved=(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Reads "$" interleaved packets and skips RTSP replies (to keep-alives) that arrive in between.</summary>
    private sealed class FrameReader
    {
        private readonly NetworkStream _s;
        private readonly byte[] _buf = new byte[65536 + 4];
        public FrameReader(NetworkStream s) => _s = s;

        public async Task<(int Channel, byte[] Payload, int Length)> NextAsync(TimeSpan timeout, CancellationToken ct)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            try
            {
                while (true)
                {
                    await ReadExactly(_buf, 1, limit.Token);
                    if (_buf[0] == (byte)'$')
                    {
                        await ReadExactly(_buf, 3, limit.Token);
                        int ch = _buf[0], len = (_buf[1] << 8) | _buf[2];
                        var payload = new byte[len];
                        await ReadExactly(payload, len, limit.Token);
                        return (ch, payload, len);
                    }
                    if (_buf[0] == (byte)'R') { await SkipRtspReply(limit.Token); continue; }
                    // Anything else: resynchronise on the next '$'.
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new LiveStreamException("The camera stopped sending video"); }
        }

        private async Task SkipRtspReply(CancellationToken ct)
        {
            var line = new StringBuilder("R");
            int contentLength = 0;
            while (true)
            {
                await ReadExactly(_buf, 1, ct);
                char c = (char)_buf[0];
                if (c == '\n')
                {
                    string l = line.ToString().TrimEnd('\r');
                    if (l.Length == 0) break;
                    if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(l[15..].Trim(), out contentLength);
                    line.Clear();
                }
                else if (line.Length < 2048) line.Append(c);
            }
            if (contentLength is > 0 and < 65536) await ReadExactly(_buf, contentLength, ct);
        }

        private async Task ReadExactly(byte[] buf, int count, CancellationToken ct)
        {
            int got = 0;
            while (got < count)
            {
                int n = await _s.ReadAsync(buf.AsMemory(got, count - got), ct);
                if (n == 0) throw new LiveStreamException("The camera closed the video connection");
                got += n;
            }
        }
    }
}

/// <summary>Minimal RTSP request/response over one TCP connection, with Basic and Digest login.</summary>
internal sealed class RtspSession
{
    private readonly NetworkStream _s;
    private readonly Credentials? _login;
    private int _cseq;
    private string? _auth; // "Basic ..." or digest parameters
    private Dictionary<string, string>? _digest;

    public RtspSession(NetworkStream s, Credentials? login) { _s = s; _login = login; }

    public sealed record Response(int Status, string Reason, Dictionary<string, string> Headers, string Body);

    public async Task<Response> RequestAsync(string method, string url, Dictionary<string, string> headers, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await WriteAsync(method, url, headers);
            var resp = await ReadResponseAsync(ct);
            if (resp.Status == 401 && attempt == 0 && _login is { IsEmpty: false } && resp.Headers.TryGetValue("www-authenticate", out var challenge))
            {
                SetChallenge(challenge);
                continue;
            }
            if (resp.Status == 401) throw new LiveStreamException(_login is { IsEmpty: false } ? "401 Unauthorized: the camera refused the login" : "401 Unauthorized: the stream needs a login", loginRefused: true);
            if (resp.Status != 200) throw new LiveStreamException($"{method} failed: {resp.Status} {resp.Reason}");
            return resp;
        }
        throw new LiveStreamException("401 Unauthorized: the camera refused the login", loginRefused: true);
    }

    public Task SendOnlyAsync(string method, string url, Dictionary<string, string> headers) => WriteAsync(method, url, headers);

    private async Task WriteAsync(string method, string url, Dictionary<string, string> headers)
    {
        var sb = new StringBuilder();
        sb.Append(method).Append(' ').Append(url).Append(" RTSP/1.0\r\n");
        sb.Append("CSeq: ").Append(++_cseq).Append("\r\n");
        sb.Append("User-Agent: CameraFinder\r\n");
        if (Authorization(method, url) is { } a) sb.Append("Authorization: ").Append(a).Append("\r\n");
        foreach (var h in headers) sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
        sb.Append("\r\n");
        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        await _s.WriteAsync(bytes);
    }

    private void SetChallenge(string challenge)
    {
        if (challenge.StartsWith("Digest", StringComparison.OrdinalIgnoreCase))
        {
            _digest = ParseParams(challenge[6..]);
            _auth = null;
        }
        else
        {
            _auth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_login!.User}:{_login.Password}"));
            _digest = null;
        }
    }

    private int _nc;

    private string? Authorization(string method, string url)
    {
        if (_auth != null) return _auth;
        if (_digest == null || _login == null) return null;
        string realm = _digest.GetValueOrDefault("realm") ?? "", nonce = _digest.GetValueOrDefault("nonce") ?? "";
        string ha1 = Md5($"{_login.User}:{realm}:{_login.Password}"), ha2 = Md5($"{method}:{url}");
        string? qop = _digest.GetValueOrDefault("qop")?.Split(',').Select(q => q.Trim()).FirstOrDefault(q => q == "auth");
        var sb = new StringBuilder($"Digest username=\"{_login.User}\", realm=\"{realm}\", nonce=\"{nonce}\", uri=\"{url}\"");
        if (qop != null)
        {
            string nc = (++_nc).ToString("x8"), cnonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            sb.Append($", qop={qop}, nc={nc}, cnonce=\"{cnonce}\", response=\"{Md5($"{ha1}:{nonce}:{nc}:{cnonce}:{qop}:{ha2}")}\"");
        }
        else sb.Append($", response=\"{Md5($"{ha1}:{nonce}:{ha2}")}\"");
        if (_digest.TryGetValue("opaque", out var opaque)) sb.Append($", opaque=\"{opaque}\"");
        return sb.ToString();
    }

    public static string Md5(string s) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    public static Dictionary<string, string> ParseParams(string s)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(s, @"(\w+)\s*=\s*(?:""([^""]*)""|([^,\s]+))"))
            d[m.Groups[1].Value] = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
        return d;
    }

    private async Task<Response> ReadResponseAsync(CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(RtspLiveSource.ReadTimeout);
        var head = new StringBuilder();
        var one = new byte[1];
        try
        {
            // Skip interleaved data that may already be flowing, until the reply's status line.
            while (true)
            {
                if (await _s.ReadAsync(one, limit.Token) == 0) throw new LiveStreamException("The camera closed the connection");
                if (one[0] == 'R') break;
            }
            head.Append('R');
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && !head.ToString().EndsWith("\n\n", StringComparison.Ordinal))
            {
                if (await _s.ReadAsync(one, limit.Token) == 0) throw new LiveStreamException("The camera closed the connection");
                head.Append((char)one[0]);
                if (head.Length > 16384) throw new LiveStreamException("Not an RTSP reply");
            }
            var lines = head.ToString().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            var status = lines[0].Split(' ', 3);
            if (status.Length < 2 || !status[0].StartsWith("RTSP/") || !int.TryParse(status[1], out int code)) throw new LiveStreamException("Not an RTSP reply");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in lines.Skip(1))
            {
                int c = l.IndexOf(':');
                if (c > 0) headers.TryAdd(l[..c].Trim().ToLowerInvariant(), l[(c + 1)..].Trim());
            }
            string body = "";
            if (headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out int len) && len is > 0 and < 65536)
            {
                var b = new byte[len];
                int got = 0;
                while (got < len)
                {
                    int n = await _s.ReadAsync(b.AsMemory(got), limit.Token);
                    if (n == 0) break;
                    got += n;
                }
                body = Encoding.UTF8.GetString(b, 0, got);
            }
            return new Response(code, status.Length > 2 ? status[2] : "", headers, body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new LiveStreamException("The camera did not answer (RTSP timed out)"); }
    }
}

/// <summary>The parts of an SDP description needed to play the video track.</summary>
public sealed record Sdp(string? SessionControl, Sdp.Media? Video)
{
    public sealed record Media(string? Codec, string? Control, int? ClockRate = null);

    public static Sdp Parse(string sdp)
    {
        string? sessionControl = null, codec = null, control = null;
        int? clock = null;
        bool inVideo = false, inAny = false, videoDone = false;
        foreach (var raw in sdp.Split('\n'))
        {
            string l = raw.TrimEnd('\r');
            if (l.StartsWith("m="))
            {
                if (inVideo) videoDone = true;
                inAny = true;
                inVideo = !videoDone && l.StartsWith("m=video");
                continue;
            }
            if (l.StartsWith("a=control:"))
            {
                if (!inAny) sessionControl = l[10..].Trim();
                else if (inVideo) control = l[10..].Trim();
            }
            else if (inVideo && l.StartsWith("a=rtpmap:"))
            {
                var parts = l.Split(' ', 2);
                if (parts.Length == 2)
                {
                    var enc = parts[1].Split('/');
                    codec = enc[0].Trim().ToUpperInvariant();
                    if (enc.Length > 1 && int.TryParse(enc[1].Trim(), out int hz) && hz > 0) clock = hz;
                }
            }
        }
        return new Sdp(sessionControl, videoDone || inVideo ? new Media(codec, control, clock) : null);
    }

    /// <summary>Control attributes are absolute ("rtsp://...") or relative to the Content-Base ("trackID=1"); "*" means the base itself.</summary>
    public static string Resolve(string baseUrl, string? control)
    {
        if (string.IsNullOrEmpty(control) || control == "*") return baseUrl;
        if (control.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)) return control;
        return baseUrl.EndsWith('/') ? baseUrl + control : baseUrl + "/" + control;
    }
}
