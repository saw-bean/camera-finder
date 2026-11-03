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
        var doc = new XmlDocument { XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(data),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc.Load(reader);
        }
        catch (XmlException) { return null; }

        if (doc.GetElementsByTagName("ProbeMatch", "*").Count == 0 && FindLocal(doc, "ProbeMatch") == null) return null;

        string? xaddrs = FindLocal(doc, "XAddrs")?.InnerText;
        string? scopes = FindLocal(doc, "Scopes")?.InnerText;

        IPAddress ip = from;
        if (xaddrs != null)
        {
            foreach (var url in xaddrs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
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

    private static XmlNode? FindLocal(XmlNode node, string localName)
    {
        if (node.LocalName == localName) return node;
        foreach (XmlNode child in node.ChildNodes)
        {
            var hit = FindLocal(child, localName);
            if (hit != null) return hit;
        }
        return null;
    }
}
