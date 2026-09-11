using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Xunit;

namespace RemoteDebugger.Core.Tests;

public sealed class IconAssetTests
{
    [Fact]
    public void WindowsIconContainsRequiredShellSizes()
    {
        byte[] icon = File.ReadAllBytes(AssetPath("RemoteDebugger.ico"));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(icon));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(icon.AsSpan(2)));

        int count = BinaryPrimitives.ReadUInt16LittleEndian(icon.AsSpan(4));
        var sizes = Enumerable.Range(0, count)
            .Select(index => icon[6 + index * 16] == 0 ? 256 : icon[6 + index * 16])
            .ToHashSet();

        Assert.Subset(sizes, new HashSet<int> { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 });
    }

    [Fact]
    public void SourcePngIsSquareRgbaArtwork()
    {
        byte[] png = File.ReadAllBytes(AssetPath("RemoteDebugger.png"));
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        int width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16));
        int height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20));

        Assert.Equal(width, height);
        Assert.True(width >= 1024);
        Assert.Equal(6, png[25]); // RGBA color type.
    }

    private static string AssetPath(string file, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "src", "RemoteDebugger", "Assets", file));
}
