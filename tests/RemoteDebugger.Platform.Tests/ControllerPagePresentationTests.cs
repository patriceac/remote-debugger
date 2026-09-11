using RemoteDebugger;
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
        var presentation = MainForm.ControllerPagePresentation(index);

        Assert.Equal(expectedTitle, presentation.Title);
        Assert.False(string.IsNullOrWhiteSpace(presentation.Subtitle));
    }
}
