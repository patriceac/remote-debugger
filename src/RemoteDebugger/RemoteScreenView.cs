using System.Windows.Forms;

namespace RemoteDebugger;

// PictureBox is non-selectable by default. Remote input is sent only while this
// surface owns focus, so it must explicitly opt in to keyboard selection.
internal sealed class RemoteScreenView : PictureBox
{
    private Point? lastMousePosition;
    private long lastMouseMove;

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
