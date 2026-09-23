using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

internal sealed class ViewerComboBox : LocalizedComboBox
{
    public ViewerComboBox()
    {
        SetStyle(Forms.ControlStyles.UserPaint | Forms.ControlStyles.AllPaintingInWmPaint | Forms.ControlStyles.OptimizedDoubleBuffer, true);
        DrawMode = Forms.DrawMode.OwnerDrawFixed; ItemHeight = 24;
    }
    protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
    protected override void OnDrawItem(Forms.DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index >= 0) Forms.TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, e.Bounds, e.ForeColor,
            Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.NoPrefix);
        e.DrawFocusRectangle();
    }
    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        float scale = DeviceDpi / 96f;
        e.Graphics.Clear(Parent?.BackColor ?? Color.White);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(.5f * scale, .5f * scale, Width - scale, Height - scale);
        using var path = new GraphicsPath();
        float diameter = 6 * scale;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure();
        using var fill = new SolidBrush(AppTheme.Background(Color.FromArgb(253, 253, 254)));
        using var pen = new Pen(Focused ? Color.FromArgb(0, 99, 255) : AppTheme.Line(Color.FromArgb(195, 200, 205)), scale);
        e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(pen, path);
        Forms.TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle((int)(7 * scale), 0, Width - (int)(30 * scale), Height), ForeColor,
            Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.NoPrefix | Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.SingleLine);
        using var arrow = new Pen(AppTheme.Ink(Color.FromArgb(106, 115, 124)), scale);
        float x = Width - 13 * scale, y = Height / 2f;
        e.Graphics.DrawLines(arrow, new PointF[] { new(x - 4 * scale, y - 2 * scale), new(x, y + 2 * scale), new(x + 4 * scale, y - 2 * scale) });
    }
}

internal sealed class ViewerStatusPill : Forms.Panel
{
    public ViewerStatusPill() => DoubleBuffered = true;
    protected override void OnPaintBackground(Forms.PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Color.White);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = new GraphicsPath();
        float diameter = Height - 1;
        if (diameter <= 0 || Width < Height) return;
        path.AddArc(0, 0, diameter, diameter, 90, 180);
        path.AddArc(Width - diameter - 1, 0, diameter, diameter, 270, 180);
        path.CloseFigure();
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillPath(brush, path);
    }
}

internal sealed class ViewerCheckBox : Forms.CheckBox
{
    public ViewerCheckBox() => SetStyle(Forms.ControlStyles.UserPaint | Forms.ControlStyles.AllPaintingInWmPaint | Forms.ControlStyles.OptimizedDoubleBuffer, true);
    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = Forms.TextRenderer.MeasureText(Text, Font, Size.Empty, Forms.TextFormatFlags.NoPadding);
        return new Size(text.Width + (int)Math.Round(23 * DeviceDpi / 96f), Math.Max(text.Height, (int)Math.Round(20 * DeviceDpi / 96f)));
    }
    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        if (Forms.SystemInformation.HighContrast) { base.OnPaint(e); return; }
        e.Graphics.Clear(BackColor);
        float scale = DeviceDpi / 96f, size = 15 * scale, top = (Height - size) / 2;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = new GraphicsPath();
        var box = new RectangleF(scale / 2, top, size - scale, size - scale);
        float diameter = 4 * scale;
        path.AddArc(box.Left, box.Top, diameter, diameter, 180, 90);
        path.AddArc(box.Right - diameter, box.Top, diameter, diameter, 270, 90);
        path.AddArc(box.Right - diameter, box.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(box.Left, box.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure();
        using var fill = new SolidBrush(Checked ? Color.FromArgb(0, 99, 255) : AppTheme.Background(Color.FromArgb(250, 251, 252)));
        using var border = new Pen(Checked ? Color.FromArgb(0, 99, 255) : AppTheme.Line(Color.FromArgb(153, 158, 165)), scale);
        e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(border, path);
        if (Checked)
        {
            using var check = new Pen(Color.White, 1.3f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            e.Graphics.DrawLines(check, new PointF[] { new(3.5f * scale, top + 7.5f * scale), new(6.3f * scale, top + 10 * scale), new(11 * scale, top + 4.5f * scale) });
        }
        var textBounds = new Rectangle((int)(23 * scale), 0, Math.Max(0, Width - (int)(23 * scale)), Height);
        Forms.TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, ForeColor,
            Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.NoPrefix | Forms.TextFormatFlags.SingleLine | Forms.TextFormatFlags.VerticalCenter);
        if (Focused && ShowFocusCues) Forms.ControlPaint.DrawFocusRectangle(e.Graphics, textBounds);
    }
}
