using System.Drawing.Drawing2D;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

// Measure wrapping text at the column's available width. The native table's
// unconstrained preferred size otherwise reserves multiple lines of empty space.
internal sealed class ConnectionLayoutPanel : Forms.TableLayoutPanel
{
    public override Size GetPreferredSize(Size proposedSize)
    {
        int width = Math.Max(1, proposedSize.Width > 1 ? proposedSize.Width : Width);
        int available = Math.Max(0, width - Padding.Horizontal);
        float fixedWidth = ColumnStyles.Cast<Forms.ColumnStyle>().Where(c => c.SizeType == Forms.SizeType.Absolute).Sum(c => c.Width);
        float percent = ColumnStyles.Cast<Forms.ColumnStyle>().Where(c => c.SizeType == Forms.SizeType.Percent).Sum(c => c.Width);
        var heights = new int[Math.Max(1, RowCount)];
        foreach (Forms.Control child in Controls)
        {
            if (!child.Visible) continue;
            int column = GetColumn(child), row = GetRow(child);
            if (column < 0 || row < 0 || row >= heights.Length) continue;
            var style = ColumnStyles[column];
            int columnWidth = style.SizeType == Forms.SizeType.Absolute ? (int)style.Width
                : (int)(Math.Max(0, available - fixedWidth) * style.Width / Math.Max(1, percent));
            int height = child.AutoSize ? child.GetPreferredSize(new(Math.Max(1, columnWidth - child.Margin.Horizontal), 0)).Height : child.Height;
            heights[row] = Math.Max(heights[row], height + child.Margin.Vertical);
        }
        return new(width, Padding.Vertical + heights.Sum());
    }
}

internal static class UiGlyph
{
    internal const string Computer = "\uE7F4", Link = "\uE71B", Processes = "\uE8FD", Folder = "\uE8B7",
        Diagnostics = "\uE9D9", Give = "\uE72A", Take = "\uE72B", Refresh = "\uE72C", Update = "\uE898",
        Wake = "\uE7E8", Settings = "\uE713", Down = "\uE70D", Right = "\uE76C", Close = "close", Info = "info", Shield = "shield",
        Globe = "globe", Moon = "moon", CheckCircle = "checkCircle";

    internal static void Draw(Graphics graphics, string glyph, Rectangle bounds, Color color)
    {
        if (glyph is Computer or Processes or Folder or Diagnostics or Wake or Close or Info or Shield or Link or Give or Take or Refresh or Update or Down or Right or Globe or Moon or CheckCircle)
        {
            var state = graphics.Save();
            float size = Math.Min(bounds.Width, bounds.Height);
            graphics.TranslateTransform(bounds.X + (bounds.Width - size) / 2f, bounds.Y + (bounds.Height - size) / 2f);
            graphics.ScaleTransform(size / 24f, size / 24f); graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(color, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            switch (glyph)
            {
                case Globe:
                    graphics.DrawEllipse(pen, 2, 2, 20, 20); graphics.DrawEllipse(pen, 7, 2, 10, 20); graphics.DrawLine(pen, 2, 12, 22, 12); break;
                case Moon:
                    using (var moon = new GraphicsPath()) { moon.AddArc(2, 2, 20, 20, 270, -270); moon.AddBezier(22, 12, 13, 17, 7, 10, 12, 2); moon.CloseFigure(); graphics.DrawPath(pen, moon); } break;
                case CheckCircle:
                    using (var fill = new SolidBrush(color)) graphics.FillEllipse(fill, 0, 0, 24, 24);
                    using (var check = new Pen(Color.White, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        graphics.DrawLines(check, new PointF[] { new(6.5f, 12), new(10, 15.5f), new(17.5f, 8) }); break;
                case Computer:
                    graphics.DrawRectangle(pen, 2, 3, 20, 14); graphics.DrawLine(pen, 12, 17, 12, 21); graphics.DrawLine(pen, 7, 21, 17, 21); break;
                case Folder:
                    graphics.DrawPolygon(pen, new PointF[] { new(2, 6), new(9, 6), new(11, 9), new(22, 9), new(22, 21), new(2, 21) }); break;
                case Processes:
                    for (int y = 5; y <= 19; y += 7) { graphics.DrawEllipse(pen, 2, y - 1, 2, 2); graphics.DrawLine(pen, 8, y, 22, y); } break;
                case Diagnostics:
                    graphics.DrawLines(pen, new PointF[] { new(1, 13), new(6, 13), new(9, 3), new(14, 22), new(18, 10), new(23, 10) }); break;
                case Wake:
                    graphics.DrawArc(pen, 3, 3, 18, 18, 310, 280); graphics.DrawLine(pen, 12, 1, 12, 11); break;
                case Close:
                    graphics.DrawLine(pen, 4, 4, 20, 20); graphics.DrawLine(pen, 20, 4, 4, 20); break;
                case Info:
                    graphics.DrawEllipse(pen, 2, 2, 20, 20); graphics.DrawLine(pen, 12, 10, 12, 17); graphics.DrawLine(pen, 12, 6, 12, 6.5f); break;
                case Shield:
                    graphics.DrawLines(pen, new PointF[] { new(12, 2), new(21, 6), new(20, 16), new(17, 20), new(12, 23), new(7, 20), new(4, 16), new(3, 6), new(12, 2) }); break;
                case Down:
                    graphics.DrawLines(pen, new PointF[] { new(5, 9), new(12, 16), new(19, 9) }); break;
                case Right:
                    graphics.DrawLines(pen, new PointF[] { new(9, 5), new(16, 12), new(9, 19) }); break;
                case Give: case Take:
                    graphics.DrawLine(pen, 3, 9, 21, 9); graphics.DrawLines(pen, new PointF[] { new(16, 4), new(21, 9), new(16, 14) });
                    graphics.DrawLine(pen, 3, 17, 12, 17); graphics.DrawLine(pen, 3, 17, 3, 21); break;
                case Refresh:
                    graphics.DrawArc(pen, 3, 3, 18, 18, 35, 300); graphics.DrawLines(pen, new PointF[] { new(16, 5), new(22, 6), new(22, 0) }); break;
                case Update:
                    graphics.DrawLine(pen, 12, 3, 12, 17); graphics.DrawLines(pen, new PointF[] { new(7, 8), new(12, 3), new(17, 8) }); graphics.DrawLine(pen, 3, 22, 21, 22); break;
                case Link:
                    graphics.TranslateTransform(12, 12); graphics.RotateTransform(-42); graphics.TranslateTransform(-12, -12);
                    graphics.DrawArc(pen, 1, 7, 13, 10, 45, 270); graphics.DrawArc(pen, 10, 7, 13, 10, 225, 270); graphics.DrawLine(pen, 7, 12, 17, 12); break;
            }
            graphics.Restore(state); return;
        }
        using var font = new Font("Segoe MDL2 Assets", Math.Max(9, Math.Min(bounds.Width, bounds.Height) * .72f), FontStyle.Regular, GraphicsUnit.Pixel);
        Forms.TextRenderer.DrawText(graphics, glyph, font, bounds, color,
            Forms.TextFormatFlags.HorizontalCenter | Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.NoPadding);
    }

    internal static Forms.Control Icon(string glyph, int size, Color color) => new GlyphView(glyph, color) { Size = new(size, size), Margin = Forms.Padding.Empty };
    private sealed class GlyphView(string glyph, Color color) : Forms.Control
    {
        protected override void OnPaint(Forms.PaintEventArgs e) => Draw(e.Graphics, glyph, ClientRectangle, color);
    }
}

internal sealed class ConnectionComboBox : Forms.ComboBox
{
    internal ConnectionComboBox()
    {
        DrawMode = Forms.DrawMode.OwnerDrawFixed;
        SetStyle(Forms.ControlStyles.UserPaint | Forms.ControlStyles.OptimizedDoubleBuffer | Forms.ControlStyles.AllPaintingInWmPaint, true);
    }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); ItemHeight = Font.Height + 2; }
    protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
    protected override void OnDrawItem(Forms.DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index >= 0) Forms.TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, e.Bounds, e.ForeColor, Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.VerticalCenter);
        e.DrawFocusRectangle();
    }
    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        Forms.TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(0, 0, Math.Max(0, Width - 30), Height), ForeColor,
            Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.NoPrefix | Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.EndEllipsis);
        UiGlyph.Draw(e.Graphics, UiGlyph.Down, new(Width - 22, (Height - 20) / 2, 20, 20), ForeColor);
    }
}

internal static class ConnectionField
{
    internal static Forms.Panel Wrap(Forms.Control editor, int height = 50)
    {
        var field = new Forms.Panel { Dock = Forms.DockStyle.Top, Height = height, Margin = Forms.Padding.Empty };
        editor.Dock = Forms.DockStyle.None; editor.Margin = Forms.Padding.Empty; field.Controls.Add(editor);
        void LayoutEditor() => editor.SetBounds(12, (field.Height - editor.PreferredSize.Height) / 2, Math.Max(0, field.Width - 24), editor.PreferredSize.Height);
        field.SizeChanged += (_, _) => LayoutEditor(); editor.FontChanged += (_, _) => LayoutEditor();
        void RefreshField() { field.BackColor = editor.BackColor = editor.Enabled ? Color.White : Color.FromArgb(237, 243, 247); field.Invalidate(); }
        editor.Enter += (_, _) => RefreshField(); editor.Leave += (_, _) => RefreshField(); editor.EnabledChanged += (_, _) => RefreshField();
        field.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(editor.ContainsFocus ? Color.FromArgb(0, 153, 165) : Color.FromArgb(200, 213, 229));
            using var path = new GraphicsPath();
            path.AddArc(0, 0, 8, 8, 180, 90); path.AddArc(field.Width - 9, 0, 8, 8, 270, 90);
            path.AddArc(field.Width - 9, field.Height - 9, 8, 8, 0, 90); path.AddArc(0, field.Height - 9, 8, 8, 90, 90); path.CloseFigure();
            e.Graphics.DrawPath(pen, path);
        };
        RefreshField(); LayoutEditor(); return field;
    }
}

internal sealed class UpdateTimeline : Forms.Control
{
    private string stage = "preparing";
    private IReadOnlyList<string> completed = [];
    internal bool Interrupted { get; set; }
    internal static int StepIndex(string value) => value switch
    {
        "idle" or "hashing" or "preparing" => 0, "transferring" => 1, "verifying" => 2,
        "restarting" or "finalizing" or "complete" => 3, _ => -1
    };
    internal static string StepState(int step, string current, IReadOnlyList<string> finished)
    {
        if (current == "complete") return "complete";
        if (StepIndex(current) == step) return "active";
        return finished.Any(value => StepIndex(value) == step) ? "complete" : "pending";
    }
    internal void SetProgress(string current, IReadOnlyList<string> finished)
    {
        stage = current; completed = finished;
        AccessibleName = string.Join("; ", Enumerable.Range(0, 4).Select(i => Title(i) + ": " + Status(i)));
        Invalidate();
    }
    internal UpdateTimeline()
    {
        Name = "updateTimeline"; AccessibleRole = Forms.AccessibleRole.List;
        SetStyle(Forms.ControlStyles.UserPaint | Forms.ControlStyles.OptimizedDoubleBuffer | Forms.ControlStyles.AllPaintingInWmPaint, true);
    }
    private static string Title(int step) => UiText.Get(new[] { "ConnectionPrepare", "ConnectionTransfer", "ConnectionVerify", "ConnectionRestart" }[step]);
    private string Status(int step) => UiText.Get(StepState(step, stage, completed) switch
    {
        "complete" => "ConnectionComplete", "active" => Interrupted ? "ConnectionInterrupted" : "ConnectionInProgress", _ => "ConnectionPending"
    });
    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96f;
        using var titleFont = new Font("Segoe UI", 12.5f);
        using var detailFont = new Font("Segoe UI", 11);
        using var line = new Pen(Color.FromArgb(6, 156, 168), 2 * scale);
        e.Graphics.DrawLine(line, 22 * scale, 16 * scale, 22 * scale, 172 * scale);
        for (int i = 0; i < 4; i++)
        {
            int y = (int)(i * 52 * scale);
            string state = StepState(i, stage, completed);
            Color accent = Interrupted && state == "active" ? Color.FromArgb(184, 61, 73) : Color.FromArgb(0, 151, 165);
            var circle = new RectangleF(9 * scale, y + 4 * scale, 26 * scale, 26 * scale);
            using var fill = new SolidBrush(state == "complete" ? accent : BackColor);
            using var outline = new Pen(state == "pending" ? Color.FromArgb(207, 220, 226) : accent, 2 * scale);
            e.Graphics.FillEllipse(fill, circle); e.Graphics.DrawEllipse(outline, circle);
            if (state == "complete")
            {
                using var check = new Pen(Color.White, 1.6f * scale);
                e.Graphics.DrawLines(check, new PointF[] { new(16 * scale, y + 17 * scale), new(20 * scale, y + 21 * scale), new(28 * scale, y + 12 * scale) });
            }
            int left = (int)(60 * scale);
            Forms.TextRenderer.DrawText(e.Graphics, Title(i), titleFont, new Rectangle(left, y, Width - left, (int)(25 * scale)), Color.FromArgb(11, 21, 43), Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.NoPrefix);
            Forms.TextRenderer.DrawText(e.Graphics, Status(i), detailFont, new Rectangle(left, y + (int)(26 * scale), Width - left, (int)(22 * scale)), Color.FromArgb(108, 134, 171), Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.NoPrefix);
        }
    }
}
