using RemoteDebugger.Core;
using Xunit;

public sealed class MaintenanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);
    private static MaintenanceLease Valid() => new(42, 1234, "RemoteDebugger.Admin." + Guid.NewGuid().ToString("N"), Now.AddHours(1));
    [Fact] public void OneHourLocalLeaseIsAccepted() => Valid().Validate(Now);
    [Fact] public void ExpiredLeaseIsRejected() => Assert.Throws<ArgumentException>(() => (Valid() with { ExpiresUtc = Now }).Validate(Now));
    [Fact] public void LongerLeaseIsRejected() => Assert.Throws<ArgumentException>(() => (Valid() with { ExpiresUtc = Now.AddHours(2) }).Validate(Now));
    [Fact] public void ArbitraryPipeIsRejected() => Assert.Throws<ArgumentException>(() => (Valid() with { PipeName = "another-service" }).Validate(Now));
    [Fact] public void CommandMustBeExplicitAndBounded()
    {
        var r = new Request(Guid.NewGuid().ToString(), "", "command", Json.Element(new { file = "whoami.exe" }), 30);
        MaintenanceLease.ValidateCommand(r);
        Assert.Throws<ArgumentException>(() => MaintenanceLease.ValidateCommand(r with { Operation = "install-service" }));
        Assert.Throws<ArgumentException>(() => MaintenanceLease.ValidateCommand(r with { TimeoutSeconds = 301 }));
        Assert.Throws<ArgumentException>(() => MaintenanceLease.ValidateCommand(r with { Args = Json.Element(new { }) }));
    }
}
