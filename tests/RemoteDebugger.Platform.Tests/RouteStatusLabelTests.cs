using System.Drawing;
using System.Reflection;
using RemoteDebugger;
using Xunit;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Platform.Tests;

public sealed class RouteStatusLabelTests
{
    [Theory]
    [InlineData("Direct LAN", "", 34, 197, 94)]
    [InlineData("Direct WAN", "", 34, 197, 94)]
    [InlineData("Relay", "", 245, 158, 11)]
    [InlineData("Relay", "PC · Yolande · ", 245, 158, 11)]
    public void RouteHasARoundDiscBeforeItsText(string route, string prefix, int red, int green, int blue)
    {
        using var label = StatusLabel(prefix + route + " · H.264 · 2.8 fps · 0.7 Mbit/s · capture 17 ms");
        label.RouteTextOffset = prefix.Length;
        using var bitmap = Render(label);
        var color = Color.FromArgb(red, green, blue);
        var disc = ColoredBounds(bitmap, color);
        Assert.False(disc.IsEmpty);
        Assert.Equal(disc.Width, disc.Height);
        Assert.NotEqual(color.ToArgb(), bitmap.GetPixel(disc.Left, disc.Top).ToArgb());
        using var graphics = Graphics.FromImage(bitmap);
        int prefixWidth = prefix.Length == 0 ? 0 : Forms.TextRenderer.MeasureText(graphics, prefix, label.Font, Size.Empty,
            Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.NoPrefix | Forms.TextFormatFlags.SingleLine).Width;
        Assert.InRange(disc.Left, prefixWidth, prefixWidth + 2);
        Assert.Equal(label.Text, label.AccessibilityObject.Name);
        if (Environment.GetEnvironmentVariable("REMOTE_DEBUGGER_ROUTE_PREVIEW") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            bitmap.Save(Path.Combine(folder, route.Replace(' ', '-') + (prefix.Length > 0 ? "-fullscreen" : "") + ".png"));
        }
    }

    [Fact]
    public void PausingClearsTheRouteDisc()
    {
        using var label = StatusLabel("Relay · H.264 · 2.8 fps");
        using var bitmap = Render(label);
        Assert.False(ColoredBounds(bitmap, Color.FromArgb(245, 158, 11)).IsEmpty);
        label.Text = "Viewing paused";
        Paint(label, bitmap);
        Assert.True(ColoredBounds(bitmap, Color.FromArgb(245, 158, 11)).IsEmpty);
    }

    private static RouteStatusLabel StatusLabel(string text) => new()
    {
        Size = new(600, 57), BackColor = Color.FromArgb(34, 34, 34), ForeColor = Color.FromArgb(191, 195, 199),
        Font = new Font("Segoe UI", 9.5F), TextAlign = ContentAlignment.MiddleLeft, Text = text
    };
    private static Bitmap Render(RouteStatusLabel label)
    {
        var bitmap = new Bitmap(label.Width, label.Height);
        Paint(label, bitmap);
        return bitmap;
    }
    private static void Paint(RouteStatusLabel label, Bitmap bitmap)
    {
        using var graphics = Graphics.FromImage(bitmap);
        using var args = new Forms.PaintEventArgs(graphics, label.ClientRectangle);
        typeof(RouteStatusLabel).GetMethod("OnPaintBackground", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(label, [args]);
        typeof(RouteStatusLabel).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(label, [args]);
    }
    private static Rectangle ColoredBounds(Bitmap bitmap, Color color)
    {
        int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).ToArgb() == color.ToArgb())
                {
                    left = Math.Min(left, x); top = Math.Min(top, y);
                    right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                }
        return right < left ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }
}
