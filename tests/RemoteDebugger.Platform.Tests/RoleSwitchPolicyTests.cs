using RemoteDebugger;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class RoleSwitchPolicyTests
{
    [Theory]
    [InlineData(0, 1, true, false)]
    [InlineData(1, 0, false, true)]
    [InlineData(0, 1, true, true)]
    [InlineData(1, 0, true, true)]
    public void SwitchingTopLevelRolePreservesAnActivePeerSession(
        int currentRole, int targetRole, bool agentRunning, bool controllerSessionActive)
    {
        Assert.True(MainForm.ShouldPreserveActiveSessionsOnRoleSwitch(
            currentRole, targetRole, agentRunning, controllerSessionActive));
    }

    [Theory]
    [InlineData(0, 0, true, false)]
    [InlineData(1, 1, false, true)]
    [InlineData(0, 1, false, false)]
    public void PreservationOnlyAppliesToARealSwitchWithAnActiveSession(
        int currentRole, int targetRole, bool agentRunning, bool controllerSessionActive)
    {
        Assert.False(MainForm.ShouldPreserveActiveSessionsOnRoleSwitch(
            currentRole, targetRole, agentRunning, controllerSessionActive));
    }
}
