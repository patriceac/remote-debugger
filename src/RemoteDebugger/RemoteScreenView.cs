using System.Windows.Forms;
using RemoteDebugger.Core;

namespace RemoteDebugger;

// PictureBox is non-selectable by default. Remote input is sent only while this
// surface owns focus, so it must explicitly opt in to keyboard selection.
internal sealed class RemoteScreenView : PictureBox
{
    private Point? lastMousePosition;
    private long lastMouseMove;
    private readonly SharedCursor peerCursor = new();
    private DesktopGeometry? cursorGeometry;

    internal void UpdateCursor(CursorPosition? cursor, DesktopGeometry? geometry)
    {
        cursorGeometry = geometry;
        peerCursor.Update(cursor, Environment.TickCount64); Invalidate();
    }

    internal static PointF? MapCursor(DesktopGeometry geometry, Size size, Point point)
    {
        if (geometry.Width <= 0 || geometry.Height <= 0 || size.Width <= 0 || size.Height <= 0 ||
            !new Rectangle(geometry.X, geometry.Y, geometry.Width, geometry.Height).Contains(point)) return null;
        float scale = Math.Min((float)size.Width / geometry.Width, (float)size.Height / geometry.Height);
        return new PointF((size.Width - geometry.Width * scale) / 2 + (point.X - geometry.X) * scale,
            (size.Height - geometry.Height * scale) / 2 + (point.Y - geometry.Y) * scale);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Image == null || cursorGeometry is not { } geometry || peerCursor.Position is not { } cursor ||
            MapCursor(geometry, ClientSize, new(cursor.X, cursor.Y)) is not { } tip) return;
        float scale = Math.Min((float)Width / geometry.Width, (float)Height / geometry.Height);
        var clip = Rectangle.Round(new RectangleF((Width - geometry.Width * scale) / 2, (Height - geometry.Height * scale) / 2, geometry.Width * scale, geometry.Height * scale));
        peerCursor.Paint(e.Graphics, tip, DeviceDpi / 96f, clip, Environment.TickCount64);
    }

    public RemoteScreenView()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
    }

    protected override bool IsInputKey(Keys keyData) => true;

    internal void ResetMouseMove() => lastMousePosition = null;

    internal bool ShouldForwardMouseMove(Point position, long now)
    {
        // Windows can repeat MouseMove during frame/layout updates while the mouse is still.
        if (lastMousePosition == position || lastMousePosition != null && now - lastMouseMove < 33) return false;
        lastMousePosition = position;
        lastMouseMove = now;
        return true;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        if (Image == null) return;
        float scale = DeviceDpi / 96f;
        using var pen = new Pen(Color.FromArgb(40, Color.Black), scale);
        int spacing = Math.Max(1, (int)Math.Round(28 * scale));
        // The opaque screen image paints over this, leaving stripes only in its margins.
        for (int x = -Height; x < Width; x += spacing)
            e.Graphics.DrawLine(pen, x, Height, x + Height, 0);
    }
}
