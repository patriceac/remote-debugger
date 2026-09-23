using RemoteDebugger;
using Xunit;

public sealed class DesktopDropTests
{
    [Fact]
    public void DesktopPlacementKeepsOnlyTheCopiedTopLevelItems()
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Assert.Equal(new[] { Path.Combine(desktop, "report.txt"), Path.Combine(desktop, "Folder") },
            ExplorerFiles.DesktopItems(desktop, ["report.txt", "Folder", "REPORT.txt"]));
        Assert.Throws<ArgumentException>(() => ExplorerFiles.DesktopItems(Path.GetTempPath(), ["report.txt"]));
        Assert.Throws<ArgumentException>(() => ExplorerFiles.DesktopItems(desktop, ["Folder/child.txt"]));
        Assert.Throws<ArgumentException>(() => ExplorerFiles.DesktopItems(desktop, ["../report.txt"]));
    }
}
