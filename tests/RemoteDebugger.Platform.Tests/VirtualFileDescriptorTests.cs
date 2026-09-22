using System.Text;
using System.Runtime.InteropServices;
using RemoteDebugger;
using RemoteDebugger.Core;
using Xunit;

public sealed class VirtualFileDescriptorTests
{
    [Fact]
    public void ExplorerReceivesUnicodeNestedNamesDirectoryFlagsAnd64BitSizes()
    {
        Assert.True(Marshal.IsTypeVisibleFromCom(typeof(VirtualFileDrop.IDropSource)));
        Assert.True(Marshal.IsTypeVisibleFromCom(typeof(VirtualFileDrop.IDataObjectAsyncCapability)));
        var date = DateTimeOffset.UtcNow;
        byte[] bytes = VirtualFileDrop.DescriptorBytes([
            new("C:\\source", "dossier", true, 0, date),
            new("C:\\source\\café.bin", "dossier\\café.bin", false, 5L * 1024 * 1024 * 1024, date)]);
        Assert.Equal(4 + 2 * 592, bytes.Length);
        Assert.Equal(2, BitConverter.ToInt32(bytes, 0));
        Assert.Equal(0x10u, BitConverter.ToUInt32(bytes, 4 + 36));
        int file = 4 + 592;
        Assert.Equal(1u, BitConverter.ToUInt32(bytes, file + 64));
        Assert.Equal(1024u * 1024 * 1024, BitConverter.ToUInt32(bytes, file + 68));
        Assert.Equal("dossier\\café.bin", Encoding.Unicode.GetString(bytes, file + 72, 520).TrimEnd('\0'));
    }
}
