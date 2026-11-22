using System.Drawing.Drawing2D;
using System.Net;
using CameraFinder.Core.Discovery;
using CameraFinder.Core.Health;
using CameraFinder.Core.Model;

namespace CameraFinder.App;

/// <summary>What the panel needs from the main window for the current scan (real network or demo site).</summary>
internal sealed class DeviceTools
{
    /// <summary>Makes the device reachable; mayJoin = allowed to add a temporary IP.</summary>
    public required Func<Device, bool, Task<WebPageResult>> Reach { get; init; }
    /// <summary>Is this laptop address a temporary one (added with skipassource)?</summary>
    public required Func<IPAddress?, bool> IsTemporary { get; init; }
    public required Func<HealthCheck> Health { get; init; }
    public required ILiveStreamSource Live { get; init; }
    public CredentialStore? Store { get; init; }
    /// <summary>Login the demo cameras accept.</summary>
    public Credentials? DemoLogin { get; init; }
    public required Func<bool> SafeMode { get; init; }
    public required Action<string> Log { get; init; }

    /// <summary>Managed switches of this site (found once per scan over SNMP).</summary>
    public required SwitchDirectory Switches { get; init; }
    /// <summary>Addresses to ask whether they are a switch: every device found by the scan.</summary>
    public required Func<IEnumerable<IPAddress>> SwitchCandidates { get; init; }
    /// <summary>Laptop address to ask a device from, or null when it is not reachable without adding an address.</summary>
    public required Func<IPAddress, IPAddress?> SourceFor { get; init; }
    /// <summary>SNMP read communities to try, the saved one first.</summary>
    public required Func<IReadOnlyList<string>> Communities { get; init; }
    public Action<string>? SaveCommunity { get; init; }
}

/// <summary>
/// Everything about the selected device, filled in automatically when it is selected: who it is, its health
/// diagnosis, the bandwidth its video stream is using right now (live graph), the ffmpeg stream check, and a button to
/// its web page. Selecting another device stops the checks for this one.
/// </summary>
internal sealed class DevicePanel : Panel
{
    public event Action<Device>? OpenWebPage;
    public event Action<Device, IPAddress?>? PingRequested;
    public event Action<Device, IPAddress?>? TraceRequested;

    // Stream addresses typed in by hand, per device IP (cameras with ONVIF off, phones, encoders...).
    private static readonly Dictionary<string, string> CustomUrls = new();

    // Logins a camera refused this session: never retried automatically, so clicking through cameras cannot lock accounts.
    private static readonly HashSet<string> Refused = new();

    private readonly Label _title = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 2) };
    private readonly Label _subtitle = new() { AutoSize = true, Tag = "secondary", Margin = new Padding(0, 0, 0, 6) };
    private readonly Label _state = new() { AutoSize = true, Margin = new Padding(0, 2, 0, 8) };
    private readonly FluentButton _open = new() { Text = "Open web page", Primary = true, MinimumSize = new Size(120, 32) };
    private readonly FluentButton _ping = new() { Text = "Ping", MinimumSize = new Size(60, 32) };
    private readonly FluentButton _trace = new() { Text = "Trace", MinimumSize = new Size(60, 32) };
    private readonly FluentButton _copy = new() { Text = "Copy IP", MinimumSize = new Size(70, 32) };
    private readonly FluentButton _again = new() { Text = "↻", MinimumSize = new Size(36, 32) };

    private readonly FlowLayoutPanel _reachRow = new() { AutoSize = true, WrapContents = true, Visible = false, Margin = new Padding(0, 0, 0, 6) };
    private readonly Label _reachNote = new() { AutoSize = true, MaximumSize = new Size(380, 0), Margin = new Padding(0, 8, 6, 0) };
    private readonly FluentButton _join = new() { Text = "Add temporary IP" };

    private readonly FlowLayoutPanel _loginRow = new() { AutoSize = true, WrapContents = true, Visible = false, Margin = new Padding(0, 0, 0, 6) };
    private readonly Label _loginNote = new() { AutoSize = true, MaximumSize = new Size(380, 0), Margin = new Padding(0, 4, 0, 2) };
    private readonly TextBox _user = new() { Width = 110, PlaceholderText = "user", Margin = new Padding(0, 4, 3, 3) };
    private readonly TextBox _password = new() { Width = 110, PlaceholderText = "password", UseSystemPasswordChar = true, Margin = new Padding(3, 4, 3, 3) };
    private readonly FluentButton _useLogin = new() { Text = "Use", Primary = true, MinimumSize = new Size(60, 32) };

    private readonly Label _pathTitle = Heading("Path (trace)");
    private readonly Label _path = new() { AutoSize = true, MaximumSize = new Size(400, 0), Margin = new Padding(0, 0, 0, 4) };
    private readonly Label _portTitle = Heading("Switch port bandwidth (SNMP)");
    private readonly Label _port = new() { AutoSize = true, MaximumSize = new Size(400, 0), Tag = "secondary", Margin = new Padding(0, 0, 0, 2) };
    private readonly Label _portKbps = new() { AutoSize = true, Text = "", Margin = new Padding(0, 0, 0, 0) };
    private readonly Sparkline _portGraph = new() { Height = 56, Margin = new Padding(0, 2, 0, 4), Visible = false };
    private readonly FlowLayoutPanel _snmpRow = new() { AutoSize = true, WrapContents = true, Visible = false, FlowDirection = FlowDirection.TopDown, Margin = new Padding(0, 0, 0, 4) };
    private readonly Label _snmpNote = new() { AutoSize = true, MaximumSize = new Size(380, 0), Margin = new Padding(0, 2, 0, 2) };
    private readonly TextBox _community = new() { Width = 160, PlaceholderText = "SNMP community", Margin = new Padding(0, 4, 3, 3) };
    private readonly FluentButton _useCommunity = new() { Text = "Use", Primary = true, MinimumSize = new Size(60, 32) };
    private readonly Label _videoTitle = Heading("Video check (ffmpeg)");
    private readonly Label _delayTitle = Heading("Live view delay");
    private readonly Label _delay = new() { AutoSize = true, MaximumSize = new Size(400, 0), Margin = new Padding(0, 0, 0, 4) };
    private readonly FluentButton _flash = new() { Text = "Measure camera delay (flash test)…", MinimumSize = new Size(120, 32), Enabled = false };
    private readonly Label _diagTitle = Heading("Diagnosis");
    private readonly Label _bwTitle = new() { Text = "Video stream bandwidth (RTSP)", AutoSize = true, Margin = new Padding(0, 10, 8, 2) };
    private readonly ComboBox _streams = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, Margin = new Padding(0, 0, 0, 0) };
    private readonly Label _kbps = new() { AutoSize = true, Text = "-", Margin = new Padding(0, 4, 0, 0) };
    private readonly Label _bwDetails = new() { AutoSize = true, MaximumSize = new Size(400, 0), Tag = "secondary", Margin = new Padding(0, 0, 0, 4) };
    private readonly Sparkline _graph = new() { Height = 70, Dock = DockStyle.Top, Margin = new Padding(0, 2, 0, 6) };
    private readonly FlowLayoutPanel _urlRow = new() { AutoSize = true, WrapContents = true, Visible = false, FlowDirection = FlowDirection.TopDown, Margin = new Padding(0, 0, 0, 6) };
    private readonly Label _urlNote = new() { AutoSize = true, MaximumSize = new Size(380, 0), Margin = new Padding(0, 4, 0, 2) };
    private readonly TextBox _url = new() { Width = 290, PlaceholderText = "rtsp://address:554/path", Margin = new Padding(0, 4, 3, 3) };
    private readonly FluentButton _useUrl = new() { Text = "Use", Primary = true, MinimumSize = new Size(60, 32) };
    private readonly Label _video = new() { AutoSize = true, MaximumSize = new Size(400, 0), Margin = new Padding(0, 2, 0, 8) };

    private readonly FluentButton _showDiagnosis = new() { Text = "Diagnosis", MinimumSize = new Size(80, 28), Primary = true };
    private readonly FluentButton _showEvents = new() { Text = "Events", MinimumSize = new Size(64, 28) };
    private readonly FluentButton _showRaw = new() { Text = "Data", MinimumSize = new Size(56, 28) };
    private readonly TextBox _output = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, WordWrap = true };

    private static Label Heading(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 10, 0, 2) };

    private DeviceTools? _tools;
    private CancellationTokenSource? _portCts;
    private SwitchPortReport? _portReport;
    private readonly List<LiveSample> _portHistory = new();
    private Device? _device;
    private CancellationTokenSource? _cts;      // the whole check for the current device
    private CancellationTokenSource? _liveCts;  // just the live bandwidth (restarted when another stream is picked)
    private HealthResult? _result;
    private HealthSnapshot? _snapshot;
    private WebPageResult? _reach;
    private Credentials? _login;
    private LiveStats? _live;
    private bool _streamFromLive;
    private int _view;
    private bool _waitingForScan;
    private bool _fillingStreams;
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 350 };

    public DevicePanel()
    {
        Dock = DockStyle.Fill;
        // On a small screen the panel scrolls instead of squeezing the diagnosis out of sight.
        AutoScroll = true;
        AutoScrollMinSize = new Size(0, 1280);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14, 10, 14, 10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var buttons = Flow(_open, _ping, _trace, _copy);
        var titleRow = Flow(_title, _again);
        _again.Margin = new Padding(10, 0, 0, 0);
        _reachRow.Controls.AddRange(new Control[] { _reachNote, _join });
        var loginFields = Flow(_user, _password, _useLogin);
        _loginRow.FlowDirection = FlowDirection.TopDown;
        _loginRow.Controls.AddRange(new Control[] { _loginNote, loginFields });
        var bwHeader = Flow(_bwTitle, _streams);
        _snmpRow.Controls.AddRange(new Control[] { _snmpNote, Flow(_community, _useCommunity) });
        _urlRow.Controls.AddRange(new Control[] { _urlNote, Flow(_url, _useUrl) });
        var views = Flow(_showDiagnosis, _showEvents, _showRaw);

        var rows = new Control[]
        {
            titleRow, _subtitle, _state, buttons, _reachRow, _loginRow,
            _pathTitle, _path,
            _portTitle, _portKbps, _port, _snmpRow, _portGraph,
            bwHeader, _kbps, _bwDetails, _urlRow, _graph,
            _videoTitle, _video,
            _delayTitle, _delay, _flash,
            _diagTitle, views, _output,
        };
        layout.RowCount = rows.Length;
        for (int i = 0; i < rows.Length; i++)
        {
            layout.RowStyles.Add(new RowStyle(i == rows.Length - 1 ? SizeType.Percent : SizeType.AutoSize, 100));
            if (rows[i] is not Sparkline and not TextBox) rows[i].Anchor = AnchorStyles.Left | AnchorStyles.Top;
            layout.Controls.Add(rows[i], 0, i);
        }
        _graph.Dock = DockStyle.Fill;
        _graph.MinimumSize = new Size(100, 70);
        _portGraph.Dock = DockStyle.Fill;
        _portGraph.MinimumSize = new Size(100, 56);
        _output.MinimumSize = new Size(100, 240);
        Controls.Add(layout);

        var tips = new ToolTip { AutoPopDelay = 15000 };
        tips.SetToolTip(_kbps, "Bandwidth the camera's video stream uses right now, measured on the wire every second.");
        tips.SetToolTip(_streams, "Which of the camera's streams to measure (the NVR usually records the main stream).");
        tips.SetToolTip(_trace, "Path to the device hop by hop, like mtr.");
        tips.SetToolTip(_again, "Run all checks again.");

        _open.Click += (_, _) => { if (_device != null) OpenWebPage?.Invoke(_device); };
        _ping.Click += (_, _) => { if (_device != null) PingRequested?.Invoke(_device, _reach?.Source); };
        _trace.Click += (_, _) => { if (_device != null) TraceRequested?.Invoke(_device, _reach?.Source); };
        _copy.Click += async (_, _) =>
        {
            if (_device == null) return;
            try { Clipboard.SetText(_device.Ip.ToString()); } catch (Exception) { return; }
            _copy.Text = "Copied ✓";
            await Task.Delay(1500);
            if (!IsDisposed) _copy.Text = "Copy IP";
        };
        _again.Click += (_, _) => Start();
        _join.Click += (_, _) => Start(joinAllowed: true);
        _useLogin.Click += (_, _) =>
        {
            if (_device == null || _user.Text.Trim().Length == 0) return;
            _login = new Credentials(_user.Text.Trim(), _password.Text);
            Refused.Remove(RefusedKey(_device, _login));
            Start(loginOverride: _login);
        };
        _useUrl.Click += (_, _) =>
        {
            string u = _url.Text.Trim();
            if (_device == null || !u.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)) { _urlNote.Text = "Enter an address starting with rtsp://"; return; }
            CustomUrls[_device.Ip.ToString()] = u;
            Start();
        };
        _flash.Click += (_, _) => RunFlashTest();
        _useCommunity.Click += (_, _) =>
        {
            string c = _community.Text.Trim();
            if (c.Length == 0 || _tools == null) return;
            _tools.SaveCommunity?.Invoke(c);
            _tools.Switches.Reset();
            Start();
        };
        _community.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _useCommunity.PerformClick(); } };
        _url.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _useUrl.PerformClick(); } };
        _password.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _useLogin.PerformClick(); } };
        _streams.SelectedIndexChanged += (_, _) => { if (!_fillingStreams) StartLive(); };
        _showDiagnosis.Click += (_, _) => ShowView(0);
        _showEvents.Click += (_, _) => ShowView(1);
        _showRaw.Click += (_, _) => ShowView(2);
        _debounce.Tick += (_, _) => { _debounce.Stop(); Start(); };
        Disposed += (_, _) => { _cts?.Cancel(); _liveCts?.Cancel(); _debounce.Dispose(); };

        Clear();
    }

    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 4) };
        f.Controls.AddRange(controls);
        return f;
    }

    /// <summary>Call after Theme.Apply on the main window: fonts and colours the theme does not set.</summary>
    public void ApplyTheme()
    {
        _title.Font = Theme.Subtitle;
        foreach (var h in new[] { _bwTitle, _pathTitle, _portTitle, _videoTitle, _delayTitle, _diagTitle }) h.Font = Theme.BodyStrong;
        _portKbps.Font = new Font(Theme.Subtitle.FontFamily, 14F, FontStyle.Bold);
        _portGraph.BackColor = Theme.Card;
        _path.Font = Theme.Body;
        _kbps.Font = new Font(Theme.Subtitle.FontFamily, 20F, FontStyle.Bold);
        _output.Font = Theme.Body;
        _output.ForeColor = Theme.Text;
        _graph.BackColor = Theme.Card;
        if (Theme.Icons != null) { _again.Font = Theme.Icons; _again.Text = "\uE72C"; } // Refresh icon
    }

    public DeviceTools? Tools
    {
        get => _tools;
        set { _tools = value; Clear(); }
    }

    /// <summary>Show a device (null = nothing selected). Checks start after a short pause, so arrowing through the list does not flood the network.</summary>
    public void ShowDevice(Device? d, bool scanning)
    {
        if (ReferenceEquals(d, _device))
        {
            // Same device (its row was updated): keep the checks running, just refresh the header.
            RefreshInfo();
            if (!scanning) ScanFinished();
            return;
        }
        Stop();
        _device = d;
        _url.Text = "";
        _result = null;
        _snapshot = null;
        _flashResult = null;
        _reach = null;
        _login = null;
        if (d == null) { Clear(); return; }
        ResetSections();
        RefreshInfo();
        _waitingForScan = scanning;
        if (scanning) { SetState("Checks start when the scan finishes.", Theme.TextSecondary); return; }
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>The scan finished: start the checks that were waiting for it.</summary>
    public void ScanFinished()
    {
        if (_device != null && _waitingForScan) { _waitingForScan = false; Start(); }
    }

    /// <summary>Stops everything (e.g. before a new scan).</summary>
    public void Stop()
    {
        _debounce.Stop();
        _cts?.Cancel();
        _cts = null;
        StopLive();
        _portCts?.Cancel();
        _portCts = null;
    }

    public void RefreshInfo()
    {
        if (_device is not { } d) return;
        string name = string.IsNullOrWhiteSpace(d.Description) ? d.Ip.ToString() : d.Description;
        _title.Text = name;
        _subtitle.Text = string.Join("  ·  ", new[] { d.Ip.ToString(), d.Vendor, d.Mac, d.OpenPorts.Count > 0 ? "ports " + d.OpenPortsText : null }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        _open.Enabled = true;
    }

    private void Clear()
    {
        Stop();
        _device = null;
        _title.Text = "No device selected";
        _subtitle.Text = "Select a device in the list: its health, live bandwidth and video are checked automatically.";
        SetState("", Theme.TextSecondary);
        foreach (var b in new[] { _open, _ping, _trace, _copy, _again }) b.Enabled = false;
        ResetSections();
        _output.Text = "";
    }

    private void ResetSections()
    {
        foreach (var b in new[] { _ping, _trace, _copy, _again }) b.Enabled = _device != null;
        _reachRow.Visible = false;
        _loginRow.Visible = false;
        _urlRow.Visible = false;
        _snmpRow.Visible = false;
        _path.Text = _device == null ? "" : "Tracing…";
        _port.Text = _device == null ? "" : "Looking for the switch port…";
        _portKbps.Text = "";
        _portGraph.Visible = false;
        _portGraph.Data = Array.Empty<LiveSample>();
        lock (_portHistory) _portHistory.Clear();
        _portReport = null;
        _kbps.Text = "-";
        _bwDetails.Text = "";
        _video.Text = "";
        _delay.Text = _device == null ? "" : "Measured from the live stream once it plays.";
        _flash.Enabled = false;
        _graph.Data = Array.Empty<LiveSample>();
        _fillingStreams = true;
        _streams.Items.Clear();
        _streams.Visible = false;
        _fillingStreams = false;
        _streamFromLive = false;
        _output.Text = _device == null ? "" : "Checking…";
    }

    // ------------------------------------------------------------------ the checks

    private void Start(bool joinAllowed = false, Credentials? loginOverride = null)
    {
        if (_device is not { } d || _tools is not { } tools) return;
        _debounce.Stop();
        _cts?.Cancel();
        StopLive();
        _portCts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        ResetSections();
        _ = RunAsync(d, tools, joinAllowed, loginOverride, ct);
        _ = RunPortAsync(d, tools, ct);
    }

    private async Task RunAsync(Device d, DeviceTools tools, bool joinAllowed, Credentials? loginOverride, CancellationToken ct)
    {
        try
        {
            SetState("Checking…", Theme.TextSecondary);

            // 1. Reach it. Outside safe mode a temporary IP is added automatically, like Open web page does.
            bool mayJoin = joinAllowed || !tools.SafeMode();
            var reach = await Task.Run(() => tools.Reach(d, mayJoin), ct); // adding a temporary IP runs netsh: keep it off the UI thread
            if (ct.IsCancellationRequested) return;
            _reach = reach;
            if (reach.TemporaryAddress != null) tools.Log($"Added temporary IP {reach.TemporaryAddress} in {reach.Range} to check {d.Ip}.");
            if (reach.Status == WebPageStatus.NeedsTemporaryAddress)
            {
                ShowReach($"{d.Ip} is in {reach.Range}, a range this laptop is not in. Safe mode is on, so no temporary IP was added.", join: true);
                SetState("Not reachable from this laptop yet.", Theme.TextSecondary);
                _output.Text = "";
                return;
            }
            if (reach.Status == WebPageStatus.JoinFailed)
                ShowReach($"Could not add a temporary IP in {reach.Range} (run as administrator?). Checking anyway; replies are unlikely.", join: false);
            if (reach.Status == WebPageStatus.NotJoinable)
                ShowReach($"{d.Ip} is in a public range; no temporary IP is added there. Checking from the laptop's own address.", join: false);

            // 2. Health: ping, path, ONVIF. Video is measured by the live stream and ffmpeg below.
            //    Devices that are not cameras get ping and path only: no ONVIF, so no camera password is sent to them.
            string? customUrl = CustomUrls.GetValueOrDefault(d.Ip.ToString());
            bool camera = d.Kind == DeviceKind.Camera || customUrl != null;
            _login = loginOverride ?? (camera ? PickLogin(d, tools) : null);
            var check = tools.Health();
            var progress = new Progress<string>(s => { if (!ct.IsCancellationRequested) SetState(s, Theme.TextSecondary); });
            var options = new HealthCheckOptions { Login = _login, CheckStream = false, CheckOnvif = camera, SourceIsTemporary = tools.IsTemporary(reach.Source) };
            var result = await Task.Run(() => check.RunAsync(d, reach.Source, options, progress, ct), ct);
            if (ct.IsCancellationRequested) return;
            _snapshot = result.Snapshot;
            _snapshot.NotChecked.RemoveAll(n => n.StartsWith("Video stream not checked"));
            _snapshot.Port = _portReport;
            ShowPath(_snapshot.Path);
            UpdateLogin(d, tools, _snapshot.Onvif);
            ShowResult(HealthCheck.Analyse(_snapshot));

            if (!camera)
            {
                _kbps.Text = "-";
                _video.Text = "Not a camera.";
                AskUrl("Not a camera, so there is no video stream to measure (its total traffic is under Switch port). If it does stream video (an encoder, a phone app), enter its stream address:");
                return;
            }

            // 3. Live bandwidth and the ffmpeg check, side by side.
            FillStreams(_snapshot);
            StartLive();
            if (check.StreamProber is { } ffprobe && !options.SourceIsTemporary && (customUrl ?? HealthCheck.StreamUrl(_snapshot)) is { } url)
                _ = RunFfprobeAsync(ffprobe, url, ct);
            else
                _video.Text = "ffmpeg: " + (options.SourceIsTemporary
                    ? "measured from the live stream instead (ffprobe cannot connect from a temporary IP)."
                    : check.StreamMissingReason ?? "no stream address known.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) { SetState("The check failed: " + ex.Message, Red); }
        }
    }

    // ------------------------------------------------------------------ path and switch port

    private void ShowPath(TraceReport? t)
    {
        if (t == null) { _path.Text = "Not traced."; return; }
        var lines = new List<string> { PathTracer.Summary(t) };
        if (t.Hops.Count > 1 || !t.ReachedTarget)
            lines.AddRange(t.Hops.Select(h =>
                $"  {h.Ttl}.  {h.Address?.ToString() ?? "(no reply)",-15}  {(h.Received == 0 ? "-" : $"{h.AvgMs:0} ms")}  {h.LossPercent:0.#}% loss"));
        _path.Text = string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Finds the device's switch port over SNMP (asking the site's devices once per scan which of them are managed
    /// switches) and watches its traffic every 2 s - for any device, not just cameras.
    /// </summary>
    private async Task RunPortAsync(Device d, DeviceTools tools, CancellationToken ct)
    {
        try
        {
            if (d.Mac == null) { _port.Text = "The device's MAC address is not known, so its switch port cannot be found."; return; }
            if (!tools.Switches.Discovered) _port.Text = "Asking the site's devices which are managed switches (SNMP)…";
            await tools.Switches.DiscoverAsync(tools.SwitchCandidates(), tools.Communities(), tools.SourceFor, CancellationToken.None);
            if (ct.IsCancellationRequested) return;
            if (tools.Switches.Switches.Count == 0)
            {
                _port.Text = "No managed switch answered SNMP, so per-port bandwidth is not available.";
                _snmpNote.Text = $"Tried community \"{string.Join("\", \"", tools.Communities())}\". If the switch has SNMP on with another read community, enter it (it is saved):";
                _snmpRow.Visible = true;
                return;
            }
            var port = await Task.Run(() => tools.Switches.LocateAsync(d.Mac, ct), ct);
            if (ct.IsCancellationRequested) return;
            if (port == null)
            {
                _port.Text = $"None of the managed switches ({string.Join(", ", tools.Switches.Switches.Select(s => s.Name ?? s.Ip.ToString()))}) has learned this device's MAC. It may be behind an unmanaged switch, or idle.";
                return;
            }
            _portReport = port;
            if (_snapshot != null) _snapshot.Port = port;
            _port.Text = PortLine(port);
            _portGraph.Visible = true;

            _portCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pct = _portCts.Token;
            int samples = 0;
            _ = Task.Run(() => tools.Switches.MonitorAsync(port, TimeSpan.FromSeconds(2), s => Post(() =>
            {
                if (pct.IsCancellationRequested) return;
                List<LiveSample> hist;
                lock (_portHistory)
                {
                    _portHistory.Add(new LiveSample(s.Time, s.FromDeviceKbps, 0, s.NewErrors > 0, 0));
                    if (_portHistory.Count > 60) _portHistory.RemoveAt(0);
                    hist = _portHistory.ToList();
                }
                _portGraph.Data = hist;
                _portKbps.Text = $"↑ {LogNormalizer.Mbps(s.FromDeviceKbps)} from device   ↓ {LogNormalizer.Mbps(s.ToDeviceKbps)} to device";
                _port.Text = PortLine(port);
                // Errors only mean something once watched for a while: update the diagnosis after ~10 s, then every minute.
                if (_snapshot != null && (++samples == 5 || samples % 30 == 0))
                {
                    _snapshot.Port = port;
                    ShowResult(HealthCheck.Analyse(_snapshot));
                }
            }), pct), pct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) _port.Text = "Switch port: " + ex.Message; }
    }

    private static string PortLine(SwitchPortReport p) =>
        p.Describe() +
        (p.SpeedMbps is { } sp ? $" · {(sp >= 1000 ? $"{sp / 1000.0:0.#} Gbit/s" : $"{sp} Mbit/s")}" : "") +
        (p.Duplex != null ? $" {p.Duplex} duplex" : "") +
        (p.Up == false ? " · LINK DOWN" : "") +
        (p.ErrorsDuringCheck is > 0 ? $" · {p.ErrorsDuringCheck} CRC errors while watched" : "") +
        (p.MacsOnPort > 3 ? $" · shared by {p.MacsOnPort} devices (traffic includes theirs)" : "") +
        "\nWhat the device sends (↑) and receives (↓) on the wire, all traffic, read from the switch every 2 s.";

    private async Task RunFfprobeAsync(IStreamProber ffprobe, string url, CancellationToken ct)
    {
        _video.Text = "ffmpeg: checking the stream…";
        try
        {
            var report = await Task.Run(() => ffprobe.ProbeAsync(url, _login, TimeSpan.FromSeconds(6), ct), ct);
            if (ct.IsCancellationRequested || _snapshot == null) return;
            _video.Text = "ffmpeg: " + (report.Opened
                ? $"{report.Codec?.ToUpperInvariant()} {report.Width}x{report.Height}, {report.MeasuredFps:0.#} fps received" +
                  (report.NominalFps is { } n ? $" (stream says {n:0.#})" : "") +
                  (report.KeyframeInterval is { } k ? $", key frame every {k:0.#} s" : "") +
                  (report.LongestGap is { } g && g > 0.5 ? $", longest pause {g:0.#} s" : "")
                : "could not open the stream - " + report.Error);
            _snapshot.Stream = report;
            _streamFromLive = false;
            ShowResult(HealthCheck.Analyse(_snapshot));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) _video.Text = "ffmpeg: " + ex.Message; }
    }

    private void FillStreams(HealthSnapshot s)
    {
        _fillingStreams = true;
        _streams.Items.Clear();
        if (s.Onvif is { } o)
            foreach (var p in o.Profiles.Where(p => o.StreamUris.ContainsKey(p.Token)))
                _streams.Items.Add(new StreamChoice(p.Token, $"{p.Name ?? p.Token} ({LogNormalizer.ProfileText(p)})", p));
        if (CustomUrls.ContainsKey(s.Ip.ToString()))
        {
            _streams.Items.Clear();
            _streams.Items.Add(new StreamChoice(null, "Address typed in", null));
        }
        if (_streams.Items.Count == 0 && HealthCheck.StreamUrl(s) != null)
            _streams.Items.Add(new StreamChoice(null, "Main stream (usual address for this brand)", null));
        if (_streams.Items.Count > 0) _streams.SelectedIndex = 0;
        _streams.Visible = _streams.Items.Count > 1;
        _fillingStreams = false;
    }

    private sealed record StreamChoice(string? Token, string Label, OnvifProfile? Profile)
    {
        public override string ToString() => Label;
    }

    private void StopLive()
    {
        _liveCts?.Cancel();
        _liveCts = null;
    }

    private void StartLive()
    {
        StopLive();
        if (_device is not { } d || _tools is not { } tools || _snapshot is not { } snap || _cts is not { } parent) return;
        var choice = _streams.SelectedItem as StreamChoice;
        string? url = CustomUrls.GetValueOrDefault(d.Ip.ToString()) ?? HealthCheck.StreamUrl(snap, choice?.Token);
        if (url == null)
        {
            _kbps.Text = "-";
            _bwDetails.Text = "";
            AskUrl("No stream address: the camera did not report one over ONVIF and its brand is not known. Enter it (see the camera's manual or web page):");
            return;
        }
        _liveCts = CancellationTokenSource.CreateLinkedTokenSource(parent.Token);
        var ct = _liveCts.Token;
        var stats = _live = new LiveStats();
        var login = _login;
        var source = _reach?.Source;
        _graph.Data = Array.Empty<LiveSample>();
        _kbps.Text = "…";
        _bwDetails.Text = $"Connecting to {FfprobeStreamProber.Redact(url)}…";
        double? configured = choice?.Profile?.BitrateKbps;
        double? nominalFps = choice?.Profile?.FrameRate;
        bool main = choice == null || ReferenceEquals(_streams.Items[0], choice);

        _ = Task.Run(async () =>
        {
            try
            {
                await tools.Live.RunAsync(url, login, source, stats, sample => Post(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    _graph.Data = stats.History;
                    _graph.Peak = configured;
                    _kbps.Text = LogNormalizer.Mbps(sample.Kbps);
                    _bwDetails.Text = $"{stats.Codec} · {stats.Fps:0} fps · average {LogNormalizer.Mbps(stats.AverageKbps)} · peak {LogNormalizer.Mbps(stats.PeakKbps)}" +
                        (stats.KeyframeInterval is { } k ? $" · key frame every {k:0.#} s" : "") +
                        (configured is { } c ? $" · set to max {LogNormalizer.Mbps(c)}" : "") +
                        (stats.LossPercent >= 0.5 ? $" · {stats.LossPercent:0.#}% packets missing" : "") +
                        $"\nMeasured over the last {Math.Min(stats.Seconds, LiveStats.HistorySeconds)} s. This is one viewer's copy; the NVR pulls its own.";
                    // Live view delay: refresh the numbers every few seconds.
                    if (main && stats.Seconds % 3 == 0)
                    {
                        var delay = stats.Delay.Report();
                        if (_snapshot != null) delay.CameraDelay ??= _snapshot.Delay?.CameraDelay;
                        _delay.Text = string.Join(Environment.NewLine, DiagnosisWriter.DelayLines(MergeFlash(delay)));
                        _flash.Enabled = true;
                    }
                    // Without ffprobe (or from a temporary IP), the live measurement is the video check.
                    if (main && stats.Seconds == 10 && _snapshot != null && (_snapshot.Stream == null || _streamFromLive))
                    {
                        _snapshot.Stream = stats.ToReport(FfprobeStreamProber.Redact(url), nominalFps);
                        _streamFromLive = true;
                    }
                    // Update the diagnosis with the delay numbers after 10 s and once it can tell whether delay builds up.
                    if (main && stats.Seconds is 10 or 45 && _snapshot != null)
                    {
                        _snapshot.Delay = MergeFlash(stats.Delay.Report());
                        ShowResult(HealthCheck.Analyse(_snapshot));
                    }
                }), ct);
            }
            catch (OperationCanceledException) { }
            catch (LiveStreamException ex)
            {
                Post(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    _kbps.Text = "-";
                    _bwDetails.Text = "Live bandwidth: " + ex.Message;
                    if (!ex.LoginRefused) AskUrl("Wrong stream address? Enter another one:");
                    if (ex.LoginRefused && _device != null && _tools != null)
                    {
                        if (login != null) Refused.Add(RefusedKey(_device, login));
                        AskLogin(login == null ? "The video stream needs the camera's user name and password." : "The camera refused this login for video. Enter the right one:");
                    }
                    if (main && _snapshot is { Stream: null })
                    {
                        _snapshot.Stream = new StreamReport { Url = FfprobeStreamProber.Redact(url), Error = ex.Message };
                        ShowResult(HealthCheck.Analyse(_snapshot));
                    }
                });
            }
            catch (Exception ex)
            {
                Post(() => { if (!ct.IsCancellationRequested) { _kbps.Text = "-"; _bwDetails.Text = "Live bandwidth: " + ex.Message; } });
            }
        }, ct);
    }

    // ------------------------------------------------------------------ live view delay

    private DelayReport? _flashResult;

    /// <summary>Keep the flash test's result when the live numbers are refreshed.</summary>
    private DelayReport MergeFlash(DelayReport r)
    {
        if (_flashResult is { } f && r.CameraDelay == null && r.FlashesShown == 0)
        {
            r.CameraDelay = f.CameraDelay; r.CameraDelayMin = f.CameraDelayMin; r.CameraDelayMax = f.CameraDelayMax;
            r.FlashesSeen = f.FlashesSeen; r.FlashesShown = f.FlashesShown;
        }
        return r;
    }

    private void RunFlashTest()
    {
        if (_live is not { } stats || _snapshot == null) return;
        using var w = new FlashTestWindow(stats.Delay);
        w.ShowDialog(FindForm());
        if (w.Result is not { } r) return;
        _flashResult = r;
        _snapshot.Delay = MergeFlash(stats.Delay.Report());
        _delay.Text = string.Join(Environment.NewLine, DiagnosisWriter.DelayLines(_snapshot.Delay));
        ShowResult(HealthCheck.Analyse(_snapshot));
        ShowView(0);
    }

    // ------------------------------------------------------------------ logins

    private static string RefusedKey(Device d, Credentials c) => $"{d.Ip}|{d.Mac}|{c.User}|{c.Password.GetHashCode()}";

    /// <summary>Saved login for this camera, else the last one that worked anywhere (a site usually shares one) - unless this camera refused it.</summary>
    private static Credentials? PickLogin(Device d, DeviceTools tools)
    {
        if (tools.DemoLogin != null) return tools.DemoLogin;
        var store = tools.Store;
        string key = CredentialStore.KeyFor(d.Mac, d.Ip.ToString());
        foreach (var c in new[] { store?.Get(key), store?.Get(CredentialStore.LastUsedKey) })
            if (c != null && !Refused.Contains(RefusedKey(d, c))) return c;
        return null;
    }

    private void UpdateLogin(Device d, DeviceTools tools, OnvifReport? onvif)
    {
        if (onvif == null || !onvif.Answered) return;
        if (onvif.LoginAccepted == true && _login != null)
        {
            tools.Store?.Save(CredentialStore.KeyFor(d.Mac, d.Ip.ToString()), _login); // worked: remember it (encrypted)
            _loginRow.Visible = false;
        }
        else if (onvif.LoginAccepted == false)
        {
            if (_login != null) Refused.Add(RefusedKey(d, _login));
            AskLogin(_login == null
                ? "Enter the camera's user name and password to read its log, settings and video:"
                : $"The camera refused the login \"{_login.User}\". It will not be tried again automatically. Enter the right one:");
        }
    }

    private void AskUrl(string note)
    {
        _urlNote.Text = note;
        if (_device != null && _url.Text.Length == 0) _url.Text = CustomUrls.GetValueOrDefault(_device.Ip.ToString()) ?? $"rtsp://{_device.Ip}:554/";
        _urlRow.Visible = true;
    }

    private void AskLogin(string note)
    {
        _loginNote.Text = note;
        if (_login != null && _user.Text.Length == 0) _user.Text = _login.User;
        _password.Text = "";
        _loginRow.Visible = true;
    }

    // ------------------------------------------------------------------ showing results

    private void ShowResult(HealthResult r)
    {
        _result = r;
        int problems = r.Findings.Count(f => f.Severity >= Severity.Warning);
        if (problems == 0) SetState("● Healthy - no problems found", Green);
        else
        {
            var main = r.Findings[0];
            SetState($"● {(problems == 1 ? "1 problem" : $"{problems} problems")}: {main.Title}", main.Severity == Severity.Problem ? Red : Amber);
        }
        ShowView(_view);
    }

    private void ShowView(int view)
    {
        _view = view;
        _showDiagnosis.Primary = view == 0;
        _showEvents.Primary = view == 1;
        _showRaw.Primary = view == 2;
        foreach (var b in new[] { _showDiagnosis, _showEvents, _showRaw }) b.Invalidate();
        if (_result == null) return;
        _output.Font = view == 0 ? Theme.Body : Theme.Mono;
        _output.WordWrap = view == 0;
        string text = view switch
        {
            0 => _result.Diagnosis,
            1 => DiagnosisWriter.Timeline(_result.Events),
            _ => DiagnosisWriter.CollectedData(_result.Snapshot),
        };
        _output.Text = text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        _output.SelectionStart = 0;
        _output.ScrollToCaret();
    }

    private void ShowReach(string text, bool join)
    {
        _reachNote.Text = text;
        _join.Visible = join;
        _reachRow.Visible = true;
    }

    private static Color Green => Theme.Dark ? Color.FromArgb(108, 203, 95) : Color.FromArgb(15, 123, 15);
    private static Color Red => Theme.Dark ? Color.FromArgb(255, 153, 164) : Color.FromArgb(196, 43, 28);
    private static Color Amber => Theme.Dark ? Color.FromArgb(252, 225, 0) : Color.FromArgb(157, 93, 0);

    private void SetState(string text, Color color)
    {
        _state.Text = text;
        _state.ForeColor = color;
    }

    private void Post(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke(new MethodInvoker(() => { if (!IsDisposed) action(); })); }
        catch (InvalidOperationException) { /* closing */ }
    }
}

/// <summary>Bandwidth over the last two minutes as a filled line, with key frames marked and the configured maximum as a dashed line.</summary>
internal sealed class Sparkline : Control
{
    private IReadOnlyList<LiveSample> _data = Array.Empty<LiveSample>();

    public Sparkline()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    public IReadOnlyList<LiveSample> Data { get => _data; set { _data = value; Invalidate(); } }
    /// <summary>Configured maximum bitrate (kbit/s), drawn as a dashed line.</summary>
    public double? Peak { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var area = new RectangleF(1, 4, Width - 2, Height - 18);
        using (var axis = new Pen(Theme.CardBorder)) g.DrawLine(axis, area.Left, area.Bottom, area.Right, area.Bottom);
        if (_data.Count < 2)
        {
            TextRenderer.DrawText(g, "waiting for video…", Font, ClientRectangle, Theme.TextSecondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        double max = Math.Max(_data.Max(s => s.Kbps), Peak ?? 0) * 1.15;
        if (max <= 0) max = 1;
        // Fill the width from the start: 30 s at first, growing to the full two minutes.
        int window = Math.Clamp(_data.Count, 30, LiveStats.HistorySeconds);
        float step = area.Width / (window - 1);
        float x0 = area.Right - step * (_data.Count - 1);
        PointF At(int i) => new(x0 + step * i, area.Bottom - (float)(_data[i].Kbps / max) * area.Height);

        var pts = Enumerable.Range(0, _data.Count).Select(At).ToArray();
        using (var fill = new GraphicsPath())
        {
            fill.AddLines(pts);
            fill.AddLine(pts[^1], new PointF(pts[^1].X, area.Bottom));
            fill.AddLine(new PointF(pts[^1].X, area.Bottom), new PointF(pts[0].X, area.Bottom));
            fill.CloseFigure();
            using var brush = new SolidBrush(Color.FromArgb(Theme.Dark ? 70 : 45, Theme.Accent));
            g.FillPath(brush, fill);
        }
        using (var line = new Pen(Theme.Accent, 1.8f)) g.DrawLines(line, pts);
        using (var key = new SolidBrush(Theme.Accent))
            for (int i = 0; i < _data.Count; i++)
                if (_data[i].KeyFrame) g.FillEllipse(key, pts[i].X - 1.5f, pts[i].Y - 1.5f, 3, 3);
        if (Peak is { } p && p > 0)
        {
            float y = area.Bottom - (float)(p / max) * area.Height;
            using var dash = new Pen(Theme.TextSecondary) { DashStyle = DashStyle.Dash };
            g.DrawLine(dash, area.Left, y, area.Right, y);
        }
        string scale = $"{LogNormalizer.Mbps(max / 1.15)} max shown · last {_data.Count} s";
        TextRenderer.DrawText(g, scale, Font, new Point(0, Height - 15), Theme.TextSecondary);
    }
}
