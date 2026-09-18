using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class InstallerDefinitionTests
{
    [Fact]
    public void InstallerIsPerMachineAndCreatesStartMenuShortcut()
    {
        string definition = File.ReadAllText(ProjectFile("installer", "RemoteDebugger.iss"));

        Assert.Contains("PrivilegesRequired=admin", definition, StringComparison.Ordinal);
        Assert.Contains("DefaultDirName={autopf}\\RemoteDebugger", definition, StringComparison.Ordinal);
        Assert.DoesNotContain("DefaultDirName={localappdata}", definition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Name: \"{commonprograms}\\Remote Debugger\"", definition, StringComparison.Ordinal);
        Assert.Contains("UninstallDisplayIcon={app}\\{#AppExeName}", definition, StringComparison.Ordinal);
        Assert.Contains("Flags: nowait postinstall skipifsilent runasoriginaluser", definition, StringComparison.Ordinal);
        Assert.DoesNotContain("runascurrentuser", definition, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallerStartsInTrayAtSignInForTheCurrentUser()
    {
        string definition = File.ReadAllText(ProjectFile("installer", "RemoteDebugger.iss"));
        string startup = Assert.Single(definition.Split('\n'), line => line.StartsWith("Name: \"{userstartup}\\", StringComparison.Ordinal));
        Assert.Contains("Filename: \"{app}\\{#AppExeName}\"", startup, StringComparison.Ordinal);
        Assert.Contains("WorkingDir: \"{app}\"", startup, StringComparison.Ordinal);
        Assert.Contains("Parameters: \"--startup\"", startup, StringComparison.Ordinal);
        Assert.DoesNotContain("--enable-support", startup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Tasks:", startup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("uninsneveruninstall", startup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{commonstartup}", definition, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallerPackagesTheReleaseExecutableAndCanLaunchItNormally()
    {
        string definition = File.ReadAllText(ProjectFile("installer", "RemoteDebugger.iss"));

        Assert.Contains("Source: \"..\\artifacts\\release\\RemoteDebugger.exe\"", definition, StringComparison.Ordinal);
        Assert.Contains("Excludes: \"VALIDATION*.md\"", definition, StringComparison.Ordinal);
        Assert.Contains("Filename: \"{app}\\{#AppExeName}\"", definition, StringComparison.Ordinal);
        Assert.Contains("Flags: nowait postinstall skipifsilent", definition, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerStopsLegacyCopiesAndRefreshesTheProtectedService()
    {
        string definition = File.ReadAllText(ProjectFile("installer", "RemoteDebugger.iss"));

        Assert.Contains("RemoteDebugger-InstallerHelper.exe", definition, StringComparison.Ordinal);
        Assert.Contains("--installer-shutdown", definition, StringComparison.Ordinal);
        Assert.Contains("unins000.exe", definition, StringComparison.Ordinal);
        Assert.Contains("ExecAsOriginalUser(LegacyUninstaller", definition, StringComparison.Ordinal);
        Assert.Contains("DelTree(LegacyPath", definition, StringComparison.Ordinal);
        Assert.Contains("--support-refresh", definition, StringComparison.Ordinal);
        Assert.Contains("ewWaitUntilTerminated, ResultCode", definition, StringComparison.Ordinal);
        Assert.Contains("CustomMessage('SupportRefreshFailed')", definition, StringComparison.Ordinal);
    }

    private static string ProjectFile(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. segments]);
    }

    [Fact]
    public void PrivateInstallerImportsItsEmbeddedProfileBeforeTheNormalLaunchEvenWhenSilent()
    {
        string definition = File.ReadAllText(ProjectFile("installer", "RemoteDebugger.iss"));
        Assert.Contains("Source: \"{#RelayProfilePath}\"; DestName: \"RemoteDebugger-Internet.rdrelay\"; Flags: dontcopy", definition);
        Assert.Contains("if CurStep = ssPostInstall then", definition);
        Assert.Contains("cli internet-import --file", definition);
        Assert.Contains("ExecAsOriginalUser", definition, StringComparison.Ordinal);
        Assert.Contains("SW_HIDE, ewWaitUntilTerminated, ResultCode", definition);
        Assert.Contains("if ResultCode <> 0 then", definition);
        Assert.Contains("finally\n      DeleteFile(ProfilePath);", definition.Replace("\r\n", "\n"));
        Assert.DoesNotContain("WizardSilent", definition);
    }

    [Fact]
    public void InstallerUsesWindowsUiLanguageWithEnglishFirstAsFallback()
    {
        string definition = File.ReadAllText(ProjectFile("installer", "RemoteDebugger.iss"));
        Assert.Contains("LanguageDetectionMethod=uilanguage", definition);
        Assert.Contains("ShowLanguageDialog=no", definition);
        Assert.Contains("UsePreviousLanguage=no", definition);
        Assert.Contains("compiler:Languages\\French.isl", definition);
        Assert.Contains("compiler:Languages\\Spanish.isl", definition);
        Assert.True(definition.IndexOf("Name: \"english\"", StringComparison.Ordinal) < definition.IndexOf("Name: \"french\"", StringComparison.Ordinal));
        Assert.Contains("{cm:LaunchProgram,Remote Debugger}", definition);
    }
}
