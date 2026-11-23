using CameraFinder.Core.Net;

namespace CameraFinder.App;

/// <summary>
/// Shown while a device's web page is open with the laptop's network switched over to the device's network
/// (<see cref="WebSession"/>). Closing it - or pressing "Done" - puts the network back to normal.
/// </summary>
public sealed class WebSessionWindow : Form
{
    private readonly FluentButton _done = new() { Text = "Done - restore network", Primary = true, MinimumSize = new Size(190, 32) };
    private readonly FluentButton _reopen = new() { Text = "Open page again", MinimumSize = new Size(130, 32) };

    public WebSession Session { get; }

    /// <param name="reopen">Opens the device's page in the browser again.</param>
    public WebSessionWindow(WebSession session, string url, string description, Action reopen, Form? owner = null)
    {
        Session = session;
        Text = $"Web page open - {session.Device}" + (string.IsNullOrWhiteSpace(description) ? "" : $" ({description})");
        Width = 560;
        Height = 270;
        MinimumSize = new Size(480, 240);
        StartPosition = FormStartPosition.CenterParent;
        Padding = new Padding(16, 12, 16, 10);
        if (owner?.Icon != null) Icon = owner.Icon;

        var title = new Label { Text = $"Connected to {session.Range} for {session.Device}", AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        var body = new Label
        {
            AutoSize = false, Dock = DockStyle.Fill, Tag = "secondary",
            Text =
                $"This laptop is using {session.Source} on {session.AdapterName} as its address in {session.Range}, " +
                "so the browser reaches the device as if the laptop were set up in that network.\n\n" +
                "Internet and other networks on this adapter may not work until you are done.\n\n" +
                $"{url}\n\n" +
                "Close this window (or the app) when you have finished with the web page: the network goes back to normal.",
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        buttons.Controls.AddRange(new Control[] { _done, _reopen });

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(body, 0, 1);
        Controls.Add(layout);
        Controls.Add(buttons);
        Theme.Apply(this);
        title.Font = Theme.Subtitle;

        _done.Click += (_, _) => Close();
        _reopen.Click += (_, _) => reopen();
    }
}
