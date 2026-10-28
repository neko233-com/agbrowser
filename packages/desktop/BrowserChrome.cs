using System.Drawing.Drawing2D;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AgBrowser.Desktop;

internal static class BrowserPalette
{
    public static readonly Color Chrome = Color.FromArgb(241, 242, 244);
    public static readonly Color Toolbar = Color.FromArgb(247, 248, 250);
    public static readonly Color Ink = Color.FromArgb(53, 60, 71);
    public static readonly Color Accent = Color.FromArgb(40, 105, 205);
    public static readonly Color Hover = Color.FromArgb(228, 231, 236);
}

internal static class FavoriteGlyphs
{
    internal static readonly object FallbackMarker = new();
    public static Bitmap Folder()
    {
        var image = new Bitmap(16, 16);
        using var g = Graphics.FromImage(image); g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(84, 96, 110), 1.25f) { LineJoin = LineJoin.Round };
        using var shape = new GraphicsPath();
        shape.AddLines([new PointF(1.6f, 4), new PointF(5.4f, 4), new PointF(6.8f, 5.5f), new PointF(14.2f, 5.5f), new PointF(14.2f, 12.6f), new PointF(1.6f, 12.6f), new PointF(1.6f, 4)]);
        g.DrawPath(pen, shape); return image;
    }
    public static Bitmap Site()
    {
        var image = new Bitmap(16, 16) { Tag = FallbackMarker };
        using var g = Graphics.FromImage(image); g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(105, 116, 130), 1.15f);
        g.DrawEllipse(pen, 1.5f, 1.5f, 13, 13); g.DrawEllipse(pen, 5, 1.5f, 6, 13); g.DrawLine(pen, 2, 8, 14, 8);
        return image;
    }
}

/// <summary>Paints navigation icons without depending on installed glyph fonts.</summary>
internal sealed class NavButton : Button
{
    private bool hovered;
    public NavButton()
    {
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; TabStop = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(SystemInformation.HighContrast ? SystemColors.Control : Parent?.BackColor ?? BrowserPalette.Toolbar);
        if (hovered && Enabled)
        {
            using var shape = RailTabButton.RoundedPath(Rectangle.Inflate(ClientRectangle, -3, -3), 7);
            using var brush = new SolidBrush(SystemInformation.HighContrast ? SystemColors.ControlDark : BrowserPalette.Hover);
            e.Graphics.FillPath(brush, shape);
        }
        var color = SystemInformation.HighContrast ? (Enabled ? SystemColors.ControlText : SystemColors.GrayText) : Enabled ? ForeColor : Color.FromArgb(142, 149, 159);
        float scale = Math.Min(DeviceDpi / 96f, Math.Min(Width, Height) / 26f);
        if (scale <= 0) return;
        var state = e.Graphics.Save();
        e.Graphics.TranslateTransform(Width / 2f, Height / 2f);
        e.Graphics.ScaleTransform(scale, scale);
        using var pen = new Pen(color, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        switch (Text)
        {
            case "‹":
            case "›":
                float direction = Text == "‹" ? 1 : -1;
                e.Graphics.DrawLines(pen, [new PointF(2 * direction, -6), new PointF(-4 * direction, 0), new PointF(2 * direction, 6)]);
                break;
            case "↻":
                e.Graphics.DrawArc(pen, -7, -7, 14, 14, 40, 290);
                e.Graphics.DrawLines(pen, [new PointF(2, -7), new PointF(7, -7), new PointF(7, -2)]);
                break;
            case "⌂":
                e.Graphics.DrawLines(pen, [new PointF(-8, -1), new PointF(0, -8), new PointF(8, -1)]);
                e.Graphics.DrawLines(pen, [new PointF(-6, -2), new PointF(-6, 7), new PointF(-2, 7), new PointF(-2, 2), new PointF(2, 2), new PointF(2, 7), new PointF(6, 7), new PointF(6, -2)]);
                break;
            case "↓":
                e.Graphics.DrawLine(pen, 0, -7, 0, 3);
                e.Graphics.DrawLines(pen, [new PointF(-4, -1), new PointF(0, 3), new PointF(4, -1)]);
                e.Graphics.DrawLines(pen, [new PointF(-7, 4), new PointF(-7, 8), new PointF(7, 8), new PointF(7, 4)]);
                break;
            case "⋯":
                using (var fill = new SolidBrush(color)) foreach (float x in new[] { -6f, 0f, 6f }) e.Graphics.FillEllipse(fill, x - 1.5f, -1.5f, 3, 3);
                break;
            case "+":
                e.Graphics.DrawLine(pen, -6, 0, 6, 0); e.Graphics.DrawLine(pen, 0, -6, 0, 6);
                break;
            case "☆":
                var star = Enumerable.Range(0, 10).Select(i => { double angle = -Math.PI / 2 + i * Math.PI / 5; float radius = i % 2 == 0 ? 8 : 3.6f; return new PointF((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius); }).ToArray();
                e.Graphics.DrawPolygon(pen, star);
                break;
            default:
                e.Graphics.Restore(state);
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                return;
        }
        e.Graphics.Restore(state);
    }
}

/// <summary>A compact favicon button with a visible selected state and an accessible name.</summary>
internal sealed class RailTabButton : Button
{
    private bool hovered;
    private bool selected;
    private bool expanded;
    private bool dropBefore, dropAfter;
    private bool closePressed, closeHovered;
    private bool loading;
    public event EventHandler? CloseRequested;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected { get => selected; set { if (selected != value) { selected = value; Invalidate(); } } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Fallback { get; set; } = "•";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Expanded { get => expanded; set { if (expanded != value) { expanded = value; RefreshDpiMetrics(); Invalidate(); } } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string DisplayTitle { get; set; } = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Loading { get => loading; set { if (loading != value) { loading = value; Invalidate(); } } }
    public void SetDropMarker(bool before, bool after) { dropBefore = before; dropAfter = after; Invalidate(); }

    public RailTabButton()
    {
        Width = 40; Height = 40; Margin = new Padding(2, 2, 2, 3); FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0; BackColor = BrowserPalette.Chrome; TabStop = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    private int Px(int logical) => Math.Max(1, (int)Math.Round(logical * DeviceDpi / 96d));
    public void RefreshDpiMetrics()
    {
        Width = Px(expanded ? 228 : 40); Height = Px(40);
        Margin = new Padding(Px(2), Px(2), Px(2), Px(3));
    }
    private Rectangle CloseBounds => new(Width - Px(31), (Height - Px(24)) / 2, Px(24), Px(24));
    public bool IsCloseHit(Point location) => expanded && CloseBounds.Contains(location);
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); RefreshDpiMetrics(); }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; closeHovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { closePressed = e.Button == MouseButtons.Left && IsCloseHit(e.Location); base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool overClose = IsCloseHit(e.Location);
        if (closeHovered != overClose) { closeHovered = overClose; Invalidate(); }
        base.OnMouseMove(e);
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        bool close = closePressed && e.Button == MouseButtons.Left && IsCloseHit(e.Location);
        base.OnMouseUp(e);
        closePressed = false;
        if (close) CloseRequested?.Invoke(this, EventArgs.Empty);
    }
    protected override void OnClick(EventArgs e) { if (!closePressed) base.OnClick(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { closePressed = false; Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(BrowserPalette.Chrome);
        var rect = new Rectangle(Px(3), Px(2), Width - Px(6), Height - Px(4));
        if (selected || hovered)
        {
            using var shape = RoundedPath(rect, Px(8));
            using var fill = new SolidBrush(selected ? Color.White : BrowserPalette.Hover);
            e.Graphics.FillPath(fill, shape);
            if (selected) { using var pen = new Pen(Color.FromArgb(221, 225, 231)); e.Graphics.DrawPath(pen, shape); }
        }
        if (selected) { using var accent = new SolidBrush(BrowserPalette.Accent); e.Graphics.FillRectangle(accent, 0, Px(10), Px(3), Height - Px(20)); }
        if (Image is { } image)
        {
            int size = Math.Min(Px(24), Math.Min(Width - Px(12), Height - Px(12)));
            e.Graphics.DrawImage(image, expanded ? Px(8) : (Width - size) / 2, (Height - size) / 2, size, size);
        }
        else
        {
            using var font = new Font("Segoe UI", 11, FontStyle.Bold);
            var iconBounds = expanded ? new Rectangle(Px(5), 0, Px(30), Height) : ClientRectangle;
            TextRenderer.DrawText(e.Graphics, Fallback, font, iconBounds, BrowserPalette.Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
        if (loading)
        {
            using var dot = new SolidBrush(BrowserPalette.Accent);
            e.Graphics.FillEllipse(dot, expanded ? Px(29) : Width - Px(10), Px(5), Px(5), Px(5));
        }
        if (expanded)
        {
            TextRenderer.DrawText(e.Graphics, DisplayTitle, Font, new Rectangle(Px(37), 0, Math.Max(0, Width - Px(75)), Height), BrowserPalette.Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            var close = CloseBounds;
            if (closeHovered)
            {
                using var closeFill = new SolidBrush(Color.FromArgb(216, 222, 231));
                using var closeShape = RoundedPath(close, Px(5));
                e.Graphics.FillPath(closeFill, closeShape);
            }
            using var closePen = new Pen(BrowserPalette.Ink, Math.Max(1.5f, DeviceDpi / 96f * 1.5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            int cx = close.Left + close.Width / 2, cy = close.Top + close.Height / 2, radius = Px(4);
            e.Graphics.DrawLine(closePen, cx - radius, cy - radius, cx + radius, cy + radius);
            e.Graphics.DrawLine(closePen, cx + radius, cy - radius, cx - radius, cy + radius);
        }
        if (dropBefore || dropAfter)
        {
            using var marker = new Pen(BrowserPalette.Accent, 2);
            int y = dropBefore ? 1 : Height - 2;
            e.Graphics.DrawLine(marker, 5, y, Width - 5, y);
        }
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(rect, -3, -3));
    }
    internal static GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure(); return path;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var image = Image; Image = null; image?.Dispose();
            ContextMenuStrip?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Indeterminate activity only; WebView2 does not expose a reliable percentage.</summary>
internal sealed class LoadingIndicator : Control
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 60 };
    private int phase;
    public LoadingIndicator()
    {
        Dock = DockStyle.Fill; Margin = Padding.Empty; Visible = false; AccessibleName = "页面加载中";
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        timer.Tick += (_, _) => { phase = (phase + 5) % 140; Invalidate(); };
        VisibleChanged += (_, _) => { if (Visible) timer.Start(); else timer.Stop(); };
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BrowserPalette.Toolbar);
        if (Width <= 0) return;
        int length = Math.Max(18, Width / 4);
        int left = (Width + length) * phase / 140 - length;
        using var fill = new SolidBrush(BrowserPalette.Accent);
        e.Graphics.FillRectangle(fill, left, 0, length, Height);
    }
    protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
}

internal sealed class AddressPanel : Panel
{
    public AddressPanel() { Dock = DockStyle.Fill; Margin = new Padding(2, 0, 0, 0); Padding = new Padding(12, 7, 12, 5); BackColor = BrowserPalette.Toolbar; DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = RailTabButton.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 9);
        using var fill = new SolidBrush(Color.White); e.Graphics.FillPath(fill, shape);
        using var pen = new Pen(Color.FromArgb(220, 224, 230)); e.Graphics.DrawPath(pen, shape);
    }
}

/// <summary>A lightweight native start surface for tabs with no page yet.</summary>
internal sealed class NewTabPage : Panel
{
    private readonly Panel group = new() { BackColor = Color.FromArgb(250, 251, 253) };
    private readonly Label heading = new() { Text = "新建标签页", TextAlign = ContentAlignment.MiddleCenter, ForeColor = BrowserPalette.Ink, Font = new Font("Microsoft YaHei UI", 22, FontStyle.Bold) };
    private readonly Label hint = new() { Text = "也可按 Ctrl+L 定位地址栏", TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(111, 122, 135), Font = new Font("Microsoft YaHei UI", 9) };
    private readonly NewTabSearchButton search = new();
    public event EventHandler? SearchRequested;

    public NewTabPage()
    {
        Dock = DockStyle.Fill; BackColor = Color.FromArgb(250, 251, 253); Visible = false;
        AccessibleName = "新建标签页";
        group.Controls.Add(heading); group.Controls.Add(search); group.Controls.Add(hint); Controls.Add(group);
        search.Click += (_, _) => SearchRequested?.Invoke(this, EventArgs.Empty);
        SizeChanged += (_, _) => Arrange();
    }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Arrange(); }
    private void Arrange()
    {
        int Px(int value) => LogicalToDeviceUnits(value);
        int width = Math.Max(Px(180), Math.Min(Px(520), ClientSize.Width - Px(48)));
        int height = Px(172);
        group.SetBounds(Math.Max(0, (ClientSize.Width - width) / 2), Math.Max(0, (ClientSize.Height - height) / 2 - Px(24)), width, height);
        heading.SetBounds(0, 0, width, Px(48));
        search.SetBounds(0, Px(64), width, Px(54));
        hint.SetBounds(0, Px(132), width, Px(30));
    }
}

internal sealed class NewTabSearchButton : Button
{
    private bool hovered;
    public NewTabSearchButton()
    {
        Text = "搜索或输入网址"; AccessibleName = "定位地址栏以搜索或输入网址"; Cursor = Cursors.Hand;
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Font = new Font("Microsoft YaHei UI", 10.5f);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 2 || Height < 2) return;
        int Px(int value) => LogicalToDeviceUnits(value);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? BrowserPalette.Toolbar);
        using var shape = RailTabButton.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), Px(10));
        using var fill = new SolidBrush(SystemInformation.HighContrast ? SystemColors.Window : hovered ? Color.FromArgb(248, 250, 253) : Color.White);
        using var border = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(213, 219, 227));
        e.Graphics.FillPath(fill, shape); e.Graphics.DrawPath(border, shape);
        using var icon = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(104, 116, 132), Math.Max(1.5f, DeviceDpi / 96f * 1.5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        int y = (Height - Px(14)) / 2;
        e.Graphics.DrawEllipse(icon, Px(19), y, Px(12), Px(12));
        e.Graphics.DrawLine(icon, Px(29), y + Px(10), Px(34), y + Px(15));
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(Px(48), 0, Math.Max(0, Width - Px(60)), Height),
            SystemInformation.HighContrast ? SystemColors.WindowText : BrowserPalette.Ink,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
    }
}

/// <summary>Use the Windows title bar and caption buttons while matching the app chrome.</summary>
internal static class NativeCaption
{
    private const int CaptionColor = 35;
    private const int CaptionTextColor = 36;
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Form window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || SystemInformation.HighContrast) return;
        int background = ColorTranslator.ToWin32(BrowserPalette.Chrome);
        int foreground = ColorTranslator.ToWin32(BrowserPalette.Ink);
        _ = DwmSetWindowAttribute(window.Handle, CaptionColor, ref background, sizeof(int));
        _ = DwmSetWindowAttribute(window.Handle, CaptionTextColor, ref foreground, sizeof(int));
    }
}

internal static class NativeForeground
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    public static bool IsCurrentProcess()
    {
        IntPtr window = GetForegroundWindow();
        return window != IntPtr.Zero && GetWindowThreadProcessId(window, out uint processId) != 0 && processId == Environment.ProcessId;
    }

    public static bool RemoveTopmost(IntPtr window)
    {
        const uint noSize = 0x0001, noMove = 0x0002, noActivate = 0x0010;
        return SetWindowPos(window, new IntPtr(-2), 0, 0, 0, 0, noSize | noMove | noActivate);
    }
}

internal sealed class AddressSuggestionPopup : ToolStripDropDown
{
    public void ShowAttached(Control owner, Point location)
    {
        Show(owner, location);
        // ToolStripDropDown makes every top-level dropdown topmost when shown.
        // Keep the list above its owner without covering unrelated applications.
        if (Visible && !NativeForeground.RemoveTopmost(Handle)) Close();
    }
}
