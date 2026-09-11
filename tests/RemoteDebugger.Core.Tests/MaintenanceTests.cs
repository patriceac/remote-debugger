using RemoteDebugger.Core;
using Xunit;

public sealed class MaintenanceTests
{
    private static MaintenanceLease Valid() => new(42, 1234, 2, "S-1-5-21-123-456-789-1001", @"C:\Tools\RemoteDebugger.exe", Guid.NewGuid().ToString("N"));
    [Fact] public void ProcessBoundLeaseHasNoArbitraryWallClockExpiry() => Valid().Validate();
    [Fact] public void NonInteractiveSessionIsRejected() => Assert.Throws<ArgumentException>(() => (Valid() with { SessionId = 0 }).Validate());
    [Fact] public void RelativeExecutableIsRejected() => Assert.Throws<ArgumentException>(() => (Valid() with { ExecutablePath = "RemoteDebugger.exe" }).Validate());
    [Fact] public void InvalidSidIsRejected() => Assert.Throws<ArgumentException>(() => (Valid() with { UserSid = "Administrators" }).Validate());
    [Fact] public void CommandMustBeExplicitAndBounded()
    {
        var r = new Request(Guid.NewGuid().ToString(), "", "maintenance.command", Json.Element(new { file = "whoami.exe", arguments = Array.Empty<string>() }), 30);
        MaintenanceLease.ValidateCommand(r);
        Assert.Throws<ArgumentException>(() => MaintenanceLease.ValidateCommand(r with { Operation = "install-service" }));
        Assert.Throws<ArgumentException>(() => MaintenanceLease.ValidateCommand(r with { TimeoutSeconds = 301 }));
        Assert.Throws<ArgumentException>(() => MaintenanceLease.ValidateCommand(r with { Args = Json.Element(new { }) }));
        Assert.Throws<ArgumentException>(() => MaintenanceLease.ValidateCommand(r with { Args = Json.Element(new { file = "whoami.exe", arguments = Enumerable.Repeat("x", 129) }) }));
    }
}
