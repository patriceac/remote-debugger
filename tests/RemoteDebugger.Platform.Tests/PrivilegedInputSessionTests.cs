using System.Buffers.Binary;
using System.Text.Json;
using RemoteDebugger;
using RemoteDebugger.Core;
using Xunit;

public sealed class PrivilegedInputSessionTests
{
    [Fact]
    public async Task CursorStateReturnsThroughThePrivilegedHelperWithoutDroppingItsPayload()
    {
        using var stream = new BrokerStream();
        var input = new PrivilegedInputSession(_ => Task.FromResult<Stream>(stream), () => { });
        var reply = await input.SendAsync(new { kind = "pointer" }, () => true, default);
        Assert.Equal(123, reply.GetProperty("cursor").Int("x"));
        Assert.Equal("Remote PC", reply.GetProperty("cursor").Str("name"));
        input.End();
    }
    [Fact]
    public async Task StartupPreparationAndLaterSessionsReuseOneAuthenticatedHelper()
    {
        using var stream = new BrokerStream();
        int opens = 0, localReleases = 0;
        var input = new PrivilegedInputSession(_ => { opens++; return Task.FromResult<Stream>(stream); }, () => localReleases++);
        await Task.WhenAll(input.PrepareAsync(() => true, default), input.PrepareAsync(() => true, default));
        Assert.True(input.Ready);
        await input.SendAsync(new { kind = "keyDown", virtualKey = 65 }, () => true, default);
        await input.ReleaseAsync();
        await input.PrepareAsync(() => true, default);
        await input.SendAsync(new { kind = "keyDown", virtualKey = 66 }, () => true, default);

        Assert.Equal(1, opens);
        Assert.Equal(1, localReleases);
        Assert.Equal(["input.open", "ui.input", "ui.input", "ui.input", "ui.input"], stream.Requests.Select(r => r.Operation));
        Assert.Equal(["release", "keyDown", "release", "keyDown"], stream.Requests.Skip(1).Select(r => r.Args.Str("kind")));
        Assert.False(stream.Closed);
        input.End();
        Assert.True(stream.Closed);
        Assert.False(input.Ready);
    }

    [Fact]
    public async Task ShutdownDuringPreparationCannotResurrectTheHelper()
    {
        using var stream = new BrokerStream();
        var opening = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = new PrivilegedInputSession(_ => opening.Task, () => { });
        Task preparing = input.PrepareAsync(() => true, default);
        input.End();
        opening.SetResult(stream);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing);
        Assert.True(stream.Closed);
        Assert.False(input.Ready);
        Assert.DoesNotContain(stream.Requests, r => r.Operation == "ui.input");
    }

    [Fact]
    public async Task QueuedReleaseCannotReopenTheHelperAfterShutdown()
    {
        using var stream = new BrokerStream();
        int opens = 0;
        var input = new PrivilegedInputSession(_ => { opens++; return Task.FromResult<Stream>(stream); }, () => { });
        await input.PrepareAsync(() => true, default);
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stream.WriteBarrier = barrier.Task;
        Task sending = input.SendAsync(new { kind = "release" }, () => true, default);
        Task releasing = input.ReleaseAsync();
        input.End();
        barrier.SetResult();
        await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => sending);
        await releasing;
        Assert.Equal(1, opens);
        Assert.False(input.Ready);
    }

    [Fact]
    public async Task RevokedPermissionPreventsInputOnAnAlreadyPreparedHelper()
    {
        using var stream = new BrokerStream();
        var input = new PrivilegedInputSession(_ => Task.FromResult<Stream>(stream), () => { });
        await input.PrepareAsync(() => true, default);
        await Assert.ThrowsAsync<InputBlockedException>(() => input.SendAsync(new { kind = "keyDown", virtualKey = 65 }, () => false, default));
        Assert.DoesNotContain(stream.Requests, r => r.Args.Str("kind") == "keyDown");
        Assert.True(stream.Closed);
        Assert.False(input.Ready);
    }

    private sealed class BrokerStream : MemoryStream
    {
        public List<Request> Requests { get; } = [];
        public bool Closed { get; private set; }
        public Task? WriteBarrier { get; set; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (WriteBarrier is { } barrier) await barrier.WaitAsync(cancellationToken);
            ObjectDisposedException.ThrowIf(Closed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var request = JsonSerializer.Deserialize<Request>(buffer.Span[4..], Json.Options)!;
            Requests.Add(request);
            byte[] reply = JsonSerializer.SerializeToUtf8Bytes(Reply.Success(request.Id, new { ready = true, cursor = new { x = 123, y = 45, name = "Remote PC" } }), Json.Options);
            SetLength(0); Position = 0;
            byte[] prefix = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(prefix, reply.Length);
            Write(prefix); Write(reply); Position = 0;
        }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }
}
