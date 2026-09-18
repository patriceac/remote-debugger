using RemoteDebugger;
using Xunit;

public sealed class SupportPlatformTests
{
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
