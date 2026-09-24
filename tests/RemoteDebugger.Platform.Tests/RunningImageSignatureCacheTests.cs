using RemoteDebugger;
using Xunit;

public sealed class RunningImageSignatureCacheTests : IDisposable
{
    private readonly string directory = Directory.CreateDirectory(Path.GetFullPath(Path.Combine("work", "signature-cache-" + Guid.NewGuid().ToString("N")))).FullName;
    private string Image => Path.Combine(directory, "image.exe");
    private static AuthenticodeSignatureInfo Valid => new("publisher", "test", true, true, 0)
    { ValidFromUtc = DateTime.UtcNow.AddDays(-1), ValidUntilUtc = DateTime.UtcNow.AddDays(1) };

    [Fact]
    public async Task ConcurrentPipesVerifyOnceButInPlaceChangeWithRestoredWriteTimeRequiresVerification()
    {
        File.WriteAllText(Image, "image");
        int checks = 0;
        var cache = new RunningImageSignatureCache((_, _) => { checks++; return Valid; });
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => cache.Get(Image, "publisher", (42, 100L)))));
        Assert.Equal(1, checks);
        DateTime originalTime = File.GetLastWriteTimeUtc(Image);
        File.WriteAllText(Image, "other");
        File.SetLastWriteTimeUtc(Image, originalTime);
        cache.Get(Image, "publisher", (42, 100L));
        Assert.Equal(2, checks);
    }

    [Fact]
    public void ProcessRestartPinChangeAndSameSizeFileReplacementRequireNewVerification()
    {
        File.WriteAllText(Image, "image");
        DateTime originalTime = File.GetLastWriteTimeUtc(Image);
        int checks = 0;
        var cache = new RunningImageSignatureCache((_, _) => { checks++; return Valid; });
        cache.Get(Image, "publisher", (42, 100L));
        cache.Get(Image, "publisher", (42, 200L));
        cache.Get(Image, "new-publisher", (42, 200L));
        string replacement = Path.Combine(directory, "replacement.exe");
        File.WriteAllText(replacement, "other");
        File.SetLastWriteTimeUtc(replacement, originalTime);
        File.Move(replacement, Image, overwrite: true);
        cache.Get(Image, "new-publisher", (42, 200L));
        Assert.Equal(4, checks);
    }

    [Fact]
    public void FailedVerificationAndExpiredCertificatesAreNotReused()
    {
        File.WriteAllText(Image, "image");
        int attempts = 0;
        var cache = new RunningImageSignatureCache((_, _) =>
        {
            if (++attempts <= 2) throw new UnauthorizedAccessException("bad signature");
            return Valid with { ValidUntilUtc = DateTime.UtcNow.AddSeconds(-1) };
        });
        Assert.Throws<UnauthorizedAccessException>(() => cache.Get(Image, "publisher", 42));
        Assert.Throws<UnauthorizedAccessException>(() => cache.Get(Image, "publisher", 42));
        cache.Get(Image, "publisher", 42);
        cache.Get(Image, "publisher", 42);
        Assert.Equal(4, attempts);
    }

    public void Dispose()
    {
        File.Delete(Image);
        File.Delete(Path.Combine(directory, "replacement.exe"));
        Directory.Delete(directory);
    }
}
