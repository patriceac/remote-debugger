using RemoteDebugger;
using RemoteDebugger.Core;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class MainFormActionTests
{
    [Theory]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    public void ReadyForSupportRequiresAnActiveListenerAndAnAvailableRoute(bool active, bool internet, bool lan, bool expected)
    {
        Assert.Equal(expected, MainForm.IsAgentReadyForSupport(active, internet, lan));
    }

    [Fact]
    public void ConnectionTimelineDoesNotInventSkippedVerification()
    {
        Assert.Equal("pending", UpdateTimeline.StepState(2, "restarting", ["preparing", "transferring"]));
        Assert.Equal("complete", UpdateTimeline.StepState(1, "restarting", ["preparing", "transferring"]));
        Assert.Equal("active", UpdateTimeline.StepState(3, "finalizing", ["restarting"]));
        Assert.All(Enumerable.Range(0, 4), step => Assert.Equal("complete", UpdateTimeline.StepState(step, "complete", [])));
    }

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
    [InlineData("finalizing", true)]
    [InlineData("restarting", true)]
    [InlineData("complete", false)]
    public void UpdateProgressIsShownOnlyWhileSynchronizationIsOngoing(string stage, bool expected)
    {
        Assert.Equal(expected, MainForm.IsOngoingUpdate(new AgentUpdateProgress(stage, 0, 100)));
    }

    [Theory]
    [InlineData(true, true, false, false, false, true)]
    [InlineData(false, true, false, false, false, false)]
    [InlineData(true, false, false, false, false, false)]
    [InlineData(true, true, true, false, false, false)]
    [InlineData(true, true, false, true, false, false)]
    [InlineData(true, true, false, false, true, false)]
    public void ConnectedClientUpdateRequiresAnIdleAuthenticatedSession(
        bool supportSession, bool hasClient, bool pairingBusy, bool clientUpdateBusy, bool terminating, bool expected)
    {
        Assert.Equal(expected, MainForm.CanUpdateClient(supportSession, hasClient, pairingBusy, clientUpdateBusy, terminating));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void SavedConnectedSessionSynchronizesOnlyWhenItsBinaryDiffers(bool connected, bool binaryMatched, bool expected)
    {
        Assert.Equal(expected, MainForm.ShouldSynchronizeSavedSession(connected, binaryMatched));
    }

    [Fact]
    public void ConnectionNoticeAppearsOnceWhenAUsableNewSessionStarts()
    {
        var started = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var session = new SupportSessionSnapshot(true, true, null, "connected", true, started);

        Assert.True(MainForm.ShouldShowConnectionNotice(session, null, false));
        Assert.False(MainForm.ShouldShowConnectionNotice(session, started, false));
        Assert.False(MainForm.ShouldShowConnectionNotice(session, null, true));
        Assert.False(MainForm.ShouldShowConnectionNotice(session with { BinaryMatched = false }, null, false));
        Assert.False(MainForm.ShouldShowConnectionNotice(session with { Connected = false }, null, false));
        Assert.True(MainForm.ShouldShowConnectionNotice(session with { StartedUtc = started.AddMinutes(1) }, started, false));
    }

    [Fact]
    public void MatchingClientBuildDisablesTheUpdateAction() =>
        Assert.False(MainForm.CanUpdateClient(true, true, false, false, false, clientUpToDate: true));

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void FailedSynchronizationRetainsAnAuthenticatedSession(bool connected, bool binaryMatched, bool expected) =>
        Assert.Equal(expected, MainForm.CanRetainFailedSynchronization(Json.Element(new
        {
            session = new { connected },
            binaryMatched
        })));

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void OperationalWorkspaceRequiresAnActiveNonTerminatingSession(bool supportSession, bool terminating, bool expected) =>
        Assert.Equal(expected, MainForm.CanUseControllerWorkspace(supportSession, terminating));

    [Theory]
    [InlineData(0, false, false, 0)]
    [InlineData(4, false, false, 0)]
    [InlineData(3, true, false, 3)]
    [InlineData(2, true, true, 0)]
    public void DisconnectedControllerCanOnlyOpenConnection(int requestedPage, bool supportSession, bool terminating, int expected) =>
        Assert.Equal(expected, MainForm.AvailableControllerPage(requestedPage, supportSession, terminating));

    [Fact]
    public void FleetProgressUsesOneCompactSharedUnit()
    {
        string progress = MainForm.FormatFleetBytes(24 * 1024 * 1024, 143 * 1024 * 1024);

        Assert.EndsWith(" MiB", progress);
        Assert.Equal(1, progress.Count(character => character == '/'));
        Assert.Equal(1, progress.Split("MiB").Length - 1);
    }

    [Fact]
    public void FleetFailureShowsTheActualRemoteReason() =>
        Assert.Equal("update_failed: Publisher mismatch", MainForm.FleetFailureDetail(
            new RemoteOperationException("update_failed", "Publisher mismatch")));

    [Fact]
    public void EstimatedStepProgressCannotBeMistakenForMeasuredProgress()
    {
        string estimate = MainForm.UpdateStepNumbers(new(null, TimeSpan.FromSeconds(15), true, TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain("%", estimate);
        Assert.DoesNotContain("≈", estimate);
        Assert.Contains("0:05", estimate);
        string measured = MainForm.UpdateStepNumbers(new(25, TimeSpan.FromSeconds(15), true, TimeSpan.FromSeconds(5)));
        Assert.StartsWith("25%", measured);
        Assert.DoesNotContain("≈", measured);
        string overdue = MainForm.UpdateStepNumbers(new(null, null, true, TimeSpan.FromMinutes(2)));
        Assert.DoesNotContain("ETA", overdue);
        Assert.Contains("2:00", overdue);
    }
}
