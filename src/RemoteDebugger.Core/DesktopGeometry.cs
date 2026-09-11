namespace RemoteDebugger.Core;

public sealed record DesktopGeometry(int X, int Y, int Width, int Height, string LayoutId)
{
    public (int X, int Y)? MapLetterbox(double viewWidth, double viewHeight, double x, double y)
    {
        if (Width <= 0 || Height <= 0 || viewWidth <= 0 || viewHeight <= 0) return null;
        double scale = Math.Min(viewWidth / Width, viewHeight / Height);
        double mappedX = (x - (viewWidth - Width * scale) / 2) / scale;
        double mappedY = (y - (viewHeight - Height * scale) / 2) / scale;
        if (mappedX < 0 || mappedY < 0 || mappedX >= Width || mappedY >= Height) return null;
        return (X + (int)mappedX, Y + (int)mappedY);
    }
}
