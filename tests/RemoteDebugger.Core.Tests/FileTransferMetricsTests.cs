using RemoteDebugger.Core;
using Xunit;

public sealed class FileTransferMetricsTests
{
    [Theory]
    [InlineData(250, 1000, 250, 1, 25, 250, 3)]
    [InlineData(750, 1000, 250, 2, 75, 125, 2)]
    [InlineData(1000, 1000, 1000, 4, 100, 250, 0)]
    [InlineData(0, 0, 0, 1, 100, 0, 0)]
    public void RemainingTimeUsesOnlyBytesSentInThisAttempt(long transferred, long total, long sent, double seconds, int percent, double rate, int remaining)
    {
        var metrics = FileTransferMetrics.Calculate(transferred, total, sent, TimeSpan.FromSeconds(seconds));
        Assert.Equal(percent, metrics.Percent);
        Assert.Equal(rate, metrics.BytesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(remaining), metrics.Remaining);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(50, .1)]
    public void EtaIsUnknownUntilTransferSpeedCanBeMeasured(long sent, double seconds) =>
        Assert.Null(FileTransferMetrics.Calculate(500, 1000, sent, TimeSpan.FromSeconds(seconds)).Remaining);
}
