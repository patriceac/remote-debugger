using RemoteDebugger.Core;
using Xunit;

public sealed class RpcConnectionPoolTests
{
    private static Request Request(string operation) => new(Guid.NewGuid().ToString(), "token", operation, Json.Element(new { }));

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public async Task ReusesOnlyNegotiatedConnections(bool keepAlive, int expectedOpens)
    {
        var streams = new List<ReplyStream>();
        var pool = new RpcConnectionPool(_ => { var stream = new ReplyStream(keepAlive); streams.Add(stream); return Task.FromResult<Stream>(stream); });
        await pool.CallAsync(Request("status"), default);
        await pool.CallAsync(Request("files"), default);
        Assert.Equal(expectedOpens, streams.Count);
        Assert.Equal(new[] { "status", "files" }, streams.SelectMany(s => s.Operations));
        await pool.ClearAsync();
        Assert.All(streams, s => Assert.True(s.Disposed));
    }

    [Fact]
    public async Task ConcurrentCommandsUseExclusiveConnectionsAndFailuresAreNotReplayed()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new ReplyStream(true) { ReadGate = blocked.Task };
        var second = new ReplyStream(true);
        int opens = 0;
        var pool = new RpcConnectionPool(_ => Task.FromResult<Stream>(++opens == 1 ? first : second));
        var longCommand = pool.CallAsync(Request("execute"), default);
        await pool.CallAsync(Request("cancel"), default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(longCommand.IsCompleted);
        blocked.SetResult(); await longCommand;
        second.FailRead = true;
        await Assert.ThrowsAsync<IOException>(() => pool.CallAsync(Request("mutation"), default));
        Assert.True(second.Disposed);
        Assert.Equal(2, opens);
        Assert.Equal(new[] { "cancel", "mutation" }, second.Operations);
        await pool.ClearAsync();
    }

    [Fact]
    public async Task ChangedRouteOrGrantDiscardsCachedConnection()
    {
        var streams = new List<ReplyStream>();
        var pool = new RpcConnectionPool(_ => { var stream = new ReplyStream(true); streams.Add(stream); return Task.FromResult<Stream>(stream); });
        await pool.CallAsync(Request("status"), default, "old grant");
        await pool.CallAsync(Request("status"), default, "new grant");
        Assert.Equal(2, streams.Count);
        Assert.True(streams[0].Disposed);
        await pool.ClearAsync();
    }

    private sealed class ReplyStream(bool keepAlive) : MemoryStream
    {
        private MemoryStream reply = new();
        public List<string> Operations { get; } = [];
        public Task ReadGate { get; init; } = Task.CompletedTask;
        public bool FailRead { get; set; }
        public bool Disposed { get; private set; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            using var input = new MemoryStream(buffer.ToArray());
            var request = await Wire.ReadAsync<Request>(input, ct);
            Assert.True(request.KeepAlive);
            Operations.Add(request.Operation);
            reply.Dispose(); reply = new MemoryStream();
            await Wire.WriteAsync(reply, Reply.Success(request.Id, new { }) with { KeepAlive = keepAlive }, ct);
            reply.Position = 0;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await ReadGate.WaitAsync(ct);
            if (FailRead) throw new IOException("Connection dropped.");
            return await reply.ReadAsync(buffer, ct);
        }
        protected override void Dispose(bool disposing) { Disposed = true; if (disposing) reply.Dispose(); base.Dispose(disposing); }
    }
}
