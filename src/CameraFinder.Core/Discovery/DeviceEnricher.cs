using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using CameraFinder.Core.Model;

namespace CameraFinder.Core.Discovery;

/// <summary>
/// Adds details to a found device: which common ports are open, the web page title and server name,
/// and UPnP description. Read-only: it never logs in or sends credentials.
/// </summary>
public sealed class DeviceEnricher : IDisposable
{
    /// <summary>80/443 web page, 554 RTSP video (cameras/recorders), 8080 alternate web, 8000 common camera SDK port.</summary>
    public static readonly int[] Ports = { 80, 443, 554, 8000, 8080 };

    private readonly HttpClient _http;

    public DeviceEnricher()
    {
        var handler = new HttpClientHandler
        {
            // Devices use self-signed certificates. We only read the page title, never send credentials.
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            UseCookies = false,
            UseDefaultCredentials = false,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CameraFinder/1.0");
    }

    public async Task EnrichAsync(Device d, string? ssdpDescriptionUrl, CancellationToken ct)
    {
        var open = await OpenPortsAsync(d.Ip, Ports, TimeSpan.FromMilliseconds(900), ct);
        lock (d) foreach (var p in open) d.OpenPorts.Add(p);

        string? url = PickWebUrl(d.Ip, open);
        if (url != null)
        {
            d.WebUrl = url;
            var (title, server) = await ReadPageAsync(url, ct);
            d.WebTitle ??= title;
            d.Server ??= server;
        }

        if (ssdpDescriptionUrl != null && Uri.TryCreate(ssdpDescriptionUrl, UriKind.Absolute, out var u)
            && IPAddress.TryParse(u.Host, out var host) && host.Equals(d.Ip))
        {
            try
            {
                string xml = await GetLimitedAsync(ssdpDescriptionUrl, 64 * 1024, ct);
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

    public static async Task<List<int>> OpenPortsAsync(IPAddress ip, IEnumerable<int> ports, TimeSpan timeout, CancellationToken ct)
    {
        var checks = ports.Select(async port =>
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
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

    private async Task<(string? Title, string? Server)> ReadPageAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            string? server = resp.Headers.Server.Count > 0 ? resp.Headers.Server.ToString() : null;
            string html = await ReadLimitedAsync(resp, 64 * 1024, ct);
            return (ExtractTitle(html), server);
        }
        catch { return (null, null); }
    }

    private async Task<string> GetLimitedAsync(string url, int max, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
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

    public static string? ExtractTitle(string html)
    {
        var m = Regex.Match(html, @"<title[^>]*>\s*(.*?)\s*</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!m.Success) return null;
        string t = WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, @"\s+", " ")).Trim();
        return t.Length == 0 ? null : t.Length > 80 ? t[..80] : t;
    }

    public void Dispose() => _http.Dispose();
}
