using System.Drawing.Drawing2D;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

internal sealed class ResourceMiniCharts : Forms.Control
{
    private const int HistoryLength = 36;
    private readonly List<double?[]> history = [];
    private bool stale = true;
    private static readonly Color[] Colors = [Color.FromArgb(0, 161, 174), Color.FromArgb(112, 54, 255), Color.FromArgb(218, 132, 0), Color.FromArgb(24, 145, 255)];
    public ResourceMiniCharts() { DoubleBuffered = true; Font = new Font("Segoe UI", 8.5F); Size = new Size(296, 52); Margin = new Forms.Padding(12, 0, 12, 0); AccessibleRole = Forms.AccessibleRole.Chart; }
    public void Add(double? cpu, double? memory, double? disk, double? gpu)
    {
        history.Add([cpu, memory, disk, gpu]); if (history.Count > HistoryLength) history.RemoveAt(0);
        stale = false;
        AccessibleName = string.Join(", ", new[] { "CPU", "RAM", UiText.DiskActivity, "GPU" }.Select((name, i) => name + " " + Format(history[^1][i])));
        Invalidate();
    }
    public void Reset() { history.Clear(); stale = false; SetStale(); }
    public void SetStale() { if (stale) return; stale = true; AccessibleName = UiText.ResourcesUnavailable; Invalidate(); }
    private static string Format(double? value) => value is { } v && double.IsFinite(v) ? $"{v:0}%" : "—";
    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96f;
        bool compact = ClientSize.Height < 50 * scale || ClientSize.Width < 400 * scale;
        float column = ClientSize.Width / 4f;
        int labelHeight = Forms.TextRenderer.MeasureText("CPU", Font, Size.Empty, Forms.TextFormatFlags.NoPadding).Height;
        float bottom = ClientSize.Height - 2 * scale;
        float top = compact ? labelHeight + 4 * scale : 27 * scale;
        float graphHeight = Math.Max(2, bottom - top);
        string[] names = ["CPU", "RAM", UiText.DiskActivity, "GPU"];
        using var valueFont = new Font(Font.FontFamily, compact ? Font.Size : 13, FontStyle.Bold);
        using var baseline = new Pen(Color.FromArgb(225, 230, 235));
        using var divider = new Pen(Color.FromArgb(222, 228, 235));

        for (int i = 0; i < 4; i++)
        {
            if (!compact) e.Graphics.DrawLine(divider, i * column, scale, i * column, ClientSize.Height);
            if (!compact && i == 3) e.Graphics.DrawLine(divider, ClientSize.Width - 1, scale, ClientSize.Width - 1, ClientSize.Height);
            float inset = compact ? 3 * scale : Math.Min(24 * scale, column * .15f);
            float left = i * column + inset, right = (i + 1) * column - inset;
            float width = Math.Max(1, right - left);
            string value = stale || history.Count == 0 ? "—" : Format(history[^1][i]);
            var labelColor = stale ? SystemColors.GrayText : Color.FromArgb(91, 105, 121);
            var valueColor = stale ? SystemColors.GrayText : ForeColor;
            var textFlags = Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.NoPrefix | Forms.TextFormatFlags.SingleLine | Forms.TextFormatFlags.EndEllipsis;
            if (compact)
                Forms.TextRenderer.DrawText(e.Graphics, names[i] + " " + value, Font,
                    Rectangle.Round(new RectangleF(left, 0, width, labelHeight)), valueColor, textFlags);
            else
            {
                Forms.TextRenderer.DrawText(e.Graphics, names[i], Font,
                    Rectangle.Round(new RectangleF(left, 0, width * .65f, 22 * scale)), labelColor, textFlags | Forms.TextFormatFlags.VerticalCenter);
                Forms.TextRenderer.DrawText(e.Graphics, value, valueFont,
                    Rectangle.Round(new RectangleF(left + width * .65f, -scale, width * .35f, 22 * scale)), valueColor,
                    textFlags | Forms.TextFormatFlags.Right | Forms.TextFormatFlags.VerticalCenter);
            }

            e.Graphics.DrawLine(baseline, left, bottom, right, bottom);
            var points = new List<PointF>(history.Count);
            for (int n = 0; n < history.Count; n++)
            {
                if (history[n][i] is not { } sample || !double.IsFinite(sample)) continue;
                float x = history.Count == 1 ? right : left + n * width / (history.Count - 1);
                points.Add(new PointF(x, bottom - (float)Math.Clamp(sample, 0, 100) * graphHeight / 100));
            }
            if (points.Count == 0) continue;
            if (points.Count > 1)
            {
                using var area = new GraphicsPath();
                area.AddLines(points.ToArray());
                area.AddLine(points[^1].X, bottom, points[0].X, bottom);
                area.CloseFigure();
                using var fill = new SolidBrush(Color.FromArgb(compact ? 18 : 25, stale ? Color.LightGray : Colors[i]));
                e.Graphics.FillPath(fill, area);
                using var line = new Pen(stale ? Color.LightGray : Colors[i], (compact ? 1.2f : 1.3f) * scale) { LineJoin = LineJoin.Round };
                e.Graphics.DrawLines(line, points.ToArray());
            }
            if (!stale && history[^1][i] is { } latest && double.IsFinite(latest))
            {
                using var dot = new SolidBrush(Colors[i]);
                PointF point = points[^1];
                float radius = (compact ? 1.5f : 2.7f) * scale;
                e.Graphics.FillEllipse(dot, point.X - radius, point.Y - radius, radius * 2, radius * 2);
            }
        }
    }
}
