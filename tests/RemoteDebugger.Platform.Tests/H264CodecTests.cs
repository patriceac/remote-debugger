using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using RemoteDebugger;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class H264CodecTests
{
    [Fact]
    public void BundledX264CanEncodeAndDecodeAFrameWhenRuntimeIsPresent()
    {
        Assert.True(FfmpegRuntime.IsAvailable(), "The bundled H.264 runtime must be present for this test.");
        using var bitmap = new Bitmap(320, 240, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.Clear(Color.DarkSlateBlue);
            graphics.FillRectangle(Brushes.Gold, 20, 30, 120, 80);
        }
        using var encoder = new H264Encoder(320, 240, 320, 240, 5, 65);
        var encoded = encoder.Encode(bitmap, 0);
        Assert.NotEmpty(encoded.Data);
        using var decoder = new H264Decoder();
        using var decoded = decoder.Decode(encoded.Data);
        Assert.NotNull(decoded);
        Assert.Equal(320, decoded!.Width);
        Assert.Equal(240, decoded.Height);
    }
}
