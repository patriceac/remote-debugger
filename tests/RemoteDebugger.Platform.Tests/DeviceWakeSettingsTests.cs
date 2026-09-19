using RemoteDebugger;
using Xunit;

public sealed class DeviceWakeSettingsTests
{
    [Fact]
    public void ValidateNormalizesMacDestinationAndHelperIdentityAndRejectsInvalidValues()
    {
        var value = new WakeSettings("001122334455", " Example.COM ", 55001, new('a', 64));

        Assert.Equal(new WakeSettings("00:11:22:33:44:55", "example.com", 55001, new('A', 64)), DeviceWakeSettings.Validate(value));
        Assert.Throws<ArgumentException>(() => DeviceWakeSettings.Validate(value with { Destination = "https://example.com" }));
        Assert.Throws<ArgumentException>(() => DeviceWakeSettings.Validate(value with { Destination = "example.com/path" }));
        Assert.Throws<ArgumentException>(() => DeviceWakeSettings.Validate(value with { Port = 0 }));
        Assert.Throws<ArgumentException>(() => DeviceWakeSettings.Validate(value with { Port = 65536 }));
    }
}
