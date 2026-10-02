using RemoteDebugger;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class DeviceCursorNamesTests
{
    [Fact]
    public void NicknamesPersistPerIdentityAndClearingRestoresTheWindowsName()
    {
        string root = Path.GetFullPath(Path.Combine("work", "cursor-names-" + Guid.NewGuid().ToString("N")));
        string first = new('c', 64), second = new('d', 64);
        var names = new DeviceCursorNames(root);
        var cursor = new CursorPosition(120, 80, "Yolande", true, 3, 2);
        Assert.Same(cursor, names.Apply(first, cursor));
        names.Save(first, "  Lolo  "); names.Save(second, "Bedo");
        var reopened = new DeviceCursorNames(root);
        Assert.Equal(cursor with { Name = "Lolo" }, reopened.Apply(first.ToUpperInvariant(), cursor));
        Assert.Equal("Bedo", reopened.Apply(second, cursor)!.Name);
        Assert.Same(cursor, reopened.Apply(new string('e', 64), cursor));
        Assert.Null(reopened.Apply(first, null));
        Assert.Throws<ArgumentException>(() => reopened.Save("", "Wrong PC"));
        Assert.Throws<ArgumentException>(() => reopened.Save(first, "Two\nlines"));
        reopened.Save(first, "  ");
        var cleared = new DeviceCursorNames(root);
        Assert.Same(cursor, cleared.Apply(first, cursor));
        Assert.Equal("Bedo", cleared.Get(second));
    }
}
