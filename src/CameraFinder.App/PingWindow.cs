using System.Net;
using CameraFinder.Core.Discovery;

namespace CameraFinder.App;

/// <summary>
/// Continuous ping of one device, like "ping -t": one line per request, running totals, and a clear DOWN / UP line
/// when the device stops or starts answering (handy while a camera reboots or a cable is moved).
/// Several windows can be open at once; each stops when closed.
/// </summary>
public sealed class PingWindow : Form
{
    private const int MaxLines = 3000; // keep memory flat when left running for hours

    private readonly IPinger _pinger;
    private readonly IPAddress _target;
    private readonly IPAddress? _source;
    private readonly TextBox _output = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, WordWrap = false,
        BackColor = Color.Black, ForeColor = Color.Gainsboro, Font = new Font("Consolas", 9.5F),
    };
    private readonly Label _stats = new() { Dock = DockStyle.Bottom, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0), Tag = "secondary" };
    private readonly Label _state = new() { AutoSize = true, Margin = new Padding(4, 8, 14, 0) };
    private readonly FluentButton _startStop = new() { Text = "Stop" };
    private readonly FluentButton _copy = new() { Text = "Copy IP" };
    private readonly FluentButton _clear = new() { Text = "Clear" };
    private CancellationTokenSource? _cts;
    private int _lines;

    public PingWindow(IPinger pinger, IPAddress target, IPAddress? source, string description, Form? owner = null)
    {
        _pinger = pinger;
        _target = target;
        _source = source;
        Text = $"Ping {target}" + (string.IsNullOrWhiteSpace(description) ? "" : $" - {description}");
        Width = 640;
        Height = 440;
        StartPosition = FormStartPosition.CenterParent;
        Padding = new Padding(10, 8, 10, 4);
        if (owner?.Icon != null) Icon = owner.Icon;

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 6), WrapContents = false };
        bar.Controls.AddRange(new Control[] { _state, _startStop, _copy, _clear });
        var console = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 6, 4, 6), BackColor = Color.FromArgb(12, 12, 12) };
        console.Controls.Add(_output);
        Controls.Add(console);
        Controls.Add(_stats);
        Controls.Add(bar);
        Theme.Apply(this);
        _state.Font = Theme.Subtitle;
        _output.BackColor = Color.FromArgb(12, 12, 12);
        _output.ForeColor = Color.FromArgb(204, 204, 204);
        _output.BorderStyle = BorderStyle.None;
        _output.Font = Theme.Mono;

        _startStop.Click += (_, _) => { if (_cts == null) Start(); else Stop(); };
        _copy.Click += async (_, _) =>
        {
            try { Clipboard.SetText(_target.ToString()); }
            catch (Exception) { return; } // clipboard held by another app
            _copy.Text = "Copied ✓";     // brief confirmation, then back to normal
            await Task.Delay(1500);
            if (!IsDisposed) _copy.Text = "Copy IP";
        };
        _clear.Click += (_, _) => { _output.Clear(); _lines = 0; };
        Shown += (_, _) => Start();
        FormClosed += (_, _) => Stop();
    }

    private void Start()
    {
        _cts = new CancellationTokenSource();
        _startStop.Text = "Stop";
        Write($"Pinging {_target}" + (_source != null ? $" from {_source}" : "") + " every second. Close this window to stop.");
        SetState("…", Theme.TextSecondary);
        var ct = _cts.Token;
        _ = ContinuousPing.RunAsync(_pinger, _target, _source, ContinuousPing.DefaultInterval, ContinuousPing.DefaultTimeout,
            (r, stats, change) => Post(() => Show(r, stats, change)), ct);
    }

    private void Stop()
    {
        if (_cts == null) return;
        _cts.Cancel();
        _cts.Dispose();
        _cts = null;
        if (!IsDisposed)
        {
            _startStop.Text = "Start";
            Write("Stopped.");
        }
    }

    private void Show(PingResult r, PingStats stats, string? change)
    {
        if (_cts == null) return; // a reply that arrived after Stop
        string time = DateTime.Now.ToString("HH:mm:ss");
        if (change == "DOWN")
            Write($"{time}  *** {_target} is DOWN - no reply to the last {PingStats.DownAfter} requests ***");
        Write(r.Success ? $"{time}  Reply from {_target}: time={(r.RoundTripMs < 1 ? "<1" : r.RoundTripMs.ToString())} ms" : $"{time}  {r.Status}.");
        if (change == "UP")
            Write($"{time}  *** {_target} is UP again ***");

        // Windows 11 status colours (readable on light and dark).
        var green = Theme.Dark ? Color.FromArgb(108, 203, 95) : Color.FromArgb(15, 123, 15);
        var red = Theme.Dark ? Color.FromArgb(255, 153, 164) : Color.FromArgb(196, 43, 28);
        var amber = Theme.Dark ? Color.FromArgb(252, 225, 0) : Color.FromArgb(157, 93, 0);
        if (r.Success) SetState("● UP", green);
        else if (stats.IsDown || stats.Received == 0) SetState(stats.Received == 0 ? "● NO REPLY" : "● DOWN", red);
        else SetState("● LOSING", amber);
        _stats.Text = stats.ToString();
    }

    private void SetState(string text, Color color) { _state.Text = text; _state.ForeColor = color; }

    private void Write(string line)
    {
        if (IsDisposed) return;
        if (++_lines > MaxLines)
        {
            // Drop the oldest third in one go rather than one line at a time.
            var keep = _output.Lines.Skip(MaxLines / 3).ToArray();
            _output.Lines = keep;
            _lines = keep.Length + 1;
        }
        _output.AppendText(line + Environment.NewLine);
    }

    private void Post(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke(new MethodInvoker(() => { if (!IsDisposed) action(); })); }
        catch (InvalidOperationException) { /* closing */ }
    }
}
