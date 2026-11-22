using CameraFinder.Core.Health;

namespace CameraFinder.App;

/// <summary>
/// The flash test: the screen flips between a checkerboard and its inverse every 2.5 s while the camera looks at it.
/// Each flip changes the whole picture, so the camera sends one much bigger frame; the live stream's delay tracker
/// times that frame against the moment of the flip. Gives camera + network delay, without the NVR.
/// </summary>
internal sealed class FlashTestWindow : Form
{
    public const int Flips = 8;
    private static readonly TimeSpan Every = TimeSpan.FromMilliseconds(2500);

    private readonly DelayTracker _tracker;
    private readonly System.Windows.Forms.Timer _timer = new();
    private bool _inverted, _running;
    private int _done;
    private string _message =
        "Point the camera at this screen so the pattern fills most of its picture, keep it still, then press Space or click.  (Esc cancels)";

    public DelayReport? Result { get; private set; }

    public FlashTestWindow(DelayTracker tracker)
    {
        _tracker = tracker;
        Text = "Camera delay - flash test";
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Maximized;
        TopMost = true;
        BackColor = Color.Black;
        DoubleBuffered = true;
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
            else if (e.KeyCode is Keys.Space or Keys.Enter && !_running) Begin();
        };
        MouseClick += (_, _) => { if (!_running && _done == 0) Begin(); };
        _timer.Interval = (int)Every.TotalMilliseconds;
        _timer.Tick += (_, _) => Flip();
        FormClosed += (_, _) => _timer.Dispose();
    }

    private void Begin()
    {
        _running = true;
        _tracker.ClearFlashes();
        _message = "Measuring… keep the camera still.";
        Invalidate();
        _timer.Start();
    }

    private void Flip()
    {
        if (_done >= Flips)
        {
            _timer.Stop();
            Result = _tracker.Report();
            _message = Result.CameraDelay is { } d
                ? $"Camera + network delay: {d:0.##} s  ({Result.FlashesSeen} of {Result.FlashesShown} flashes seen).  Closing…"
                : "The flashes were not seen in the video. Bring the camera closer so the pattern fills its picture, and try again.  Closing…";
            Invalidate();
            var close = new System.Windows.Forms.Timer { Interval = 3500 };
            close.Tick += (_, _) => { close.Dispose(); Close(); };
            close.Start();
            return;
        }
        _inverted = !_inverted;
        Invalidate();
        Update();                                  // paint now, then note the time of the flip
        _tracker.AddFlash(DelayTracker.Now());
        _done++;
        _message = $"Measuring… flash {_done} of {Flips}. Keep the camera still.";
        Invalidate(new Rectangle(0, ClientSize.Height - 70, ClientSize.Width, 70));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        int size = Math.Max(40, ClientSize.Width / 16);
        int band = 70;
        using var white = new SolidBrush(Color.White);
        g.Clear(Color.Black);
        for (int y = 0; y < ClientSize.Height - band; y += size)
            for (int x = 0; x < ClientSize.Width; x += size)
                if (((x / size + y / size) % 2 == 0) ^ _inverted)
                    g.FillRectangle(white, x, y, size, Math.Min(size, ClientSize.Height - band - y));
        using var bandBrush = new SolidBrush(Color.FromArgb(32, 32, 32));
        g.FillRectangle(bandBrush, 0, ClientSize.Height - band, ClientSize.Width, band);
        TextRenderer.DrawText(g, _message, new Font(Theme.Body.FontFamily, 14F), new Rectangle(20, ClientSize.Height - band, ClientSize.Width - 40, band),
            Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.WordBreak);
    }
}
