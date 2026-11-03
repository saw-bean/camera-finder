using System.Text;
using CameraFinder.Core.Model;
using CameraFinder.Core.Net;

namespace CameraFinder.Core.Export;

/// <summary>Writes the device list to CSV for the job file (opens in Excel).</summary>
public static class CsvExporter
{
    public static string ToCsv(IEnumerable<Device> devices, string? site = null)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(site)) sb.AppendLine(Row("Site", site!));
        sb.AppendLine(Row("Scanned", DateTime.Now.ToString("yyyy-MM-dd HH:mm")));
        sb.AppendLine(Row("IP", "MAC", "Manufacturer", "Type", "Model / Name", "Open ports", "Web page", "Found by", "Location notes"));
        foreach (var d in devices.OrderBy(d => Ipv4.ToUInt(d.Ip)))
        {
            sb.AppendLine(Row(
                d.Ip.ToString(),
                d.Mac ?? "",
                d.Vendor ?? "",
                d.Kind.ToString(),
                d.Description,
                string.Join(" ", d.OpenPorts),
                d.WebUrl ?? "",
                d.Sources.ToString(),
                ""));
        }
        return sb.ToString();
    }

    private static string Row(params string[] cells) => string.Join(",", cells.Select(Escape));

    private static string Escape(string s)
    {
        // Prevent spreadsheet formula injection from device-supplied text (e.g. a web title starting with "=").
        if (s.Length > 0 && "=+-@".Contains(s[0])) s = "'" + s;
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
