using RemoteDebugger;
using RemoteDebugger.Core;
using Xunit;

public sealed class InputStartupTests
{
    [Fact]
    public async Task ModernMaintenanceOpensWithoutWaitingForFirewallStatus()
    {
        var blockedFirewall = new TaskCompletionSource<SupportPlatformStatus>();
        var ready = MaintenanceSession.ReadInputCapabilityAsync(
            Json.Element(new { protocolVersion = 1, interactiveInput = true }), _ => blockedFirewall.Task, default);
        Assert.True(ready.IsCompletedSuccessfully);
        Assert.True(await ready);
        await Assert.ThrowsAsync<InvalidOperationException>(() => MaintenanceSession.ReadInputCapabilityAsync(
            Json.Element(new { protocolVersion = 2, interactiveInput = true }), _ => blockedFirewall.Task, default));
    }

    [Fact]
    public async Task OlderBrokerUsesItsReportedInputCapabilityAndRejectsUnavailableStatus()
    {
        var status = new SupportPlatformStatus(SupportPlatformAvailability.Ready, true, true, true, false, false,
            "ready", null, null, null, InteractiveInputAvailable: false);
        int reads = 0;
        Assert.False(await MaintenanceSession.ReadInputCapabilityAsync(Json.Element(new { leaseId = "legacy" }),
            _ => { reads++; return Task.FromResult(status); }, default));
        Assert.Equal(1, reads);
        await Assert.ThrowsAsync<InvalidOperationException>(() => MaintenanceSession.ReadInputCapabilityAsync(
            Json.Element(new { }), _ => Task.FromResult(status with { Available = false }), default));
    }
}
