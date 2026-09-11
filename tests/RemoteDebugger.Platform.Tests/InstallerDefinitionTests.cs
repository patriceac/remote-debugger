using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class InstallerDefinitionTests
{
    [Fact]
    public void InstallerIsPerUserAndCreatesStartMenuShortcut()
    {
        string definition = File.ReadAllText(ProjectFile("installer", "RemoteDebugger.iss"));

        Assert.Contains("PrivilegesRequired=lowest", definition, StringComparison.Ordinal);
        Assert.Contains("DefaultDirName={localappdata}\\Programs\\Remote Debugger", definition, StringComparison.Ordinal);
        Assert.Contains("Name: \"{group}\\Remote Debugger\"", definition, StringComparison.Ordinal);
        Assert.Contains("UninstallDisplayIcon={app}\\{#AppExeName}", definition, StringComparison.Ordinal);
        Assert.DoesNotContain("runascurrentuser", definition, StringComparison.OrdinalIgnoreCase);
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

    private static string ProjectFile(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. segments]);
    }
}
