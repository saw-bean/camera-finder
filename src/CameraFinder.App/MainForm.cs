using System.Diagnostics;
using System.Net;
using CameraFinder.Core.Capture;
using CameraFinder.Core.Discovery;
using CameraFinder.Core.Export;
using CameraFinder.Core.Model;
using CameraFinder.Core.Net;

namespace CameraFinder.App;

/// <summary>Main window: pick adapter, Scan, see the device list, open a device's web page.</summary>
public sealed class MainForm : Form
{
    private readonly ComboBox _adapters = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly Button _refresh = new() { Text = "↻", Width = 32 };
    private readonly CheckBox _safeMode = new() { Text = "Safe mode (listen only, no network changes)", AutoSize = true };
    private readonly TextBox _find = new() { Width = 170, PlaceholderText = "Stop when found: IP or MAC" };
    private readonly CheckBox _demo = new() { Text = "Demo (test site)", AutoSize = true };
    private readonly NumericUpDown _listen = new() { Minimum = 5, Maximum = 300, Value = 30, Width = 55 };
    private readonly Button _scan = new() { Text = "Scan", Width = 90, Height = 30, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) };
    private readonly Button _stop = new() { Text = "Stop", Width = 70, Height = 30, Enabled = false };

    private readonly ListView _list = new()
    {
        View = View.Details, FullRowSelect = true, GridLines = true, MultiSelect = false,
        Dock = DockStyle.Fill, HideSelection = false,
    };

    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = SystemColors.Window };
    private readonly Button _open = new() { Text = "Open web page", Width = 120, Enabled = false };
    private readonly Button _copy = new() { Text = "Copy IP", Width = 80, Enabled = false };
    private readonly Button _ping = new() { Text = "Ping", Width = 70, Enabled = false };
    private readonly Button _export = new() { Text = "Export CSV…", Width = 100 };
    private readonly Button _removeIps = new() { Text = "Remove temporary IPs", Width = 160 };
    private readonly LinkLabel _npcap = new() { AutoSize = true };

    private readonly ToolStripStatusLabel _status = new() { Text = "Ready", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripProgressBar _progress = new() { Width = 220 };
    private readonly ToolStripStatusLabel _update = new() { IsLink = true, Visible = false };
    private UpdateChecker.Update? _available;

    private readonly Dictionary<string, ListViewItem> _rows = new();
    private readonly TempAddressManager _temp;
    private ScanEngine? _engine;
    private CancellationTokenSource? _cts;
    private int _sortColumn;

    // Set per scan: how "Open web page" reaches a device (real network, or the demo site).
    private WebPageAccess? _web;
    private DemoSite? _demoSite;
    private bool _opening;
    private Task? _scanUi;        // the whole ScanAsync, including its clean-up
    private Task? _scanEngineTask; // the engine itself (runs off the UI thread)
    private Device? _foundTarget;

    public MainForm()
    {
        Text = $"Camera Finder {UpdateChecker.CurrentVersion.ToString(3)}";
        Width = 1100;
        Height = 720;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        using (var ico = typeof(MainForm).Assembly.GetManifestResourceStream("CameraFinder.App.app.ico"))
            if (ico != null) Icon = new Icon(ico);

        _temp = new TempAddressManager(AppendLog);
        BuildLayout();
        Wire();

        Shown += (_, _) =>
        {
            _temp.CleanupLeftovers();
            LoadAdapters();
            ShowNpcapStatus();
            AppendLog("Plug into the camera / door network, pick the adapter and press Scan.");
            _ = CheckForUpdateAsync();
        };
    }

    // ------------------------------------------------------------------ layout

    private void BuildLayout()
    {
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(6, 8, 6, 0), WrapContents = false };
        top.Controls.Add(new Label { Text = "Adapter:", AutoSize = true, Margin = new Padding(0, 6, 3, 0) });
        top.Controls.Add(_adapters);
        top.Controls.Add(_refresh);
        top.Controls.Add(new Label { Text = "Listen (s):", AutoSize = true, Margin = new Padding(12, 6, 3, 0) });
        top.Controls.Add(_listen);
        _find.Margin = new Padding(12, 4, 3, 0);
        top.Controls.Add(_find);
        new ToolTip().SetToolTip(_find, "Type the IP or MAC you are looking for (or the last digits of the MAC on the camera's label). " +
            "The scan stops as soon as that device is found. Leave empty to scan everything.");
        _safeMode.Margin = new Padding(12, 5, 6, 0);
        top.Controls.Add(_safeMode);
        _demo.Margin = new Padding(6, 5, 6, 0);
        top.Controls.Add(_demo);
        new ToolTip().SetToolTip(_demo, "Scans a simulated site with cameras in several IP ranges. Sends nothing and changes nothing - for testing and training.");
        top.Controls.Add(_scan);
        top.Controls.Add(_stop);

        foreach (var (name, width) in new[] { ("IP address", 120), ("Manufacturer", 210), ("Type", 85), ("Model / name", 230), ("MAC", 130), ("Ports", 110), ("Found by", 170) })
            _list.Columns.Add(name, width);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, Padding = new Padding(6, 4, 6, 0) };
        buttons.Controls.AddRange(new Control[] { _open, _ping, _copy, _export, _removeIps });
        new ToolTip().SetToolTip(_ping, "Ping the selected device continuously (like ping -t). Shows when it goes down and comes back.");
        _npcap.Margin = new Padding(16, 7, 0, 0);
        buttons.Controls.Add(_npcap);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(_log);

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_status);
        statusStrip.Items.Add(_update);
        statusStrip.Items.Add(_progress);

        Controls.Add(split);
        Controls.Add(buttons);
        Controls.Add(top);
        Controls.Add(statusStrip);

        Load += (_, _) => split.SplitterDistance = (int)(split.Height * 0.7);
    }

    private void Wire()
    {
        _refresh.Click += (_, _) => LoadAdapters();
        _scan.Click += async (_, _) => { _scanUi = ScanAsync(); await _scanUi; };
        _stop.Click += (_, _) => _cts?.Cancel();
        _find.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && _scan.Enabled) { e.SuppressKeyPress = true; _scan.PerformClick(); } };
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.DoubleClick += async (_, _) => await OpenSelectedAsync();
        _list.ColumnClick += (_, e) => Sort(e.Column);
        _open.Click += async (_, _) => await OpenSelectedAsync();
        _copy.Click += (_, _) =>
        {
            if (Selected() is not { } d) return;
            try { Clipboard.SetText(d.Ip.ToString()); }
            catch (Exception ex) { AppendLog("Could not copy to the clipboard: " + ex.Message); } // clipboard held by another app
        };
        _ping.Click += async (_, _) => await PingSelectedAsync();
        _export.Click += (_, _) => Export();
        _removeIps.Click += (_, _) => RemoveTemporaryAddresses();
        _npcap.LinkClicked += (_, _) => OpenUrl(PassiveListener.NpcapDownloadUrl);
        _update.Click += async (_, _) => await InstallUpdateAsync();
        FormClosing += OnClosing;
    }

    // ------------------------------------------------------------------ updates

    private async Task CheckForUpdateAsync()
    {
        _available = await UpdateChecker.CheckAsync(CancellationToken.None);
        if (_available is null || IsDisposed) return;
        _update.Text = $"Update available: {_available.Version.ToString(3)} - click to install";
        _update.Visible = true;
        AppendLog($"A newer version ({_available.Version.ToString(3)}) is available. Click the link in the status bar to install it.");
    }

    private async Task InstallUpdateAsync()
    {
        if (_available is not { } update) return;
        if (_stop.Enabled)
        {
            MessageBox.Show(this, "Stop the scan first, then install the update.", "Camera Finder", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this,
                $"Download and install Camera Finder {update.Version.ToString(3)}?\n\n" +
                "Camera Finder will close (temporary IPs are removed) and the installer will start.",
                "Update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _update.Enabled = false;
        _scan.Enabled = false;
        _status.Text = "Downloading update…";
        try
        {
            var path = await UpdateChecker.DownloadAsync(update, new Progress<int>(p => _progress.Value = Math.Clamp(p, 0, 100)), CancellationToken.None);
            UpdateChecker.StartInstaller(path);
            Close();
        }
        catch (Exception ex)
        {
            AppendLog("Update download failed: " + ex.Message);
            _status.Text = "Update failed";
            _progress.Value = 0;
            _update.Enabled = true;
            _scan.Enabled = true;
            if (MessageBox.Show(this, "The update could not be downloaded. Open the download page instead?", "Update",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                OpenUrl(UpdateChecker.ReleasesPage);
        }
    }

    // ------------------------------------------------------------------ actions

    private void LoadAdapters()
    {
        _adapters.Items.Clear();
        foreach (var a in Adapter.List()) _adapters.Items.Add(a);
        if (_adapters.Items.Count > 0) _adapters.SelectedIndex = 0;
        else AppendLog("No active network adapter found. Plug in the Ethernet cable and press ↻.");
    }

    private void ShowNpcapStatus()
    {
        if (PassiveListener.IsAvailable(out _))
        {
            _npcap.Text = "Passive listening: on (Npcap found)";
            _npcap.LinkArea = new LinkArea(0, 0);
        }
        else
        {
            _npcap.Text = "Passive listening: off - install Npcap to find devices in unknown ranges";
            _npcap.LinkArea = new LinkArea(_npcap.Text.IndexOf("install", StringComparison.Ordinal), "install Npcap".Length);
        }
    }

    private async Task ScanAsync()
    {
        bool demo = _demo.Checked;
        Adapter adapter;
        ScanOptions options;
        if (demo)
        {
            _demoSite = new DemoSite();
            adapter = _demoSite.Laptop;
            options = DemoSite.Options(TimeSpan.FromSeconds(Math.Min(8, (double)_listen.Value)), _safeMode.Checked);
            _engine = new ScanEngine(_demoSite, _demoSite, capture: _demoSite, readDetails: _demoSite.ReadDetailsAsync);
            var site = _demoSite;
            _web = new WebPageAccess(site, () => site.Laptop, site.ReadDetailsAsync);
            AppendLog("DEMO: scanning a simulated site. Nothing is sent and the laptop's settings are not changed.");
        }
        else
        {
            if (_adapters.SelectedItem is not Adapter picked) { AppendLog("Pick an adapter first."); return; }
            // Refresh the adapter: its addresses may have changed since the list was loaded.
            adapter = Adapter.List().FirstOrDefault(a => a.Id == picked.Id) ?? picked;
            options = new ScanOptions { SafeMode = _safeMode.Checked, ListenTime = TimeSpan.FromSeconds((double)_listen.Value) };
            _demoSite = null;
            _engine = new ScanEngine(new WindowsArpResolver(), _temp);
            string adapterId = adapter.Id;
            _web = new WebPageAccess(OperatingSystem.IsWindows() ? _temp : null,
                () => Adapter.List().FirstOrDefault(a => a.Id == adapterId),
                async (d, ct) => { using var enricher = new DeviceEnricher(); await enricher.EnrichAsync(d, null, ct); });
        }

        _list.Items.Clear();
        _rows.Clear();
        SetScanning(true);
        _cts = new CancellationTokenSource();

        _engine.Log += AppendLog;
        var engine = _engine;
        string? wanted = string.IsNullOrWhiteSpace(_find.Text) ? null : _find.Text.Trim();
        _foundTarget = null;
        if (wanted != null) AppendLog($"The scan will stop as soon as {wanted} is found.");
        _engine.DeviceChanged += d => Post(() =>
        {
            if (_engine != engine) return;
            UpsertRow(d);
            if (wanted != null && _foundTarget == null && _cts is { IsCancellationRequested: false } && d.Matches(wanted)) StopAtFound(d);
        });
        _engine.Progress += (phase, pct) => Post(() => { if (_engine == engine) { _status.Text = phase; _progress.Value = pct; } });

        try
        {
            // Run the engine off the UI thread: adding temporary IPs (netsh) blocks, and the window must stay responsive.
            var token = _cts.Token;
            _scanEngineTask = Task.Run(() => engine.RunAsync(adapter, options, token));
            await _scanEngineTask;
            foreach (var d in _engine.Devices) UpsertRow(d);
            _status.Text = $"Done - {_engine.Devices.Count} device(s)";
            if (wanted != null)
            {
                var hit = _foundTarget ?? _engine.Devices.FirstOrDefault(x => x.Matches(wanted));
                if (hit != null) { AppendLog($"Found {hit.Ip} ({hit.Description})."); SelectRow(hit); }
                else AppendLog($"{wanted} was not found. Check the cable/VLAN, try a longer listen time, or leave the box empty to see everything.");
            }
        }
        catch (OperationCanceledException)
        {
            int n = _engine.Devices.Count;
            foreach (var d in _engine.Devices) UpsertRow(d);
            if (_foundTarget != null)
            {
                AppendLog($"Found {_foundTarget.Ip} ({_foundTarget.Description}) - scan stopped. {n} device(s) found so far are listed.");
                SelectRow(_foundTarget);
            }
            else AppendLog($"Scan stopped. {n} device(s) found so far are listed.");
            _status.Text = "Stopped";
        }
        catch (Exception ex)
        {
            AppendLog("Scan failed: " + ex.Message);
            _status.Text = "Error";
        }
        finally
        {
            SetScanning(false);
            if (!demo && _temp.Added.Count > 0)
                AppendLog($"{_temp.Added.Count} temporary IP(s) stay on the adapter so you can open web pages. They are removed when you close the app.");
            AppendLog("Tip: Open web page also works for devices in other ranges - the app adds a temporary IP in that range first.");
        }
    }

    private void UpsertRow(Device d)
    {
        string key = d.Ip.ToString();
        if (!_rows.TryGetValue(key, out var item))
        {
            item = new ListViewItem(key) { Tag = d };
            for (int i = 1; i < _list.Columns.Count; i++) item.SubItems.Add("");
            _rows[key] = item;
            _list.Items.Add(item);
        }
        item.SubItems[1].Text = d.Vendor ?? "";
        item.SubItems[2].Text = d.Kind == DeviceKind.Unknown ? "" : d.Kind.ToString();
        item.SubItems[3].Text = d.Description;
        item.SubItems[4].Text = d.Mac ?? "";
        item.SubItems[5].Text = d.OpenPortsText;
        item.SubItems[6].Text = d.FoundBy;
        item.ForeColor = d.Kind == DeviceKind.Camera ? Color.DarkBlue : SystemColors.WindowText;
        UpdateButtons();
    }

    private Device? Selected() => _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as Device : null;

    private void UpdateButtons()
    {
        var d = Selected();
        _open.Enabled = d != null && !_opening;
        _copy.Enabled = d != null;
        _ping.Enabled = d != null && !_opening;
    }

    /// <summary>
    /// Opens the selected device's web page. If the laptop is not in the device's range, adds a temporary (virtual) IP
    /// in that range first - automatically, or after asking when safe mode is on - then re-checks the device's ports.
    /// </summary>
    private async Task OpenSelectedAsync()
    {
        var d = Selected();
        if (d == null || _opening) return;
        if (_web == null || _engine == null) { AppendLog("Run a scan first."); return; }
        if (_stop.Enabled) await StopScanAsync($"Stopping the scan to open {d.Ip}…");

        _opening = true;
        UpdateButtons();
        _status.Text = $"Checking {d.Ip}…";
        try
        {
            var avoid = _engine.Devices.Select(x => x.Ip).ToHashSet();
            var result = await _web.PrepareAsync(d, mayJoin: !_safeMode.Checked, avoid, CancellationToken.None);
            if (result.Status == WebPageStatus.NeedsTemporaryAddress)
            {
                var answer = MessageBox.Show(this,
                    $"{d.Ip} is in {result.Range}, a range this laptop is not in.\n\n" +
                    "Add a temporary IP address in that range so its web page can be opened?\n\n" +
                    "Safe mode is on: this changes the laptop's network settings until you press \"Remove temporary IPs\" or close the app.",
                    "Open web page", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) { AppendLog($"{d.Ip}: not opened (no temporary IP added)."); return; }
                result = await _web.PrepareAsync(d, mayJoin: true, avoid, CancellationToken.None);
            }

            if (result.TemporaryAddress != null)
                AppendLog($"Added temporary IP {result.TemporaryAddress} in {result.Range} to reach {d.Ip}{(_demoSite != null ? " (demo)" : "")}.");
            UpsertRow(d);

            switch (result.Status)
            {
                case WebPageStatus.Ready when _demoSite != null:
                    AppendLog($"DEMO: would open {result.Url} ({d.Description}). Demo devices have no real web page.");
                    break;
                case WebPageStatus.Ready:
                    AppendLog($"Opening {result.Url}");
                    OpenUrl(result.Url!);
                    break;
                case WebPageStatus.NoWebPage:
                    AppendLog($"{d.Ip} has no web page on ports 80/443/8080. Use the manufacturer's tool for this device.");
                    break;
                case WebPageStatus.JoinFailed:
                    AppendLog($"Could not add a temporary IP in {result.Range} to reach {d.Ip}. Run Camera Finder as administrator, or set an IP in that range on the laptop by hand.");
                    break;
                case WebPageStatus.NotJoinable:
                    AppendLog($"{d.Ip} is in a public IP range; the app only adds temporary IPs in private ranges. Set an address on the laptop by hand.");
                    break;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Could not open {d.Ip}: {ex.Message}");
        }
        finally
        {
            _opening = false;
            _status.Text = "Ready";
            UpdateButtons();
        }
    }

    /// <summary>
    /// Opens a continuous-ping window for the selected device. If the laptop is not in the device's range, a temporary
    /// IP is added first (asking in safe mode), and pings are sent from it.
    /// </summary>
    private async Task PingSelectedAsync()
    {
        var d = Selected();
        if (d == null || _opening) return;
        if (_web == null || _engine == null) { AppendLog("Run a scan first."); return; }
        if (_stop.Enabled) await StopScanAsync($"Stopping the scan to ping {d.Ip}…");

        _opening = true;
        UpdateButtons();
        try
        {
            var avoid = _engine.Devices.Select(x => x.Ip).ToHashSet();
            var reach = await _web.EnsureReachableAsync(d.Ip, mayJoin: !_safeMode.Checked, avoid, CancellationToken.None);
            if (reach.Status == WebPageStatus.NeedsTemporaryAddress)
            {
                var answer = MessageBox.Show(this,
                    $"{d.Ip} is in {reach.Range}, a range this laptop is not in, so it will not answer pings.\n\n" +
                    "Add a temporary IP address in that range first?\n\n" +
                    "Safe mode is on: this changes the laptop's network settings until you press \"Remove temporary IPs\" or close the app.",
                    "Ping", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) return;
                if (answer == DialogResult.Yes) reach = await _web.EnsureReachableAsync(d.Ip, mayJoin: true, avoid, CancellationToken.None);
            }
            if (reach.TemporaryAddress != null)
                AppendLog($"Added temporary IP {reach.TemporaryAddress} in {reach.Range} to reach {d.Ip}{(_demoSite != null ? " (demo)" : "")}.");
            if (reach.Status == WebPageStatus.JoinFailed)
                AppendLog($"Could not add a temporary IP in {reach.Range}; pinging anyway (replies are unlikely).");

            IPinger pinger = _demoSite != null ? _demoSite : new IcmpPinger();
            new PingWindow(pinger, d.Ip, reach.Source, d.Description).Show(this);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not ping {d.Ip}: {ex.Message}");
        }
        finally
        {
            _opening = false;
            UpdateButtons();
        }
    }

    /// <summary>
    /// Opens a URL in the user's normal browser. Launching through explorer.exe means the browser does not
    /// inherit this app's administrator rights.
    /// </summary>
    private static void OpenUrl(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + url + "\"") { UseShellExecute = false }); }
        catch
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show("Could not open " + url + ": " + ex.Message, "Camera Finder", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }

    private void Export()
    {
        if (_engine == null || _engine.Devices.Count == 0) { AppendLog("Nothing to export yet - run a scan first."); return; }
        using var dlg = new SaveFileDialog
        {
            Filter = "CSV (Excel)|*.csv",
            FileName = $"devices-{DateTime.Now:yyyy-MM-dd-HHmm}.csv",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dlg.FileName, CsvExporter.ToCsv(_engine.Devices), new System.Text.UTF8Encoding(true));
            AppendLog("Saved " + dlg.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Typically the previous export is still open in Excel, which locks the file.
            AppendLog("Could not save: " + ex.Message);
            MessageBox.Show(this, "Could not save the file. If it is open in Excel, close it and try again.\n\n" + ex.Message,
                "Export CSV", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RemoveTemporaryAddresses()
    {
        if (_stop.Enabled) { AppendLog("Stop the scan first."); return; }
        if (_demoSite != null && _demoSite.Joined.Count > 0)
        {
            AppendLog($"DEMO: forgot {_demoSite.Joined.Count} simulated temporary IP(s).");
            _demoSite.ForgetJoined();
        }
        int n = _temp.Added.Count;
        _temp.RemoveAll();
        AppendLog(n == 0 ? "No temporary IPs to remove." : $"Removed {n} temporary IP(s). The adapter is back to normal.");
    }

    private void Sort(int column)
    {
        _sortColumn = column;
        _list.ListViewItemSorter = new RowComparer(_sortColumn);
        _list.Sort();
    }

    private void SetScanning(bool scanning)
    {
        _scan.Enabled = !scanning;
        _stop.Enabled = scanning;
        _adapters.Enabled = !scanning;
        _refresh.Enabled = !scanning;
        _safeMode.Enabled = !scanning;
        _demo.Enabled = !scanning;
        _find.Enabled = !scanning;
        _listen.Enabled = !scanning;
        _removeIps.Enabled = !scanning;
        if (!scanning) _progress.Value = 0;
    }

    /// <summary>The device the user is looking for has appeared: stop the scan, keep the list, select it.</summary>
    private void StopAtFound(Device d)
    {
        _foundTarget = d;
        _status.Text = $"Found {d.Ip} - stopping…";
        SelectRow(d);
        _cts?.Cancel();
    }

    private void SelectRow(Device d)
    {
        if (!_rows.TryGetValue(d.Ip.ToString(), out var item)) return;
        _list.SelectedItems.Clear();
        item.Selected = true;
        item.Focused = true;
        item.EnsureVisible();
        _list.Focus();
    }

    /// <summary>Stops a running scan and waits until it has wrapped up (the list keeps what was found).</summary>
    private async Task StopScanAsync(string message)
    {
        if (_scanUi == null || _scanUi.IsCompleted) return;
        AppendLog(message);
        _cts?.Cancel();
        try { await _scanUi; } catch { /* ScanAsync reports its own errors */ }
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        _cts?.Cancel();
        // Let the engine wind down before undoing its network changes, so it cannot add an address afterwards.
        try { _scanEngineTask?.Wait(TimeSpan.FromSeconds(8)); } catch { /* cancelled or failed: fine */ }
        if (_temp.Added.Count > 0)
        {
            _status.Text = "Removing temporary IPs…";
            Refresh();
            _temp.RemoveAll();
        }
    }

    private void AppendLog(string message)
    {
        if (InvokeRequired) { Post(() => AppendLog(message)); return; }
        if (IsDisposed || _log.IsDisposed) return;
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread. Scan threads can still report for a moment after the window
    /// closes; BeginInvoke on a closed window throws, which would otherwise crash the app on a background thread.
    /// </summary>
    private void Post(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke(new MethodInvoker(() => { if (!IsDisposed) action(); })); }
        catch (InvalidOperationException) { /* window is closing */ }
    }

    private sealed class RowComparer(int column) : System.Collections.IComparer
    {
        public int Compare(object? x, object? y)
        {
            var a = (ListViewItem)x!;
            var b = (ListViewItem)y!;
            if (column == 0 && IPAddress.TryParse(a.Text, out var ia) && IPAddress.TryParse(b.Text, out var ib))
                return Ipv4.ToUInt(ia).CompareTo(Ipv4.ToUInt(ib));
            return string.Compare(a.SubItems[column].Text, b.SubItems[column].Text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
