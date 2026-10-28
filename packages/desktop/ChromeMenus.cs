using System.Drawing.Drawing2D;

namespace AgBrowser.Desktop;

internal sealed class ChromeContextMenu : ContextMenuStrip
{
    public ChromeContextMenu()
    {
        ChromeMenuStyle.Apply(this);
    }
}

internal static class ChromeMenuStyle
{
    private static readonly ToolStripRenderer Renderer = new ChromeMenuRenderer();
    private static readonly Font MenuFont = new("Microsoft YaHei UI", 9.5f);
    public static void Apply(ToolStripDropDown menu)
    {
        menu.Font = MenuFont;
        menu.BackColor = SystemInformation.HighContrast ? SystemColors.Menu : Color.White;
        menu.Padding = new Padding(4, 6, 4, 6);
        menu.Renderer = Renderer;
        menu.ItemAdded += (_, e) => { if (e.Item is ToolStripMenuItem item) item.Padding = new Padding(8, 5, 8, 5); };
        menu.SizeChanged += (_, _) => {
            if (SystemInformation.HighContrast) { var old = menu.Region; menu.Region = null; old?.Dispose(); return; }
            if (menu.Width < 2 || menu.Height < 2) return;
            using var shape = RailTabButton.RoundedPath(new Rectangle(0, 0, menu.Width, menu.Height), Math.Max(6, menu.DeviceDpi * 8 / 96));
            var previous = menu.Region; menu.Region = new Region(shape); previous?.Dispose();
        };
    }
}

internal sealed class ChromeMenuRenderer : ToolStripProfessionalRenderer
{
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var fill = new SolidBrush(SystemInformation.HighContrast ? SystemColors.Menu : Color.White);
        e.Graphics.FillRectangle(fill, e.AffectedBounds);
    }
    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using var fill = new SolidBrush(SystemInformation.HighContrast ? SystemColors.Menu : Color.White);
        e.Graphics.FillRectangle(fill, e.AffectedBounds);
    }
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item.Selected || e.Item.Pressed)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(2, 1, Math.Max(1, e.Item.Width - 4), Math.Max(1, e.Item.Height - 2));
            using var shape = RailTabButton.RoundedPath(bounds, 6);
            using var fill = new SolidBrush(SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(241, 243, 244));
            e.Graphics.FillPath(fill, shape);
        }
    }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? SystemColors.GrayText : SystemInformation.HighContrast && e.Item.Selected ? SystemColors.HighlightText : SystemInformation.HighContrast ? SystemColors.MenuText : Color.FromArgb(32, 33, 36);
        base.OnRenderItemText(e);
    }
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = e.ImageRectangle;
        using var pen = new Pen(SystemInformation.HighContrast ? SystemColors.MenuText : Color.FromArgb(26, 115, 232), 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        e.Graphics.DrawLines(pen, [new Point(bounds.Left + 3, bounds.Top + bounds.Height / 2), new Point(bounds.Left + 6, bounds.Bottom - 4), new Point(bounds.Right - 3, bounds.Top + 4)]);
    }
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(SystemInformation.HighContrast ? SystemColors.MenuText : Color.FromArgb(232, 234, 237));
        int y = e.Item.Height / 2; e.Graphics.DrawLine(pen, 8, y, Math.Max(8, e.Item.Width - 8), y);
    }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(218, 220, 224));
        using var shape = RailTabButton.RoundedPath(new Rectangle(0, 0, Math.Max(1, e.ToolStrip.Width - 1), Math.Max(1, e.ToolStrip.Height - 1)), Math.Max(6, e.ToolStrip.DeviceDpi * 8 / 96));
        e.Graphics.DrawPath(pen, shape);
    }
}
