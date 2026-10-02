using System.Drawing;
using System.Windows.Forms;
using RemoteDebugger;
using RemoteDebugger.Core;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class RemoteScreenViewTests
{
    [Theory]
    [InlineData("Patrice Dupont", "patri", "Patrice")]
    [InlineData("  Élodie-Marie  Martin ", "elo", "Élodie-Marie")]
    [InlineData(null, "support", "support")]
    [InlineData(" \t ", "support", "support")]
    public void PeerNameUsesFirstNameOrLoginWhenWindowsHasNoDisplayName(string? fullName, string login, string expected)
        => Assert.Equal(expected, CursorUserName.FirstName(fullName, login));

    [Fact]
    public void PeerLabelStaysVisibleWhileStationaryAndClicksAnimateOnce()
    {
        var cursor = new SharedCursor();
        var state = new CursorPosition(120, 80, "Remote PC", true, 1, 0);
        cursor.Update(state, 100);
        cursor.Update(state, 2000);
        Assert.True(cursor.LabelVisible(2100));
        Assert.Equal(1, cursor.LabelOpacity(2100));
        Assert.True(cursor.Visible(2100));
        cursor.Update(state with { Activity = 2, Click = 1 }, 2200);
        Assert.True(cursor.LabelVisible(2200));
        Assert.Equal(0, cursor.Pulse(2200));
        cursor.Update(state with { Activity = 2, Click = 1 }, 2400);
        Assert.Equal(1, cursor.Pulse(2650));
        Assert.Equal((120, 80), (cursor.Position!.X, cursor.Position.Y));
        cursor.Update(state with { Activity = 2, Click = 1 }, 10_000);
        Assert.True(cursor.LabelVisible(10_000));
        Assert.Equal(1, cursor.LabelOpacity(10_000));
        Assert.False(cursor.Visible(13_000));
        Assert.False(cursor.LabelVisible(13_000));
        Assert.Equal(0, cursor.LabelOpacity(13_000));
        cursor.Update(state with { Visible = false }, 14_000);
        Assert.False(cursor.LabelVisible(14_000));
        cursor.Update(null, 15_000);
        Assert.False(cursor.Visible(15_000));
        Assert.False(cursor.LabelVisible(15_000));
        cursor.Update(state with { Name = "" }, 16_000);
        Assert.True(cursor.Visible(16_000));
        Assert.False(cursor.LabelVisible(16_000));
    }

    [Theory]
    [InlineData(-1920, 0, -960, 540, 400, 300)]
    [InlineData(0, 0, 960, 540, 400, 300)]
    [InlineData(0, -1080, 0, -1080, 0, 75)]
    public void PeerCursorMapsThroughLetterboxingAndNegativeMonitorOrigins(int x, int y, int px, int py, float expectedX, float expectedY)
    {
        var geometry = new DesktopGeometry(x, y, 1920, 1080, "layout");
        Assert.Equal(new PointF(expectedX, expectedY), RemoteScreenView.MapCursor(geometry, new(800, 600), new(px, py)));
        Assert.Null(RemoteScreenView.MapCursor(geometry, new(800, 600), new(x - 1, y)));
    }

    [Fact]
    public void StationaryMouseDoesNotReassertItsPositionButRealMovementStillForwards()
    {
        using var view = new RemoteScreenView();
        var first = new Point(120, 80);
        var next = new Point(160, 90);

        Assert.True(view.ShouldForwardMouseMove(first, 0));
        Assert.False(view.ShouldForwardMouseMove(first, 34));
        Assert.False(view.ShouldForwardMouseMove(first, 5_000));
        Assert.True(view.ShouldForwardMouseMove(next, 5_034));
        Assert.True(view.ShouldForwardMouseMove(first, 5_067));
        Assert.False(view.ShouldForwardMouseMove(first, 5_100));
        view.ResetMouseMove();
        Assert.True(view.ShouldForwardMouseMove(first, 5_134));
    }

    [Fact]
    public void ThrottledMovementCanStillForwardAfterTheInterval()
    {
        using var view = new RemoteScreenView();

        Assert.True(view.ShouldForwardMouseMove(new(120, 80), 0));
        Assert.False(view.ShouldForwardMouseMove(new(160, 90), 10));
        Assert.True(view.ShouldForwardMouseMove(new(160, 90), 33));
    }

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
