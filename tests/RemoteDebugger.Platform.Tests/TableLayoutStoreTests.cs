using RemoteDebugger;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class TableLayoutStoreTests
{
    private static readonly TableColumnLayout[] Defaults = [new("name", 160, 0), new("size", 100, 1), new("date", 180, 2)];

    [Fact]
    public void RestoresWidthsAndOrderByIdentityWhenColumnsAreAddedOrRemoved()
    {
        var restored = TableLayoutStore.Normalize([new("size", 137.5, 0), new("removed", 99, 1), new("name", 240, 2)], Defaults);
        Assert.Equal(new TableColumnLayout("name", 240, 1), restored[0]);
        Assert.Equal(new TableColumnLayout("size", 137.5, 0), restored[1]);
        Assert.Equal(new TableColumnLayout("date", 180, 2), restored[2]);
    }

    [Fact]
    public void InvalidAndDuplicateEntriesCannotHideColumnsOrBreakOrdering()
    {
        var restored = TableLayoutStore.Normalize([null, new(null!, 12, 0), new("size", -5, -8), new("size", 800, 0), new("date", double.NaN, 0)], Defaults);
        Assert.Equal(100, restored[1].Width);
        Assert.Equal(180, restored[2].Width);
        Assert.Equal(new[] { 0, 1, 2 }, restored.Select(column => column.Order).Order());
    }

    [Fact]
    public void SavesIndependentTablePreferencesAndRecoversFromCorruptJson()
    {
        string root = Path.Combine(Path.GetTempPath(), "rd-table-test-" + Guid.NewGuid());
        try
        {
            TableLayoutStore.Save(root, "processes", [new("name", 255.5, 2), new("size", 125, 0), new("date", 190, 1)]);
            TableLayoutStore.Save(root, "files", Defaults);
            Assert.Equal(255.5, TableLayoutStore.Load(root, "processes", Defaults)[0].Width);
            Assert.Equal(2, TableLayoutStore.Load(root, "processes", Defaults)[0].Order);
            Assert.Equal(Defaults, TableLayoutStore.Load(root, "files", Defaults));
            File.WriteAllText(TableLayoutStore.PathFor(root, "files"), "{broken");
            Assert.Equal(Defaults, TableLayoutStore.Load(root, "files", Defaults));
            Assert.Equal(Defaults, TableLayoutStore.Load(root, "missing", Defaults));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
