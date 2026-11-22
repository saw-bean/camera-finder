namespace CameraFinder.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Only one copy at a time: two copies would fight over the temporary addresses.
        using var mutex = new Mutex(true, @"Global\CameraFinder.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("Camera Finder is already running.", "Camera Finder", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // Hidden: --screenshot <folder> runs a demo scan and saves screenshots of the windows (used to check the UI in CI).
        int i = Array.IndexOf(args, "--screenshot");
        string? shots = i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        if (shots != null)
        {
            // In the CI smoke test nobody can click an error dialog away: write errors to a file and carry on.
            Directory.CreateDirectory(shots);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => File.AppendAllText(Path.Combine(shots, "ui-errors.txt"), e.Exception + Environment.NewLine + Environment.NewLine);
        }
        Application.Run(new MainForm { ScreenshotFolder = shots });
    }
}
