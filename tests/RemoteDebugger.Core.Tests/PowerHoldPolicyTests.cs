using RemoteDebugger.Core;
using Xunit;

public sealed class PowerHoldPolicyTests
{
    [Fact]
    public void UnpairedAgentMaySleep()
    {
        var session = new SupportSession();

        Assert.False(PowerHoldPolicy.ShouldHoldForAgent(session.Snapshot));
    }

    [Fact]
    public void SynchronizingAgentMaySleep()
    {
        var session = new SupportSession();
        session.Pair(binaryMatched: false);

        Assert.False(PowerHoldPolicy.ShouldHoldForAgent(session.Snapshot));
    }

    [Fact]
    public void SynchronizingAgentStartsHoldingAfterBinaryMatch()
    {
        var session = new SupportSession();
        session.Pair(binaryMatched: false);
        session.SetBinaryMatched(true);

        Assert.True(PowerHoldPolicy.ShouldHoldForAgent(session.Snapshot));
    }

    [Fact]
    public void ConnectedMatchedAgentStaysAwake()
    {
        var session = new SupportSession();
        session.Pair(binaryMatched: true);

        Assert.True(PowerHoldPolicy.ShouldHoldForAgent(session.Snapshot));
    }

    [Fact]
    public void ReconnectingMatchedAgentStaysAwakeDuringGrace()
    {
        var session = new SupportSession();
        session.Pair(binaryMatched: true);
        session.Disconnect();

        Assert.True(PowerHoldPolicy.ShouldHoldForAgent(session.Snapshot));
    }

    [Fact]
    public void EndedAgentMaySleep()
    {
        var session = new SupportSession();
        session.Pair(binaryMatched: true);
        session.End();

        Assert.False(PowerHoldPolicy.ShouldHoldForAgent(session.Snapshot));
    }
}
