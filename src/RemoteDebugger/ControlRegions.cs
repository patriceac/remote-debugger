using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

internal static class ControlRegions
{
    public static bool ApplyRounded(Forms.Control control, ref Size appliedSize, int radius)
    {
        Size size = control.ClientSize;
        if (size.Width <= 0 || size.Height <= 0 || (size == appliedSize && control.Region != null)) return false;

        var next = Rounded(size, radius);
        var previous = control.Region;
        control.Region = next;
        previous?.Dispose();
        appliedSize = size;
        return true;
    }

    private static Region Rounded(Size size, int radius)
    {
        using var path = new GraphicsPath();
        path.AddArc(0, 0, radius * 2, radius * 2, 180, 90);
        path.AddArc(size.Width - radius * 2, 0, radius * 2, radius * 2, 270, 90);
        path.AddArc(size.Width - radius * 2, size.Height - radius * 2, radius * 2, radius * 2, 0, 90);
        path.AddArc(0, size.Height - radius * 2, radius * 2, radius * 2, 90, 90);
        path.CloseFigure();
        return new Region(path);
    }
}
