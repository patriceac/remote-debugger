using RemoteDebugger;
using RemoteDebugger.Core;
using Xunit;

public sealed class AuditLifecycleTests
{
    [Theory]
    [InlineData(UpdateTransactionState.Staged, true)]
    [InlineData(UpdateTransactionState.Completed, true)]
    [InlineData(UpdateTransactionState.Armed, false)]
    [InlineData(UpdateTransactionState.Replacing, false)]
    [InlineData(UpdateTransactionState.AwaitingStartupHealth, false)]
    [InlineData(UpdateTransactionState.RunningPendingRemoteHealth, false)]
    public void UninstallRefusesActiveReplacementAndHealthChecks(UpdateTransactionState state, bool expected) =>
        Assert.Equal(expected, SupportInstaller.CanUninstall(state));

    [Fact]
    public void ElevatedInputPipeRejectsCommandsAndOversizedRequests()
    {
        var request = new Request(Guid.NewGuid().ToString(), "", "ui.input", Json.Element(new { kind = "release" }));
        InteractiveInputBroker.ValidateRequest(request);
        Assert.Throws<ArgumentException>(() => InteractiveInputBroker.ValidateRequest(request with { Operation = "command" }));
        Assert.Throws<ArgumentException>(() => InteractiveInputBroker.ValidateRequest(request with { Id = "not-a-request" }));
        Assert.Throws<ArgumentException>(() => InteractiveInputBroker.ValidateRequest(request with { Args = Json.Element(new { text = new string('x', 32769) }) }));
    }
}
