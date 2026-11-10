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
    private readonly Label _stats = new() { Dock = DockStyle.Bottom, Height = 24, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0) };
    private readonly Label _state = new() { AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold), Margin = new Padding(6, 6, 12, 0) };
    private readonly Button _startStop = new() { Text = "Stop", Width = 80 };
    private readonly Button _copy = new() { Text = "Copy", Width = 70 };
    private readonly Button _clear = new() { Text = "Clear", Width = 70 };
    private CancellationTokenSource? _cts;
    private int _lines;

    public PingWindow(IPinger pinger, IPAddress target, IPAddress? source, string description)
    {
        _pinger = pinger;
        _target = target;
        _source = source;
        Text = $"Ping {target}" + (string.IsNullOrWhiteSpace(description) ? "" : $" - {description}");
        Width = 620;
        Height = 420;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9F);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(4, 4, 4, 0), WrapContents = false };
        bar.Controls.AddRange(new Control[] { _state, _startStop, _copy, _clear });
        Controls.Add(_output);
        Controls.Add(_stats);
        Controls.Add(bar);

        _startStop.Click += (_, _) => { if (_cts == null) Start(); else Stop(); };
        _copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(_output.Text + Environment.NewLine + _stats.Text); }
            catch (Exception) { /* clipboard busy */ }
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
        SetState("…", Color.DimGray);
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

        if (r.Success) SetState("UP", Color.ForestGreen);
        else if (stats.IsDown || stats.Received == 0) SetState(stats.Received == 0 ? "NO REPLY" : "DOWN", Color.Firebrick);
        else SetState("LOSING", Color.DarkOrange);
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
