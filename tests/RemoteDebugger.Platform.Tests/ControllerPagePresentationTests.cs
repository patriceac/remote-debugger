using RemoteDebugger;
using System.Globalization;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class ControllerPagePresentationTests
{
    [Theory]
    [InlineData(0, "Connexion")]
    [InlineData(1, "Écran distant")]
    [InlineData(2, "Processus")]
    [InlineData(3, "Fichiers")]
    [InlineData(4, "Diagnostics")]
    public void ActiveControllerPageOwnsTheSingleWorkspaceTitle(int index, string expectedTitle)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr");
            var presentation = MainForm.ControllerPagePresentation(index);
            Assert.Equal(expectedTitle, presentation.Title);
            Assert.False(string.IsNullOrWhiteSpace(presentation.Subtitle));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Theory]
    [InlineData("en-GB", "Remote screen", "Files")]
    [InlineData("es-MX", "Pantalla remota", "Archivos")]
    [InlineData("fr-CA", "Écran distant", "Fichiers")]
    [InlineData("de-DE", "Remote screen", "Files")]
    public void PageTitlesUseTheCurrentUiLanguage(string culture, string screen, string files)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(screen, MainForm.ControllerPagePresentation(1).Title);
            Assert.Equal(files, MainForm.ControllerPagePresentation(3).Title);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }
}
