using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace CameraFinder.Core.Health;

public interface IOnvifClient
{
    /// <param name="source">Local address to connect from (a temporary address in the camera's range), or null.</param>
    Task<OnvifReport> CollectAsync(IPAddress camera, int port, Credentials? login, IPAddress? source, CancellationToken ct);
}

/// <summary>
/// Reads health data from a camera over ONVIF (SOAP over HTTP): identity, clock, system log, video profiles and the
/// RTSP stream address. Logs in with WS-Security UsernameToken (password digest), timed with the camera's own clock
/// so a camera whose clock is off still accepts the login; HTTP digest is answered too, for cameras that want it.
/// </summary>
public sealed class OnvifClient : IOnvifClient
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);
    private const int MaxResponseBytes = 4 << 20; // system logs can be long; anything bigger is not a camera reply

    public async Task<OnvifReport> CollectAsync(IPAddress camera, int port, Credentials? login, IPAddress? source, CancellationToken ct)
    {
        string url = $"http://{(port == 80 ? camera.ToString() : $"{camera}:{port}")}/onvif/device_service";
        var report = new OnvifReport { ServiceUrl = url };
        using var http = CreateHttp(login, source);
        var clock = TimeSpan.Zero; // camera time - laptop time, used to time-stamp logins

        // 1. Clock (no login needed by the standard).
        var time = await CallAsync(http, url, Device("GetSystemDateAndTime"), null, clock, ct);
        if (!time.IsOnvif) return report; // nothing listening, or a web server that does not speak ONVIF
        report.Answered = true;
        if (time.Doc != null) ReadTime(time.Doc, report);
        if (report.ClockOffset is { } off) clock = off;

        // 2. Identity: the first call that needs a login tells whether the login works.
        var info = await CallAsync(http, url, Device("GetDeviceInformation"), login, clock, ct);
        if (info.AuthFailed)
        {
            report.LoginAccepted = false;
            report.LoginError = login == null || login.IsEmpty ? "the camera needs a user name and password" : "user name or password not accepted";
            return report;
        }
        if (info.Doc != null)
        {
            report.Manufacturer = Text(info.Doc, "Manufacturer");
            report.Model = Text(info.Doc, "Model");
            report.Firmware = Text(info.Doc, "FirmwareVersion");
            report.Serial = Text(info.Doc, "SerialNumber");
            if (login is { IsEmpty: false }) report.LoginAccepted = true;
        }
        else report.Unsupported.Add("The camera did not report its model over ONVIF");

        // 3. System log.
        var log = await CallAsync(http, url,
            "<tds:GetSystemLog xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\"><tds:LogType>System</tds:LogType></tds:GetSystemLog>", login, clock, ct);
        report.SystemLog = log.Doc == null ? null : ReadSystemLog(log.Doc);
        if (log.AuthFailed && login is { IsEmpty: false })
        {
            // GetDeviceInformation worked with this login, so it is right - but its user level is too low for the log.
            report.LoginLevelTooLow = true;
            report.Unsupported.Add("The login's user level is too low to read the system log (an administrator login can)");
        }
        else if (report.SystemLog == null) report.Unsupported.Add("The camera does not share its system log over ONVIF");

        // 3b. The camera's own network settings: address, mask, gateway, DHCP, NTP servers, link, services.
        var nics = await CallAsync(http, url, Device("GetNetworkInterfaces"), login, clock, ct);
        if (nics.Doc != null) report.Network = ReadNetworkInterfaces(nics.Doc, camera);
        if (report.Network is { } net)
        {
            var gw = await CallAsync(http, url, Device("GetNetworkDefaultGateway"), login, clock, ct);
            if (gw.Doc != null) net.Gateways.AddRange(ReadGateways(gw.Doc));
            var ntp = await CallAsync(http, url, Device("GetNTP"), login, clock, ct);
            if (ntp.Doc != null) ReadNtp(ntp.Doc, net);
        }
        else report.Unsupported.Add("The camera did not share its network settings over ONVIF");
        var protocols = await CallAsync(http, url, Device("GetNetworkProtocols"), login, clock, ct);
        if (protocols.Doc != null) foreach (var (name, on, servicePort) in ReadProtocols(protocols.Doc)) report.Protocols.TryAdd(name, (on, servicePort));

        // 4. Media service: profiles and the stream address.
        var caps = await CallAsync(http, url,
            "<tds:GetCapabilities xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\"><tds:Category>Media</tds:Category></tds:GetCapabilities>", login, clock, ct);
        string? media = caps.Doc == null ? null : MediaAddress(caps.Doc, camera, port);
        if (media == null) { report.Unsupported.Add("The camera did not say where its ONVIF media service is"); return report; }

        var profiles = await CallAsync(http, media, "<trt:GetProfiles xmlns:trt=\"http://www.onvif.org/ver10/media/wsdl\"/>", login, clock, ct);
        if (profiles.Doc != null) report.Profiles.AddRange(ReadProfiles(profiles.Doc));
        if (report.Profiles.Count == 0) { report.Unsupported.Add("The camera reported no video profiles over ONVIF"); return report; }

        foreach (var profile in report.Profiles.Take(3)) // main, sub and third stream are plenty
        {
            var uri = await CallAsync(http, media,
                "<trt:GetStreamUri xmlns:trt=\"http://www.onvif.org/ver10/media/wsdl\" xmlns:tt=\"http://www.onvif.org/ver10/schema\">" +
                "<trt:StreamSetup><tt:Stream>RTP-Unicast</tt:Stream><tt:Transport><tt:Protocol>RTSP</tt:Protocol></tt:Transport></trt:StreamSetup>" +
                $"<trt:ProfileToken>{SecurityElement(profile.Token)}</trt:ProfileToken></trt:GetStreamUri>", login, clock, ct);
            if (uri.Doc != null && Text(uri.Doc, "Uri") is { } u) report.StreamUris.TryAdd(profile.Token, u);
        }
        report.StreamUri = report.StreamUris.GetValueOrDefault(report.Profiles[0].Token) ?? report.StreamUris.Values.FirstOrDefault();
        return report;
    }

    // ---- HTTP / SOAP

    /// <param name="IsOnvif">The reply was a SOAP envelope (even a fault) or an HTTP login challenge: something speaks ONVIF here.</param>
    private sealed record SoapResult(XDocument? Doc, HttpStatusCode? Status, bool AuthFailed, bool IsOnvif);

    private static HttpClient CreateHttp(Credentials? login, IPAddress? source)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(4),
            AllowAutoRedirect = false,
            UseProxy = false,
            Credentials = login is { IsEmpty: false } ? new NetworkCredential(login.User, login.Password) : null,
            PreAuthenticate = false,
        };
        if (source != null)
        {
            // Temporary addresses are added with skipassource, so Windows never picks them on its own: bind explicitly.
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
        }
        return new HttpClient(handler) { Timeout = RequestTimeout };
    }

    private static async Task<SoapResult> CallAsync(HttpClient http, string url, string body, Credentials? login, TimeSpan clock, CancellationToken ct)
    {
        string envelope = Envelope(body, login, DateTime.UtcNow + clock);
        try
        {
            using var content = new StringContent(envelope, Encoding.UTF8);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/soap+xml; charset=utf-8");
            using var resp = await http.PostAsync(url, content, ct);
            byte[] bytes = await ReadLimitedAsync(resp.Content, ct);
            var doc = Load(bytes);
            bool soap = doc?.Root?.Name.LocalName == "Envelope";
            bool fault = soap && doc!.Descendants().Any(e => e.Name.LocalName == "Fault");
            bool challenge = resp.StatusCode == HttpStatusCode.Unauthorized && resp.Headers.WwwAuthenticate.Count > 0;
            bool auth = challenge || resp.StatusCode == HttpStatusCode.Forbidden && soap || (fault && IsAuthFault(doc!));
            return new SoapResult(!soap || fault || !resp.IsSuccessStatusCode ? null : doc, resp.StatusCode, auth, soap || challenge);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new SoapResult(null, null, false, false); } // timeout
        catch (HttpRequestException) { return new SoapResult(null, null, false, false); }
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        await using var s = await content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[16384];
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            if (ms.Length + n > MaxResponseBytes) break;
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    public static string Envelope(string body, Credentials? login, DateTime cameraUtcNow)
    {
        string header = "";
        if (login is { IsEmpty: false })
        {
            var nonce = RandomNumberGenerator.GetBytes(16);
            string created = cameraUtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            string digest = PasswordDigest(nonce, created, login.Password);
            header =
                "<s:Header><wsse:Security s:mustUnderstand=\"1\" " +
                "xmlns:wsse=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd\" " +
                "xmlns:wsu=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\">" +
                $"<wsse:UsernameToken><wsse:Username>{SecurityElement(login.User)}</wsse:Username>" +
                "<wsse:Password Type=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest\">" +
                $"{digest}</wsse:Password>" +
                "<wsse:Nonce EncodingType=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary\">" +
                $"{Convert.ToBase64String(nonce)}</wsse:Nonce><wsu:Created>{created}</wsu:Created></wsse:UsernameToken></wsse:Security></s:Header>";
        }
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
               "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\">" + header + "<s:Body>" + body + "</s:Body></s:Envelope>";
    }

    /// <summary>WS-Security password digest: Base64(SHA-1(nonce + created + password)).</summary>
    public static string PasswordDigest(byte[] nonce, string created, string password)
    {
        var data = nonce.Concat(Encoding.UTF8.GetBytes(created + password)).ToArray();
        return Convert.ToBase64String(SHA1.HashData(data));
    }

    private static string Device(string op) => $"<tds:{op} xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\"/>";

    private static string SecurityElement(string s) => System.Security.SecurityElement.Escape(s) ?? "";

    // ---- parsing (public for tests)

    /// <summary>Parses XML from a device safely: no DTDs, no external entities, bounded size.</summary>
    public static XDocument? Load(byte[] data)
    {
        if (data.Length == 0) return null;
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, MaxCharactersInDocument = MaxResponseBytes,
        };
        try
        {
            using var r = XmlReader.Create(new MemoryStream(data), settings);
            return XDocument.Load(r);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException) { return null; }
    }

    public static XDocument? Load(string xml) => Load(Encoding.UTF8.GetBytes(xml));

    public static bool IsAuthFault(XDocument doc) =>
        doc.Descendants().Any(e => e.Name.LocalName is "Value" or "Subcode" or "Text" or "faultstring" &&
            (e.Value.Contains("NotAuthorized", StringComparison.OrdinalIgnoreCase)
             || e.Value.Contains("FailedAuthentication", StringComparison.OrdinalIgnoreCase)
             || e.Value.Contains("Sender not Authorized", StringComparison.OrdinalIgnoreCase)));

    public static void ReadTime(XDocument doc, OnvifReport report)
    {
        var sdt = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "SystemDateAndTime");
        if (sdt == null) return;
        report.LaptopUtc = DateTime.UtcNow;
        report.TimeSource = Child(sdt, "DateTimeType")?.Value.Trim();
        report.TimeZone = Child(Child(sdt, "TimeZone"), "TZ")?.Value.Trim();
        var utc = Child(sdt, "UTCDateTime");
        var date = Child(utc, "Date");
        var time = Child(utc, "Time");
        if (date == null || time == null) return;
        try
        {
            report.CameraUtc = new DateTime(Num(date, "Year"), Num(date, "Month"), Num(date, "Day"),
                Num(time, "Hour"), Num(time, "Minute"), Num(time, "Second"), DateTimeKind.Utc);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or FormatException or OverflowException) { /* camera sent an impossible date */ }
    }

    public static string? ReadSystemLog(XDocument doc)
    {
        var log = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "SystemLog");
        var str = log == null ? null : Child(log, "String");
        string? text = str?.Value;
        return string.IsNullOrWhiteSpace(text) ? null : text.Replace("\r\n", "\n");
    }

    /// <summary>Media service address from GetCapabilities, re-pointed at the address we reached the camera on (cameras behind NAT or with a second IP often report the wrong host).</summary>
    public static string? MediaAddress(XDocument doc, IPAddress camera, int port)
    {
        var media = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Media");
        string? x = Child(media, "XAddr")?.Value.Trim();
        if (x == null || !Uri.TryCreate(x, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttp) return null;
        return new UriBuilder(u) { Host = camera.ToString(), Port = port }.Uri.ToString();
    }

    public static IEnumerable<OnvifProfile> ReadProfiles(XDocument doc)
    {
        foreach (var p in doc.Descendants().Where(e => e.Name.LocalName == "Profiles"))
        {
            string? token = p.Attribute("token")?.Value;
            if (string.IsNullOrEmpty(token)) continue;
            var enc = Child(p, "VideoEncoderConfiguration");
            var res = Child(enc, "Resolution");
            var rate = Child(enc, "RateControl");
            int? gov = OptNum(Child(enc, "H264"), "GovLength") ?? OptNum(Child(enc, "MPEG4"), "GovLength") ?? OptNum(enc, "GovLength");
            yield return new OnvifProfile(token, Child(p, "Name")?.Value.Trim(), Child(enc, "Encoding")?.Value.Trim(),
                OptNum(res, "Width"), OptNum(res, "Height"), OptNum(rate, "FrameRateLimit"), OptNum(rate, "BitrateLimit"), gov);
        }
    }

    /// <summary>
    /// GetNetworkInterfaces: the interface that carries <paramref name="camera"/> (else the first enabled one) - its
    /// IPv4 address and prefix (manual or from DHCP), whether DHCP is on, the negotiated link and MTU.
    /// </summary>
    public static OnvifNetwork? ReadNetworkInterfaces(XDocument doc, IPAddress camera)
    {
        var nics = doc.Descendants().Where(e => e.Name.LocalName == "NetworkInterfaces").ToList();
        if (nics.Count == 0) return null;
        OnvifNetwork Parse(XElement nic)
        {
            var n = new OnvifNetwork();
            var v4 = Child(nic, "IPv4");
            var cfg = Child(v4, "Config");
            n.Dhcp = Bool(Child(cfg, "DHCP"));
            var addr = n.Dhcp == true ? Child(cfg, "FromDHCP") ?? Child(cfg, "Manual") : Child(cfg, "Manual") ?? Child(cfg, "FromDHCP");
            if (IPAddress.TryParse(Child(addr, "Address")?.Value.Trim(), out var ip)) n.Address = ip;
            n.Prefix = OptNum(addr, "PrefixLength");
            var oper = Child(Child(nic, "Link"), "OperSettings") ?? Child(Child(nic, "Link"), "AdminSettings");
            n.LinkSpeedMbps = OptNum(oper, "Speed");
            n.Duplex = Child(oper, "Duplex")?.Value.Trim().ToLowerInvariant() switch { "full" => "full", "half" => "half", _ => null };
            n.AutoNegotiation = Bool(Child(Child(Child(nic, "Link"), "AdminSettings"), "AutoNegotiation"));
            n.Mtu = OptNum(Child(nic, "Info"), "MTU");
            return n;
        }
        var parsed = nics.Select(Parse).ToList();
        return parsed.FirstOrDefault(n => camera.Equals(n.Address)) ?? parsed.FirstOrDefault(n => n.Address != null) ?? parsed[0];
    }

    public static IEnumerable<IPAddress> ReadGateways(XDocument doc) =>
        doc.Descendants().Where(e => e.Name.LocalName == "IPv4Address")
            .Select(e => IPAddress.TryParse(e.Value.Trim(), out var a) ? a : null)
            .Where(a => a != null && !a.Equals(IPAddress.Any)).Select(a => a!);

    /// <summary>GetNTP: whether the servers come from DHCP, and the servers in use (from DHCP or set by hand).</summary>
    public static void ReadNtp(XDocument doc, OnvifNetwork net)
    {
        var info = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "NTPInformation");
        if (info == null) return;
        net.NtpFromDhcp = Bool(Child(info, "FromDHCP"));
        var lists = info.Elements().Where(e => e.Name.LocalName is "NTPManual" or "NTPFromDHCP")
            .OrderBy(e => (e.Name.LocalName == "NTPFromDHCP") == (net.NtpFromDhcp == true) ? 0 : 1);
        foreach (var server in lists)
        {
            string? v = (Child(server, "IPv4Address") ?? Child(server, "DNSname") ?? Child(server, "IPv6Address"))?.Value.Trim();
            if (!string.IsNullOrEmpty(v) && v != "0.0.0.0" && !net.NtpServers.Contains(v)) net.NtpServers.Add(v);
        }
    }

    /// <summary>GetNetworkProtocols: (HTTP, true, 80), (RTSP, true, 554)...</summary>
    public static IEnumerable<(string Name, bool Enabled, int? Port)> ReadProtocols(XDocument doc) =>
        doc.Descendants().Where(e => e.Name.LocalName == "NetworkProtocols")
            .Select(p => (Child(p, "Name")?.Value.Trim() ?? "", Bool(Child(p, "Enabled")) ?? true, OptNum(p, "Port")))
            .Where(p => p.Item1.Length > 0);

    private static bool? Bool(XElement? e) => e?.Value.Trim().ToLowerInvariant() switch { "true" or "1" => true, "false" or "0" => false, _ => null };

    private static XElement? Child(XElement? e, string local) => e?.Elements().FirstOrDefault(c => c.Name.LocalName == local);
    private static string? Text(XDocument d, string local) =>
        d.Descendants().FirstOrDefault(e => e.Name.LocalName == local)?.Value.Trim() is { Length: > 0 } v ? v : null;
    private static int Num(XElement e, string local) => int.Parse(Child(e, local)?.Value.Trim() ?? "x", CultureInfo.InvariantCulture);
    private static int? OptNum(XElement? e, string local) =>
        int.TryParse(Child(e, local)?.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}
