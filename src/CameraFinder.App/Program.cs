namespace CameraFinder.App;

internal static class Program
{
    [STAThread]
    private static void Main()
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
        Application.Run(new MainForm());
    }
}
