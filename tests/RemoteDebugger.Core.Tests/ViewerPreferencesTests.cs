using RemoteDebugger.Core;
using Xunit;

namespace RemoteDebugger.Core.Tests;

public sealed class ViewerPreferencesTests
{
    [Fact]
    public void TogglesSurviveReloadAndAreIsolatedByDeviceIdentity()
    {
        string root = Path.Combine(Path.GetTempPath(), "RemoteDebugger-viewer-tests", Guid.NewGuid().ToString("N"));
        try
        {
            new ViewerPreferences(false, true).Save(root, "device-a");
            new ViewerPreferences(true, false).Save(root, "device-b");
            Assert.Equal(new ViewerPreferences(false, true), ViewerPreferences.Load(root, "DEVICE-A"));
            Assert.Equal(new ViewerPreferences(true, false), ViewerPreferences.Load(root, "device-b"));
            Assert.Equal(new ViewerPreferences(), ViewerPreferences.Load(root, "new-device"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void BriefReconnectIsInvisibleAndRepeatedFailuresDoNotRestartGracePeriod()
    {
        var notice = new ReconnectNotice();
        notice.Interrupt(1000);
        Assert.False(notice.IsVisible(2999));
        notice.Interrupt(2500);
        Assert.True(notice.IsVisible(3000));
        notice.Recover();
        Assert.False(notice.IsVisible(9000));
        notice.Interrupt(9000);
        Assert.False(notice.IsVisible(9001));
    }
}
