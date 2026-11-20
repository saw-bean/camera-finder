using System.Net;
using CameraFinder.Core.Health;
using CameraFinder.Core.Model;

namespace CameraFinder.App;

/// <summary>
/// Camera health check for one device: collects ping, path, ONVIF (identity, clock, system log, video settings) and
/// a few seconds of video, then shows a readable diagnosis - what is most likely wrong and what to try first. The
/// plain-language event timeline and the raw collected data are one click away for anyone who wants to check it.
/// </summary>
internal sealed class HealthWindow : Form
{
    private readonly Device _device;
    private readonly HealthCheck _check;
    private readonly IPAddress? _source;
    private readonly bool _sourceIsTemporary;
    private readonly CredentialStore? _store;
    private readonly string _key;

    private readonly TextBox _user = new() { Width = 140, PlaceholderText = "admin", Margin = new Padding(3, 4, 3, 3) };
    private readonly TextBox _password = new() { Width = 140, UseSystemPasswordChar = true, Margin = new Padding(3, 4, 3, 3) };
    private readonly CheckBox _remember = new() { Text = "Remember", AutoSize = true, Margin = new Padding(8, 7, 3, 0) };
    private readonly CheckBox _video = new() { Text = "Check video", AutoSize = true, Checked = true, Margin = new Padding(8, 7, 3, 0) };
    private readonly FluentButton _run = new() { Text = "Run check", Primary = true, MinimumSize = new Size(100, 32) };
    private readonly FluentButton _copy = new() { Text = "Copy", MinimumSize = new Size(70, 32) };
    private readonly FluentButton _showDiagnosis = new() { Text = "Diagnosis", MinimumSize = new Size(90, 30) };
    private readonly FluentButton _showEvents = new() { Text = "Events", MinimumSize = new Size(80, 30) };
    private readonly FluentButton _showRaw = new() { Text = "Collected data", MinimumSize = new Size(110, 30) };
    private readonly TextBox _output = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, WordWrap = true };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Tag = "secondary" };

    private HealthResult? _result;
    private CancellationTokenSource? _cts;
    private int _view; // 0 diagnosis, 1 events, 2 raw

    public HealthWindow(Device device, HealthCheck check, IPAddress? source, bool sourceIsTemporary, CredentialStore? store, Credentials? demoLogin, Form? owner)
    {
        _device = device;
        _check = check;
        _source = source;
        _sourceIsTemporary = sourceIsTemporary;
        _store = store;
        _key = CredentialStore.KeyFor(device.Mac, device.Ip.ToString());
        Text = $"Health check {device.Ip}" + (string.IsNullOrWhiteSpace(device.Description) ? "" : $" - {device.Description}");
        Width = 760;
        Height = 620;
        MinimumSize = new Size(620, 420);
        StartPosition = FormStartPosition.CenterParent;
        Padding = new Padding(10, 8, 10, 4);
        if (owner?.Icon != null) Icon = owner.Icon;

        var login = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 4) };
        login.Controls.AddRange(new Control[] { Caption("User"), _user, Caption("Password", 8), _password, _remember, _video, _run, _copy });
        var views = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 2, 0, 6) };
        views.Controls.AddRange(new Control[] { _showDiagnosis, _showEvents, _showRaw });
        var card = new FluentCard { Dock = DockStyle.Fill, RowCount = 1 };
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(_output, 0, 0);

        Controls.Add(card);
        Controls.Add(views);
        Controls.Add(login);
        Controls.Add(_status);
        Theme.Apply(this);
        _output.Font = Theme.Body;
        _output.ForeColor = Theme.Text;

        var tips = new ToolTip { AutoPopDelay = 15000 };
        tips.SetToolTip(_user, "Camera login (ONVIF / web user). Needed to read the camera's log and video settings.");
        tips.SetToolTip(_remember, "Save this login for this camera, encrypted for your Windows user (DPAPI). The last login used is also offered for new cameras.");
        tips.SetToolTip(_video, "Watch the camera's video stream for 8 seconds with ffprobe to measure frame rate, bitrate and freezes.");
        tips.SetToolTip(_showEvents, "Every measurement and camera log line, rewritten as a plain sentence.");
        tips.SetToolTip(_showRaw, "What was collected, close to how the camera sent it.");

        var saved = demoLogin ?? store?.Get(_key) ?? store?.Get(CredentialStore.LastUsedKey);
        if (saved != null) { _user.Text = saved.User; _password.Text = saved.Password; }
        _remember.Checked = store?.Get(_key) != null;
        _remember.Enabled = store != null;

        _run.Click += async (_, _) => { if (_cts == null) await RunAsync(); else _cts.Cancel(); };
        _copy.Click += (_, _) =>
        {
            if (_output.TextLength == 0) return;
            try { Clipboard.SetText(_output.Text); _status.Text = "Copied to the clipboard."; }
            catch (Exception) { /* clipboard busy */ }
        };
        _showDiagnosis.Click += (_, _) => ShowView(0);
        _showEvents.Click += (_, _) => ShowView(1);
        _showRaw.Click += (_, _) => ShowView(2);
        _password.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter && _cts == null) { e.SuppressKeyPress = true; await RunAsync(); } };
        FormClosed += (_, _) => _cts?.Cancel();
        Shown += async (_, _) =>
        {
            ShowView(0);
            if (_user.Text.Length > 0) await RunAsync();
            else
            {
                _output.Text = "Enter the camera's user name and password, then press Run check." + Environment.NewLine + Environment.NewLine +
                               "Without a login the check still pings the camera, traces the path and reads its clock, " +
                               "but cannot read its log or video settings.";
                _user.Focus();
            }
        };
    }

    private static Label Caption(string text, int left = 0) => new() { Text = text, AutoSize = true, Margin = new Padding(left, 9, 2, 0) };

    private async Task RunAsync()
    {
        var login = _user.Text.Trim().Length == 0 ? null : new Credentials(_user.Text.Trim(), _password.Text);
        if (_store != null && login != null)
        {
            if (_remember.Checked) _store.Save(_key, login);
            else _store.Forget(_key);
        }

        _cts = new CancellationTokenSource();
        _run.Text = "Stop";
        _user.Enabled = _password.Enabled = _video.Enabled = false;
        _output.Text = "Checking… this takes about 20 seconds.";
        var progress = new Progress<string>(s => { if (!IsDisposed) _status.Text = s; });
        try
        {
            var options = new HealthCheckOptions { Login = login, CheckStream = _video.Checked, SourceIsTemporary = _sourceIsTemporary };
            _result = await Task.Run(() => _check.RunAsync(_device, _source, options, progress, _cts.Token));
            if (IsDisposed) return;
            int problems = _result.Findings.Count(f => f.Severity >= Severity.Warning);
            _status.Text = problems == 0 ? "Done - no problems found." : $"Done - {problems} problem{(problems == 1 ? "" : "s")} found.";
            ShowView(_view);
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed) { _status.Text = "Stopped."; _output.Text = "Check stopped."; }
        }
        catch (Exception ex)
        {
            if (!IsDisposed) { _status.Text = "The check failed."; _output.Text = "The check failed: " + ex.Message; }
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            if (!IsDisposed)
            {
                _run.Text = "Run again";
                _user.Enabled = _password.Enabled = _video.Enabled = true;
            }
        }
    }

    private void ShowView(int view)
    {
        _view = view;
        _showDiagnosis.Primary = view == 0;
        _showEvents.Primary = view == 1;
        _showRaw.Primary = view == 2;
        foreach (var b in new[] { _showDiagnosis, _showEvents, _showRaw }) b.Invalidate();
        if (_result == null) return;
        // Diagnosis reads as prose; the other two line up in columns.
        _output.Font = view == 0 ? Theme.Body : Theme.Mono;
        _output.WordWrap = view == 0;
        string text = view switch
        {
            0 => _result.Diagnosis,
            1 => DiagnosisWriter.Timeline(_result.Events),
            _ => DiagnosisWriter.CollectedData(_result.Snapshot),
        };
        _output.Text = text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine); // a TextBox needs CRLF
        _output.SelectionStart = 0;
        _output.ScrollToCaret();
    }
}
