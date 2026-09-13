using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;
using RemoteDebugger.Core;
using Xunit;

namespace RemoteDebugger.Core.Tests;

public sealed class LocalizationTests
{
    [Theory]
    [InlineData("fr", "fr", "Donner le contrôle")]
    [InlineData("fr-FR", "fr", "Donner le contrôle")]
    [InlineData("fr-CA", "fr", "Donner le contrôle")]
    [InlineData("en-US", "en", "Give control")]
    [InlineData("en-GB", "en", "Give control")]
    [InlineData("es", "es", "Ceder el control")]
    [InlineData("es-ES", "es", "Ceder el control")]
    [InlineData("es-MX", "es", "Ceder el control")]
    [InlineData("es-AR", "es", "Ceder el control")]
    [InlineData("de-DE", "en", "Give control")]
    [InlineData("ja-JP", "en", "Give control")]
    [InlineData("", "en", "Give control")]
    public void SystemUiLanguageSelectsSupportedFamilyOrEnglish(string name, string expected, string label)
    {
        var culture = CultureInfo.GetCultureInfo(name);
        Assert.Equal(expected, UiCulture.Resolve(culture).Name);
        Assert.Equal(label, UiText.Get(nameof(UiText.GiveControl), culture));
    }

    [Fact]
    public void MissingSystemLanguageFallsBackToEnglish() => Assert.Equal("en", UiCulture.Resolve(null).Name);

    [Theory]
    [InlineData(null, "fr")]
    [InlineData("es-MX", "es")]
    [InlineData("de-DE", "en")]
    [InlineData("!invalid!", "en")]
    public void OptionalLaunchOverrideDefaultsToSystemLanguage(string? languageOverride, string expected) =>
        Assert.Equal(expected, UiCulture.Select(CultureInfo.GetCultureInfo("fr-CA"), languageOverride).Name);

    [Fact]
    public void EveryTranslationContainsEveryTypedKeyAndPreservesFormatArguments()
    {
        var resources = new ResourceManager("RemoteDebugger.Core.Localization.Strings", typeof(UiText).Assembly);
        var keys = typeof(UiText).GetProperties(BindingFlags.Static | BindingFlags.Public).Select(p => p.Name).Order().ToArray();
        var baseline = Read(CultureInfo.InvariantCulture);
        Assert.Equal(keys, baseline.Keys.Order());
        foreach (string language in new[] { "fr", "es" })
        {
            // Disable parent fallback here: a missing translation must fail the test.
            var translations = Read(CultureInfo.GetCultureInfo(language));
            Assert.Equal(keys, translations.Keys.Order());
            foreach (string key in keys)
            {
                Assert.False(string.IsNullOrWhiteSpace(translations[key]), $"{language}/{key} is empty");
                Assert.Equal(CompositeFormat.Parse(baseline[key]).MinimumArgumentCount,
                    CompositeFormat.Parse(translations[key]).MinimumArgumentCount);
                Assert.Equal(Placeholders(baseline[key]), Placeholders(translations[key]));
            }
        }

        Dictionary<string, string> Read(CultureInfo culture) => resources.GetResourceSet(culture, true, false)!
            .Cast<DictionaryEntry>().ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!);
        static string[] Placeholders(string value) => Regex.Matches(value, @"\{(\d+)(?:,[^}:]+)?(?::([^}]+))?\}")
            .Select(match => match.Value).Order().ToArray();
    }

    [Theory]
    [InlineData("en-US", "New code in 02:05", "Transfer · 50% · 1.5 / 3.0 MiB")]
    [InlineData("fr-FR", "Nouveau code dans 02:05", "Transfert · 50 % · 1,5 / 3,0 Mio")]
    [InlineData("es-MX", "Código nuevo en 02:05", "Transferencia · 50 % · 1.5 / 3.0 MiB")]
    public async Task DynamicMessagesUseUiLanguageAndRegionalFormattingAcrossAsyncWork(string culture, string countdown, string progress)
    {
        var previousUi = CultureInfo.CurrentUICulture;
        var previousFormat = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var result = await Task.Run(() => (
                UiText.Format(UiText.NewCodeCountdown, TimeSpan.FromSeconds(125)),
                UiText.Format(UiText.TransferProgress, 50, 1.5, 3.0)));
            Assert.Equal((countdown, progress), result);
        }
        finally { CultureInfo.CurrentUICulture = previousUi; CultureInfo.CurrentCulture = previousFormat; }
    }
}
