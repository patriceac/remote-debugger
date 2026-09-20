using System.Drawing;
using RemoteDebugger;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class RememberedListViewTests
{
    [Fact]
    public void LogicalRowHeightAppliesDpiScaledListSpacing()
    {
        using var list = new RememberedListView { LogicalRowHeight = 44 };

        int expected = (int)Math.Round(44 * list.DeviceDpi / 96d);
        Assert.Equal(new Size(1, expected), list.SmallImageList!.ImageSize);
    }

    [Fact]
    public void LogicalRowHeightRejectsNegativeValues()
    {
        using var list = new RememberedListView();
        Assert.Throws<ArgumentOutOfRangeException>(() => list.LogicalRowHeight = -1);
    }
}
