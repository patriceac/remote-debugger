using System.Globalization;
using RemoteDebugger.Core;
using Xunit;

namespace RemoteDebugger.Core.Tests;

public sealed class LanguagePreferenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RemoteDebugger-language-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(null)]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public void ChoiceSurvivesReloadAndCanReturnToSystem(string? language)
    {
        LanguagePreference.Save(root, language);
        Assert.Equal(language, LanguagePreference.Load(root));
        LanguagePreference.Save(root, null);
        Assert.Null(LanguagePreference.Load(root));
        Assert.Single(Directory.GetFiles(root));
    }

    [Theory]
    [InlineData("fr-CA", null, null, "fr")]
    [InlineData("fr-CA", "es", null, "es")]
    [InlineData("es-MX", "en", null, "en")]
    [InlineData("de-DE", null, null, "en")]
    [InlineData("fr-FR", "unknown", null, "fr")]
    [InlineData("fr-FR", "es", "en-GB", "en")]
    public void ChoiceOverridesSystemAndOptionalLaunchOverrideTakesPrecedence(string system, string? saved, string? argument, string expected) =>
        Assert.Equal(expected, LanguagePreference.Resolve(CultureInfo.GetCultureInfo(system), saved, argument).Name);

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"Language\":\"unknown\"}")]
    [InlineData("{\"Language\":42}")]
    public void CorruptOrUnknownPreferenceUsesSystem(string json)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "language.json"), json);
        Assert.Null(LanguagePreference.Load(root));
    }

    [Fact]
    public void MissingPreferenceUsesSystem() => Assert.Null(LanguagePreference.Load(root));

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
