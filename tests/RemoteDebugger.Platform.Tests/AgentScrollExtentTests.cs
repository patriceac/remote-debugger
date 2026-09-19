using System.Drawing;
using RemoteDebugger;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class AgentScrollExtentTests
{
    [Theory]
    [InlineData(0, 600, 0)]
    [InlineData(720, 600, 0)]
    [InlineData(720, 720, 0)]
    [InlineData(720, 721, 721)]
    public void ScrollExtentIsRequestedOnlyAfterContentExceedsViewport(int viewportHeight, int contentHeight, int expectedHeight)
    {
        Assert.Equal(new Size(0, expectedHeight), MainForm.CalculateAgentScrollExtent(viewportHeight, contentHeight));
    }

    [Fact]
    public void ScrollExtentRemainsStableAcrossOnePixelLayoutRounding()
    {
        Size visible = MainForm.CalculateAgentScrollExtent(720, 721);

        Assert.Equal(visible, MainForm.CalculateAgentScrollExtent(720, 720, visible.Height));
        Assert.Equal(Size.Empty, MainForm.CalculateAgentScrollExtent(740, 720, visible.Height));
    }
}
