using System.Net;
using System.Text;
using System.Xml;

namespace CameraFinder.Core.Discovery;

/// <summary>Result of an ONVIF WS-Discovery, SSDP or manufacturer discovery reply.</summary>
public sealed record ProbeHit(
    IPAddress Ip, string? Model, string? Name, string? Manufacturer, string? Server, string? DescriptionUrl,
    string? Mac = null,        // reported by the device itself (manufacturer protocols)
    bool Camera = false,       // the protocol only exists on cameras / recorders
    string? Protocol = null);  // manufacturer protocol name, shown in "Found by"

/// <summary>ONVIF WS-Discovery: the standard almost every professional IP camera answers, whatever the brand.</summary>
public static class OnvifProbe
{
    public static readonly IPEndPoint Group = new(IPAddress.Parse("239.255.255.250"), 3702);

    public static byte[] BuildProbe(string? types)
    {
        string typesXml = types == null ? "" : $"<d:Types>{types}</d:Types>";
        string xml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<e:Envelope xmlns:e=\"http://www.w3.org/2003/05/soap-envelope\" " +
            "xmlns:w=\"http://schemas.xmlsoap.org/ws/2004/08/addressing\" " +
            "xmlns:d=\"http://schemas.xmlsoap.org/ws/2005/04/discovery\" " +
            "xmlns:dn=\"http://www.onvif.org/ver10/network/wsdl\">" +
            "<e:Header>" +
            $"<w:MessageID>uuid:{Guid.NewGuid()}</w:MessageID>" +
            "<w:To e:mustUnderstand=\"true\">urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>" +
            "<w:Action e:mustUnderstand=\"true\">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action>" +
            "</e:Header>" +
            $"<e:Body><d:Probe>{typesXml}</d:Probe></e:Body>" +
            "</e:Envelope>";
        return Encoding.UTF8.GetBytes(xml);
    }

    /// <summary>Two probes: one for video devices, one untyped (some devices only answer one of them).</summary>
    public static IEnumerable<byte[]> Probes() => new[] { BuildProbe("dn:NetworkVideoTransmitter"), BuildProbe(null) };

    /// <summary>Parses a ProbeMatch reply. Returns null if it is not one.</summary>
    public static ProbeHit? Parse(IPAddress from, byte[] data)
    {
        var found = SafeXml.Extract(data, new[] { "XAddrs", "Scopes" }, markers: new[] { "ProbeMatch" });
        if (found == null || !found.ContainsKey("ProbeMatch")) return null;
        found.TryGetValue("XAddrs", out var xaddrs);
        found.TryGetValue("Scopes", out var scopes);

        IPAddress ip = from;
        if (xaddrs != null)
        {
            foreach (var url in xaddrs.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var u) && IPAddress.TryParse(u.Host, out var a)
                    && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    ip = a;
                    break;
                }
            }
        }

        string? model = Scope(scopes, "hardware");
        string? name = Scope(scopes, "name");
        string? mfr = Scope(scopes, "mfr") ?? Scope(scopes, "manufacturer");
        return new ProbeHit(ip, model, name, mfr, null, null);
    }

    /// <summary>Reads e.g. "onvif://www.onvif.org/hardware/P3245-V" from the space-separated scope list.</summary>
    internal static string? Scope(string? scopes, string key)
    {
        if (scopes == null) return null;
        string prefix = $"onvif://www.onvif.org/{key}/";
        foreach (var s in scopes.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string v = Uri.UnescapeDataString(s[prefix.Length..]).Replace('_', ' ').Trim('/', ' ');
                if (v.Length > 0) return v;
            }
        }
        return null;
    }
}

/// <summary>
/// Reads small XML documents from devices (ONVIF replies, UPnP descriptions) without building a DOM.
/// Streaming and depth-limited, so a hostile packet cannot overflow the stack, expand entities or hang the parser.
/// </summary>
internal static class SafeXml
{
    public const int MaxDepth = 32;

    private static XmlReaderSettings Settings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        MaxCharactersInDocument = 1 << 20,
    };

    public static Dictionary<string, string>? Extract(byte[] data, IEnumerable<string> texts, IEnumerable<string>? markers = null) =>
        Extract(() => XmlReader.Create(new MemoryStream(data), Settings()), texts, markers);

    public static Dictionary<string, string>? Extract(string xml, IEnumerable<string> texts, IEnumerable<string>? markers = null) =>
        Extract(() => XmlReader.Create(new StringReader(xml), Settings()), texts, markers);

    /// <summary>
    /// Returns the trimmed text of the first element (by local name) in <paramref name="texts"/> that has non-blank
    /// text, plus an empty entry for every name in <paramref name="markers"/> that occurs. Null if the XML is malformed.
    /// </summary>
    private static Dictionary<string, string>? Extract(Func<XmlReader> open, IEnumerable<string> texts, IEnumerable<string>? markers)
    {
        var want = new HashSet<string>(texts);
        var mark = new HashSet<string>(markers ?? Array.Empty<string>());
        var result = new Dictionary<string, string>();
        try
        {
            using var r = open();
            r.Read();
            while (!r.EOF)
            {
                if (r.NodeType == XmlNodeType.Element)
                {
                    if (r.Depth > MaxDepth) return null;
                    if (mark.Contains(r.LocalName)) result.TryAdd(r.LocalName, "");
                    if (want.Contains(r.LocalName) && !result.ContainsKey(r.LocalName))
                    {
                        string name = r.LocalName;
                        string text = ReadText(r).Trim();
                        if (text.Length > 0) result[name] = text;
                        continue; // ReadText left the reader on the next node
                    }
                }
                r.Read();
            }
            return result;
        }
        catch (Exception ex) when (ex is XmlException or ArgumentException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>Text of the element the reader is on (including text inside child elements); moves past its end tag.</summary>
    private static string ReadText(XmlReader r)
    {
        if (r.IsEmptyElement) { r.Read(); return ""; }
        int depth = r.Depth;
        var sb = new StringBuilder();
        r.Read();
        while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
        {
            if (r.Depth > MaxDepth) throw new XmlException("XML nested too deeply");
            if (r.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                sb.Append(r.Value);
            r.Read();
        }
        r.Read();
        return sb.ToString();
    }
}
