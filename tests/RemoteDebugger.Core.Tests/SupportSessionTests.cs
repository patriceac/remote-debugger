using RemoteDebugger.Core;
using Xunit;

public sealed class SupportSessionTests
{
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero) + TimeSpan.FromTicks(ticks);
        public void Advance(TimeSpan duration) => ticks += duration.Ticks;
    }
    [Fact] public void UnpairedLaunchHasNoDisconnectDeadline()
    {
        var clock = new Clock(); var session = new SupportSession(clock); clock.Advance(TimeSpan.FromHours(3));
        Assert.False(session.Snapshot.Connected); Assert.Null(session.Snapshot.DisconnectDeadlineUtc); Assert.False(session.ShouldExit);
    }
    [Fact] public void MissingHeartbeatsExitAfterTenMinuteGrace()
    {
        var clock = new Clock(); var session = new SupportSession(clock); session.Pair(true);
        clock.Advance(SupportSession.HeartbeatTimeout); Assert.False(session.Snapshot.Connected); Assert.False(session.ShouldExit);
        clock.Advance(SupportSession.DisconnectGrace - TimeSpan.FromMilliseconds(1)); Assert.False(session.ShouldExit);
        clock.Advance(TimeSpan.FromMilliseconds(1)); Assert.True(session.ShouldExit);
    }
    [Fact] public void ReconnectionCancelsDeadline()
    {
        var clock = new Clock(); var session = new SupportSession(clock); session.Pair(true); session.Disconnect();
        clock.Advance(TimeSpan.FromMinutes(9)); session.Observe(); Assert.True(session.Snapshot.Connected); Assert.Null(session.Snapshot.DisconnectDeadlineUtc);
    }
    [Fact] public void ExpiredSessionCannotBeRevived()
    {
        var clock = new Clock(); var session = new SupportSession(clock); session.Pair(true); session.Disconnect();
        clock.Advance(TimeSpan.FromMinutes(10)); session.Observe(); Assert.True(session.ShouldExit); Assert.False(session.Snapshot.Connected);
    }
    [Fact] public void HealthyTrayHeartbeatKeepsLogicalSessionAlive()
    {
        var clock = new Clock(); var session = new SupportSession(clock); session.Pair(true);
        for (int i = 0; i < 800; i++) { clock.Advance(SupportSession.HeartbeatInterval); session.Observe(); }
        Assert.True(session.Snapshot.Connected); Assert.False(session.ShouldExit);
    }
    [Fact] public void MismatchedPairingIsSynchronizingUntilMatched()
    {
        var session = new SupportSession(); session.Pair(false); Assert.Equal("synchronizing", session.Snapshot.State);
        session.SetBinaryMatched(true); Assert.Equal("connected", session.Snapshot.State); session.End(); Assert.True(session.ShouldExit);
    }
}
