using System.IO.Pipes;
using RemoteDebugger;
using Xunit;

public sealed class SupportPipeIdentityTests
{
    [Fact]
    public async Task ClientUsesActualProcessIdentityWithoutPublisherVerification()
    {
        string name = "RemoteDebugger.IdentityTest." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connected = server.WaitForConnectionAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        await connected;

        var identity = ProcessIdentity.Capture(Environment.ProcessId);
        var configuration = new SupportConfiguration(SupportPlatformPaths.ProtocolVersion,
            identity.ExecutablePath, identity.UserSid, "not-a-publisher-pin", DateTimeOffset.UtcNow, "test");

        Assert.Equal(identity, SupportPipeIdentity.VerifyClient(server, configuration));
        Assert.Throws<UnauthorizedAccessException>(() => SupportPipeIdentity.VerifyClient(server,
            configuration with { RegisteredUserSid = "S-1-0-0" }));
        Assert.Throws<UnauthorizedAccessException>(() => SupportPipeIdentity.VerifyClient(server,
            configuration with { RegisteredApplicationPath = identity.ExecutablePath + ".other" }));
    }
}
