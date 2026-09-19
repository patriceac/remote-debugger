using RemoteDebugger;
using RemoteDebugger.Core;
using System.ComponentModel;
using System.ServiceProcess;
using Xunit;

public sealed class SupportPlatformTests
{
    [Theory]
    [InlineData(ServiceControllerStatus.Stopped)]
    [InlineData(ServiceControllerStatus.StopPending)]
    [InlineData(ServiceControllerStatus.Running)]
    public async Task BrokerStartsAfterIdleShutdownAndWaitsForRunning(ServiceControllerStatus initial)
    {
        var states = new Queue<ServiceControllerStatus>(initial == ServiceControllerStatus.Running
            ? [initial] : initial == ServiceControllerStatus.StopPending
                ? [initial, ServiceControllerStatus.Stopped, ServiceControllerStatus.StartPending, ServiceControllerStatus.Running]
                : [initial, ServiceControllerStatus.StartPending, ServiceControllerStatus.Running]);
        int starts = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await SupportPlatform.EnsureServiceRunningAsync(states.Dequeue, () => starts++, timeout.Token);
        Assert.Empty(states);
        Assert.Equal(initial == ServiceControllerStatus.Running ? 0 : 1, starts);
    }

    [Fact]
    public async Task BrokerStartFailureIsReportedWithoutRetryingAndStartupCanBeCancelled()
    {
        var denied = new InvalidOperationException("Service start denied.", new Win32Exception(5));
        Assert.Same(denied, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SupportPlatform.EnsureServiceRunningAsync(() => ServiceControllerStatus.Stopped, () => throw denied, CancellationToken.None)));
        using var stop = new CancellationTokenSource();
        var waiting = SupportPlatform.EnsureServiceRunningAsync(() => ServiceControllerStatus.StartPending,
            () => throw new InvalidOperationException("Must not start an already starting service."), stop.Token);
        Assert.False(waiting.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task ConcurrentBrokerStartWaitsForRunningAndStoppedStartupFails()
    {
        var states = new Queue<ServiceControllerStatus>([ServiceControllerStatus.Stopped, ServiceControllerStatus.StartPending, ServiceControllerStatus.Running]);
        await SupportPlatform.EnsureServiceRunningAsync(states.Dequeue,
            () => throw new InvalidOperationException("Already running.", new Win32Exception(1056)), CancellationToken.None);
        Assert.Empty(states);
        int starts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => SupportPlatform.EnsureServiceRunningAsync(
            () => ServiceControllerStatus.Stopped, () => starts++, CancellationToken.None));
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task DisabledMaintenanceRejectsPrivilegedCommands()
    {
        string root = Path.Combine(Path.GetTempPath(), "RemoteDebugger-disabled-maintenance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var maintenance = new MaintenanceSession(root);
            maintenance.SetEnabled(false);

            Assert.False(maintenance.Enabled);
            Assert.False(maintenance.CurrentStatus.Active);
            Assert.Contains("disabled", maintenance.CurrentStatus.Message, StringComparison.OrdinalIgnoreCase);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                maintenance.RunAsync("whoami.exe", [], CancellationToken.None));

            var operations = new Operations(root);
            operations.Maintenance.SetEnabled(false);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                operations.ExecuteAsync("maintenance.elevated", Json.Element(new { file = "whoami.exe", arguments = Array.Empty<string>() }), CancellationToken.None));
            operations.Maintenance.Dispose();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProvisionedButUnavailableSupportDoesNotRequestAdministratorProvisioning()
    {
        var status = new SupportPlatformStatus(
            SupportPlatformAvailability.ServiceStopped,
            Provisioned: true,
            Available: false,
            IdentityVerified: true,
            FirewallReady: false,
            RequiresAdministratorConsent: false,
            Message: "The broker is temporarily unavailable.",
            RegisteredApplicationPath: "C:\\Program Files\\RemoteDebugger\\RemoteDebugger.exe",
            PublisherThumbprint: "ABC",
            ServiceVersion: "0.4.8");

        Assert.False(SupportPlatform.RequiresAdministratorProvisioning(status));
    }

    [Fact]
    public void MissingSupportProvisioningStillRequestsAdministratorProvisioning()
    {
        var status = new SupportPlatformStatus(
            SupportPlatformAvailability.NotProvisioned,
            Provisioned: false,
            Available: false,
            IdentityVerified: false,
            FirewallReady: false,
            RequiresAdministratorConsent: true,
            Message: "Provisioning is required.",
            RegisteredApplicationPath: null,
            PublisherThumbprint: null,
            ServiceVersion: null);

        Assert.True(SupportPlatform.RequiresAdministratorProvisioning(status));
    }
}
