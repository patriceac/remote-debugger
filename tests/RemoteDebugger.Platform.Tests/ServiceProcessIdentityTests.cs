using Xunit;
using RemoteDebugger.Core;

namespace RemoteDebugger.Platform.Tests;

public sealed class ServiceProcessIdentityTests
{
    [Fact]
    public void MatchingScmFactsAttestBrokerWithoutOpeningSystemProcess()
    {
        string command = $"\"{SupportPlatformPaths.ServiceExecutable}\" --platform-service";

        string path = ServiceProcessIdentity.ValidateScmAttestation(
            pipeServerProcessId: 731,
            serviceState: 4,
            serviceProcessId: 731,
            serviceType: 0x10,
            serviceStartName: "LocalSystem",
            serviceCommand: command);

        Assert.Equal(Path.GetFullPath(SupportPlatformPaths.ServiceExecutable), path, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void DifferentPipePidIsRejected()
    {
        string command = $"\"{SupportPlatformPaths.ServiceExecutable}\" --platform-service";

        Assert.Throws<UnauthorizedAccessException>(() => ServiceProcessIdentity.ValidateScmAttestation(
            pipeServerProcessId: 731,
            serviceState: 4,
            serviceProcessId: 732,
            serviceType: 0x10,
            serviceStartName: "LocalSystem",
            serviceCommand: command));
    }
}

public sealed class SupportOperationTimeoutTests
{
    [Fact]
    public void ColdPlatformOperationsKeepClientPastBrokerDeadline()
    {
        Assert.True(SupportOperationTimeouts.PlatformStatusExecutionSeconds >= 60);
        Assert.True(SupportOperationTimeouts.PlatformStatusRoundTripSeconds >
                    SupportOperationTimeouts.PlatformStatusExecutionSeconds);
        Assert.True(SupportOperationTimeouts.FirewallEnsureExecutionSeconds >= 60);
        Assert.True(SupportOperationTimeouts.FirewallEnsureRoundTripSeconds >
                    SupportOperationTimeouts.FirewallEnsureExecutionSeconds);
    }

    [Fact]
    public void UpdateBudgetsCoverTransferAndColdRestartPreparation()
    {
        Assert.True(SupportOperationTimeouts.ControllerSynchronizationSeconds >
                    SupportOperationTimeouts.PairingHandshakeSeconds);
        Assert.True(SupportOperationTimeouts.UpdateStageSeconds > 180);
        Assert.Equal(SupportOperationTimeouts.UpdateStageSeconds, AgentUpdateService.StageBrokerTimeoutSeconds);

        int supportedColdPreparationSeconds =
            SupportOperationTimeouts.PlatformStatusRoundTripSeconds * 2 +
            SupportOperationTimeouts.FirewallEnsureRoundTripSeconds;
        Assert.True(SupportOperationTimeouts.UpdateStartupHealthReportSeconds > supportedColdPreparationSeconds);
        Assert.True(SupportOperationTimeouts.UpdateStartupHealthRollbackSeconds >
                    SupportOperationTimeouts.UpdateStartupHealthReportSeconds + 15);
        Assert.True(UpdateReconnectGrant.MaximumLifetime.TotalSeconds >
                    SupportOperationTimeouts.UpdateStartupHealthRollbackSeconds);
    }
}

public sealed class FleetUpdateRecoveryTests
{
    [Fact]
    public void OnlyDisconnectedUpdateOnlySessionsCanBeReclaimed()
    {
        var disconnected = new SupportSessionSnapshot(false, true, DateTimeOffset.UtcNow.AddMinutes(10),
            "reconnecting", false, DateTimeOffset.UtcNow);
        var connected = disconnected with { Connected = true, State = "connected" };

        Assert.True(AgentServer.CanReclaimUpdateSession(true, disconnected, null));
        Assert.False(AgentServer.CanReclaimUpdateSession(false, disconnected, null));
        Assert.False(AgentServer.CanReclaimUpdateSession(true, connected, null));
        Assert.False(AgentServer.CanReclaimUpdateSession(true, disconnected,
            new UpdateExitPlan(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddMinutes(1))));
    }

    [Fact]
    public void VerificationRetriesOnlyTransientFailures()
    {
        Assert.True(AgentUpdateClient.IsRetryableStageFailure(new OperationCanceledException()));
        Assert.True(AgentUpdateClient.IsRetryableStageFailure(new RemoteOperationException("update_cancelled", "Timed out")));
        Assert.False(AgentUpdateClient.IsRetryableStageFailure(new RemoteOperationException("update_failed", "Hash mismatch")));
    }
}

public sealed class AgentUpdateHealthTests
{
    [Fact]
    public void StartupHealthStateIsTheOnlyRetryableRemoteHealthState()
    {
        Assert.Equal(UpdateRemoteHealthReadiness.PendingStartupHealth,
            AgentUpdateService.ClassifyRemoteHealthReadiness(Json.Element(new { state = nameof(UpdateTransactionState.AwaitingStartupHealth) })));
        Assert.Equal(UpdateRemoteHealthReadiness.Ready,
            AgentUpdateService.ClassifyRemoteHealthReadiness(Json.Element(new { state = nameof(UpdateTransactionState.RunningPendingRemoteHealth) })));
        Assert.Equal(UpdateRemoteHealthReadiness.Ready,
            AgentUpdateService.ClassifyRemoteHealthReadiness(Json.Element(new { state = nameof(UpdateTransactionState.Completed) })));

        var failure = Assert.Throws<InvalidOperationException>(() =>
            AgentUpdateService.ClassifyRemoteHealthReadiness(Json.Element(new
            {
                state = nameof(UpdateTransactionState.RolledBack),
                lastError = "startup validation failed"
            })));
        Assert.Contains("startup validation failed", failure.Message);
    }
}

public sealed class AgentUpdateProgressTests
{
    [Theory]
    [InlineData(-1, 100, 0)]
    [InlineData(0, 100, 0)]
    [InlineData(25, 100, 25)]
    [InlineData(199, 200, 99)]
    [InlineData(100, 100, 100)]
    [InlineData(150, 100, 100)]
    [InlineData(10, 0, 0)]
    public void TransferPercentIsIntegralAndBounded(long transferred, long total, int expected)
    {
        var progress = new AgentUpdateProgress("transferring", transferred, total);

        Assert.Equal(expected, progress.TransferPercent);
        Assert.Equal(transferred, progress.TransferredBytes);
        Assert.Equal(total, progress.TotalBytes);
    }
}
