using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using RemoteDebugger;
using RemoteDebugger.Core;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class UnattendedSupportTests
{
    [Fact]
    public void DisablingDoesNotReadPrivateCredentials()
        => Assert.Equal(new UnattendedProfile(false), UnattendedSupport.CreateProfile("invalid\0path", false));

    [Theory]
    [InlineData(true, 1, null, true)]
    [InlineData(false, 1, null, false)]
    [InlineData(true, 0, null, false)]
    [InlineData(true, -1, null, false)]
    [InlineData(true, 1, "S-1-5-21-1001", false)]
    public void OnlyAnEnabledSignedOutConsoleStartsAWorker(bool enabled, int session, string? user, bool expected)
        => Assert.Equal(expected, UnattendedSupportHost.ShouldRun(enabled, session, user));

    [Fact]
    public void BrokerRequiresTheExactSupervisedSystemProcess()
    {
        var expected = new VerifiedProcessIdentity(125, 10000, 1, UnattendedSupport.SystemSid, @"C:\Program Files\RemoteDebugger\RemoteDebugger.exe");
        Assert.True(UnattendedSupportHost.IsWorkerIdentity(expected, expected));
        foreach (var other in new[] { expected with { ProcessId = 126 }, expected with { StartTicks = 10001 },
            expected with { SessionId = 2 }, expected with { UserSid = "S-1-5-21-1001" }, expected with { ExecutablePath = @"C:\Temp\RemoteDebugger.exe" } })
            Assert.False(UnattendedSupportHost.IsWorkerIdentity(other, expected));
        Assert.False(UnattendedSupportHost.IsWorkerIdentity(expected, null));
        var desktop = expected with { UserSid = "S-1-5-21-1001" };
        Assert.False(UnattendedSupportHost.IsWorkerIdentity(desktop, desktop));
    }

    [Fact]
    public void EnrollmentRequiresTheReceivingIdentityAndAnUnlockedPrivateSetup()
    {
        new UnattendedProfile(false).Validate();
        Assert.Throws<InvalidDataException>(() => new UnattendedProfile(true).Validate());
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Unattended unit test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var settings = new InternetSettings("https://relay.invalid", new string('A', 64), new string('B', 64));
        var invitation = JsonSerializer.SerializeToUtf8Bytes(new { id = new string('C', 16), key = new string('D', 64) }, Json.Options);
        var profile = new UnattendedProfile(true, certificate.Export(X509ContentType.Pfx), settings, invitation, Path.GetTempPath());
        profile.Validate();
        Assert.Throws<InvalidDataException>(() => (profile with { Certificate = certificate.Export(X509ContentType.Cert) }).Validate());
        Assert.Throws<InvalidDataException>(() => (profile with { Internet = null }).Validate());
        Assert.Throws<ArgumentException>(() => (profile with { Internet = settings with { PairingKey = "" } }).Validate());
    }
}
