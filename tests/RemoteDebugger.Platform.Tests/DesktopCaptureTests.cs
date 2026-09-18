using System.Drawing;
using System.Drawing.Imaging;
using RemoteDebugger;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class DesktopCaptureTests
{
    [Fact]
    public void FingerprintChangesOnlyWhenCapturedPixelsChange()
    {
        using var first = new Bitmap(2, 2, PixelFormat.Format32bppArgb);
        using var same = new Bitmap(2, 2, PixelFormat.Format32bppArgb);
        using var changed = new Bitmap(2, 2, PixelFormat.Format32bppArgb);
        first.SetPixel(1, 1, Color.CornflowerBlue);
        same.SetPixel(1, 1, Color.CornflowerBlue);
        changed.SetPixel(1, 1, Color.IndianRed);

        var bounds = new Rectangle(10, 20, 2, 2);
        string firstFingerprint = DesktopCapture.Fingerprint(first, bounds, "layout");

        Assert.Equal(firstFingerprint, DesktopCapture.Fingerprint(same, bounds, "layout"));
        Assert.NotEqual(firstFingerprint, DesktopCapture.Fingerprint(changed, bounds, "layout"));
        Assert.NotEqual(firstFingerprint, DesktopCapture.Fingerprint(first, new Rectangle(11, 20, 2, 2), "layout"));
    }
}
