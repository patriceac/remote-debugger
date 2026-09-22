using System.Drawing;
using System.Windows.Forms;
using RemoteDebugger;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class RemoteScreenViewTests
{
    [Theory]
    [InlineData(240, 120, 0, 60)]
    [InlineData(120, 240, 60, 0)]
    [InlineData(240, 240, 0, 0)]
    public void StripesMarkOnlyUnusedMargins(int imageWidth, int imageHeight, int left, int top)
    {
        using var frame = new Bitmap(imageWidth, imageHeight);
        using (var graphics = Graphics.FromImage(frame)) graphics.Clear(Color.CornflowerBlue);
        using var view = new RemoteScreenView { Size = new(240, 240), BackColor = Color.LightGray, SizeMode = PictureBoxSizeMode.Zoom, Image = frame };
        using var rendered = new Bitmap(240, 240);
        view.DrawToBitmap(rendered, view.ClientRectangle);
        var imageBounds = new Rectangle(left, top, imageWidth, imageHeight);
        var margins = new HashSet<int>();
        var imageColors = new HashSet<int>();
        for (int y = 0; y < rendered.Height; y++)
            for (int x = 0; x < rendered.Width; x++)
                (imageBounds.Contains(x, y) ? imageColors : margins).Add(rendered.GetPixel(x, y).ToArgb());

        Assert.Equal(Color.CornflowerBlue.ToArgb(), Assert.Single(imageColors));
        if (left != 0 || top != 0)
        {
            Assert.Contains(Color.LightGray.ToArgb(), margins);
            Assert.Contains(margins, color => Color.FromArgb(color).R < Color.LightGray.R);
        }
        else Assert.Empty(margins);
    }
}
