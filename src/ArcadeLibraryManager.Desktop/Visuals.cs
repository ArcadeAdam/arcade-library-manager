using System.Drawing.Drawing2D;

namespace ArcadeLibraryManager.Desktop;

internal static class Palette
{
    // FFB Blaster MASTER beta.16: embedded style.css :root tokens.
    public static readonly Color Ink = ColorTranslator.FromHtml("#ECE2FF");
    public static readonly Color Navy = ColorTranslator.FromHtml("#0B0716");
    public static readonly Color Muted = ColorTranslator.FromHtml("#BBA9D4");
    public static readonly Color Accent = ColorTranslator.FromHtml("#FF2D95");
    public static readonly Color Cyan = ColorTranslator.FromHtml("#16E0FF");
    public static readonly Color Amber = ColorTranslator.FromHtml("#FFC247");
    public static readonly Color Page = ColorTranslator.FromHtml("#07050D");
    public static readonly Color Surface = ColorTranslator.FromHtml("#100A1C");
    public static readonly Color Input = ColorTranslator.FromHtml("#0B0715");
    public static readonly Color Line = ColorTranslator.FromHtml("#392453");
    public static readonly Color Soft = ColorTranslator.FromHtml("#170F28");
    public static readonly Color Hover = ColorTranslator.FromHtml("#28183D");
    public static readonly Color Selection = ColorTranslator.FromHtml("#45275E");
    public static Color Blend(Color background, Color foreground, double strength) => Color.FromArgb(
        (int)Math.Round(background.R + (foreground.R - background.R) * strength),
        (int)Math.Round(background.G + (foreground.G - background.G) * strength),
        (int)Math.Round(background.B + (foreground.B - background.B) * strength));
    public static Font Font(float size = 10, FontStyle style = FontStyle.Regular) => new("Segoe UI", size, style);
    public static Button Button(string label, bool primary = false, int width = 140)
    {
        var b = new ThemeButton { Text = label, Width = width, Height = 38, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            BackColor = Soft, ForeColor = primary ? ColorTranslator.FromHtml("#FFD9EC") : Ink,
            Font = Font(9.5f, FontStyle.Bold), UseMnemonic = false, Margin = new Padding(0, 0, 10, 8), UseVisualStyleBackColor = false };
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = primary ? Accent : Cyan;
        b.FlatAppearance.MouseOverBackColor = Hover;
        b.FlatAppearance.MouseDownBackColor = Surface;
        return b;
    }
    public static Label Label(string text, float size = 10, Color? color = null, bool bold = false) => new()
    { Text = text, AutoSize = true, UseMnemonic = false, ForeColor = color ?? Ink, Font = Font(size, bold ? FontStyle.Bold : FontStyle.Regular), Margin = new Padding(0, 0, 0, 8) };
    public static TextBox TextBox(bool multiline = false) => new ExampleTextBox() { BorderStyle = BorderStyle.FixedSingle, Font = Font(), Height = multiline ? 80 : 30, Multiline = multiline, BackColor = Input, ForeColor = Ink, Dock = DockStyle.Fill };
    public static DataGridView Grid()
    {
        var grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, BackgroundColor = Surface,
            BorderStyle = BorderStyle.None, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = Line, RowHeadersVisible = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true,
            ColumnHeadersHeight = 42, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            EnableHeadersVisualStyles = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            Font = Font(9.5f), RowTemplate = { Height = 37 }, ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Soft, ForeColor = Cyan, Font = Font(9, FontStyle.Bold), Padding = new Padding(8, 0, 4, 0) };
        grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Surface, ForeColor = Ink, SelectionBackColor = Selection, SelectionForeColor = Ink, Padding = new Padding(8, 0, 4, 0) };
        grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Input };
        grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        grid.DataError += (_, e) => { e.ThrowException = false; };
        return grid;
    }
    public static void TextColumn(DataGridView grid, string property, string header, int fill = 100) => grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = property, HeaderText = header, FillWeight = fill, ReadOnly = true, SortMode = DataGridViewColumnSortMode.Automatic });
    public static void CheckColumn(DataGridView grid) => grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = "Selected", HeaderText = "", Width = 42, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, ReadOnly = false });
}

internal sealed class ThemeButton : Button
{
    private bool hovered, pressed;
    public ThemeButton() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    protected override void OnMouseEnter(EventArgs e) { hovered = true; base.OnMouseEnter(e); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; base.OnMouseLeave(e); Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) pressed = true; base.OnMouseDown(e); Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; base.OnMouseUp(e); Invalidate(); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) pressed = true; base.OnKeyDown(e); Invalidate(); }
    protected override void OnKeyUp(KeyEventArgs e) { pressed = false; base.OnKeyUp(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { pressed = false; base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { if (!Enabled) pressed = false; base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 5 || Height < 5) return;
        var accent = FlatAppearance.BorderColor.IsEmpty ? Palette.Cyan : FlatAppearance.BorderColor;
        var frame = new Rectangle(2, 2, Width - 5, Height - 5);
        using var background = new SolidBrush(BackColor); e.Graphics.FillRectangle(background, ClientRectangle);
        var intensity = !Enabled ? .035 : pressed ? .22 : hovered || Focused ? .19 : .085;
        using var fill = new LinearGradientBrush(frame, Palette.Blend(BackColor, accent, intensity), Palette.Blend(BackColor, Palette.Cyan, intensity / 3), 90f);
        e.Graphics.FillRectangle(fill, frame);
        using var glow = new Pen(Color.FromArgb(!Enabled ? 12 : hovered || Focused ? 76 : 38, accent), 5);
        using var edge = new Pen(Enabled ? accent : Palette.Blend(Palette.Line, accent, .18), 1);
        e.Graphics.DrawRectangle(glow, frame); e.Graphics.DrawRectangle(edge, frame);
        var textBounds = Rectangle.Inflate(ClientRectangle, -8, -3); if (pressed && Enabled) textBounds.Offset(1, 1);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        flags |= TextAlign is ContentAlignment.MiddleLeft or ContentAlignment.TopLeft or ContentAlignment.BottomLeft ? TextFormatFlags.Left : TextAlign is ContentAlignment.MiddleRight or ContentAlignment.TopRight or ContentAlignment.BottomRight ? TextFormatFlags.Right : TextFormatFlags.HorizontalCenter;
        if (!UseMnemonic) flags |= TextFormatFlags.NoPrefix; else if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
        TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, Enabled ? ForeColor : Palette.Muted, flags);
        if (Focused && ShowFocusCues && Enabled) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(frame, -4, -4), Palette.Ink, BackColor);
    }
}

internal sealed class NeonProgressBar : Control
{
    private int minimum, maximum = 100, value;
    private ProgressBarStyle style = ProgressBarStyle.Continuous;
    private readonly System.Windows.Forms.Timer marqueeTimer = new() { Interval = 40 };
    private double marqueePosition = .25;
    private bool timerDisposed;

    public NeonProgressBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false); TabStop = false; AccessibleRole = AccessibleRole.ProgressBar;
        BackColor = Palette.Input; ForeColor = Palette.Cyan; Height = 18;
        marqueeTimer.Tick += (_, _) => { marqueePosition = (marqueePosition + .018) % 1; Invalidate(); };
    }
    [System.ComponentModel.DefaultValue(0)]
    public int Minimum
    {
        get => minimum;
        set { ArgumentOutOfRangeException.ThrowIfNegative(value); minimum = value; if (maximum < minimum) maximum = minimum; this.value = Math.Clamp(this.value, minimum, maximum); NotifyValueChanged(); }
    }
    [System.ComponentModel.DefaultValue(100)]
    public int Maximum
    {
        get => maximum;
        set { ArgumentOutOfRangeException.ThrowIfNegative(value); maximum = value; if (minimum > maximum) minimum = maximum; this.value = Math.Clamp(this.value, minimum, maximum); NotifyValueChanged(); }
    }
    [System.ComponentModel.DefaultValue(0)]
    public int Value
    {
        get => value;
        set { if (value < minimum || value > maximum) throw new ArgumentOutOfRangeException(nameof(value)); if (this.value == value) return; this.value = value; NotifyValueChanged(); }
    }
    [System.ComponentModel.DefaultValue(ProgressBarStyle.Continuous)]
    public ProgressBarStyle Style
    {
        get => style;
        set { if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value)); style = value; UpdateTimer(); NotifyValueChanged(); }
    }
    private void NotifyValueChanged() { Invalidate(); if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1); }
    private void UpdateTimer()
    {
        if (timerDisposed) return;
        marqueeTimer.Enabled = style == ProgressBarStyle.Marquee && Visible && Enabled && IsHandleCreated && !IsDisposed && !Disposing;
    }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); UpdateTimer(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); UpdateTimer(); Invalidate(); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UpdateTimer(); }
    protected override void OnHandleDestroyed(EventArgs e) { if (!timerDisposed) marqueeTimer.Stop(); base.OnHandleDestroyed(e); }
    protected override AccessibleObject CreateAccessibilityInstance() => new NeonProgressAccessibleObject(this);
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        if (Width < 5 || Height < 4) return;
        var outer = new Rectangle(0, 0, Width - 1, Height - 1);
        using var edge = new Pen(Palette.Blend(Palette.Line, Palette.Cyan, .32)); e.Graphics.DrawRectangle(edge, outer);
        var inset = Height < 12 ? 1 : 3;
        var track = new Rectangle(inset, inset, Width - inset * 2, Height - inset * 2);
        Rectangle fill;
        if (style == ProgressBarStyle.Marquee)
        {
            var band = Math.Max(12, (int)(track.Width * .32));
            fill = new Rectangle(track.Left + (int)((track.Width + band) * marqueePosition) - band, track.Top, band, track.Height);
        }
        else
        {
            var ratio = maximum == minimum ? 0 : ((double)value - minimum) / ((double)maximum - minimum);
            var pixels = (int)Math.Round(track.Width * ratio);
            if (value < maximum && track.Width > 1) pixels = Math.Min(pixels, track.Width - 1);
            fill = new Rectangle(track.Left, track.Top, pixels, track.Height);
        }
        if (fill.Width <= 0) return;
        var state = e.Graphics.Save(); e.Graphics.SetClip(track);
        using var neon = new LinearGradientBrush(fill, Enabled ? Palette.Accent : Palette.Blend(BackColor, Palette.Accent, .45), Enabled ? Palette.Cyan : Palette.Blend(BackColor, Palette.Cyan, .45), 0f);
        e.Graphics.FillRectangle(neon, fill);
        using var shine = new Pen(Color.FromArgb(110, Color.White)); e.Graphics.DrawLine(shine, fill.Left, fill.Top, fill.Right, fill.Top);
        e.Graphics.Restore(state);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !timerDisposed) { marqueeTimer.Stop(); marqueeTimer.Dispose(); timerDisposed = true; }
        base.Dispose(disposing);
    }
    private sealed class NeonProgressAccessibleObject(NeonProgressBar owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.ProgressBar;
        public override AccessibleStates State => base.State | AccessibleStates.ReadOnly;
        public override string? Value
        {
            get => owner.Style == ProgressBarStyle.Marquee ? "Working" : $"{Math.Min(owner.Value < owner.Maximum ? 99 : 100, Math.Round(owner.Maximum == owner.Minimum ? 0 : 100d * (owner.Value - (double)owner.Minimum) / (owner.Maximum - (double)owner.Minimum))):0}%";
            set { }
        }
    }
}

internal sealed class NeonHeading : Label
{
    public NeonHeading()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        ForeColor = Color.White;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var bounds = new Rectangle(Padding.Left, Padding.Top, Math.Max(0, ClientSize.Width - Padding.Horizontal), Math.Max(0, ClientSize.Height - Padding.Vertical));
        var flags = TextFormatFlags.WordBreak;
        flags |= TextAlign is ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter ? TextFormatFlags.HorizontalCenter : TextAlign is ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight ? TextFormatFlags.Right : TextFormatFlags.Left;
        flags |= TextAlign is ContentAlignment.MiddleLeft or ContentAlignment.MiddleCenter or ContentAlignment.MiddleRight ? TextFormatFlags.VerticalCenter : TextAlign is ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight ? TextFormatFlags.Bottom : TextFormatFlags.Top;
        if (!UseMnemonic) flags |= TextFormatFlags.NoPrefix; else if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
        if (AutoEllipsis) flags |= TextFormatFlags.EndEllipsis;
        var background = BackColor.A == 0 ? Parent?.BackColor ?? Palette.Page : BackColor;
        var shadow = bounds; shadow.Offset(-1, 1);
        TextRenderer.DrawText(e.Graphics, Text, Font, shadow, Palette.Blend(background, Palette.Accent, .65), flags);
        shadow = bounds; shadow.Offset(1, 1);
        TextRenderer.DrawText(e.Graphics, Text, Font, shadow, Palette.Blend(background, Palette.Cyan, .45), flags);
        TextRenderer.DrawText(e.Graphics, Text, Font, bounds, Enabled ? ForeColor : Palette.Muted, flags);
    }
}

internal sealed class ExampleTextBox : TextBox
{
    // Native edit controls omit cue banners from WM_PRINT on some Windows themes.
    // Draw the same display-only hint after both live painting and offscreen printing.
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg is not (0x000F or 0x0317 or 0x0318) || TextLength != 0 || Focused || !Enabled || string.IsNullOrEmpty(PlaceholderText)) return;
        if (m.Msg == 0x000F) { using var graphics = CreateGraphics(); DrawExample(graphics); }
        else if (m.WParam != IntPtr.Zero) { using var graphics = Graphics.FromHdc(m.WParam); DrawExample(graphics); }
    }
    private void DrawExample(Graphics graphics)
    {
        var bounds = ClientRectangle; bounds.Inflate(-3, -2);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        TextRenderer.DrawText(graphics, PlaceholderText, Font, bounds, Palette.Muted, BackColor,
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | (Multiline ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter));
    }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
}
internal sealed class ThemeTabs : TabControl
{
    public ThemeTabs()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        ForeColor = Palette.Ink;
    }
    protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Palette.Page);
        using var border = new Pen(Palette.Blend(Palette.Line, Palette.Cyan, .28));
        var body = DisplayRectangle; body.Inflate(2, 2); e.Graphics.DrawRectangle(border, body);
        for (var index = 0; index < TabPages.Count; index++) {
            var bounds = GetTabRect(index); var selected = index == SelectedIndex;
            using var fill = new LinearGradientBrush(bounds, selected ? Palette.Hover : Palette.Surface, Palette.Surface, 90f);
            e.Graphics.FillRectangle(fill, bounds);
            TextRenderer.DrawText(e.Graphics, TabPages[index].Text, Font, bounds, selected ? Palette.Ink : Palette.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            if (selected)
            {
                using var accent = new LinearGradientBrush(bounds, Palette.Accent, Palette.Cyan, 0f);
                e.Graphics.FillRectangle(accent, bounds.Left, bounds.Bottom - 3, bounds.Width, 3);
                using var glow = new Pen(Color.FromArgb(80, Palette.Accent), 3); e.Graphics.DrawLine(glow, bounds.Left + 1, bounds.Bottom - 5, bounds.Right - 1, bounds.Bottom - 5);
            }
            if (selected && Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -4, -4), Palette.Cyan, Palette.Soft);
        }
        base.OnPaint(e);
    }
}

internal sealed class MetricCard : Panel
{
    private readonly Label _value;
    public MetricCard(string title, string value, string description)
    {
        Height = 102; BackColor = Palette.Surface; Padding = new Padding(17, 12, 12, 9); Margin = new Padding(0, 0, 12, 0);
        _value = Palette.Label(value, 25, Palette.Cyan, true); _value.Location = new Point(16, 27);
        var caption = Palette.Label(title.ToUpperInvariant(), 8.2f, Palette.Muted, true); caption.Location = new Point(18, 12);
        var note = Palette.Label(description, 8.5f, Palette.Muted); note.Location = new Point(18, 76);
        Controls.AddRange([caption, _value, note]);
        Paint += (_, e) => {
            if (Width < 3 || Height < 3) return;
            using var pen = new Pen(Palette.Blend(Palette.Line, Palette.Cyan, .3)); e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            using var accent = new LinearGradientBrush(ClientRectangle, Palette.Accent, Palette.Cyan, 0f); e.Graphics.FillRectangle(accent, 1, 1, Width - 2, 2);
            using var glow = new Pen(Color.FromArgb(45, Palette.Accent), 5); e.Graphics.DrawLine(glow, 3, 5, 3, Height - 5);
        };
    }
    public void SetValue(int value) => _value.Text = value.ToString("N0");
}

internal sealed class BrandMark : Control
{
    public BrandMark() { DoubleBuffered = true; Size = new Size(47, 47); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var neon = new LinearGradientBrush(new Rectangle(0, 0, 46, 46), Palette.Accent, Palette.Cyan, 45f);
        using var dark = new SolidBrush(Palette.Navy);
        e.Graphics.FillRectangle(neon, 0, 0, 46, 46);
        Point[] shape = [new(9, 33), new(20, 11), new(27, 11), new(38, 33), new(30, 33), new(23, 19), new(17, 33)];
        e.Graphics.FillPolygon(dark, shape);
        e.Graphics.FillRectangle(dark, 20, 29, 7, 5);
    }
}