using RemoteDebugger.Core;
using Xunit;

public sealed class TransferTreeTests
{
    [Fact]
    public void FolderCopyIncludesItsRootNestedFilesAndEmptyDirectories()
    {
        string root = Path.Combine(Path.GetTempPath(), "rd-tree-" + Guid.NewGuid().ToString("N"));
        try
        {
            string folder = Path.Combine(root, "folder"); Directory.CreateDirectory(Path.Combine(folder, "empty"));
            Directory.CreateDirectory(Path.Combine(folder, "nested")); File.WriteAllText(Path.Combine(folder, "nested", "café.txt"), "abc");
            var entries = TransferTree.Read([folder]);
            Assert.Equal(4, entries.Length);
            Assert.Contains(entries, entry => entry.RelativePath == "folder" && entry.Directory);
            Assert.Contains(entries, entry => entry.RelativePath == Path.Combine("folder", "empty") && entry.Directory);
            Assert.Contains(entries, entry => entry.RelativePath == Path.Combine("folder", "nested", "café.txt") && !entry.Directory && entry.Size > 0);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("../escape")]
    [InlineData("folder/../../escape")]
    [InlineData("file:stream")]
    [InlineData("folder/NUL.txt")]
    [InlineData("folder/trailing.")]
    public void ExportedNamesCannotEscapeOrAddressWindowsDevices(string path) =>
        Assert.Throws<ArgumentException>(() => TransferTree.ValidateRelativePath(path));
}
