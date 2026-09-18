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
}
