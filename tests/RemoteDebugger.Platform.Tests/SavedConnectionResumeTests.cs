using System.Buffers.Binary;
using System.Text.Json;
using RemoteDebugger;
using RemoteDebugger.Core;
using Xunit;

public sealed class SavedConnectionResumeTests
{
    [Fact]
    public async Task MatchingSavedPrivateGrantResumesTheExistingSession()
    {
        string root = NewRoot();
        try
        {
            string fingerprint = new('a', 64), token = new('b', 64);
            var saved = Target(fingerprint, token);
            SaveConnection(root, saved);
            var stream = new ReplyStream(request => Reply.Success(request.Id, new { session = new { connected = true } }));
            var calls = new List<Connection>();
            var client = CreateClient(root, Target(fingerprint), (route, _) =>
            {
                calls.Add(route);
                return Task.FromResult<Stream>(stream);
            });

            Assert.True(await client.TryResumeSavedConnectionAsync(CancellationToken.None));
            Assert.Equal(token, client.Connection.Token);
            Assert.Equal(saved with { Token = token }, calls.Single());
            var request = Assert.Single(stream.Requests);
            Assert.Equal("session.heartbeat", request.Operation);
            Assert.Equal(token, request.Token);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SavedGrantForAnotherComputerIsIgnoredWithoutOpeningTransport()
    {
        string root = NewRoot();
        try
        {
            SaveConnection(root, Target(new('b', 64), new('c', 64)));
            int opens = 0;
            var client = CreateClient(root, Target(new('a', 64)), (route, _) =>
            {
                opens++;
                throw new InvalidOperationException("A mismatched identity must not open transport.");
            });

            Assert.False(await client.TryResumeSavedConnectionAsync(CancellationToken.None));
            Assert.Equal(0, opens);
            Assert.Empty(client.Connection.Token);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ExpiredSavedGrantFallsBackWithoutRetainingItsToken()
    {
        string root = NewRoot();
        try
        {
            string fingerprint = new('a', 64), token = new('b', 64);
            SaveConnection(root, Target(fingerprint, token));
            var stream = new ReplyStream(request => Reply.Failure(request.Id, "access_denied", "The support session has ended."));
            var client = CreateClient(root, Target(fingerprint), (_, _) => Task.FromResult<Stream>(stream));

            Assert.False(await client.TryResumeSavedConnectionAsync(CancellationToken.None));
            Assert.Empty(client.Connection.Token);
            Assert.Equal(token, Assert.Single(stream.Requests).Token);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("transport")]
    [InlineData("auth")]
    public async Task ResumeFailuresPropagateAndRestoreTheOriginalConnection(string failure)
    {
        string root = NewRoot();
        try
        {
            string fingerprint = new('a', 64), token = new('b', 64);
            SaveConnection(root, Target(fingerprint, token));
            int opens = 0;
            var client = CreateClient(root, Target(fingerprint), (route, ct) =>
            {
                opens++;
                if (failure == "cancel") return Task.FromCanceled<Stream>(ct);
                if (failure == "transport") throw new IOException("scripted transport failure");
                return Task.FromResult<Stream>(new ReplyStream(request => Reply.Failure(request.Id, "authentication_failed", "scripted authentication failure.")));
            });
            using var cancellation = new CancellationTokenSource();
            if (failure == "cancel") cancellation.Cancel();

            if (failure == "cancel")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.TryResumeSavedConnectionAsync(cancellation.Token));
            else if (failure == "transport")
                await Assert.ThrowsAsync<IOException>(() => client.TryResumeSavedConnectionAsync(CancellationToken.None));
            else
                await Assert.ThrowsAsync<RemoteOperationException>(() => client.TryResumeSavedConnectionAsync(CancellationToken.None));

            Assert.Equal(1, opens);
            Assert.Empty(client.Connection.Token);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static RemoteClient CreateClient(string root, Connection target, Func<Connection, CancellationToken, Task<Stream>> factory) =>
        new(target, factory) { AdminRoot = root };

    private static Connection Target(string fingerprint, string token = "") =>
        new("RD-0123-4567-89AB-CDEF", 443, fingerprint, token, "https://relay.example", new('d', 64));

    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "RemoteDebugger-Resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void SaveConnection(string root, Connection connection) =>
        Vault.Save(Path.Combine(root, "controller.connection"), JsonSerializer.SerializeToUtf8Bytes(connection, Json.Options));

    private sealed class ReplyStream(Func<Request, Reply> replyFactory) : Stream
    {
        private readonly List<byte> pending = [];
        private readonly List<byte> incoming = [];
        public List<Request> Requests { get; } = [];
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int count = Math.Min(buffer.Length, incoming.Count);
            if (count == 0) return ValueTask.FromResult(0);
            incoming.Take(count).ToArray().CopyTo(buffer);
            incoming.RemoveRange(0, count);
            return ValueTask.FromResult(count);
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            pending.AddRange(buffer.ToArray());
            while (pending.Count >= 4)
            {
                int size = BinaryPrimitives.ReadInt32BigEndian(pending.Take(4).ToArray());
                if (size < 1 || pending.Count < size + 4) break;
                byte[] body = pending.Skip(4).Take(size).ToArray();
                pending.RemoveRange(0, size + 4);
                var request = JsonSerializer.Deserialize<Request>(body, Json.Options) ?? throw new InvalidDataException("Missing scripted request.");
                Requests.Add(request);
                QueueReply(replyFactory(request));
            }
            return ValueTask.CompletedTask;
        }
        private void QueueReply(Reply reply)
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(reply, Json.Options);
            byte[] frame = new byte[4 + body.Length];
            BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
            body.CopyTo(frame, 4);
            incoming.AddRange(frame);
        }
    }
}
