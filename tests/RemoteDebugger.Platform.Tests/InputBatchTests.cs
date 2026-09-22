using System.Buffers.Binary;
using System.Text.Json;
using System.Threading.Channels;
using RemoteDebugger;
using RemoteDebugger.Core;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class InputBatchTests
{
    [Fact]
    public async Task PersistentInputWritesAgainBeforeFirstReplyAndPreservesReplyOrder()
    {
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new DelayedInputStream(async request =>
        {
            if (request.Operation == "ui.input.open") return Reply.Success(request.Id, new { inputBatches = true });
            if (request.Args.Int("x") == 1) await releaseFirst.Task;
            return Reply.Success(request.Id, new { sequence = request.Args.Int("x") });
        });
        var client = NewClient(stream);

        Task<Reply> first = client.SendInputAsync(new { kind = "move", x = 1, y = 1 });
        await stream.FirstInputWrite.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<Reply> second = client.SendInputAsync(new { kind = "move", x = 2, y = 2 });
        await stream.SecondInputWrite.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(first.IsCompleted);
        Assert.Equal(["ui.input.open", "ui.input", "ui.input"], stream.Requests.Select(request => request.Operation));
        releaseFirst.SetResult();

        Reply[] replies = await Task.WhenAll(first, second);
        Assert.Equal(1, replies[0].Data.Int("sequence"));
        Assert.Equal(2, replies[1].Data.Int("sequence"));
    }

    [Fact]
    public async Task BatchFallsBackToOrderedIndividualEventsWhenCapabilityIsMissing()
    {
        var stream = new DelayedInputStream(request =>
            Task.FromResult(request.Operation == "ui.input.open"
                ? Reply.Success(request.Id, new { inputBatches = false })
                : Reply.Success(request.Id, new { sequence = request.Args.Int("sequence") })));
        var client = NewClient(stream);
        object batch = new
        {
            kind = "batch",
            events = new object[]
            {
                new { kind = "keyDown", virtualKey = 65, sequence = 1 },
                new { kind = "keyUp", virtualKey = 65, sequence = 2 }
            }
        };

        Assert.True((await client.SendInputAsync(batch)).Ok);
        Assert.Equal(["ui.input.open", "ui.input", "ui.input"], stream.Requests.Select(request => request.Operation));
        Assert.Equal(["keyDown", "keyUp"], stream.Requests.Skip(1).Select(request => request.Args.Str("kind")));
        Assert.Equal([1, 2], stream.Requests.Skip(1).Select(request => request.Args.Int("sequence")));
    }

    [Fact]
    public async Task PersistentInputWindowBlocksFifthWriteUntilFirstReply()
    {
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new DelayedInputStream(async request =>
        {
            if (request.Operation == "ui.input.open") return Reply.Success(request.Id, new { inputBatches = true });
            if (request.Args.Int("x") == 1) await releaseFirst.Task;
            return Reply.Success(request.Id, new { sequence = request.Args.Int("x") });
        });
        var client = NewClient(stream);

        Task<Reply>[] calls = Enumerable.Range(1, 5)
            .Select(x => client.SendInputAsync(new { kind = "move", x, y = x }))
            .ToArray();

        await stream.FourthInputWrite.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(stream.FifthInputWrite.Task.IsCompleted);

        releaseFirst.SetResult();
        await stream.FifthInputWrite.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Reply[] replies = await Task.WhenAll(calls);

        Assert.Equal([1, 2, 3, 4, 5], replies.Select(reply => reply.Data.Int("sequence")));
        Assert.Equal(5, stream.Requests.Count(request => request.Operation == "ui.input"));
    }

    private static RemoteClient NewClient(Stream stream) =>
        new(new Connection("127.0.0.1", 45832, new string('a', 64), "token"), (_, _) => Task.FromResult(stream));

    private sealed class DelayedInputStream(Func<Request, Task<Reply>> replyFactory) : Stream
    {
        private readonly Channel<byte[]> replies = Channel.CreateUnbounded<byte[]>();
        private readonly object sync = new();
        private Task replyTail = Task.CompletedTask;
        private byte[]? current;
        private int currentOffset;
        private int inputWrites;
        public List<Request> Requests { get; } = [];
        public TaskCompletionSource FirstInputWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondInputWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FourthInputWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FifthInputWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (current == null || currentOffset == current.Length)
            {
                current = await replies.Reader.ReadAsync(cancellationToken);
                currentOffset = 0;
            }
            int count = Math.Min(buffer.Length, current.Length - currentOffset);
            current.AsMemory(currentOffset, count).CopyTo(buffer);
            currentOffset += count;
            return count;
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int size = BinaryPrimitives.ReadInt32BigEndian(buffer.Span[..4]);
            Request request = JsonSerializer.Deserialize<Request>(buffer.Span.Slice(4, size), Json.Options)!;
            lock (sync) Requests.Add(request);
            if (request.Operation == "ui.input")
            {
                int write = Interlocked.Increment(ref inputWrites);
                if (write == 1) FirstInputWrite.TrySetResult();
                if (write == 2) SecondInputWrite.TrySetResult();
                if (write == 4) FourthInputWrite.TrySetResult();
                if (write == 5) FifthInputWrite.TrySetResult();
            }
            Task previous = replyTail;
            replyTail = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                Reply reply = await replyFactory(request).ConfigureAwait(false);
                byte[] body = JsonSerializer.SerializeToUtf8Bytes(reply, Json.Options);
                byte[] frame = new byte[4 + body.Length];
                BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
                body.CopyTo(frame, 4);
                await replies.Writer.WriteAsync(frame).ConfigureAwait(false);
            });
            return ValueTask.CompletedTask;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) replies.Writer.TryComplete();
            base.Dispose(disposing);
        }
    }
}
