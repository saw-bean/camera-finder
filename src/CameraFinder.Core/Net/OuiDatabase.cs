using System.IO.Compression;
using System.Reflection;

namespace CameraFinder.Core.Net;

/// <summary>
/// Looks up the manufacturer of a MAC address from the IEEE OUI registry (bundled, works offline).
/// Supports the 24-bit (MA-L), 28-bit (MA-M) and 36-bit (MA-S) assignments.
/// </summary>
public sealed class OuiDatabase
{
    private readonly Dictionary<string, string> _map;

    // Registered owner names that are not what an installer would recognise on the box.
    // Only add entries here that are known to be right.
    private static readonly Dictionary<string, string> FriendlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["001885"] = "Avigilon (Motorola Solutions)",
    };

    private static readonly Lazy<OuiDatabase> _default = new(LoadEmbedded);
    public static OuiDatabase Default => _default.Value;

    public OuiDatabase(Dictionary<string, string> map) => _map = map;

    public int Count => _map.Count;

    public static OuiDatabase LoadEmbedded()
    {
        var asm = typeof(OuiDatabase).Assembly;
        using var stream = asm.GetManifestResourceStream("CameraFinder.Core.oui.tsv.gz")
            ?? throw new InvalidOperationException("OUI resource missing");
        using var gz = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        return Load(reader);
    }

    public static OuiDatabase Load(TextReader reader)
    {
        var map = new Dictionary<string, string>(60000, StringComparer.OrdinalIgnoreCase);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            int tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            map[line[..tab]] = line[(tab + 1)..];
        }
        return new OuiDatabase(map);
    }

    /// <summary>Returns the vendor for a MAC like "00:18:85:12:34:56", or null if unknown.</summary>
    public string? Lookup(string? mac)
    {
        if (string.IsNullOrEmpty(mac)) return null;
        string hex = new string(mac.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (hex.Length < 6) return null;

        if (FriendlyNames.TryGetValue(hex[..6], out var friendly)) return friendly;

        // Longest prefix first: MA-S (9 hex), MA-M (7 hex), MA-L (6 hex).
        foreach (int len in new[] { 9, 7, 6 })
        {
            if (hex.Length >= len && _map.TryGetValue(hex[..len], out var vendor)) return vendor;
        }

        // Locally administered addresses (bit 1 of first byte) are random/virtual, not a manufacturer.
        if ((Convert.ToByte(hex[..2], 16) & 0x02) != 0) return "(random / locally administered MAC)";
        return null;
    }

    public static string FormatMac(ReadOnlySpan<byte> mac) =>
        string.Join(":", mac.ToArray().Select(b => b.ToString("X2")));
}
