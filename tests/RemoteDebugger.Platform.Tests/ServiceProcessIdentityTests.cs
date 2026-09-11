using Xunit;

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
    public void FirewallClientOutlivesBrokerExecutionDeadline()
    {
        Assert.True(SupportOperationTimeouts.FirewallEnsureExecutionSeconds >= 60);
        Assert.True(SupportOperationTimeouts.FirewallEnsureRoundTripSeconds >
                    SupportOperationTimeouts.FirewallEnsureExecutionSeconds);
    }
}
