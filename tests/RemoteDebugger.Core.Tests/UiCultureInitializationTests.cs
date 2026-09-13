using System.Globalization;
using RemoteDebugger.Core;
using Xunit;

namespace RemoteDebugger.Core.Tests;

[CollectionDefinition("UI culture initialization", DisableParallelization = true)]
public sealed class UiCultureInitializationCollection;

[Collection("UI culture initialization")]
public sealed class UiCultureInitializationTests
{
    [Fact]
    public async Task InFlightOperationUsesNewLanguageAfterItsOldCultureWasCaptured()
    {
        var oldUi = CultureInfo.CurrentUICulture;
        var oldDefault = CultureInfo.DefaultThreadCurrentUICulture;
        var oldApplication = UiCulture.ApplicationLanguage;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            UiCulture.Apply(CultureInfo.GetCultureInfo("fr"));
            var pending = Task.Run(async () =>
            {
                await release.Task;
                Assert.Equal("fr", CultureInfo.CurrentUICulture.Name);
                return UiText.MeasurementComplete;
            });
            UiCulture.Apply(CultureInfo.GetCultureInfo("es"));
            release.SetResult();
            Assert.Equal("Medición completada", await pending);
        }
        finally
        {
            release.TrySetResult();
            UiCulture.ApplicationLanguage = oldApplication;
            CultureInfo.CurrentUICulture = oldUi;
            CultureInfo.DefaultThreadCurrentUICulture = oldDefault;
        }
    }

    [Theory]
    [InlineData("fr-CA", "fr", "Donner le contrôle")]
    [InlineData("es-MX", "es", "Ceder el control")]
    [InlineData("de-DE", "en", "Give control")]
    public void StartupSetsUiAndBackgroundDefaultsWithoutChangingRegionalFormats(string systemLanguage, string expected, string title)
    {
        var oldUi = CultureInfo.CurrentUICulture;
        var oldApplication = UiCulture.ApplicationLanguage;
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
            UiCulture.ApplicationLanguage = oldApplication;
            CultureInfo.DefaultThreadCurrentUICulture = oldDefault;
            CultureInfo.CurrentCulture = oldFormat;
        }
    }
}
