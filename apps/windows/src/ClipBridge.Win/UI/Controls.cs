using System.Drawing.Drawing2D;
using ClipBridge.Core.Models;

namespace ClipBridge.Win.UI;

internal static class Art
{
    public static GraphicsPath Round(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
    public static void Icon(Graphics g, string name, RectangleF r, Color color)
    {
        var state = g.Save();
        g.TranslateTransform(r.X, r.Y);
        g.ScaleTransform(r.Width / 24, r.Height / 24);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var p = new Pen(color, 1.65f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        switch (name)
        {
            case "home":
                g.DrawLines(p, new PointF[] {new(3, 11), new(12, 3), new(21, 11)});
                g.DrawLines(p, new PointF[] {new(5, 10), new(5, 21), new(10, 21), new(10, 15), new(14, 15), new(14, 21), new(19, 21), new(19, 10)}); break;
            case "history":
                g.DrawArc(p, 4, 4, 16, 16, 205, 300);
                g.DrawLines(p, new PointF[] {new(3, 4), new(3, 10), new(9, 10)});
                g.DrawLines(p, new PointF[] {new(12, 7), new(12, 12), new(16, 14)}); break;
            case "settings":
                g.DrawEllipse(p, 4, 4, 16, 16); g.DrawEllipse(p, 9, 9, 6, 6);
                for (int i = 0; i < 8; i++) { var s = g.Save(); g.TranslateTransform(12, 12); g.RotateTransform(i * 45); g.DrawLine(p, 0, 8, 0, 11); g.Restore(s); } break;
            case "pc":
                using (var path = Round(new(3, 4, 18, 13), 2)) g.DrawPath(p, path);
                g.DrawLine(p, 12, 17, 12, 21); g.DrawLine(p, 8, 21, 16, 21); break;
            case "phone":
                using (var path = Round(new(6, 2, 12, 20), 2.5f)) g.DrawPath(p, path);
                g.DrawLine(p, 10, 5, 14, 5); g.DrawLine(p, 10, 19, 14, 19); break;
            case "copy":
                using (var path = Round(new(8, 8, 12, 13), 2)) g.DrawPath(p, path);
                g.DrawLines(p, new PointF[] {new(15, 4), new(4, 4), new(4, 16)}); break;
            case "pause": g.DrawLine(p, 9, 5, 9, 19); g.DrawLine(p, 15, 5, 15, 19); break;
            case "play": g.DrawPolygon(p, new PointF[] {new(8, 4), new(19, 12), new(8, 20)}); break;
            case "arrow": g.DrawLine(p, 4, 12, 20, 12); g.DrawLines(p, new PointF[] {new(15, 7), new(20, 12), new(15, 17)}); break;
        }
        g.Restore(state);
    }
}

public class Surface : Panel
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Palette Colors { get; set; } = Palette.Create(false);
    public Surface() { DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Art.Round(new(0.5f, .5f, Width - 1, Height - 1), 12 * DeviceDpi / 96f);
        using var brush = new SolidBrush(Colors.Surface);
        using var pen = new Pen(Colors.Border);
        e.Graphics.FillPath(brush, path);
        e.Graphics.DrawPath(pen, path);
    }
}

public class ActionButton : Button
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Palette Colors { get; set; } = Palette.Create(false);
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string? Glyph { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Primary { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Selected { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Quiet { get; set; }
    private bool _hover, _pressed;
    public ActionButton()
    {
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false; Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Font = new Font("Segoe UI", 9.5f);
    }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { _pressed = true; Invalidate(); } base.OnKeyDown(e); }
    protected override void OnKeyUp(KeyEventArgs e) { _pressed = false; Invalidate(); base.OnKeyUp(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; float d = DeviceDpi / 96f;
        g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
        var fill = Primary ? Colors.Accent : Selected ? Colors.Selected : _hover || _pressed ? Colors.Hover : Quiet ? BackColor : Colors.Surface;
        if (_pressed) fill = ControlPaint.Dark(fill, .08f);
        if (Primary && _hover) fill = ControlPaint.Light(fill, .08f);
        var ink = !Enabled ? Colors.Muted : Primary ? Colors.AccentText : Colors.Text;
        if (!Enabled) fill = Colors.Hover;
        using var path = Art.Round(new(.5f, .5f, Width - 1, Height - 1), 7 * d);
        using var b = new SolidBrush(fill);
        using var p = new Pen(Quiet || Primary || Selected ? fill : Colors.Border);
        g.FillPath(b, path); g.DrawPath(p, path);
        if (Selected && Quiet)
        {
            using var marker = new Pen(Colors.Accent, 3 * d) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(marker, 2 * d, Height * .33f, 2 * d, Height * .67f);
        }
        int left = Glyph is null ? (int)(8 * d) : (int)(40 * d);
        if (Glyph is not null) Art.Icon(g, Glyph, new(13 * d, Height / 2f - 10 * d, 20 * d, 20 * d), ink);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(left, 0, Width - left - (int)(8 * d), Height), ink,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine |
            (Glyph is null ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left));
        if (Focused && ShowFocusCues)
        {
            using var focus = Art.Round(new(3 * d, 3 * d, Width - 6 * d, Height - 6 * d), 5 * d);
            using var fp = new Pen(Primary ? Colors.AccentText : Colors.Accent, 1.5f * d);
            g.DrawPath(fp, focus);
        }
    }
}

public sealed class BridgeView : Control
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Palette Colors { get; set; } = Palette.Create(false);
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Connected { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Paused { get; set; }
    public BridgeView() { DoubleBuffered = true; AccessibleName = "Windows, this PC, linked to iPhone via Shortcut"; AccessibleRole = AccessibleRole.Graphic; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; float d = DeviceDpi / 96f;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float left = Width * .22f, right = Width * .78f, middle = Width / 2f;
        using var pen = new Pen(Connected ? Colors.Accent : Colors.Border, 1.4f * d);
        if (!Connected) pen.DashStyle = DashStyle.Dash;
        g.DrawLine(pen, left + 42 * d, 42 * d, middle - 20 * d, 42 * d);
        g.DrawLine(pen, middle + 20 * d, 42 * d, right - 42 * d, 42 * d);
        if (Paused) Art.Icon(g, "pause", new(middle - 10 * d, 32 * d, 20 * d, 20 * d), Colors.Muted);
        else
        {
            Art.Icon(g, "arrow", new(middle - 10 * d, 30 * d, 20 * d, 20 * d), Connected ? Colors.Accent : Colors.Muted);
            var state = g.Save(); g.TranslateTransform(middle, 50 * d); g.RotateTransform(180);
            Art.Icon(g, "arrow", new(-10 * d, -10 * d, 20 * d, 20 * d), Connected ? Colors.Accent : Colors.Muted); g.Restore(state);
        }
        DrawDevice(left, "pc", "Windows", "This PC");
        DrawDevice(right, "phone", "iPhone", "via Shortcut");
        void DrawDevice(float x, string glyph, string label, string sub)
        {
            using var path = Art.Round(new(x - 29 * d, 12 * d, 58 * d, 58 * d), 14 * d);
            using var brush = new SolidBrush(Colors.Background); g.FillPath(brush, path);
            Art.Icon(g, glyph, new(x - 17 * d, 24 * d, 34 * d, 34 * d), Colors.Text);
            using var font = new Font("Segoe UI Semibold", 10.5f);
            using var small = new Font("Segoe UI", 9f);
            TextRenderer.DrawText(g, label, font, new Rectangle((int)(x - 70 * d), (int)(77 * d), (int)(140 * d), (int)(24 * d)), Colors.Text, TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, sub, small, new Rectangle((int)(x - 70 * d), (int)(101 * d), (int)(140 * d), (int)(24 * d)), Colors.Muted, TextFormatFlags.HorizontalCenter);
        }
    }
}

public sealed class HistoryList : ListBox
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Palette Colors { get; set; } = Palette.Create(false);
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string LocalDevice { get; set; } = "windows-main";
    public HistoryList()
    {
        DrawMode = DrawMode.OwnerDrawFixed; BorderStyle = BorderStyle.None;
        IntegralHeight = false; ItemHeight = 67; AccessibleName = "Clipboard history";
        Font = new Font("Segoe UI", 9.5f);
    }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); ItemHeight = (int)(67 * DeviceDpi / 96f); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ItemHeight = (int)(67 * DeviceDpi / 96f); WindowChrome.Scrollbar(this, Colors.Dark); }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || Items[e.Index] is not HistoryRow row) return;
        var g = e.Graphics; float d = DeviceDpi / 96f; var r = e.Bounds;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var b = new SolidBrush(selected ? Colors.Selected : Colors.Surface); g.FillRectangle(b, r);
        using var p = new Pen(Colors.Border); g.DrawLine(p, r.Left + 12 * d, r.Bottom - 1, r.Right - 12 * d, r.Bottom - 1);
        using var font = new Font("Segoe UI Semibold", 9.5f);
        using var small = new Font("Segoe UI", 9f);
        var title = new Rectangle(r.Left + (int)(12 * d), r.Top + (int)(9 * d), r.Width - (int)(170 * d), (int)(22 * d));
        TextRenderer.DrawText(g, HistoryPresentation.Route(row.Clip, LocalDevice) + " · " + HistoryPresentation.Size(row.Clip.SizeBytes), font, title, Colors.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        var time = new Rectangle(r.Right - (int)(158 * d), title.Top, (int)(146 * d), title.Height);
        TextRenderer.DrawText(g, HistoryPresentation.Time(row.Clip.ReceivedAt), small, time, Colors.Muted, TextFormatFlags.Right | TextFormatFlags.SingleLine);
        var preview = new Rectangle(title.Left, r.Top + (int)(34 * d), r.Width - (int)(24 * d), (int)(24 * d));
        TextRenderer.DrawText(g, row.Preview, Font, preview, Colors.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        if ((e.State & DrawItemState.Focus) != 0) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(r, -2, -2), Colors.Text, Colors.Selected);
    }
}

public sealed class HistoryRow(ClipRecord clip, string localDevice)
{
    public ClipRecord Clip { get; } = clip;
    public string Preview { get; } = HistoryPresentation.Preview(clip.Content);
    public override string ToString() => $"{HistoryPresentation.Route(Clip, localDevice)}, {HistoryPresentation.Time(Clip.ReceivedAt)}, {HistoryPresentation.Size(Clip.SizeBytes)}, {Preview}";
}

/// <summary>Route keyboard and context-menu copy through the same safe host action.</summary>
public sealed class HistoryText : RichTextBox
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wparam, IntPtr lparam);
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Func<string, bool>? SafeCopy { get; set; }
    public HistoryText()
    {
        Multiline = true; ReadOnly = true; BorderStyle = BorderStyle.None;
        ScrollBars = RichTextBoxScrollBars.None; WordWrap = true; MaxLength = int.MaxValue;
        DetectUrls = false; EnableAutoDragDrop = false;
        AccessibleName = "Selected clipboard text"; ShortcutsEnabled = true;
    }
    public int FirstLine => IsHandleCreated ? (int)SendMessage(Handle, 0xCE, IntPtr.Zero, IntPtr.Zero) : 0;
    public int TotalLines => TextLength == 0 ? 1 : GetLineFromCharIndex(TextLength) + 1;
    public int VisibleLines => Math.Max(1, ClientSize.Height / Font.Height);
    public void ScrollToLine(int line) => SendMessage(Handle, 0xB6, IntPtr.Zero, (IntPtr)(line - FirstLine));
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // RichEdit handles keyboard copy internally without sending WM_COPY.
        if (keyData is (Keys.Control | Keys.C) or (Keys.Control | Keys.Insert))
        {
            if (SelectionLength > 0) SafeCopy?.Invoke(SelectedText);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x301) { if (SelectionLength > 0) SafeCopy?.Invoke(SelectedText); return; }
        base.WndProc(ref m);
    }
}

/// <summary>A themed, event-driven scrollbar for the native plain-text reader.</summary>
public sealed class HistoryScrollBar : Control
{
    private readonly HistoryText _text;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Palette Colors { get; set; } = Palette.Create(false);
    private bool _drag;
    private int _grabOffset;
    public HistoryScrollBar(HistoryText text)
    {
        _text = text; DoubleBuffered = true; TabStop = true;
        AccessibleName = "Scroll selected text"; AccessibleRole = AccessibleRole.ScrollBar;
        text.VScroll += (_, _) => Invalidate(); text.TextChanged += (_, _) => Invalidate();
        text.Resize += (_, _) => Invalidate();
    }
    private int MaxLine => Math.Max(0, _text.TotalLines - _text.VisibleLines);
    private Rectangle Thumb()
    {
        int height = Math.Min(Height, Math.Max((int)(26 * DeviceDpi / 96f), Height * _text.VisibleLines / Math.Max(1, _text.TotalLines)));
        int top = MaxLine == 0 ? 0 : (Height - height) * Math.Min(MaxLine, _text.FirstLine) / MaxLine;
        return new Rectangle(Width / 3, top, Math.Max(3, Width / 3), height);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        if (MaxLine == 0) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var p = Art.Round(Thumb(), Math.Max(2, Width / 6));
        using var b = new SolidBrush(Focused || _drag ? Colors.Accent : Colors.Muted);
        e.Graphics.FillPath(b, p);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); Focus();
        var thumb = Thumb(); _grabOffset = thumb.Contains(e.Location) ? e.Y - thumb.Top : thumb.Height / 2;
        _drag = true; Capture = true; MoveThumb(e.Y);
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (_drag) MoveThumb(e.Y); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _drag = false; Capture = false; Invalidate(); }
    private void MoveThumb(int y)
    {
        int travel = Height - Thumb().Height;
        if (travel > 0) _text.ScrollToLine((int)Math.Clamp((long)(y - _grabOffset) * MaxLine / travel, 0, MaxLine));
        Invalidate();
    }
    protected override void OnMouseWheel(MouseEventArgs e) { _text.ScrollToLine(Math.Max(0, _text.FirstLine - e.Delta / 40)); Invalidate(); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        int next = e.KeyCode switch { Keys.Up => _text.FirstLine - 1, Keys.Down => _text.FirstLine + 1,
            Keys.PageUp => _text.FirstLine - _text.VisibleLines, Keys.PageDown => _text.FirstLine + _text.VisibleLines,
            Keys.Home => 0, Keys.End => MaxLine, _ => _text.FirstLine };
        _text.ScrollToLine(Math.Clamp(next, 0, MaxLine)); Invalidate(); base.OnKeyDown(e);
    }
}

internal sealed class TrayRenderer(Palette colors) : ToolStripProfessionalRenderer(new TrayColors(colors))
{
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? colors.Text : colors.Muted;
        base.OnRenderItemText(e);
    }
    private sealed class TrayColors(Palette p) : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => p.Surface;
        public override Color ImageMarginGradientBegin => p.Surface;
        public override Color ImageMarginGradientMiddle => p.Surface;
        public override Color ImageMarginGradientEnd => p.Surface;
        public override Color MenuItemSelected => p.Hover;
        public override Color MenuItemBorder => p.Border;
        public override Color MenuBorder => p.Border;
        public override Color SeparatorDark => p.Border;
        public override Color SeparatorLight => p.Surface;
        public override Color CheckBackground => p.Selected;
        public override Color CheckSelectedBackground => p.Selected;
    }
}
