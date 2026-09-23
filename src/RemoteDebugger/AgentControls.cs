using System.Drawing.Drawing2D;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

internal sealed class AgentMaintenanceSwitch : Forms.CheckBox
{
    internal AgentMaintenanceSwitch()
    {
        SetStyle(Forms.ControlStyles.UserPaint | Forms.ControlStyles.AllPaintingInWmPaint | Forms.ControlStyles.OptimizedDoubleBuffer, true);
        Cursor = Forms.Cursors.Hand;
        FlatStyle = Forms.FlatStyle.Flat;
    }
    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        if (Forms.SystemInformation.HighContrast) { base.OnPaint(e); return; }
        e.Graphics.Clear(Parent?.BackColor ?? SystemColors.Control);
        float scale = DeviceDpi / 96f, diameter = 34 * scale, width = 62 * scale, top = (Height - diameter) / 2;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = new GraphicsPath();
        path.AddArc(0, top, diameter, diameter, 90, 180); path.AddArc(width - diameter, top, diameter, diameter, 270, 180); path.CloseFigure();
        using var track = new SolidBrush(Enabled && Checked ? Color.FromArgb(0, 153, 165) : Color.FromArgb(161, 171, 185));
        using var thumb = new SolidBrush(Color.White);
        e.Graphics.FillPath(track, path); e.Graphics.FillEllipse(thumb, Checked ? width - diameter + 4 * scale : 4 * scale, top + 4 * scale, diameter - 8 * scale, diameter - 8 * scale);
        Forms.TextRenderer.DrawText(e.Graphics, UiText.Get(Checked ? "GiveOn" : "GiveOff"), Font,
            new Rectangle((int)(78 * scale), 0, Math.Max(0, Width - (int)(78 * scale)), Height), AppTheme.Ink(Color.FromArgb(109, 135, 172)),
            Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.NoPrefix | Forms.TextFormatFlags.VerticalCenter);
        if (Focused && ShowFocusCues) Forms.ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -1, -1));
    }
}

internal sealed class AgentStepNumber(int number) : Forms.Control
{
    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var fill = new SolidBrush(AppTheme.Background(Color.FromArgb(228, 239, 245)));
        using var font = new Font("Segoe UI", 13, FontStyle.Bold);
        e.Graphics.FillEllipse(fill, 0, 0, Width - 1, Height - 1);
        Forms.TextRenderer.DrawText(e.Graphics, number.ToString(), font, ClientRectangle, AppTheme.Ink(Color.FromArgb(9, 18, 38)),
            Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.HorizontalCenter | Forms.TextFormatFlags.VerticalCenter);
    }
}

internal sealed class AgentInstructionLabel : Forms.Control
{
    internal string Emphasis { get; set; } = "";
    private const Forms.TextFormatFlags Flags = Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.NoPrefix | Forms.TextFormatFlags.SingleLine;
    internal AgentInstructionLabel() { AccessibleRole = Forms.AccessibleRole.StaticText; SetStyle(Forms.ControlStyles.OptimizedDoubleBuffer, true); }
    protected override void OnTextChanged(EventArgs e) { AccessibleName = Text; base.OnTextChanged(e); Invalidate(); }
    public override Size GetPreferredSize(Size proposedSize) => new(proposedSize.Width, DrawWords(null, Math.Max(1, proposedSize.Width)));
    protected override void OnPaint(Forms.PaintEventArgs e) => DrawWords(e.Graphics, Width);
    private int DrawWords(Graphics? graphics, int width)
    {
        using var bold = new Font(Font, FontStyle.Bold);
        int x = 0, y = 0, lineHeight = Font.Height;
        foreach (string word in System.Text.RegularExpressions.Regex.Split(Text, "(\\s+)"))
        {
            Font font = Emphasis.Length > 0 && word.Contains(Emphasis, StringComparison.Ordinal) ? bold : Font;
            int length = Forms.TextRenderer.MeasureText(word, font, Size.Empty, Flags).Width;
            if (x > 0 && x + length > width) { x = 0; y += lineHeight; }
            if (x == 0 && string.IsNullOrWhiteSpace(word)) continue;
            if (graphics != null) Forms.TextRenderer.DrawText(graphics, word, font, new Point(x, y), ForeColor, Flags);
            x += length;
        }
        return y + lineHeight;
    }
}
