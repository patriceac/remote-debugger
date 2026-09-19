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
            using var uploadChannel = new TransferChannel(new MemoryStream(bytes));
            await operations.TransferAsync(uploadChannel, new Request("upload", "", "file.upload", Json.Element(new { transfer })), () =>
            {
                Assert.True(operations.FileTransfer!.Receiving);
                Assert.Equal(bytes.Length, operations.FileTransfer.TransferredBytes);
                Assert.Equal("transferring", operations.FileTransfer.Stage);
            }, default);
            Assert.Equal("complete", operations.FileTransfer!.Stage);
            Assert.Equal(target, operations.FileTransfer.Path);
            var result = Assert.IsType<Operations.BinaryInfo>(await operations.ExecuteAsync("file.info", Json.Element(new { path = target }), default));
            Assert.Equal(target, result.Path);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(target));
            var listing = Json.Element(await operations.ExecuteAsync("files", Json.Element(new { path = folder }), default));
            Assert.Equal(target, listing[0].Str("path"));
            using var acknowledgements = new MemoryStream();
            await Wire.WriteAsync(acknowledgements, (long)bytes.Length, default); acknowledgements.Position = 0;
            using var downloadChannel = new TransferChannel(acknowledgements);
            await operations.TransferAsync(downloadChannel, new Request("download", "", "file.download", Json.Element(new { path = target })), () => { }, default);
            Assert.False(operations.FileTransfer!.Receiving);
            Assert.Equal("complete", operations.FileTransfer.Stage);
            Assert.Equal(bytes.Length, operations.FileTransfer.BytesThisAttempt);
            await Assert.ThrowsAsync<ArgumentException>(() => operations.ExecuteAsync("upload.begin", Json.Element(new { transfer = Guid.NewGuid().ToString("N"), path = "../escape.txt", size = 0, sha256 = new string('0', 64) }), default));
            await Assert.ThrowsAsync<ArgumentException>(() => operations.ExecuteAsync("upload.begin", Json.Element(new { transfer = Guid.NewGuid().ToString("N"), path = target + ":stream", size = 0, sha256 = new string('0', 64) }), default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class TransferChannel(Stream input) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => input.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => ValueTask.CompletedTask;
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
    }
}
