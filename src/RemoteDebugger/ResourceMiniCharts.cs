using System.Drawing.Drawing2D;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

internal sealed class ResourceMiniCharts : Forms.Control
{
    private readonly List<double?[]> history = [];
    private bool stale = true;
    private static readonly Color[] Colors = [Color.FromArgb(8, 127, 131), Color.FromArgb(105, 93, 175), Color.FromArgb(174, 116, 33), Color.FromArgb(55, 119, 176)];
    public ResourceMiniCharts() { DoubleBuffered = true; Font = new Font("Segoe UI", 8.5F); Size = new Size(296, 52); Margin = new Forms.Padding(12, 0, 12, 0); AccessibleRole = Forms.AccessibleRole.Chart; }
    public void Add(double? cpu, double? memory, double? disk, double? gpu)
    {
        history.Add([cpu, memory, disk, gpu]); if (history.Count > 36) history.RemoveAt(0);
        stale = false;
        AccessibleName = string.Join(", ", new[] { "CPU", "RAM", UiText.DiskActivity, "GPU" }.Select((name, i) => name + " " + Format(history[^1][i])));
        Invalidate();
    }
    public void Reset() { history.Clear(); stale = false; SetStale(); }
    public void SetStale() { if (stale) return; stale = true; AccessibleName = UiText.ResourcesUnavailable; Invalidate(); }
    private static string Format(double? value) => value is { } v && double.IsFinite(v) ? $"{v:0}%" : "—";
    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float column = ClientSize.Width / 4f;
        string[] names = ["CPU", "RAM", UiText.DiskActivity, "GPU"];
        for (int i = 0; i < 4; i++)
        {
            int left = (int)(i * column), width = Math.Max(1, (int)column - 8);
            Forms.TextRenderer.DrawText(e.Graphics, names[i] + " " + (stale || history.Count == 0 ? "—" : Format(history[^1][i])), Font,
                new Rectangle(left, 0, width, 18), stale ? SystemColors.GrayText : ForeColor, Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.EndEllipsis);
            using var line = new Pen(stale ? Color.LightGray : Colors[i], 1.5f);
            using var baseline = new Pen(Color.FromArgb(225, 230, 232));
            int bottom = ClientSize.Height - 3, graphHeight = Math.Max(2, ClientSize.Height - 22);
            e.Graphics.DrawLine(baseline, left, bottom, left + width, bottom);
            PointF? previous = null;
            for (int n = 0; n < history.Count; n++)
            {
                if (history[n][i] is not { } value || !double.IsFinite(value)) { previous = null; continue; }
                var point = new PointF(left + (n + 36 - history.Count) * width / 35f, bottom - (float)Math.Clamp(value, 0, 100) * graphHeight / 100);
                if (previous is { } start) e.Graphics.DrawLine(line, start, point);
                previous = point;
            }
        }
    }
}
