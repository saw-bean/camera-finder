using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using CameraFinder.Core.Model;
using CameraFinder.Core.Net;

namespace CameraFinder.Core.Discovery;

/// <summary>
/// Adds details to a found device: which common ports are open, the web page title and server name,
/// and UPnP description. Read-only: it never logs in or sends credentials.
/// </summary>
public sealed class DeviceEnricher : IDisposable
{
    /// <summary>80/443 web page, 554 RTSP video (cameras/recorders), 8080 alternate web, 8000 common camera SDK port.</summary>
    public static readonly int[] Ports = { 80, 443, 554, 8000, 8080 };

    // One client per laptop address to send from (null = the laptop's normal address).
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new();

    private HttpClient ClientFor(IPAddress? source) => _clients.GetOrAdd(source?.ToString() ?? "", _ =>
    {
        var handler = BoundSocket.Http(source);
        // Devices use self-signed certificates. We only read the page title, never send credentials.
        handler.SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true };
        handler.AllowAutoRedirect = true;
        handler.MaxAutomaticRedirections = 3;
        handler.UseCookies = false;
        handler.UseProxy = false;
        handler.ConnectTimeout = TimeSpan.FromSeconds(3);
        var http = new HttpClient(handler) { Timeout = RequestTimeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CameraFinder/1.0");
        return http;
    });

    /// <param name="source">The laptop address to connect from: the temporary address in the device's range, if any.</param>
    public async Task EnrichAsync(Device d, string? ssdpDescriptionUrl, CancellationToken ct, IPAddress? source = null)
    {
        var http = ClientFor(source);
        var open = await OpenPortsAsync(d.Ip, Ports, TimeSpan.FromMilliseconds(900), ct, source);
        lock (d) foreach (var p in open) d.OpenPorts.Add(p);

        string? url = PickWebUrl(d.Ip, open);
        if (url != null)
        {
            d.WebUrl = url;
            var (title, server) = await ReadPageAsync(http, url, ct);
            d.WebTitle ??= title;
            d.Server ??= server;
        }

        if (ssdpDescriptionUrl != null && Uri.TryCreate(ssdpDescriptionUrl, UriKind.Absolute, out var u)
            && IPAddress.TryParse(u.Host, out var host) && host.Equals(d.Ip))
        {
            try
            {
                string xml = await GetLimitedAsync(http, ssdpDescriptionUrl, 64 * 1024, ct);
                var (name, mfr, model) = SsdpProbe.ParseDescription(xml);
                d.Name ??= name;
                d.Model ??= model;
                if (mfr != null && d.Vendor == null) d.Vendor = mfr;
            }
            catch { /* description not reachable */ }
        }
    }

    public static string? PickWebUrl(IPAddress ip, ICollection<int> open)
    {
        if (open.Contains(80)) return $"http://{ip}/";
        if (open.Contains(443)) return $"https://{ip}/";
        if (open.Contains(8080)) return $"http://{ip}:8080/";
        return null;
    }

    public static async Task<List<int>> OpenPortsAsync(IPAddress ip, IEnumerable<int> ports, TimeSpan timeout, CancellationToken ct, IPAddress? source = null)
    {
        var checks = ports.Select(async port =>
        {
            using var client = BoundSocket.Tcp(source);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await client.ConnectAsync(ip, port, cts.Token);
                return (port, open: true);
            }
            catch { return (port, open: false); }
        });
        var results = await Task.WhenAll(checks);
        return results.Where(r => r.open).Select(r => r.port).OrderBy(p => p).ToList();
    }

    /// <summary>
    /// Whole-request limit. HttpClient.Timeout only covers the headers when reading with ResponseHeadersRead, so a
    /// device that sends its page very slowly would otherwise hold up the scan forever.
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private static async Task<(string? Title, string? Server)> ReadPageAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(RequestTimeout);
            ct = limit.Token;
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            string? server = resp.Headers.Server.Count > 0 ? resp.Headers.Server.ToString() : null;
            string html = await ReadLimitedAsync(resp, 64 * 1024, ct);
            return (ExtractTitle(html), server);
        }
        catch { return (null, null); }
    }

    private static async Task<string> GetLimitedAsync(HttpClient http, string url, int max, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(RequestTimeout);
        ct = limit.Token;
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        return await ReadLimitedAsync(resp, max, ct);
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage resp, int max, CancellationToken ct)
    {
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        var buf = new byte[max];
        int total = 0, n;
        while (total < max && (n = await s.ReadAsync(buf.AsMemory(total, max - total), ct)) > 0) total += n;
        return Encoding.UTF8.GetString(buf, 0, total);
    }

    /// <summary>
    /// Text of the first &lt;title&gt; element, whitespace collapsed, at most 80 characters.
    /// Plain index search, not a backtracking regex: a hostile or broken page must not hang the scan.
    /// </summary>
    public static string? ExtractTitle(string html)
    {
        int open = html.IndexOf("<title", StringComparison.OrdinalIgnoreCase);
        while (open >= 0)
        {
            int after = open + "<title".Length;
            if (after < html.Length && (html[after] == '>' || char.IsWhiteSpace(html[after]))) break;
            open = html.IndexOf("<title", after, StringComparison.OrdinalIgnoreCase);
        }
        if (open < 0) return null;
        int gt = html.IndexOf('>', open);
        if (gt < 0) return null;
        int close = html.IndexOf("</title", gt + 1, StringComparison.OrdinalIgnoreCase);
        if (close < 0) return null;
        string t = WebUtility.HtmlDecode(WhitespaceRun.Replace(html[(gt + 1)..close], " ")).Trim();
        return t.Length == 0 ? null : t.Length > 80 ? t[..80] : t;
    }

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    public void Dispose() { foreach (var c in _clients.Values) c.Dispose(); }
}
