using RemoteDebugger.Core;
using Xunit;

namespace RemoteDebugger.Core.Tests;

public sealed class AdminMaintenancePreferenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RemoteDebugger-maintenance-preference-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChoiceSurvivesReload(bool enabled)
    {
        AdminMaintenancePreference.Save(root, enabled);
        Assert.Equal(enabled, AdminMaintenancePreference.Load(root));
    }

    [Fact]
    public void MissingOrCorruptChoiceKeepsMaintenanceEnabledByDefault()
    {
        Assert.True(AdminMaintenancePreference.Load(root));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "admin-maintenance.json"), "{");
        Assert.True(AdminMaintenancePreference.Load(root));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
