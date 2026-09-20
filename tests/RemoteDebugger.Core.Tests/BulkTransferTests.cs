using RemoteDebugger.Core;
using Xunit;

public sealed class BulkTransferTests
{
    [Fact]
    public async Task BinaryWindowsPreserveBytesAndAcknowledgeResumedOffsets()
    {
        byte[] bytes = new byte[BulkTransfer.WindowSize * 2 + 37];
        new Random(42).NextBytes(bytes);
        const int offset = 123;
        using var incoming = new MemoryStream(bytes[offset..]);
        using var acknowledgements = new MemoryStream();
        using var destination = new MemoryStream();
        destination.Write(bytes, 0, offset);
        using var receiver = new DuplexStream(incoming, acknowledgements);
        await BulkTransfer.ReceiveAsync(receiver, destination, offset, bytes.Length, null, CancellationToken.None);
        Assert.Equal(bytes, destination.ToArray());
        acknowledgements.Position = 0;
        using var source = new MemoryStream(bytes); source.Position = offset;
        using var transmitted = new MemoryStream();
        using var sender = new DuplexStream(acknowledgements, transmitted);
        var progress = new List<long>();
        await BulkTransfer.SendAsync(source, sender, offset, bytes.Length, progress.Add, CancellationToken.None);
        Assert.Equal(bytes[offset..], transmitted.ToArray());
        Assert.Equal(2, progress.Count);
        Assert.Equal(bytes.Length, progress[^1]);
    }

    [Fact]
    public async Task SenderPipelinesButStopsAtItsBoundWhenAcknowledgementIsInvalid()
    {
        using var acknowledgements = new MemoryStream();
        await Wire.WriteAsync(acknowledgements, 0L, CancellationToken.None); acknowledgements.Position = 0;
        using var transmitted = new MemoryStream();
        var readGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var channel = new DuplexStream(acknowledgements, transmitted, readGate.Task);
        using var source = new MemoryStream(new byte[BulkTransfer.WindowSize * (BulkTransfer.WindowsInFlight + 1)]);
        var send = BulkTransfer.SendAsync(source, channel, 0, source.Length, null, CancellationToken.None);
        Assert.False(send.IsCompleted);
        Assert.Equal(BulkTransfer.WindowSize * BulkTransfer.WindowsInFlight, transmitted.Length);
        readGate.SetResult();
        await Assert.ThrowsAsync<IOException>(() => send.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(BulkTransfer.WindowSize * BulkTransfer.WindowsInFlight, transmitted.Length);
    }

    private sealed class DuplexStream(Stream input, Stream output, Task? readGate = null) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { if (readGate != null) await readGate.WaitAsync(ct); return await input.ReadAsync(buffer, ct); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => output.WriteAsync(buffer, ct);
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
