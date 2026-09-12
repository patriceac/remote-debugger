using Forms = System.Windows.Forms;

namespace RemoteDebugger;

/// <summary>Text whose layout edge is independent of its font size.</summary>
internal sealed class WorkspaceLabel : Forms.Label
{
    public bool WrapText { get; set; } = true;
    public WorkspaceLabel()
    {
        Margin = Forms.Padding.Empty;
        UseMnemonic = false;
    }

    private Forms.TextFormatFlags TextFlags => Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.NoPrefix |
        (WrapText ? Forms.TextFormatFlags.WordBreak : Forms.TextFormatFlags.SingleLine) | (AutoEllipsis ? Forms.TextFormatFlags.EndEllipsis : 0);

    public override Size GetPreferredSize(Size proposedSize)
    {
        int width = WrapText && proposedSize.Width > Padding.Horizontal ? proposedSize.Width - Padding.Horizontal : int.MaxValue;
        if (MaximumSize.Width > 0) width = Math.Min(width, Math.Max(1, MaximumSize.Width - Padding.Horizontal));
        var text = Forms.TextRenderer.MeasureText(Text, Font, new Size(width, int.MaxValue), TextFlags);
        return new Size(text.Width + Padding.Horizontal, text.Height + Padding.Vertical);
    }

    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        var bounds = new Rectangle(Padding.Left, Padding.Top,
            Math.Max(0, ClientSize.Width - Padding.Horizontal), Math.Max(0, ClientSize.Height - Padding.Vertical));
        var flags = TextFlags;
        if (TextAlign is ContentAlignment.MiddleLeft or ContentAlignment.MiddleCenter or ContentAlignment.MiddleRight)
            flags |= Forms.TextFormatFlags.VerticalCenter;
        else if (TextAlign is ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight)
            flags |= Forms.TextFormatFlags.Bottom;
        if (TextAlign is ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter)
            flags |= Forms.TextFormatFlags.HorizontalCenter;
        else if (TextAlign is ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight)
            flags |= Forms.TextFormatFlags.Right;
        Forms.TextRenderer.DrawText(e.Graphics, Text, Font, bounds, Enabled ? ForeColor : SystemColors.GrayText, flags);
    }
}
