using System.Diagnostics;
using System.Text;

namespace CameraFinder.Core.Health;

/// <summary>
/// The helper programs the installer ships in the "tools" folder next to CameraFinder.exe (ffprobe for video
/// checks, iperf3 for bandwidth tests). Falls back to PATH so a developer build can use installed copies.
/// </summary>
public static class ExternalTool
{
    public const string FfprobeExe = "ffprobe";
    public const string Iperf3Exe = "iperf3";

    /// <summary>Full path of the tool, or null if it cannot be found.</summary>
    public static string? Find(string name, string? baseDirectory = null)
    {
        string file = OperatingSystem.IsWindows() ? name + ".exe" : name;
        string dir = baseDirectory ?? AppContext.BaseDirectory;
        foreach (var candidate in new[] { Path.Combine(dir, "tools", file), Path.Combine(dir, file) })
            if (File.Exists(candidate)) return candidate;
        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(p.Trim('"'), file);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* bad PATH entry */ }
        }
        return null;
    }
}

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

/// <summary>Runs a helper program without a console window and collects its output, with a hard time limit.</summary>
public static class ProcessRunner
{
    public const int MaxOutputChars = 8 << 20;

    public static async Task<ProcessResult> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct,
        Action<string>? onStdErrLine = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdout) if (stdout.Length < MaxOutputChars) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stderr) if (stderr.Length < 65536) stderr.AppendLine(e.Data);
            onStdErrLine?.Invoke(e.Data);
        };
        p.Start();
        p.StandardInput.Close();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        bool timedOut = false;
        try { await p.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await p.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)); } catch (TimeoutException) { }
            ct.ThrowIfCancellationRequested();
        }
        if (p.HasExited) p.WaitForExit(); // flush the async readers
        string o, e;
        lock (stdout) o = stdout.ToString();
        lock (stderr) e = stderr.ToString();
        return new ProcessResult(p.HasExited ? p.ExitCode : -1, o, e, timedOut);
    }
}
