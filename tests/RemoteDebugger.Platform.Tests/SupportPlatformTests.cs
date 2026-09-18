using RemoteDebugger;
using RemoteDebugger.Core;
using Xunit;

public sealed class SupportPlatformTests
{
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
