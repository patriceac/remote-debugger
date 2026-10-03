using RemoteDebugger.Core;
using Xunit;

namespace RemoteDebugger.Core.Tests;

public sealed class AutoHideToolbarTests
{
    [Fact]
    public void ToolbarSlidesBetweenHiddenAndShownOverTwoHundredMilliseconds()
    {
        var bar = new AutoHideToolbar();
        bar.Reveal(0);
        Assert.Equal(0, bar.VisibleFraction(0));
        Assert.InRange(bar.VisibleFraction(50), 0.01, 0.99);
        Assert.Equal(1, bar.VisibleFraction(200));
        bar.Update(1000, atTopEdge: false, overToolbar: false);
        Assert.Equal(1, bar.VisibleFraction(1000));
        Assert.InRange(bar.VisibleFraction(1050), 0.01, 0.99);
        Assert.Equal(0, bar.VisibleFraction(1200));
    }

    [Fact]
    public void ReturningToTheTopEdgeReversesTheSlideWithoutJumping()
    {
        var bar = new AutoHideToolbar();
        bar.Reveal(0);
        _ = bar.VisibleFraction(0);
        _ = bar.VisibleFraction(200);
        bar.Update(1000, atTopEdge: false, overToolbar: false);
        _ = bar.VisibleFraction(1000);
        double partial = bar.VisibleFraction(1050);
        bar.Update(1050, atTopEdge: true, overToolbar: false);
        Assert.Equal(partial, bar.VisibleFraction(1050));
        Assert.True(bar.VisibleFraction(1100) > partial);
        Assert.Equal(1, bar.VisibleFraction(1250));
    }

    [Fact]
    public void EnteringFullScreenGivesOneSecondToFindTheControls()
    {
        var bar = new AutoHideToolbar();
        bar.Reveal(100);
        Assert.True(bar.Update(1099, atTopEdge: false, overToolbar: false));
        Assert.False(bar.Update(1100, atTopEdge: false, overToolbar: false));
    }

    [Fact]
    public void OnlyTheTopEdgeRevealsHiddenControlsAndHoveringKeepsThemOpen()
    {
        var bar = new AutoHideToolbar();
        Assert.False(bar.Update(100, atTopEdge: false, overToolbar: true));
        Assert.True(bar.Update(200, atTopEdge: true, overToolbar: false));
        Assert.True(bar.Update(1100, atTopEdge: false, overToolbar: true));
        Assert.True(bar.Update(2099, atTopEdge: false, overToolbar: false));
        Assert.False(bar.Update(2100, atTopEdge: false, overToolbar: false));
    }
}
