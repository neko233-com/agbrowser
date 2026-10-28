using System.Drawing.Drawing2D;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace AgBrowser.Desktop;

/// <summary>Shared native modal surface for browser settings and confirmations.</summary>
internal sealed class ChromeDialog : Form
{
    private readonly bool resizable;
    private readonly Panel header;
    public Panel Body { get; } = new() { Dock = DockStyle.Fill, BackColor = Color.White, AutoScroll = true };

    public ChromeDialog(string title, Size size, bool resizable = false)
    {
        this.resizable = resizable;
        Text = title; ClientSize = size; StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96); Font = new Font("Microsoft YaHei UI", 9.5f);
        BackColor = SystemInformation.HighContrast ? SystemColors.Window : Color.White;
        Padding = new Padding(resizable ? 6 : 1);
        header = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = BackColor };
        var heading = new Label { Dock = DockStyle.Fill, Text = title, TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(24, 0, 0, 0), Font = new Font("Microsoft YaHei UI", 13.5f),
            ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(32, 33, 36), AutoEllipsis = true };
        var close = new ChromeCloseButton { Dock = DockStyle.Right, Width = 54, AccessibleName = "关闭" };
        close.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        header.Controls.Add(heading); header.Controls.Add(close);
        header.MouseDown += DragHeader; heading.MouseDown += DragHeader;
        Controls.Add(Body); Controls.Add(header);
        Body.BackColor = BackColor;
        DoubleBuffered = true;
    }

    protected override CreateParams CreateParams { get { var value = base.CreateParams; value.ClassStyle |= 0x00020000; return value; } }
    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape && CancelButton is null) { DialogResult = DialogResult.Cancel; Close(); return true; }
        return base.ProcessDialogKey(keyData);
    }

    public DialogResult ShowChromeDialog(IWin32Window? owner = null)
    {
        PrepareForDisplay();
        return owner is null ? ShowDialog() : ShowDialog(owner);
    }

    internal void PrepareForDisplay() => ApplyTheme(Body);

    private void ApplyTheme(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is ChromeButton button && ReferenceEquals(AcceptButton, button)) button.Primary = true;
            if (child is TextBox text)
            {
                if (text.BorderStyle == BorderStyle.Fixed3D) text.BorderStyle = BorderStyle.FixedSingle;
                text.ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(32, 33, 36);
                text.BackColor = SystemInformation.HighContrast ? SystemColors.Window : Color.White;
            }
            else if (child is ListBox list)
            {
                list.BorderStyle = BorderStyle.FixedSingle;
                list.BackColor = SystemInformation.HighContrast ? SystemColors.Window : Color.White;
            }
            else if (child is Panel panel && child is not ChromeInput) panel.BackColor = BackColor;
            ApplyTheme(child);
        }
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); UpdateRoundedRegion(); }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); UpdateRoundedRegion(); }
    private void UpdateRoundedRegion()
    {
        if (SystemInformation.HighContrast) { var old = Region; Region = null; old?.Dispose(); return; }
        if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
        using var shape = RailTabButton.RoundedPath(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), LogicalToDeviceUnits(16));
        var previous = Region; Region = new Region(shape); previous?.Dispose();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(218, 220, 224));
        e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    private void DragHeader(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture(); _ = SendMessage(Handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
    }
    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (!resizable || message.Msg != 0x0084 || message.Result != new IntPtr(1)) return;
        long value = message.LParam.ToInt64();
        var point = PointToClient(new Point(unchecked((short)(value & 0xffff)), unchecked((short)((value >> 16) & 0xffff))));
        int edge = LogicalToDeviceUnits(7);
        bool left = point.X < edge, right = point.X >= ClientSize.Width - edge;
        bool top = point.Y < edge, bottom = point.Y >= ClientSize.Height - edge;
        int hit = top ? (left ? 13 : right ? 14 : 12) : bottom ? (left ? 16 : right ? 17 : 15) : left ? 10 : right ? 11 : 1;
        message.Result = new IntPtr(hit);
    }
}

internal sealed class ChromeButton : Button
{
    private bool hovered;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Primary { get; set; }
    public ChromeButton()
    {
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; AutoSize = true;
        Cursor = Cursors.Hand; Margin = new Padding(4, 3, 4, 3);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        return new Size(Math.Max(LogicalToDeviceUnits(72), text.Width + LogicalToDeviceUnits(28)), LogicalToDeviceUnits(36));
    }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 2 || Height < 2) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? Color.White);
        bool high = SystemInformation.HighContrast;
        Color fill = high ? SystemColors.Control : !Enabled ? Color.FromArgb(241, 243, 244) : Primary ? (hovered ? Color.FromArgb(23, 78, 166) : Color.FromArgb(26, 115, 232)) : hovered ? Color.FromArgb(232, 240, 254) : Color.White;
        Color ink = high ? SystemColors.ControlText : !Enabled ? Color.FromArgb(154, 160, 166) : Primary ? Color.White : Color.FromArgb(26, 115, 232);
        using var path = RailTabButton.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), LogicalToDeviceUnits(18));
        using var brush = new SolidBrush(fill); e.Graphics.FillPath(brush, path);
        if (!Primary || high) { using var border = new Pen(high ? SystemColors.ControlText : Color.FromArgb(218, 220, 224)); e.Graphics.DrawPath(border, path); }
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), ink, fill);
    }
}

internal sealed class ChromeCloseButton : Button
{
    private bool hovered;
    public ChromeCloseButton() { FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Cursor = Cursors.Hand; SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true); }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.Clear(Parent?.BackColor ?? Color.White);
        int size = LogicalToDeviceUnits(32), x = (Width - size) / 2, y = (Height - size) / 2;
        if (hovered) { using var fill = new SolidBrush(SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(241, 243, 244)); e.Graphics.FillEllipse(fill, x, y, size, size); }
        using var pen = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(95, 99, 104), Math.Max(1.4f, DeviceDpi / 96f * 1.5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        int inset = LogicalToDeviceUnits(11); e.Graphics.DrawLine(pen, x + inset, y + inset, x + size - inset, y + size - inset); e.Graphics.DrawLine(pen, x + size - inset, y + inset, x + inset, y + size - inset);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(new Rectangle(x, y, size, size), -3, -3));
    }
}

internal sealed class ChromeInput : Panel
{
    public TextBox Field { get; } = new ChromeTextBox { BorderStyle = BorderStyle.None };
    public ChromeInput()
    {
        Height = 42; BackColor = Color.White; DoubleBuffered = true; Controls.Add(Field);
        Field.Enter += (_, _) => Invalidate(); Field.Leave += (_, _) => Invalidate();
        Resize += (_, _) => Arrange(); FontChanged += (_, _) => Arrange();
    }
    private void Arrange()
    {
        int inset = LogicalToDeviceUnits(14);
        Field.SetBounds(inset, Math.Max(0, (Height - Field.PreferredHeight) / 2), Math.Max(1, Width - inset * 2), Field.PreferredHeight);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = RailTabButton.RoundedPath(new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3)), LogicalToDeviceUnits(9));
        using var fill = new SolidBrush(SystemInformation.HighContrast ? SystemColors.Window : Color.White); e.Graphics.FillPath(fill, shape);
        using var pen = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : Field.Focused ? Color.FromArgb(26, 115, 232) : Color.FromArgb(189, 193, 198), Field.Focused ? 2 : 1);
        e.Graphics.DrawPath(pen, shape);
    }
}

internal sealed class ChromeTextBox : TextBox
{
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetCueBanner(IntPtr window, int message, IntPtr showWhenFocused, string text);

    [AllowNull]
    public override string PlaceholderText
    {
        get => base.PlaceholderText;
        set { base.PlaceholderText = value; UpdateCue(); }
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UpdateCue(); }
    private void UpdateCue()
    {
        if (IsHandleCreated && !Multiline) _ = SetCueBanner(Handle, 0x1501, new IntPtr(1), PlaceholderText ?? "");
    }
}

internal sealed class ChromeToggle : CheckBox
{
    public ChromeToggle() { AutoSize = false; Size = new Size(44, 26); Cursor = Cursors.Hand; SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.Clear(Parent?.BackColor ?? Color.White);
        var track = new Rectangle(1, 3, Math.Max(1, Width - 3), Math.Max(1, Height - 7));
        using var shape = RailTabButton.RoundedPath(track, track.Height / 2);
        using var fill = new SolidBrush(SystemInformation.HighContrast ? (Checked ? SystemColors.Highlight : SystemColors.Window) : Checked ? Color.FromArgb(26, 115, 232) : Color.FromArgb(218, 220, 224)); e.Graphics.FillPath(fill, shape);
        int diameter = Math.Max(1, track.Height - LogicalToDeviceUnits(6));
        int x = Checked ? track.Right - diameter - LogicalToDeviceUnits(3) : track.Left + LogicalToDeviceUnits(3);
        using var knob = new SolidBrush(SystemInformation.HighContrast ? SystemColors.WindowText : Color.White); e.Graphics.FillEllipse(knob, x, track.Top + (track.Height - diameter) / 2, diameter, diameter);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
    }
}

internal static class ChromeMessage
{
    public static DialogResult Show(string text, string caption = "agbrowser", MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None)
        => Show(null, text, caption, buttons, icon);

    public static DialogResult Show(IWin32Window? owner, string text, string caption = "agbrowser", MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None)
    {
        using var dialog = Create(text, caption, buttons, icon);
        return dialog.ShowChromeDialog(owner);
    }

    internal static ChromeDialog Create(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        using var bodyFont = new Font("Microsoft YaHei UI", 9.5f);
        int contentHeight = TextRenderer.MeasureText(text, bodyFont, new Size(450, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
        var dialog = new ChromeDialog(caption, new Size(520, Math.Clamp(contentHeight + 160, 200, 570)));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(26, 0, 26, 20) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var label = new Label { Text = text, AutoSize = true, MaximumSize = new Size(450, 0), ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(60, 64, 67) };
        scroll.Controls.Add(label); layout.Controls.Add(scroll, 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        ChromeButton Add(string title, DialogResult result)
        {
            var button = new ChromeButton { Text = title, DialogResult = result };
            actions.Controls.Add(button); return button;
        }
        if (buttons is MessageBoxButtons.YesNo or MessageBoxButtons.YesNoCancel)
        {
            dialog.AcceptButton = Add("是", DialogResult.Yes);
            dialog.CancelButton = Add("否", DialogResult.No);
            if (buttons == MessageBoxButtons.YesNoCancel) dialog.CancelButton = Add("取消", DialogResult.Cancel);
        }
        else
        {
            dialog.AcceptButton = Add("确定", DialogResult.OK);
            if (buttons == MessageBoxButtons.OKCancel) dialog.CancelButton = Add("取消", DialogResult.Cancel);
        }
        layout.Controls.Add(actions, 0, 1); dialog.Body.Controls.Add(layout);
        return dialog;
    }
}
