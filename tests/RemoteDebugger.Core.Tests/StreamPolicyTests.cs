using RemoteDebugger.Core;
using Xunit;

public sealed class StreamPolicyTests
{
    [Theory]
    [InlineData(0, 1)] [InlineData(1, 1)] [InlineData(3, 3)] [InlineData(5, 5)] [InlineData(15, 5)] [InlineData(30, 5)]
    public void StreamNeverExceedsUserRequestedCap(int requested, int expected) => Assert.Equal(expected, StreamPolicy.ClampFps(requested));

    [Fact]
    public void H264FallsBackOnlyAfterThreeSlowFrames()
    {
        Assert.False(StreamPolicy.ShouldFallbackToJpeg(StreamPolicy.H264SlowFrameCount - 1));
        Assert.True(StreamPolicy.ShouldFallbackToJpeg(StreamPolicy.H264SlowFrameCount));
        Assert.True(StreamPolicy.IsH264EncodeSlow(StreamPolicy.H264EncodeBudgetMs(5) + 1, 5));
        Assert.False(StreamPolicy.IsH264EncodeSlow(StreamPolicy.H264EncodeBudgetMs(5), 5));
    }
}
