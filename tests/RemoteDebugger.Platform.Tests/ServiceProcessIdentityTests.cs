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
