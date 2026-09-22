using System.Text.Json;
using RemoteDebugger.Core;
using Xunit;

public sealed class UpdateProgressTrackerTests
{
    private sealed class Clock : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
        public void Advance(double seconds) => Ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }

    [Theory]
    [InlineData("hashing")]
    [InlineData("checking")]
    [InlineData("preparing")]
    [InlineData("verifying")]
    [InlineData("restarting")]
    [InlineData("finalizing")]
    public void OpaqueStepsNeverClaimMeasuredProgressAndStopGuessingAfterExpectedDuration(string stage)
    {
        var clock = new Clock(); var tracker = new UpdateProgressTracker(new Dictionary<string, double> { [stage] = 20 }, clock);
        tracker.Report(stage, 1000, 1000); // A completed transfer is not completed verification/restart.
        Assert.Equal(new(null, TimeSpan.FromSeconds(20), true), tracker.Snapshot());
        clock.Advance(5); tracker.Report(stage, 1000, 1000);
        var early = tracker.Snapshot();
        Assert.Null(early.Percent);
        Assert.Equal(TimeSpan.FromSeconds(15), early.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(5), early.Elapsed);
        clock.Advance(30);
        var late = tracker.Snapshot();
        Assert.Null(late.Percent);
        Assert.Null(late.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(35), late.Elapsed);
        tracker.Report("complete");
        Assert.Equal(new(100, TimeSpan.Zero, false), tracker.Snapshot());
    }

    [Fact]
    public void TransferEtaExcludesPreparationAndPreviouslyTransferredBytes()
    {
        var clock = new Clock(); var tracker = new UpdateProgressTracker(new Dictionary<string, double>(), clock);
        tracker.Report("preparing"); clock.Advance(120);
        tracker.Report("transferring", 500, 1000);
        Assert.Null(tracker.Snapshot().Remaining);
        clock.Advance(2); tracker.Report("transferring", 750, 1000);
        Assert.Equal(new(75, TimeSpan.FromSeconds(2), false), tracker.Snapshot());
        tracker.Report("verifying", 1000, 1000);
        Assert.Null(tracker.Snapshot().Percent);
        Assert.True(tracker.Snapshot().Estimated);
    }

    [Fact]
    public void OnlySuccessfulStepsTeachTheSamePcsNextUpdateAfterReload()
    {
        var clock = new Clock(); var timings = new Dictionary<string, double>();
        var tracker = new UpdateProgressTracker(timings, clock);
        tracker.Report("preparing"); clock.Advance(8); tracker.Report("transferring");
        tracker.Report("verifying"); clock.Advance(120); tracker.Report("failed");
        Assert.Equal(8, timings["preparing"]);
        Assert.False(timings.ContainsKey("verifying"));
        Assert.Contains("preparing", tracker.CompletedStages);
        Assert.DoesNotContain("verifying", tracker.CompletedStages);
        var reloaded = JsonSerializer.Deserialize<Dictionary<string, double>>(JsonSerializer.Serialize(timings))!;
        var next = new UpdateProgressTracker(reloaded, clock); next.Report("preparing");
        Assert.Equal(TimeSpan.FromSeconds(8), next.Snapshot().Remaining);
        clock.Advance(4); next.Report("transferring");
        Assert.Equal(6, reloaded["preparing"]);
        var otherPc = new UpdateProgressTracker(new Dictionary<string, double>(), clock); otherPc.Report("preparing");
        Assert.Null(otherPc.Snapshot().Remaining);
    }

    [Fact]
    public void InvalidSavedTimingsDoNotFabricateAnEstimate()
    {
        var tracker = new UpdateProgressTracker(new Dictionary<string, double> { ["verifying"] = double.NaN });
        tracker.Report("verifying");
        Assert.True(tracker.Snapshot().Estimated);
        Assert.Null(tracker.Snapshot().Remaining);
        Assert.Null(tracker.Snapshot().Percent);
    }

    [Fact]
    public void StalledTransferWithdrawsEtaAndResumeDoesNotCountOldBytesAsThroughput()
    {
        var clock = new Clock(); var tracker = new UpdateProgressTracker(new Dictionary<string, double>(), clock);
        tracker.Report("transferring", 500, 1000);
        clock.Advance(2); tracker.Report("transferring", 750, 1000);
        Assert.Equal(TimeSpan.FromSeconds(2), tracker.Snapshot().Remaining);
        clock.Advance(4);
        Assert.Equal(75, tracker.Snapshot().Percent);
        Assert.Null(tracker.Snapshot().Remaining);
        tracker.Report("failed"); tracker.Report("transferring", 750, 1000);
        Assert.Null(tracker.Snapshot().Remaining);
        clock.Advance(1); tracker.Report("transferring", 800, 1000);
        Assert.Equal(TimeSpan.FromSeconds(4), tracker.Snapshot().Remaining);
    }
}
