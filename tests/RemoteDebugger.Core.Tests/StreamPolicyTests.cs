using RemoteDebugger.Core;
using Xunit;

public sealed class StreamPolicyTests
{
    [Theory]
    [InlineData(0, 1)] [InlineData(1, 1)] [InlineData(3, 3)] [InlineData(5, 5)] [InlineData(15, 15)] [InlineData(30, 30)] [InlineData(60, 30)]
    public void StreamNeverExceedsUserRequestedCap(int requested, int expected) => Assert.Equal(expected, StreamPolicy.ClampFps(requested));

    [Fact]
    public void H264FallsBackOnlyAfterThreeSlowFrames()
    {
        Assert.False(StreamPolicy.ShouldFallbackToJpeg(StreamPolicy.H264SlowFrameCount - 1));
        Assert.True(StreamPolicy.ShouldFallbackToJpeg(StreamPolicy.H264SlowFrameCount));
        Assert.True(StreamPolicy.IsH264EncodeSlow(StreamPolicy.H264EncodeBudgetMs(5) + 1, 5));
        Assert.False(StreamPolicy.IsH264EncodeSlow(StreamPolicy.H264EncodeBudgetMs(5), 5));
    }

    [Fact]
    public void SlowWorkReducesCadenceAndFastWorkRecoversWithoutExceedingTheCap()
    {
        var pacing = new AdaptiveFrameRate(30);
        pacing.Observe(TimeSpan.FromMilliseconds(100));
        Assert.InRange(pacing.FramesPerSecond, 1, 10);
        for (int i = 0; i < 900; i++) pacing.Observe(TimeSpan.FromMilliseconds(10));
        Assert.Equal(30, pacing.FramesPerSecond);
        Assert.True(pacing.DelayAfter(TimeSpan.FromMilliseconds(10)) > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, pacing.DelayAfter(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void RelayEconomyRemainsBoundedEvenWhenTheMachineCouldRenderFaster()
    {
        var settings = StreamPolicy.ViewingSettings(true);
        var pacing = new AdaptiveFrameRate(settings.Fps);
        for (int i = 0; i < 100; i++) pacing.Observe(TimeSpan.FromMilliseconds(1));
        Assert.Equal(3, pacing.FramesPerSecond);
        Assert.Equal(1280, settings.MaxWidth);
    }
}
