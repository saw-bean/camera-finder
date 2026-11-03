using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace CameraFinder.App;

/// <summary>
/// Checks GitHub Releases for a newer version and downloads/starts its installer.
/// Any failure (no internet on a site network, GitHub down, rate limit) is ignored - the app works offline.
/// </summary>
internal static class UpdateChecker
{
    public const string ReleasesPage = "https://github.com/saw-bean/camera-finder/releases/latest";
    private const string LatestApi = "https://api.github.com/repos/saw-bean/camera-finder/releases/latest";
    private const string AllowedDownloadPrefix = "https://github.com/saw-bean/camera-finder/releases/download/";

    public sealed record Update(Version Version, string InstallerUrl);

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CameraFinder", CurrentVersion.ToString(3)));
        return http;
    }

    /// <summary>Returns the latest release if it is newer than this build, otherwise null.</summary>
    public static async Task<Update?> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var http = NewClient(TimeSpan.FromSeconds(8));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var stream = await http.GetStreamAsync(LatestApi, ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
            if (Normalize(latest) <= Normalize(CurrentVersion)) return null;

            string? url = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                var link = asset.GetProperty("browser_download_url").GetString() ?? "";
                if (!link.StartsWith(AllowedDownloadPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals($"CameraFinder-Setup-{latest.ToString(3)}.exe", StringComparison.OrdinalIgnoreCase)) { url = link; break; }
                if (name.Equals("CameraFinder-Setup.exe", StringComparison.OrdinalIgnoreCase)) url ??= link;
            }
            return url is null ? null : new Update(latest, url);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Downloads the installer to a fresh temp folder and returns its path.</summary>
    public static async Task<string> DownloadAsync(Update update, IProgress<int> progress, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "CameraFinder-Update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"CameraFinder-Setup-{update.Version.ToString(3)}.exe");

        using var http = NewClient(TimeSpan.FromMinutes(10));
        using var response = await http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;

        await using (var src = await response.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(path))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress.Report((int)(done * 100 / total.Value));
            }
        }
        return path;
    }

    public static void StartInstaller(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    // 0.2 and 0.2.0 must compare equal; Version treats missing parts as -1.
    private static Version Normalize(Version v) =>
        new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
