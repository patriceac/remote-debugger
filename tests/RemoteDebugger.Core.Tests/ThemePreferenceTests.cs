using RemoteDebugger.Core;
using Xunit;

public sealed class ThemePreferenceTests
{
    [Theory]
    [InlineData(null, false, false)]
    [InlineData(null, true, true)]
    [InlineData("invalid", true, true)]
    [InlineData("system", false, false)]
    [InlineData("system", true, true)]
    [InlineData("light", true, false)]
    [InlineData("dark", false, true)]
    public void ExplicitChoiceOverridesSystemAndMissingChoiceFollowsIt(string? choice, bool systemDark, bool expected) =>
        Assert.Equal(expected, ThemePreference.IsDark(choice, systemDark));

    [Fact]
    public void PreferenceDefaultsToSystemAndSurvivesReload()
    {
        string root = Path.Combine(Path.GetTempPath(), "rd-theme-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal("system", ThemePreference.Load(root));
            ThemePreference.Save(root, "dark"); Assert.Equal("dark", ThemePreference.Load(root));
            ThemePreference.Save(root, "light"); Assert.Equal("light", ThemePreference.Load(root));
            ThemePreference.Save(root, "system"); Assert.Equal("system", ThemePreference.Load(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
