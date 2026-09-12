using RemoteDebugger.Core;
using Xunit;

public sealed class SessionExitCountdownTests
{
    private sealed class Clock : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }

    [Fact]
    public void OnlyAnEndedSessionStartsTheTenMinuteExitTimer()
    {
        var clock = new Clock(); var timer = new SessionExitCountdown(clock);
        clock.Ticks += TimeSpan.FromHours(1).Ticks;
        Assert.False(timer.Active); Assert.False(timer.Expired);
        timer.Start(); Assert.Equal(TimeSpan.FromMinutes(10), timer.Remaining);
        clock.Ticks += TimeSpan.FromMinutes(9).Ticks;
        timer.Start(); // Repeated termination notifications must not extend it.
        Assert.Equal(TimeSpan.FromMinutes(1), timer.Remaining); Assert.False(timer.Expired);
        clock.Ticks += TimeSpan.FromMinutes(1).Ticks;
        Assert.True(timer.Expired); Assert.Equal(TimeSpan.Zero, timer.Remaining);
    }

    [Fact]
    public void ANewSessionCancelsThePendingExitAndLaterEndsGetAFreshDeadline()
    {
        var clock = new Clock(); var timer = new SessionExitCountdown(clock);
        timer.Start(); clock.Ticks += TimeSpan.FromMinutes(9).Ticks;
        timer.Cancel(); clock.Ticks += TimeSpan.FromHours(1).Ticks;
        Assert.False(timer.Active); Assert.False(timer.Expired);
        timer.Start(); Assert.Equal(SessionExitCountdown.Delay, timer.Remaining);
    }
}
