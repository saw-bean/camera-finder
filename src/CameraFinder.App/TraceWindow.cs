using System.Net;
using CameraFinder.Core.Health;

namespace CameraFinder.App;

/// <summary>
/// mtr-style trace: every hop between the laptop and the device, refreshed every second with loss and delay per hop,
/// and one plain sentence saying where loss starts. Runs until stopped or closed.
/// </summary>
public sealed class TraceWindow : Form
{
    private readonly PathTracer _tracer;
    private IPAddress _target;
    private IPAddress? _source;
    private readonly IPAddress _device;
    private readonly IPAddress? _deviceSource;
    private readonly string _description;
    private readonly TextBox _address = new() { Width = 200, Margin = new Padding(3, 5, 3, 3), PlaceholderText = "IP or name, e.g. 8.8.8.8" };
    private readonly FluentButton _go = new() { Text = "Trace", Primary = true, MinimumSize = new Size(70, 32) };
    private readonly FluentListView _hops = new() { Dock = DockStyle.Fill, MultiSelect = false, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };
    private readonly Label _summary = new() { Dock = DockStyle.Bottom, Height = 44, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0) };
    private readonly Label _rounds = new() { AutoSize = true, Margin = new Padding(10, 9, 3, 0), Tag = "secondary" };
    private readonly FluentButton _startStop = new() { Text = "Stop" };
    private readonly FluentButton _copy = new() { Text = "Copy" };
    private CancellationTokenSource? _cts;
    private TraceReport? _last;

    private static readonly (string Name, int Width, HorizontalAlignment Align)[] Columns =
    {
        ("Hop", 44, HorizontalAlignment.Right), ("Address", 150, HorizontalAlignment.Left), ("Loss %", 64, HorizontalAlignment.Right),
        ("Sent", 54, HorizontalAlignment.Right), ("Last", 54, HorizontalAlignment.Right), ("Avg", 54, HorizontalAlignment.Right),
        ("Best", 54, HorizontalAlignment.Right), ("Worst", 58, HorizontalAlignment.Right), ("Jitter", 58, HorizontalAlignment.Right),
    };

    public TraceWindow(IHopProber prober, IPAddress target, IPAddress? source, string description, Form? owner = null)
    {
        _tracer = new PathTracer(prober);
        _target = _device = target;
        _source = _deviceSource = source;
        _description = description;
        _address.Text = target.ToString();
        SetTitle();
        Width = 720;
        Height = 420;
        MinimumSize = new Size(560, 300);
        StartPosition = FormStartPosition.CenterParent;
        Padding = new Padding(10, 8, 10, 4);
        if (owner?.Icon != null) Icon = owner.Icon;

        foreach (var c in Columns) _hops.Columns.Add(c.Name, c.Width, c.Align);
        _hops.SmallImageList = new ImageList { ImageSize = new Size(1, 26) };
        _hops.HighlightColumn = 2; // loss % in red on lossy hops
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 6) };
        bar.Controls.AddRange(new Control[] { new Label { Text = "To", AutoSize = true, Margin = new Padding(0, 9, 2, 0) }, _address, _go, _startStop, _copy, _rounds });
        var card = new FluentCard { Dock = DockStyle.Fill, RowCount = 1 };
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(_hops, 0, 0);
        Controls.Add(card);
        Controls.Add(_summary);
        Controls.Add(bar);
        Theme.Apply(this);

        _startStop.Click += (_, _) => { if (_cts == null) Start(); else Stop(); };
        _go.Click += async (_, _) => await TraceToAsync(_address.Text.Trim());
        _address.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await TraceToAsync(_address.Text.Trim()); } };
        _copy.Click += (_, _) =>
        {
            if (_last == null) return;
            try { Clipboard.SetText(AsText(_last)); } catch (Exception) { }
        };
        Shown += (_, _) => Start();
        FormClosed += (_, _) => Stop();
    }

    private void SetTitle() =>
        Text = $"Trace (mtr) {_target}" + (_target.Equals(_device) && !string.IsNullOrWhiteSpace(_description) ? $" - {_description}" : "");

    /// <summary>Trace to any address or host name (NVR, gateway, 8.8.8.8, a remote site...).</summary>
    private async Task TraceToAsync(string text)
    {
        if (text.Length == 0) return;
        IPAddress? ip = null;
        if (!IPAddress.TryParse(text, out ip))
        {
            try { ip = (await System.Net.Dns.GetHostAddressesAsync(text)).FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork); }
            catch (Exception) { ip = null; }
        }
        if (ip == null || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            _summary.Text = $"\"{text}\" is not an IPv4 address or a name that resolves to one.";
            return;
        }
        Stop();
        _target = ip;
        // The device's own (possibly temporary) source address only makes sense for the device; let Windows pick otherwise.
        _source = ip.Equals(_device) ? _deviceSource : null;
        _hops.Items.Clear();
        _last = null;
        SetTitle();
        Start();
    }

    private void Start()
    {
        _cts = new CancellationTokenSource();
        _startStop.Text = "Stop";
        _summary.Text = $"Tracing the path to {_target}" + (_source != null ? $" from {_source}" : "") + "…";
        var ct = _cts.Token;
        _ = Task.Run(() => _tracer.RunAsync(_target, _source, 0, r => Post(() => Show(r)), ct));
    }

    private void Stop()
    {
        if (_cts == null) return;
        _cts.Cancel();
        _cts.Dispose();
        _cts = null;
        if (!IsDisposed) _startStop.Text = "Start";
    }

    private void Show(TraceReport r)
    {
        if (_cts == null) return;
        _last = r;
        _hops.BeginUpdate();
        while (_hops.Items.Count > r.Hops.Count) _hops.Items.RemoveAt(_hops.Items.Count - 1);
        for (int i = 0; i < r.Hops.Count; i++)
        {
            var cells = Cells(r.Hops[i]);
            if (i >= _hops.Items.Count) _hops.Items.Add(new ListViewItem(cells));
            else for (int c = 0; c < cells.Length; c++) _hops.Items[i].SubItems[c].Text = cells[c];
            _hops.Items[i].ForeColor = r.Hops[i].LossPercent >= 2 && r.Hops[i].Sent >= 3 ? (Theme.Dark ? Color.FromArgb(255, 153, 164) : Color.FromArgb(196, 43, 28)) : Theme.Text;
        }
        _hops.EndUpdate();
        _rounds.Text = $"{r.Rounds} round{(r.Rounds == 1 ? "" : "s")}";
        _summary.Text = PathTracer.Summary(r);
    }

    private static string[] Cells(Hop h) => new[]
    {
        h.Ttl.ToString(), h.Address?.ToString() ?? "(no reply)", $"{h.LossPercent:0.#}", h.Sent.ToString(),
        h.Received == 0 ? "-" : h.LastMs.ToString(), h.Received == 0 ? "-" : $"{h.AvgMs:0}", h.Received == 0 ? "-" : h.BestMs.ToString(),
        h.Received == 0 ? "-" : h.WorstMs.ToString(), h.Received == 0 ? "-" : $"{h.JitterMs:0.#}",
    };

    private string AsText(TraceReport r) =>
        $"Trace to {_target}, {r.Rounds} rounds" + Environment.NewLine +
        "Hop  Address          Loss%  Sent  Last   Avg  Best  Worst  Jitter" + Environment.NewLine +
        string.Join(Environment.NewLine, r.Hops.Select(h =>
            $"{h.Ttl,3}  {h.Address?.ToString() ?? "???",-15}  {h.LossPercent,5:0.#}  {h.Sent,4}  {h.LastMs,4}  {h.AvgMs,4:0}  {h.BestMs,4}  {h.WorstMs,5}  {h.JitterMs,6:0.#}")) +
        Environment.NewLine + PathTracer.Summary(r) + Environment.NewLine;

    private void Post(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke(new MethodInvoker(() => { if (!IsDisposed) action(); })); }
        catch (InvalidOperationException) { /* closing */ }
    }
}
