using System.Drawing;
using System.Reflection;
using RemoteDebugger;
using Xunit;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Platform.Tests;

public sealed class ViewerCheckBoxTests
{
    [Fact]
    public void RepaintingClearsThePreviousCaptionFromTheBuffer()
    {
        using var checkBox = new ViewerCheckBox { Size = new(300, 28), BackColor = Color.White, Text = "Mouse and keyboard control" };
        using var bitmap = new Bitmap(checkBox.Width, checkBox.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(checkBox.BackColor);
        var paint = typeof(ViewerCheckBox).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var args = new Forms.PaintEventArgs(graphics, checkBox.ClientRectangle);
        paint.Invoke(checkBox, [args]);

        checkBox.Text = "";
        paint.Invoke(checkBox, [args]);

        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 35; x < bitmap.Width; x++)
                Assert.Equal(checkBox.BackColor.ToArgb(), bitmap.GetPixel(x, y).ToArgb());
    }
}
