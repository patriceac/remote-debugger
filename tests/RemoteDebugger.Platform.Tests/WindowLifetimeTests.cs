using RemoteDebugger;
using System.Windows.Forms;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class WindowLifetimeTests
{
    [Theory]
    [InlineData(AgentStopReason.SupportEnded, false)]
    [InlineData(AgentStopReason.UpdateReplacement, true)]
    public void PlannedUpdateStillExitsSoReplacementCanStart(AgentStopReason reason, bool exit) =>
        Assert.Equal(exit, WindowLifetime.ExitAfterAgentStop(reason));

    [Theory]
    [InlineData(AgentStopReason.SupportEnded, true)]
    [InlineData(AgentStopReason.UpdateReplacement, false)]
    public void OrdinarySupportEndRestartsAvailabilityWithoutClosingTheWorkspace(AgentStopReason reason, bool restart) =>
        Assert.Equal(restart, WindowLifetime.RestartAfterAgentStop(reason));

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, false)]
    public void StartupEnablesSupportOnlyWhenTheProfileIsReadyOrExplicitlyRequested(
        bool privateInternet, bool protectedProfileAvailable, bool explicitRequest, bool enabled) =>
        Assert.Equal(enabled, WindowLifetime.EnableSupportAtStartup(privateInternet, protectedProfileAvailable, explicitRequest));

    [Theory]
    [InlineData(CloseReason.UserClosing, false, true)]
    [InlineData(CloseReason.UserClosing, true, false)]
    [InlineData(CloseReason.WindowsShutDown, false, false)]
    [InlineData(CloseReason.ApplicationExitCall, false, false)]
    [InlineData(CloseReason.TaskManagerClosing, false, true)]
    [InlineData(CloseReason.None, false, true)]
    public void OnlyOrdinaryWindowCloseKeepsApplicationRunning(CloseReason reason, bool quitting, bool hide) =>
        Assert.Equal(hide, WindowLifetime.HideToTray(reason, quitting));
}
