using RemoteDebugger.Core;
using System.Text.Json;
using Xunit;

namespace RemoteDebugger.Core.Tests;

public sealed class WorkspacePresentationTests : IDisposable
{
    private readonly System.Globalization.CultureInfo previous = System.Globalization.CultureInfo.CurrentUICulture;
    public WorkspacePresentationTests() => System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
    public void Dispose() => System.Globalization.CultureInfo.CurrentUICulture = previous;

    [Theory]
    [InlineData(false, false, false, false, false, true, false)]
    [InlineData(false, false, true, false, false, false, false)]
    [InlineData(true, true, false, false, false, false, true)]
    [InlineData(true, false, false, false, false, false, false)]
    [InlineData(true, true, false, true, true, false, false)]
    public void AvailabilityFollowsLiveSessionRatherThanSavedCredentials(bool session, bool healthy, bool pairing, bool ending,
        bool action, bool canPair, bool canOperate)
    {
        var state = WorkspaceAvailability.For(session, healthy, pairing, ending, action, selectedFile: true);
        Assert.Equal(canPair, state.CanPair);
        Assert.Equal(canOperate, state.CanOperate);
        Assert.Equal(canOperate, state.CanDownload);
        Assert.Equal(session && action && !ending, state.CanCancel);
    }

    [Fact]
    public void DownloadRequiresSelectionAndCancelRequiresActiveAction()
    {
        var idle = WorkspaceAvailability.For(true, true, false, false, false, false);
        Assert.False(idle.CanDownload);
        Assert.False(idle.CanCancel);
        Assert.True(WorkspaceAvailability.For(true, true, false, false, true, true).CanCancel);
    }

    [Theory]
    [InlineData(0, "connection", "PC : host")]
    [InlineData(1, "Écran distant", "screen")]
    [InlineData(2, "resources", "measurements")]
    [InlineData(3, "files", "directory")]
    [InlineData(4, "diagnostics", "Action : status")]
    public void EachTabOwnsItsFooter(int tab, string status, string detail)
    {
        Assert.Equal((status, detail), WorkspacePresentation.Footer(tab, "connection", "screen", "resources", "files",
            "diagnostics", "host", "measurements", "directory", "status"));
    }

    [Fact]
    public void IdentityDistinguishesDisconnectedAndTemporarilyLostConnection()
    {
        Assert.Equal("Aucune connexion authentifiée.", WorkspacePresentation.Identity(false, true, "old host", "old pin"));
        Assert.StartsWith("Identité vérifiée · target", WorkspacePresentation.Identity(true, true, "target", new string('A', 64)));
        Assert.StartsWith("Dernière identité vérifiée", WorkspacePresentation.Identity(true, false, "target", new string('A', 64)));
    }

    [Fact]
    public void DiagnosticTemplateUsesSelectedPidAndPreservesOtherArguments()
    {
        using var json = JsonDocument.Parse(WorkspacePresentation.ArgumentsFor("{\"pid\":1234,\"mode\":\"graceful\"}", 42));
        Assert.Equal(42, json.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal("graceful", json.RootElement.GetProperty("mode").GetString());
        Assert.Equal("{}", WorkspacePresentation.ArgumentsFor("{}", 42));
        Assert.True(WorkspacePresentation.UsesPid("{\"pid\":1234}"));
        Assert.False(WorkspacePresentation.UsesPid("{}"));
        Assert.Throws<JsonException>(() => WorkspacePresentation.ArgumentsFor("[]", 42));
    }
}
