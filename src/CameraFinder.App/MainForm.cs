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
    private readonly ComboBox _adapters = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340 };
    private readonly Button _refresh = new() { Text = "↻", Width = 32 };
    private readonly CheckBox _safeMode = new() { Text = "Safe mode (listen only, no network changes)", AutoSize = true };
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
        _safeMode.Margin = new Padding(12, 5, 6, 0);
        top.Controls.Add(_safeMode);
        top.Controls.Add(_scan);
        top.Controls.Add(_stop);

        foreach (var (name, width) in new[] { ("IP address", 120), ("Manufacturer", 210), ("Type", 85), ("Model / name", 230), ("MAC", 130), ("Ports", 110), ("Found by", 170) })
            _list.Columns.Add(name, width);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, Padding = new Padding(6, 4, 6, 0) };
        buttons.Controls.AddRange(new Control[] { _open, _copy, _export, _removeIps });
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
        _scan.Click += async (_, _) => await ScanAsync();
        _stop.Click += (_, _) => _cts?.Cancel();
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.DoubleClick += (_, _) => OpenSelected();
        _list.ColumnClick += (_, e) => Sort(e.Column);
        _open.Click += (_, _) => OpenSelected();
        _copy.Click += (_, _) =>
        {
            if (Selected() is not { } d) return;
            try { Clipboard.SetText(d.Ip.ToString()); }
            catch (Exception ex) { AppendLog("Could not copy to the clipboard: " + ex.Message); } // clipboard held by another app
        };
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
        if (_adapters.SelectedItem is not Adapter adapter) { AppendLog("Pick an adapter first."); return; }

        // Refresh the adapter: its addresses may have changed since the list was loaded.
        adapter = Adapter.List().FirstOrDefault(a => a.Id == adapter.Id) ?? adapter;

        _list.Items.Clear();
        _rows.Clear();
        SetScanning(true);
        _cts = new CancellationTokenSource();

        _engine = new ScanEngine(new WindowsArpResolver(), _temp);
        _engine.Log += AppendLog;
        var engine = _engine;
        _engine.DeviceChanged += d => Post(() => { if (_engine == engine) UpsertRow(d); });
        _engine.Progress += (phase, pct) => Post(() => { if (_engine == engine) { _status.Text = phase; _progress.Value = pct; } });

        try
        {
            await _engine.RunAsync(adapter, new ScanOptions
            {
                SafeMode = _safeMode.Checked,
                ListenTime = TimeSpan.FromSeconds((double)_listen.Value),
            }, _cts.Token);
            foreach (var d in _engine.Devices) UpsertRow(d);
            _status.Text = $"Done - {_engine.Devices.Count} device(s)";
        }
        catch (OperationCanceledException)
        {
            AppendLog("Scan stopped.");
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
            if (_temp.Added.Count > 0)
                AppendLog($"{_temp.Added.Count} temporary IP(s) stay on the adapter so you can open web pages. They are removed when you close the app.");
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
        _open.Enabled = d?.WebUrl != null;
        _copy.Enabled = d != null;
    }

    private void OpenSelected()
    {
        var d = Selected();
        if (d == null) return;
        if (d.WebUrl == null)
        {
            AppendLog($"{d.Ip} has no web page on ports 80/443/8080. Use the manufacturer's tool for this device.");
            return;
        }
        OpenUrl(d.WebUrl);
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
        _listen.Enabled = !scanning;
        _removeIps.Enabled = !scanning;
        if (!scanning) _progress.Value = 0;
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        _cts?.Cancel();
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
