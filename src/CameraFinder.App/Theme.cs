using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CameraFinder.App;

/// <summary>
/// Windows 11 (Fluent) look for WinForms: follows the Windows light/dark setting and accent colour, uses Segoe UI
/// Variable, rounded "card" surfaces, Fluent buttons and a title bar that matches the window.
/// </summary>
internal static class Theme
{
    public static bool Dark { get; private set; }
    public static Color Background { get; private set; }   // window (Mica-like base)
    public static Color Card { get; private set; }         // raised surfaces
    public static Color CardBorder { get; private set; }
    public static Color Control { get; private set; }      // inputs and standard buttons
    public static Color ControlHover { get; private set; }
    public static Color ControlPressed { get; private set; }
    public static Color ControlBorder { get; private set; }
    public static Color Text { get; private set; }
    public static Color TextSecondary { get; private set; }
    public static Color TextDisabled { get; private set; }
    public static Color Accent { get; private set; }
    public static Color AccentHover { get; private set; }
    public static Color AccentPressed { get; private set; }
    public static Color OnAccent { get; private set; }     // text on accent fill
    public static Color CameraText { get; private set; }   // camera rows in the device list

    public static Font Body { get; private set; } = SystemFonts.MessageBoxFont!;
    public static Font BodyStrong { get; private set; } = SystemFonts.MessageBoxFont!;
    public static Font Subtitle { get; private set; } = SystemFonts.MessageBoxFont!;
    public static Font Mono { get; private set; } = new("Consolas", 9F);

    static Theme() => Load();

    public static void Load()
    {
        // CAMERAFINDER_THEME=dark|light overrides the Windows setting (testing, screenshots).
        Dark = Environment.GetEnvironmentVariable("CAMERAFINDER_THEME")?.ToLowerInvariant() switch
        {
            "dark" => true,
            "light" => false,
            _ => ReadAppsUseDarkTheme(),
        };
        string family = FirstInstalled("Segoe UI Variable Text", "Segoe UI") ?? SystemFonts.MessageBoxFont!.FontFamily.Name;
        string display = FirstInstalled("Segoe UI Variable Display", "Segoe UI Semibold", "Segoe UI") ?? family;
        Body = new Font(family, 9.75F);
        BodyStrong = new Font(family, 9.75F, FontStyle.Bold);
        Subtitle = new Font(display, 11F, FontStyle.Bold);
        Mono = new Font(FirstInstalled("Cascadia Mono", "Consolas") ?? "Courier New", 9F);

        var accent = ReadAccent() ?? Color.FromArgb(0, 95, 184);
        if (Dark)
        {
            Background = Color.FromArgb(32, 32, 32);
            Card = Color.FromArgb(43, 43, 43);
            CardBorder = Color.FromArgb(58, 58, 58);
            Control = Color.FromArgb(55, 55, 55);
            ControlHover = Color.FromArgb(62, 62, 62);
            ControlPressed = Color.FromArgb(48, 48, 48);
            ControlBorder = Color.FromArgb(72, 72, 72);
            Text = Color.FromArgb(255, 255, 255);
            TextSecondary = Color.FromArgb(200, 200, 200);
            TextDisabled = Color.FromArgb(120, 120, 120);
            Accent = Lighten(accent, 0.45);   // Fluent uses a light accent on dark surfaces
            CameraText = Lighten(accent, 0.6);
        }
        else
        {
            Background = Color.FromArgb(243, 243, 243);
            Card = Color.FromArgb(255, 255, 255);
            CardBorder = Color.FromArgb(229, 229, 229);
            Control = Color.FromArgb(251, 251, 251);
            ControlHover = Color.FromArgb(246, 246, 246);
            ControlPressed = Color.FromArgb(240, 240, 240);
            ControlBorder = Color.FromArgb(208, 208, 208);
            Text = Color.FromArgb(27, 27, 27);
            TextSecondary = Color.FromArgb(96, 96, 96);
            TextDisabled = Color.FromArgb(160, 160, 160);
            Accent = Luminance(accent) > 0.45 ? Darken(accent, 0.35) : accent; // keep white text readable
            CameraText = Luminance(accent) > 0.35 ? Darken(accent, 0.45) : accent;
        }
        AccentHover = Dark ? Darken(Accent, 0.1) : Lighten(Accent, 0.1);
        AccentPressed = Dark ? Darken(Accent, 0.2) : Lighten(Accent, 0.2);
        OnAccent = Luminance(Accent) > 0.5 ? Color.Black : Color.White;
    }

    // ------------------------------------------------------------------ applying to controls

    /// <summary>Colours and fonts for a whole window. Call after the controls are created.</summary>
    public static void Apply(Form form)
    {
        form.BackColor = Background;
        form.ForeColor = Text;
        form.Font = Body;
        foreach (Control c in form.Controls) ApplyTo(c);
        if (form.IsHandleCreated) StyleTitleBar(form);
        else form.HandleCreated += (_, _) => StyleTitleBar(form);
    }

    private static void ApplyTo(Control c)
    {
        switch (c)
        {
            case FluentCard card:
                card.BackColor = Card;
                break;
            case SplitContainer split:
                split.BackColor = Background;
                split.Panel1.BackColor = split.Panel2.BackColor = Background;
                break;
            case FluentButton b:
                b.Invalidate();
                break;
            case ListView lv:
                lv.BackColor = Card;
                lv.ForeColor = Text;
                lv.BorderStyle = BorderStyle.None;
                Native(lv, () => SetWindowTheme(lv.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null));
                break;
            case TextBox tb when tb.ReadOnly && tb.Multiline:
                tb.BackColor = Card;
                tb.ForeColor = TextSecondary;
                tb.BorderStyle = BorderStyle.None;
                tb.Font = Mono;
                Native(tb, () => SetWindowTheme(tb.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null));
                break;
            case TextBox tb:
                tb.BackColor = Control;
                tb.ForeColor = Text;
                tb.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ComboBox cb:
                cb.FlatStyle = FlatStyle.Flat;
                cb.BackColor = Control;
                cb.ForeColor = Text;
                Native(cb, () => SetWindowTheme(cb.Handle, Dark ? "DarkMode_CFD" : "Explorer", null));
                break;
            case NumericUpDown n:
                n.BackColor = Control;
                n.ForeColor = Text;
                n.BorderStyle = BorderStyle.FixedSingle;
                break;
            case CheckBox chk:
                chk.ForeColor = Text;
                chk.BackColor = Color.Transparent;
                break;
            case Label l:
                l.ForeColor = l.Tag as string == "secondary" ? TextSecondary : Text;
                l.BackColor = Color.Transparent;
                break;
            case StatusStrip ss:
                ss.Renderer = new FlatStripRenderer();
                ss.BackColor = Background;
                ss.ForeColor = TextSecondary;
                ss.SizingGrip = false;
                foreach (ToolStripItem item in ss.Items)
                {
                    item.ForeColor = TextSecondary;
                    if (item is ToolStripStatusLabel { } sl) { sl.LinkColor = Accent; sl.ActiveLinkColor = AccentHover; sl.VisitedLinkColor = Accent; }
                }
                break;
            case FlowLayoutPanel or TableLayoutPanel or Panel:
                c.BackColor = c.Parent is FluentCard ? Card : c.Parent?.BackColor ?? Background;
                break;
        }
        foreach (Control child in c.Controls) ApplyTo(child);
    }

    private static void Native(Control c, Action action)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (c.IsHandleCreated) Try(action);
        else c.HandleCreated += (_, _) => Try(action);
        static void Try(Action a) { try { a(); } catch { /* older Windows: keep the default look */ } }
    }

    /// <summary>Title bar in the window's colours (Windows 11), dark title bar in dark mode (Windows 10 1809+).</summary>
    private static void StyleTitleBar(Form form)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            int dark = Dark ? 1 : 0;
            DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(form.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            int caption = ColorTranslator.ToWin32(Background);
            DwmSetWindowAttribute(form.Handle, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            int text = ColorTranslator.ToWin32(Text);
            DwmSetWindowAttribute(form.Handle, DWMWA_TEXT_COLOR, ref text, sizeof(int));
        }
        catch { /* not supported on this Windows version */ }
    }

    // ------------------------------------------------------------------ drawing helpers

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        if (d <= 0 || r.Width < d || r.Height < d) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
    private static Color Lighten(Color c, double t) =>
        Color.FromArgb((int)(c.R + (255 - c.R) * t), (int)(c.G + (255 - c.G) * t), (int)(c.B + (255 - c.B) * t));
    private static Color Darken(Color c, double t) =>
        Color.FromArgb((int)(c.R * (1 - t)), (int)(c.G * (1 - t)), (int)(c.B * (1 - t)));

    private static string? FirstInstalled(params string[] families)
    {
        foreach (var name in families)
        {
            try
            {
                using var f = new FontFamily(name);
                return f.Name;
            }
            catch (ArgumentException) { /* not installed */ }
        }
        return null;
    }

    private static bool ReadAppsUseDarkTheme()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    private static Color? ReadAccent()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
                return Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
        }
        catch { }
        return null;
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);
}

/// <summary>Rounded raised surface ("card") holding a group of controls.</summary>
internal sealed class FluentCard : TableLayoutPanel
{
    public FluentCard()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        Padding = new Padding(12, 10, 12, 10);
        ColumnCount = 1;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.RoundedRect(r, 8);
        using var fill = new SolidBrush(Theme.Card);
        using var border = new Pen(Theme.CardBorder);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);
    }
}

/// <summary>Fluent button: rounded, flat, accent-filled when <see cref="Primary"/>, with hover and pressed states.</summary>
internal sealed class FluentButton : Button
{
    private bool _hover, _pressed;

    public bool Primary { get; set; }

    public FluentButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowOnly;
        Padding = new Padding(10, 0, 10, 0);
        MinimumSize = new Size(80, 32);
        Margin = new Padding(3, 3, 3, 3);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.RoundedRect(r, 4);

        Color fill, border, text;
        if (!Enabled)
        {
            fill = Primary ? Theme.CardBorder : Theme.Control;
            border = Theme.CardBorder;
            text = Theme.TextDisabled;
        }
        else if (Primary)
        {
            fill = _pressed ? Theme.AccentPressed : _hover ? Theme.AccentHover : Theme.Accent;
            border = fill;
            text = Theme.OnAccent;
        }
        else
        {
            fill = _pressed ? Theme.ControlPressed : _hover ? Theme.ControlHover : Theme.Control;
            border = Theme.ControlBorder;
            text = _pressed ? Theme.TextSecondary : Theme.Text;
        }
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        using (var p = new Pen(border)) g.DrawPath(p, path);
        if (Focused && ShowFocusCues)
        {
            using var focus = Theme.RoundedRect(new RectangleF(1.5f, 1.5f, Width - 3.5f, Height - 3.5f), 3);
            using var fp = new Pen(Theme.Text, 1.5f);
            g.DrawPath(fp, focus);
        }
        TextRenderer.DrawText(g, Text, Primary ? Theme.BodyStrong : Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Status bar without the old gradient and border.</summary>
internal sealed class FlatStripRenderer : ToolStripProfessionalRenderer
{
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(Theme.Background);
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }
}
