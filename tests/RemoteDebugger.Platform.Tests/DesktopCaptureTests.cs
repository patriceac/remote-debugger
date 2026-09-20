using System.Drawing;
using System.Drawing.Imaging;
using RemoteDebugger;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class DesktopCaptureTests
{
    [Fact]
    public void BinaryJpegPreservesBytesWithoutCreatingBase64()
    {
        using var bitmap = new Bitmap(16, 16);
        bitmap.SetPixel(2, 2, Color.CornflowerBlue);
        using var capture = new CapturedDesktop(bitmap, DateTimeOffset.UtcNow,
            new RemoteDebugger.Core.DesktopGeometry(0, 0, 16, 16, "layout"), "fingerprint", 0, OwnsBitmap: false);
        var legacy = DesktopCapture.EncodeJpeg(capture, 1920, 65, 0);
        var binary = DesktopCapture.EncodeJpeg(capture, 1920, 65, 0, includeBase64: false);
        Assert.Empty(binary.Frame.Data);
        Assert.Equal(Convert.FromBase64String(legacy.Frame.Data), binary.Bytes);
    }

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
