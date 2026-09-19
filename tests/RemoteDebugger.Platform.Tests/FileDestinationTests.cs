using System.Security.Cryptography;
using RemoteDebugger;
using RemoteDebugger.Core;
using Xunit;

public sealed class FileDestinationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadCommitsToTheChosenFolderAndRemainsBrowsable(bool absolute)
    {
        string root = Path.Combine(Path.GetTempPath(), "rd-files-" + Guid.NewGuid().ToString("N"));
        try
        {
            var operations = new Operations(Path.Combine(root, "agent"));
            string folder = absolute ? Path.Combine(root, "chosen folder") : Path.Combine(operations.Workspace, "chosen folder");
            string target = Path.Combine(folder, "sample.txt");
            string requested = absolute ? target : "chosen folder/sample.txt";
            byte[] bytes = "remote folder transfer"u8.ToArray();
            string transfer = Guid.NewGuid().ToString("N");
            await operations.ExecuteAsync("upload.begin", Json.Element(new { transfer, path = requested, size = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)) }), default);
            await operations.ExecuteAsync("upload.chunk", Json.Element(new { transfer, offset = 0, data = Convert.ToBase64String(bytes) }), default);
            var result = Assert.IsType<Operations.BinaryInfo>(await operations.ExecuteAsync("upload.commit", Json.Element(new { transfer }), default));
            Assert.Equal(target, result.Path);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(target));
            var listing = Json.Element(await operations.ExecuteAsync("files", Json.Element(new { path = folder }), default));
            Assert.Equal(target, listing[0].Str("path"));
            await Assert.ThrowsAsync<ArgumentException>(() => operations.ExecuteAsync("upload.begin", Json.Element(new { transfer = Guid.NewGuid().ToString("N"), path = "../escape.txt", size = 0, sha256 = new string('0', 64) }), default));
            await Assert.ThrowsAsync<ArgumentException>(() => operations.ExecuteAsync("upload.begin", Json.Element(new { transfer = Guid.NewGuid().ToString("N"), path = target + ":stream", size = 0, sha256 = new string('0', 64) }), default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
