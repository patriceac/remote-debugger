using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using RemoteDebugger;
using RemoteDebugger.Core;
using Xunit;

public sealed class InternetTransportTests
{
    [Fact]
    public void InstallerProfileImportProtectsSettingsAndPreservesThemWhenAnImportIsInvalid()
    {
        string directory = Path.Combine(Path.GetTempPath(), "RemoteDebugger-ImportTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string profile = Path.Combine(directory, "setup.rdrelay"), settingsRoot = Path.Combine(directory, "settings");
            var expected = new InternetSettings("https://relay.example", new string('a', 64), new string('b', 64));
            File.WriteAllText(profile, JsonSerializer.Serialize(expected));
            InternetSettings.Import(profile, settingsRoot);
            Assert.Equal(expected, InternetSettings.Load(settingsRoot));
            Assert.DoesNotContain(expected.AccessKey, Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(settingsRoot, "internet.dpapi"))));
            Assert.DoesNotContain(expected.PairingKey, Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(settingsRoot, "internet.dpapi"))));
            File.WriteAllText(profile, JsonSerializer.Serialize(expected with { RelayUrl = "http://relay.example" }));
            Assert.Throws<ArgumentException>(() => InternetSettings.Import(profile, settingsRoot));
            Assert.Equal(expected, InternetSettings.Load(settingsRoot));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void PrivateAuthenticationRequiresTheInstallerSecretAndBindsItToEachInvitation()
    {
        var settings = new InternetSettings("https://relay.example", new string('a', 64), new string('b', 64));
        string first = settings.AuthenticationSecret("0123456789ABCDEF");
        Assert.Equal(first, settings.AuthenticationSecret("RD-0123-4567-89AB-CDEF"));
        Assert.NotEqual(first, settings.AuthenticationSecret("0123456789ABCDE0"));
        Assert.NotEqual(first, (settings with { PairingKey = new string('c', 64) }).AuthenticationSecret("0123456789ABCDEF"));
        Assert.Throws<ArgumentException>(() => (settings with { PairingKey = "" }).AuthenticationSecret("0123456789ABCDEF"));
    }

    [Theory]
    [InlineData("http://relay.example/")]
    [InlineData("https://relay.example/path")]
    [InlineData("https://user:password@relay.example/")]
    [InlineData("https://relay.example/?token=bad")]
    public void SettingsRejectInsecureOrAmbiguousOrigins(string url) =>
        Assert.Throws<ArgumentException>(() => new InternetSettings(url, new string('a', 64)).Validate());

    [Fact]
    public void SupportIdRoundTripsAndRejectsPaths()
    {
        Assert.Equal("0123456789ABCDEF", InternetSettings.SessionId("rd-0123-4567-89ab-cdef"));
        Assert.Equal("RD-0123-4567-89AB-CDEF", InternetSettings.DisplayId("0123456789ABCDEF"));
        Assert.Throws<ArgumentException>(() => InternetSettings.SessionId("RD-../../../agent"));
    }

    [Fact]
    public async Task EncryptedWritesAreBoundedAndByteExact()
    {
        var socket = new RecordingSocket(); using var stream = new WebSocketStream(socket);
        byte[] bytes = Enumerable.Range(0, 150000).Select(i => (byte)i).ToArray();
        await stream.WriteAsync(bytes.AsMemory());
        Assert.Equal(3, socket.Sent.Count);
        Assert.All(socket.Sent, block => Assert.InRange(block.Length, 1, 65536));
        Assert.Equal(bytes, socket.Sent.SelectMany(x => x).ToArray());
    }

    [Fact]
    public async Task StreamRejectsTextInjectedIntoEncryptedChannel()
    {
        using var stream = new WebSocketStream(new RecordingSocket { ReceivedType = WebSocketMessageType.Text });
        await Assert.ThrowsAsync<IOException>(() => stream.ReadAsync(new byte[32], 0, 32));
    }

    [Fact]
    public async Task StreamReturnsEofOnPeerClose()
    {
        using var stream = new WebSocketStream(new RecordingSocket { ReceivedType = WebSocketMessageType.Close });
        Assert.Equal(0, await stream.ReadAsync(new byte[32], 0, 32));
    }

    [Fact]
    public async Task AsyncDisposalWaitsForAnOrderedCloseBeforeDestroyingTheSocket()
    {
        var close = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var socket = new RecordingSocket { CloseCompletion = close.Task };
        var stream = new WebSocketStream(socket);
        Task disposal = stream.DisposeAsync().AsTask();
        Assert.False(socket.Disposed);
        close.SetResult(); await disposal;
        Assert.True(socket.Disposed);
    }

    [Fact]
    public async Task RelayDisconnectUsesTheExistingIoRecoveryPath()
    {
        using var stream = new WebSocketStream(new RecordingSocket { Disconnect = true });
        var failure = await Assert.ThrowsAsync<IOException>(() => stream.ReadAsync(new byte[32], 0, 32));
        Assert.IsType<WebSocketException>(failure.InnerException);
    }

    [Fact]
    public void LanTargetsDoNotRequireOrLoadInternetCredentials()
    {
        var target = InternetSettings.Target("192.168.1.20", 45832, "", "missing-test-root");
        Assert.Equal("", target.RelayUrl); Assert.Equal("", target.RelayAccessKey);
    }

    [Fact]
    public async Task PrivateDiscoveryFallsBackToLanWhenTheRelayFails()
    {
        var nearby = new Peer("Nearby", "192.168.1.20", 45832, new string('a', 64), "RD-0123-4567-89AB-CDEF");
        int lanCalls = 0;
        var result = await PeerDiscovery.FindAsync(
            privateInternet: true,
            lanDiscovery: _ => { lanCalls++; return Task.FromResult(new List<Peer> { nearby }); },
            relayDiscovery: _ => Task.FromException<List<Peer>>(new HttpRequestException("relay unavailable")));

        Assert.True(result.UsedLanFallback);
        Assert.Equal(1, lanCalls);
        Assert.Equal(new[] { nearby }, result.Peers);
    }

    [Fact]
    public async Task CliDiscoveryUsesLanWhenThePrivateRelayFails()
    {
        var nearby = new Peer("Nearby", "192.168.1.20", 45832, new string('a', 64), "RD-0123-4567-89AB-CDEF");
        int lanCalls = 0;
        var settings = new InternetSettings("https://relay.example", new string('a', 64), new string('b', 64));
        var result = await Program.DiscoverPeersAsync(
            settings,
            _ => { lanCalls++; return Task.FromResult(new List<Peer> { nearby }); },
            (_, _) => Task.FromException<List<Peer>>(new HttpRequestException("relay unavailable")));

        Assert.True(result.UsedLanFallback);
        Assert.Equal(1, lanCalls);
        Assert.Equal(new[] { nearby }, result.Peers);
    }

    [Fact]
    public async Task PrivateDiscoveryDoesNotProbeLanWhenTheRelaySucceeds()
    {
        bool lanCalled = false;
        var relayPeer = new Peer("Relay PC", "RD-0123-4567-89AB-CDEF", 443, "", "RD-0123-4567-89AB-CDEF");
        var result = await PeerDiscovery.FindAsync(
            privateInternet: true,
            lanDiscovery: _ => { lanCalled = true; return Task.FromResult(new List<Peer>()); },
            relayDiscovery: _ => Task.FromResult(new List<Peer> { relayPeer }));

        Assert.False(result.UsedLanFallback);
        Assert.False(lanCalled);
        Assert.Equal(new[] { relayPeer }, result.Peers);
    }

    [Fact]
    public async Task PrivateDiscoveryPreservesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PeerDiscovery.FindAsync(
            privateInternet: true,
            lanDiscovery: _ => Task.FromResult(new List<Peer>()),
            relayDiscovery: token => Task.FromException<List<Peer>>(new OperationCanceledException(token)),
            cancellation.Token));
    }

    [Fact]
    public void PeerSerializationPreservesThePrivateSupportIdentity()
    {
        var peer = new Peer("Nearby", "192.168.1.20", 45832, new string('a', 64), "RD-0123-4567-89AB-CDEF");
        var roundTrip = JsonSerializer.Deserialize<Peer>(JsonSerializer.SerializeToUtf8Bytes(peer, Json.Options), Json.Options);

        Assert.NotNull(roundTrip);
        Assert.Equal(peer, roundTrip);
    }

    [Fact]
    public async Task RelayPairedConnectionSwitchesToAnAuthenticatedDirectEndpoint()
    {
        string root = Path.Combine(Path.GetTempPath(), "RemoteDebugger-DirectRoute-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        AgentServer? server = null;
        try
        {
            int port = ReserveTcpPort();
            server = new AgentServer(root, port, loopbackOnly: true, enableInternet: false);
            server.Start();
            string code = server.Pairing.CurrentCode ?? throw new InvalidOperationException("Agent did not expose a pairing code.");
            var paired = await PairingTransport.PairAsync(new Connection("127.0.0.1", port, server.Fingerprint, ""), code, CancellationToken.None);
            var client = new RemoteClient(new Connection(
                "RD-0123-4567-89AB-CDEF",
                443,
                paired.Fingerprint,
                paired.Token,
                "https://relay.example",
                new string('a', 64)));

            Assert.True(await client.TryPreferDirectAsync([new DirectEndpoint("127.0.0.1", port)]));
            Assert.True(client.UsesDirectTransport);
            Assert.Equal("127.0.0.1", client.Connection.DirectHost);
            Assert.Equal(port, client.Connection.DirectPort);
            Assert.Equal("https://relay.example", client.Connection.RelayUrl);
            Assert.Equal(client.Connection, JsonSerializer.Deserialize<Connection>(
                JsonSerializer.SerializeToUtf8Bytes(client.Connection, Json.Options), Json.Options));
            Assert.True((await client.HeartbeatAsync()).GetProperty("session").GetProperty("connected").GetBoolean());
        }
        finally
        {
            server?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PersistentInputReusesOneAuthenticatedStreamAndPreservesRequestOrder()
    {
        var stream = new ScriptedInputStream();
        int opens = 0;
        var client = new RemoteClient(new Connection("127.0.0.1", 45832, new string('a', 64), "token"), (_, _) =>
        {
            opens++;
            return Task.FromResult<Stream>(stream);
        });

        Assert.True((await client.SendInputAsync(new { kind = "move", x = 10, y = 20 })).Ok);
        Assert.True((await client.SendInputAsync(new { kind = "button", button = "left", down = true })).Ok);

        Assert.Equal(1, opens);
        Assert.Collection(stream.Requests,
            open => Assert.Equal("ui.input.open", open.Operation),
            move => { Assert.Equal("ui.input", move.Operation); Assert.Equal("move", move.Args.Str("kind")); },
            button => { Assert.Equal("ui.input", button.Operation); Assert.Equal("button", button.Args.Str("kind")); });
    }

    [Fact]
    public async Task PersistentInputCarriesFocusedTextWithoutAProcessTarget()
    {
        var stream = new ScriptedInputStream();
        var client = new RemoteClient(new Connection("127.0.0.1", 45832, new string('a', 64), "token"), (_, _) => Task.FromResult<Stream>(stream));

        Assert.True((await client.SendInputAsync(new { kind = "text", text = "focused text\n€" })).Ok);

        Assert.Collection(stream.Requests,
            open => Assert.Equal("ui.input.open", open.Operation),
            text =>
            {
                Assert.Equal("ui.input", text.Operation);
                Assert.Equal("text", text.Args.Str("kind"));
                Assert.Equal("focused text\n€", text.Args.Str("text"));
                Assert.False(text.Args.TryGetProperty("pid", out _));
            });
    }

    [Fact]
    public async Task PersistentUpdateReusesOneAuthenticatedStreamAndPreservesRequestOrder()
    {
        var stream = new ScriptedInputStream();
        int opens = 0;
        var client = new RemoteClient(new Connection("127.0.0.1", 45832, new string('a', 64), "token"), (_, _) =>
        {
            opens++;
            return Task.FromResult<Stream>(stream);
        });

        try
        {
            string transactionId = new string('b', 32);
            Assert.True((await client.SendUpdateAsync("update.begin", new { transactionId }, seconds: 30)).Ok);
            Assert.True((await client.SendUpdateAsync("update.chunk", new { transactionId, offset = 0L, data = "AQI=" }, seconds: 60)).Ok);
            Assert.True((await client.SendUpdateAsync("update.stage", new { transactionId }, seconds: 60)).Ok);

            Assert.Equal(1, opens);
            Assert.Collection(stream.Requests,
                open => Assert.Equal("update.open", open.Operation),
                begin => { Assert.Equal("update.begin", begin.Operation); Assert.Equal(transactionId, begin.Args.Str("transactionId")); },
                chunk => { Assert.Equal("update.chunk", chunk.Operation); Assert.Equal(0, chunk.Args.Long("offset")); },
                stage => { Assert.Equal("update.stage", stage.Operation); Assert.Equal(transactionId, stage.Args.Str("transactionId")); });
        }
        finally { await client.CloseUpdateChannelAsync(); }
    }

    [Fact]
    public async Task OlderAgentsFallbackToAuthenticatedOneShotUpdateOperations()
    {
        var rejectedChannel = new ScriptedInputStream { RejectUpdateOpen = true };
        var legacy = new ScriptedInputStream();
        int opens = 0;
        var client = new RemoteClient(new Connection("127.0.0.1", 45832, new string('a', 64), "token"), (_, _) =>
        {
            opens++;
            return Task.FromResult<Stream>(opens == 1 ? rejectedChannel : legacy);
        });

        try
        {
            string transactionId = new string('d', 32);
            Assert.True((await client.SendUpdateAsync("update.begin", new { transactionId }, seconds: 30)).Ok);
            Assert.True((await client.SendUpdateAsync("update.chunk", new { transactionId, offset = 0L, data = "AQI=" }, seconds: 60)).Ok);

            Assert.Equal(3, opens);
            Assert.Collection(rejectedChannel.Requests,
                open => Assert.Equal("update.open", open.Operation));
            Assert.Collection(legacy.Requests,
                begin => { Assert.Equal("update.begin", begin.Operation); Assert.Equal(transactionId, begin.Args.Str("transactionId")); },
                chunk => { Assert.Equal("update.chunk", chunk.Operation); Assert.Equal(0, chunk.Args.Long("offset")); });
        }
        finally { await client.CloseUpdateChannelAsync(); }
    }

    [Fact]
    public async Task FailedUpdateTransportIsDiscardedBeforeTheNextRequestReopensIt()
    {
        var failed = new ScriptedInputStream { FailAfterRequests = 2 };
        var recovered = new ScriptedInputStream();
        int opens = 0;
        var client = new RemoteClient(new Connection("127.0.0.1", 45832, new string('a', 64), "token"), (_, _) =>
        {
            opens++;
            return Task.FromResult<Stream>(opens == 1 ? failed : recovered);
        });

        try
        {
            string transactionId = new string('c', 32);
            await client.SendUpdateAsync("update.begin", new { transactionId }, seconds: 30);
            await Assert.ThrowsAsync<IOException>(() => client.SendUpdateAsync("update.chunk", new { transactionId, offset = 0L, data = "AQI=" }, seconds: 60));
            Assert.True((await client.SendUpdateAsync("update.chunk", new { transactionId, offset = 0L, data = "AQI=" }, seconds: 60)).Ok);

            Assert.Equal(2, opens);
            Assert.Collection(recovered.Requests,
                open => Assert.Equal("update.open", open.Operation),
                chunk => Assert.Equal("update.chunk", chunk.Operation));
        }
        finally { await client.CloseUpdateChannelAsync(); }
    }

    [Fact]
    public async Task AgentKeepsPersistentUpdateChannelOpenForSubsequentRequests()
    {
        string root = Path.Combine(Path.GetTempPath(), "RemoteDebugger-PersistentUpdate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        AgentServer? server = null;
        try
        {
            int port = ReserveTcpPort();
            server = new AgentServer(root, port, loopbackOnly: true, enableInternet: false);
            server.Start();
            string code = server.Pairing.CurrentCode ?? throw new InvalidOperationException("Agent did not expose a pairing code.");
            var paired = await PairingTransport.PairAsync(new Connection("127.0.0.1", port, server.Fingerprint, ""), code, CancellationToken.None);
            var client = new RemoteClient(paired);
            try
            {
                Assert.True((await client.SendUpdateAsync("update.open", seconds: 30)).Ok);
                Assert.True((await client.SendUpdateAsync("status", seconds: 30)).Ok);
            }
            finally { await client.CloseUpdateChannelAsync(); }
        }
        finally
        {
            server?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedInputTransportIsDiscardedBeforeTheNextRequestReopensIt()
    {
        var failed = new ScriptedInputStream { FailAfterRequests = 2 };
        var recovered = new ScriptedInputStream();
        int opens = 0;
        var client = new RemoteClient(new Connection("127.0.0.1", 45832, new string('a', 64), "token"), (_, _) =>
        {
            opens++;
            return Task.FromResult<Stream>(opens == 1 ? failed : recovered);
        });

        await client.SendInputAsync(new { kind = "move", x = 10, y = 20 });
        await Assert.ThrowsAsync<IOException>(() => client.SendInputAsync(new { kind = "button", button = "left", down = true }));
        Assert.True((await client.SendInputAsync(new { kind = "release" })).Ok);

        Assert.Equal(2, opens);
        Assert.Collection(recovered.Requests,
            open => Assert.Equal("ui.input.open", open.Operation),
            release => { Assert.Equal("ui.input", release.Operation); Assert.Equal("release", release.Args.Str("kind")); });
    }

    private sealed class RecordingSocket : WebSocket
    {
        public List<byte[]> Sent { get; } = [];
        public WebSocketMessageType ReceivedType { get; init; } = WebSocketMessageType.Binary;
        public bool Disconnect { get; init; }
        public Task CloseCompletion { get; init; } = Task.CompletedTask;
        public bool Disposed { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { Disposed = true; }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => CloseCompletion;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            Disconnect ? Task.FromException<WebSocketReceiveResult>(new WebSocketException())
                : Task.FromResult(new WebSocketReceiveResult(0, ReceivedType, true));
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        { Sent.Add(buffer.ToArray()); return Task.CompletedTask; }
    }

    private static int ReserveTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class ScriptedInputStream : Stream
    {
        private readonly List<byte> pending = [];
        private readonly List<byte> incoming = [];
        public List<Request> Requests { get; } = [];
        public int FailAfterRequests { get; init; } = int.MaxValue;
        public bool RejectUpdateOpen { get; init; }
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
            CollectionsMarshal.AsSpan(incoming)[..count].CopyTo(buffer.Span);
            incoming.RemoveRange(0, count);
            return ValueTask.FromResult(count);
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Requests.Count >= FailAfterRequests) throw new IOException("scripted input transport failure");
            pending.AddRange(buffer.ToArray());
            while (pending.Count >= 4)
            {
                int size = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(CollectionsMarshal.AsSpan(pending)[..4]);
                if (size < 1 || pending.Count < size + 4) break;
                byte[] body = pending.GetRange(4, size).ToArray(); pending.RemoveRange(0, size + 4);
                Request request = JsonSerializer.Deserialize<Request>(body, Json.Options)!;
                Requests.Add(request);
                if (RejectUpdateOpen && request.Operation == "update.open")
                    QueueReply(Reply.Failure(request.Id, "binary_mismatch", "Synchronize the agent with the controller executable before starting support."));
                else
                    QueueReply(Reply.Success(request.Id, new { sent = true }));
            }
            return ValueTask.CompletedTask;
        }
        private void QueueReply(Reply reply)
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(reply, Json.Options);
            byte[] length = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
            incoming.AddRange(length); incoming.AddRange(body);
        }
    }
}
