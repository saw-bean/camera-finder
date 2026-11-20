using System.Net;
using CameraFinder.Core.Health;
using CameraFinder.Core.Net;

namespace CameraFinder.App;

/// <summary>
/// Bandwidth test server (iperf3 -s). The technician runs an iperf3 client on a second laptop or phone at the far end
/// of a cable run - where the camera is, another switch, the NVR rack - pointed at one of this laptop's addresses.
/// Each finished test is shown with what it means for cameras (link speed it matches, loss, how many cameras fit).
/// </summary>
public sealed class SpeedTestWindow : Form
{
    private readonly string? _iperf;
    private readonly NumericUpDown _port = new() { Minimum = 1024, Maximum = 65535, Value = Iperf3Server.DefaultPort, Width = 80, Margin = new Padding(3, 5, 3, 3) };
    private readonly FluentButton _startStop = new() { Text = "Start server", Primary = true, MinimumSize = new Size(110, 32) };
    private readonly FluentButton _copy = new() { Text = "Copy", MinimumSize = new Size(70, 32) };
    private readonly Label _addresses = new() { AutoSize = true, Margin = new Padding(0, 4, 0, 6), MaximumSize = new Size(700, 0) };
    private readonly TextBox _output = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, WordWrap = true };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Tag = "secondary" };
    private CancellationTokenSource? _cts;

    public SpeedTestWindow(Form? owner = null)
    {
        _iperf = ExternalTool.Find(ExternalTool.Iperf3Exe);
        Text = "Speed test server (iperf3)";
        Width = 720;
        Height = 520;
        MinimumSize = new Size(560, 360);
        StartPosition = FormStartPosition.CenterParent;
        Padding = new Padding(10, 8, 10, 4);
        if (owner?.Icon != null) Icon = owner.Icon;

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
        bar.Controls.AddRange(new Control[] { new Label { Text = "Port", AutoSize = true, Margin = new Padding(0, 9, 2, 0) }, _port, _startStop, _copy });
        var info = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(0, 6, 0, 4) };
        info.Controls.Add(_addresses);
        var card = new FluentCard { Dock = DockStyle.Fill, RowCount = 1 };
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(_output, 0, 0);
        Controls.Add(card);
        Controls.Add(info);
        Controls.Add(bar);
        Controls.Add(_status);
        Theme.Apply(this);
        _output.Font = Theme.Body;
        _output.ForeColor = Theme.Text;

        ShowAddresses();
        if (_iperf == null)
        {
            _startStop.Enabled = false;
            _status.Text = "iperf3.exe was not found in the tools folder. Reinstall Camera Finder.";
        }
        else _status.Text = "Press Start server, then run the client on the other device.";
        _output.Text =
            "How to use:" + Environment.NewLine +
            "1. Press Start server here." + Environment.NewLine +
            "2. On a second laptop (or a phone iperf3 app) at the other end of the cable, run:" + Environment.NewLine +
            "     iperf3 -c <this laptop's address>          (speed towards this laptop)" + Environment.NewLine +
            "     iperf3 -c <this laptop's address> -R       (speed from this laptop)" + Environment.NewLine +
            "     iperf3 -c <this laptop's address> -u -b 50M   (UDP loss and jitter, like camera video)" + Environment.NewLine +
            "3. Each result appears here with what it means." + Environment.NewLine;

        _startStop.Click += (_, _) => { if (_cts == null) Start(); else Stop(); };
        _copy.Click += (_, _) => { try { Clipboard.SetText(_output.Text); } catch (Exception) { } };
        FormClosed += (_, _) => Stop();
    }

    private void ShowAddresses()
    {
        var addrs = Adapter.List().SelectMany(a => a.Addresses.Select(x => (a.Name, x.Address))).ToList();
        _addresses.Text = addrs.Count == 0
            ? "This laptop has no IPv4 address yet."
            : "Connect the client to: " + string.Join("   ", addrs.Select(a => $"{a.Address} ({a.Name})"));
    }

    private void Start()
    {
        if (_iperf == null) return;
        _cts = new CancellationTokenSource();
        _startStop.Text = "Stop server";
        _port.Enabled = false;
        int port = (int)_port.Value;
        var ct = _cts.Token;
        Append($"{DateTime.Now:HH:mm:ss}  Server started on port {port}. Windows may ask to allow iperf3 through the firewall: allow it on private networks.");
        var server = new Iperf3Server(_iperf);
        _ = Task.Run(async () =>
        {
            await server.RunAsync(port,
                r => Post(() => Append($"{r.Finished:HH:mm:ss}  " + string.Join(Environment.NewLine + "          ", Iperf3Server.Explain(r)))),
                s => Post(() => _status.Text = s), ct);
            Post(() => { if (_cts != null && _cts.Token == ct) Stop(); });
        });
    }

    private void Stop()
    {
        if (_cts == null) return;
        _cts.Cancel();
        _cts.Dispose();
        _cts = null;
        if (IsDisposed) return;
        _startStop.Text = "Start server";
        _port.Enabled = true;
        _status.Text = "Server stopped.";
    }

    private void Append(string text)
    {
        if (IsDisposed) return;
        _output.AppendText(Environment.NewLine + text + Environment.NewLine);
    }

    private void Post(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke(new MethodInvoker(() => { if (!IsDisposed) action(); })); }
        catch (InvalidOperationException) { /* closing */ }
    }
}
