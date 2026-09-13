using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RemoteDebugger;

/// <summary>Native accessible buttons with consistent, DPI-scaled rendering.</summary>
internal sealed class WorkspaceButton : Button
{
    private bool hovered, pressed;
    public WorkspaceButton() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    protected override void OnTextChanged(EventArgs e) { AccessibleName = Text; base.OnTextChanged(e); }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { pressed = false; Invalidate(); base.OnLostFocus(e); }
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
        Color fill = !Enabled ? SystemColors.Control : pressed ? ControlPaint.Dark(BackColor, .08f) : hovered ? ControlPaint.Light(BackColor, .08f) : BackColor;
        using var brush = new SolidBrush(fill); e.Graphics.FillPath(brush, path);
        if (FlatAppearance.BorderSize > 0 || Focused)
        {
            using var pen = new Pen(Focused ? Color.FromArgb(8, 127, 131) : FlatAppearance.BorderColor, DeviceDpi / 96f);
            e.Graphics.DrawPath(pen, path);
        }
        var textBounds = new Rectangle(Padding.Left + 4, Padding.Top, Math.Max(0, Width - Padding.Horizontal - 8), Height - Padding.Vertical);
        TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, Enabled ? ForeColor : SystemColors.GrayText,
            TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            (TextAlign == ContentAlignment.MiddleLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter));
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5), ForeColor, fill);
    }
}
