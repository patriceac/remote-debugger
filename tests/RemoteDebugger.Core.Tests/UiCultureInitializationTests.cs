using System.Globalization;
using RemoteDebugger.Core;
using Xunit;

namespace RemoteDebugger.Core.Tests;

[CollectionDefinition("UI culture initialization", DisableParallelization = true)]
public sealed class UiCultureInitializationCollection;

[Collection("UI culture initialization")]
public sealed class UiCultureInitializationTests
{
    [Theory]
    [InlineData("fr-CA", "fr", "Donner le contrôle")]
    [InlineData("es-MX", "es", "Ceder el control")]
    [InlineData("de-DE", "en", "Give control")]
    public void StartupSetsUiAndBackgroundDefaultsWithoutChangingRegionalFormats(string systemLanguage, string expected, string title)
    {
        var oldUi = CultureInfo.CurrentUICulture;
        var oldDefault = CultureInfo.DefaultThreadCurrentUICulture;
        var oldFormat = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(systemLanguage);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-CH");
            UiCulture.Initialize();
            Assert.Equal(expected, CultureInfo.CurrentUICulture.Name);
            Assert.Equal(expected, CultureInfo.DefaultThreadCurrentUICulture!.Name);
            Assert.Equal("de-CH", CultureInfo.CurrentCulture.Name);
            Assert.Equal(title, UiText.GiveControl);
        }
        finally
        {
            CultureInfo.CurrentUICulture = oldUi;
            CultureInfo.DefaultThreadCurrentUICulture = oldDefault;
            CultureInfo.CurrentCulture = oldFormat;
        }
    }
}
