using RemoteDebugger.Core;
using Xunit;

namespace RemoteDebugger.Core.Tests;

public sealed class AutoHideToolbarTests
{
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
