using System.Net;
using System.Text;
using System.Xml;

namespace CameraFinder.Core.Discovery;

/// <summary>UPnP / SSDP search. Many cameras, NVRs and other network devices answer it.</summary>
public static class SsdpProbe
{
    public static readonly IPEndPoint Group = new(IPAddress.Parse("239.255.255.250"), 1900);

    public static IEnumerable<byte[]> Probes() => new[]
    {
        Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 2\r\n" +
            "ST: ssdp:all\r\n" +
            "\r\n"),
    };

    /// <summary>Parses an SSDP HTTP-over-UDP reply.</summary>
    public static ProbeHit? Parse(IPAddress from, byte[] data)
    {
        string text = Encoding.UTF8.GetString(data);
        if (!text.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("NOTIFY", StringComparison.OrdinalIgnoreCase)) return null;

        string? location = null, server = null;
        foreach (var line in text.Split("\r\n"))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (key.Equals("LOCATION", StringComparison.OrdinalIgnoreCase)) location = value;
            else if (key.Equals("SERVER", StringComparison.OrdinalIgnoreCase)) server = value;
        }
        return new ProbeHit(from, null, null, null, server, location);
    }

    /// <summary>Reads friendlyName, manufacturer and modelName from a UPnP device description XML.</summary>
    public static (string? Name, string? Manufacturer, string? Model) ParseDescription(string xml)
    {
        var doc = new XmlDocument { XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc.Load(reader);
        }
        catch (XmlException) { return (null, null, null); }

        string? Get(string local)
        {
            var nodes = doc.GetElementsByTagName("*");
            foreach (XmlNode n in nodes)
                if (n.LocalName == local && !string.IsNullOrWhiteSpace(n.InnerText)) return n.InnerText.Trim();
            return null;
        }

        string? model = Get("modelName");
        string? number = Get("modelNumber");
        if (model != null && number != null && !model.Contains(number)) model = $"{model} {number}";
        return (Get("friendlyName"), Get("manufacturer"), model);
    }
}
