using RemoteDebugger;
using System.Drawing;
using System.Windows.Forms;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class ControlRegionsTests
{
    [Fact]
    public void RoundedRegionIsReusedUntilControlSizeChanges()
    {
        using var panel = new Panel { Size = new Size(176, 30) };
        Size appliedSize = Size.Empty;

        Assert.True(ControlRegions.ApplyRounded(panel, ref appliedSize, 16));
        var first = panel.Region;
        Assert.NotNull(first);
        Assert.False(ControlRegions.ApplyRounded(panel, ref appliedSize, 16));
        Assert.Same(first, panel.Region);

        panel.Size = new Size(180, 30);
        Assert.True(ControlRegions.ApplyRounded(panel, ref appliedSize, 16));
        Assert.NotSame(first, panel.Region);
    }
}
