using RemoteDebugger;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class MainFormActionTests
{
    [Theory]
    [InlineData(true, false, false, "pairing", false, false, false)]
    [InlineData(true, false, true, "connected", false, false, true)]
    [InlineData(true, false, false, "reconnecting", false, false, true)]
    [InlineData(true, true, true, "connected", false, false, false)]
    [InlineData(false, false, false, "", false, false, false)]
    [InlineData(false, false, false, "", true, false, true)]
    [InlineData(false, false, false, "", false, true, true)]
    public void TerminateActionIsShownOnlyForAnActiveSession(
        bool onAgent, bool agentIdle, bool agentConnected, string agentState,
        bool supportSession, bool synchronizingAgent, bool expected)
    {
        Assert.Equal(expected, MainForm.ShouldShowTerminateSession(onAgent, agentIdle, agentConnected,
            agentState, supportSession, synchronizingAgent));
    }

    [Theory]
    [InlineData("idle", false)]
    [InlineData("transferring", true)]
    [InlineData("verifying", true)]
    [InlineData("staging", true)]
    [InlineData("restarting", true)]
    [InlineData("complete", false)]
    public void UpdateProgressIsShownOnlyWhileSynchronizationIsOngoing(string stage, bool expected)
    {
        Assert.Equal(expected, MainForm.IsOngoingUpdate(new AgentUpdateProgress(stage, 0, 100)));
    }
}
