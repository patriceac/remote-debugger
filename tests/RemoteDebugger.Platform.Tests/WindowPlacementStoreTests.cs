using System.Drawing;
using RemoteDebugger;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class WindowPlacementStoreTests
{
    [Fact]
    public void KeepsVisiblePlacementAndMaximizedState()
    {
        var saved = new WindowPlacement(120, 80, 1280, 860, true);

        var restored = WindowPlacementStore.Normalize(saved, [new Rectangle(0, 0, 1920, 1040)]);

        Assert.Equal(saved, restored);
    }

    [Fact]
    public void BringsPartlyOffscreenPlacementBackIntoWorkingArea()
    {
        var restored = WindowPlacementStore.Normalize(
            new WindowPlacement(1700, 900, 1280, 860, false),
            [new Rectangle(0, 0, 1920, 1040)]);

        Assert.Equal(new WindowPlacement(640, 180, 1280, 860, false), restored);
    }

    [Fact]
    public void CentersPlacementWhenSavedDisplayIsGone()
    {
        var restored = WindowPlacementStore.Normalize(
            new WindowPlacement(-1900, 100, 1200, 800, false),
            [new Rectangle(0, 0, 1920, 1040)]);

        Assert.Equal(new WindowPlacement(360, 120, 1200, 800, false), restored);
    }

    [Fact]
    public void FitsOversizedPlacementWithinWorkingArea()
    {
        var restored = WindowPlacementStore.Normalize(
            new WindowPlacement(20, 20, 1500, 900, false),
            [new Rectangle(0, 0, 1000, 700)]);

        Assert.Equal(new WindowPlacement(0, 0, 1000, 700, false), restored);
    }
}
