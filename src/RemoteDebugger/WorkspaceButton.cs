using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RemoteDebugger;

/// <summary>Native accessible buttons with consistent, DPI-scaled rendering.</summary>
internal sealed class WorkspaceButton : Button
{
    private bool hovered, pressed;
    public string Glyph { get; set; } = "";
    public bool RailStyle { get; set; }
    public bool Selected { get; set; }
    public bool DisclosureStyle { get; set; }
    public override Size GetPreferredSize(Size proposedSize)
    {
        var size = base.GetPreferredSize(proposedSize);
        if (Glyph.Length == 0 || !AutoSize) return size;
        int text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
        return size with { Width = Math.Max(MinimumSize.Width, text + Padding.Horizontal + (int)(48 * DeviceDpi / 96f)) };
    }
    public WorkspaceButton()
    {
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnTextChanged(EventArgs e) { AccessibleName = Text; base.OnTextChanged(e); }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { pressed = false; Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { hovered = pressed = false; Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (SystemInformation.HighContrast) { base.OnPaint(e); return; }
        e.Graphics.Clear(Parent?.BackColor ?? SystemColors.Control);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(0.75f, 0.75f, Width - 1.5f, Height - 1.5f);
        float diameter = Math.Min(12f * DeviceDpi / 96f, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0) return;
        using var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure();
        Color fill = !Enabled && !RailStyle ? AppTheme.Background(Color.FromArgb(238, 242, 245)) : pressed ? ControlPaint.Dark(BackColor, .08f) : hovered && Enabled ? ControlPaint.Light(BackColor, .08f) : BackColor;
        using var brush = new SolidBrush(fill); e.Graphics.FillPath(brush, path);
        if (FlatAppearance.BorderSize > 0 || Focused && !DisclosureStyle)
        {
            using var pen = new Pen(Focused ? Color.FromArgb(8, 127, 131) : FlatAppearance.BorderColor, DeviceDpi / 96f);
            e.Graphics.DrawPath(pen, path);
        }
        int iconSpace = Glyph.Length == 0 ? 0 : (int)((RailStyle ? 40 : DisclosureStyle ? 28 : 32) * DeviceDpi / 96f);
        var textBounds = new Rectangle(Padding.Left + 4 + iconSpace, Padding.Top, Math.Max(0, Width - Padding.Horizontal - 8 - iconSpace), Height - Padding.Vertical);
        Color ink = Enabled ? ForeColor : RailStyle ? Color.FromArgb(103, 124, 137) : Color.FromArgb(146, 164, 189);
        int iconLeft = DisclosureStyle ? 0 : RailStyle ? Padding.Left : 14;
        if (Glyph.Length > 0) UiGlyph.Draw(e.Graphics, Glyph, Text.Length == 0 ? Rectangle.Inflate(ClientRectangle, -8, -8) : new Rectangle((int)(iconLeft * (RailStyle ? 1 : DeviceDpi / 96f)), 0, iconSpace - 8, Height), RailStyle && Selected && Name == "roleController" ? Color.FromArgb(0, 211, 224) : ink);
        if (RailStyle && Selected && (Name.StartsWith("nav", StringComparison.Ordinal) || Name == "roleAgent"))
        {
            using var accent = new SolidBrush(Color.FromArgb(8, 170, 178));
            e.Graphics.FillRectangle(accent, 0, 0, Math.Max(6, DeviceDpi / 16), Height);
        }
        TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, ink,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            (TextAlign == ContentAlignment.MiddleLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter));
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5), ForeColor, fill);
    }
}
