using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class PowerPreflightTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("\0 \0\t\r\n\0", false)]
    [InlineData("Please acknowledge this notice.", true)]
    [InlineData("\0 Please acknowledge this notice.\0", true)]
    public void OnlyNonemptyLogonNoticesRequireInteraction(string? text, bool expected)
        => Assert.Equal(expected, PrivilegedPowerManager.HasLogonNotice(text));
}
