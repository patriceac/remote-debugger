using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
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
    public async Task OptInWriteBufferingCoalescesWritesUntilFlush()
    {
        var socket = new RecordingSocket(); using var stream = new WebSocketStream(socket);
        stream.EnableWriteBuffering();

        await stream.WriteAsync(new byte[] { 1, 2 });
        await stream.WriteAsync(new byte[] { 3, 4, 5 });
        Assert.Empty(socket.Sent);

        await stream.FlushAsync();

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, Assert.Single(socket.Sent));
    }

    [Fact]
    public async Task BufferedWritesFlushBeforeTheFirstRead()
    {
        var socket = new RecordingSocket { ReceivedType = WebSocketMessageType.Close };
        using var stream = new WebSocketStream(socket);
        stream.EnableWriteBuffering();
        await stream.WriteAsync(new byte[] { 9, 8, 7 });
        Assert.Empty(socket.Sent);

        Assert.Equal(0, await stream.ReadAsync(new byte[8]));

        Assert.Equal(new byte[] { 9, 8, 7 }, Assert.Single(socket.Sent));
    }

    [Fact]
    public async Task TlsHandshakeStaysUnbufferedThenBufferedFramesCarryWireAndLargePayloads()
    {
        var (clientSocket, serverSocket) = LoopbackSocket.CreatePair();
        await using var clientTransport = new WebSocketStream(clientSocket);
        await using var serverTransport = new WebSocketStream(serverSocket);
        using var certificate = CreateTestCertificate();
        await using var clientTls = new SslStream(clientTransport, leaveInnerStreamOpen: true, (_, _, _, _) => true);
        await using var serverTls = new SslStream(serverTransport, leaveInnerStreamOpen: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Task serverHandshake = serverTls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, deadline.Token);
        Task clientHandshake = clientTls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "RemoteDebugger",
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, deadline.Token);
        await Task.WhenAll(serverHandshake, clientHandshake);

        int handshakeFrames = clientSocket.Sent.Count;
        Assert.True(handshakeFrames > 0);
        clientTransport.EnableWriteBuffering();
        serverTransport.EnableWriteBuffering();

        var request = new Request("tls-request", "token", "status", Json.Element(new { value = "é日本" }));
        Task<Request> requestRead = Wire.ReadAsync<Request>(serverTls, deadline.Token);
        await Wire.WriteAsync(clientTls, request, deadline.Token);
        Request actualRequest = await requestRead;
        Assert.Equal(request.Id, actualRequest.Id);
        Assert.Equal(request.Token, actualRequest.Token);
        Assert.Equal(request.Operation, actualRequest.Operation);
        Assert.Equal(request.Args.Str("value"), actualRequest.Args.Str("value"));

        Task<Reply> replyRead = Wire.ReadAsync<Reply>(clientTls, deadline.Token);
        await Wire.WriteAsync(serverTls, Reply.Success(request.Id, new { accepted = true }), deadline.Token);
        Reply reply = await replyRead;
        Assert.True(reply.Ok);
        Assert.True(reply.Data.GetProperty("accepted").GetBoolean());

        byte[] payload = Enumerable.Range(0, 100_000).Select(i => (byte)i).ToArray();
        int payloadFrameStart = clientSocket.Sent.Count;
        await clientTls.WriteAsync(payload, deadline.Token);
        await clientTls.FlushAsync(deadline.Token);
        byte[] received = new byte[payload.Length];
        await serverTls.ReadExactlyAsync(received, deadline.Token);

        Assert.Equal(payload, received);
        var payloadFrames = clientSocket.Sent.Skip(payloadFrameStart).ToArray();
        Assert.Equal(2, payloadFrames.Length);
        Assert.All(payloadFrames, frame => Assert.InRange(frame.Length, 1, 65536));
    }

    [Fact]
    public void RetryDelayStaysWithinTheFiveMinuteCapAtIntegerExtremes()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(800), InternetAgent.RetryDelay(int.MinValue, 0));
        Assert.Equal(TimeSpan.FromSeconds(300), InternetAgent.RetryDelay(int.MaxValue, 1));
        Assert.Equal(TimeSpan.FromSeconds(1.6), InternetAgent.RetryDelay(2, -1));
        Assert.Equal(TimeSpan.FromSeconds(1.8), InternetAgent.RetryDelay(2, 0.5));
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
    public void LanPeerIdentityCollapsesMultipleAddressesAndKeepsAnEndpoint()
    {
        string fingerprint = new string('a', 64);
        var firstAddress = new Peer("PC-PATRICE", "192.168.1.20", 45832, fingerprint, "RD-0123-4567-89AB-CDEF");
        var secondAddress = firstAddress with { Host = "172.16.0.20" };
        var otherPeer = new Peer("PC-YOLANDE", "192.168.1.21", 45832, new string('b', 64), "RD-9876-5432-10FE-DCBA");

        var result = PeerDiscovery.DistinctPeers(new[] { firstAddress, secondAddress, otherPeer });

        Assert.Equal(2, result.Count);
        Assert.Equal(firstAddress, Assert.Single(result, peer => peer.Fingerprint == fingerprint));
        Assert.Equal(otherPeer, Assert.Single(result, peer => peer.Fingerprint == otherPeer.Fingerprint));
    }

    [Fact]
    public async Task PrivateLanFallbackReturnsOneEntryForDuplicatePeerAddresses()
    {
        string fingerprint = new string('a', 64);
        var firstAddress = new Peer("PC-PATRICE", "192.168.1.20", 45832, fingerprint, "RD-0123-4567-89AB-CDEF");
        var secondAddress = firstAddress with { Host = "172.16.0.20" };

        var result = await PeerDiscovery.FindAsync(
            privateInternet: true,
            lanDiscovery: _ => Task.FromResult(new List<Peer> { firstAddress, secondAddress }),
            relayDiscovery: _ => Task.FromException<List<Peer>>(new HttpRequestException("relay unavailable")));

        Assert.True(result.UsedLanFallback);
        Assert.Equal(firstAddress, Assert.Single(result.Peers));
    }

    [Fact]
    public void RelayPeersWithoutFingerprintsRemainDistinctByHostAndPort()
    {
        var first = new Peer("PC-PATRICE", "RD-0123-4567-89AB-CDEF", 443, "");
        var duplicate = first with { Name = "Renamed PC-PATRICE" };
        var otherHost = first with { Host = "RD-9876-5432-10FE-DCBA" };
        var otherPort = first with { Port = 45832 };

        var result = PeerDiscovery.DistinctPeers(new[] { first, duplicate, otherHost, otherPort });

        Assert.Equal(3, result.Count);
        Assert.Equal(first, Assert.Single(result, peer => peer.Host == first.Host && peer.Port == first.Port));
        Assert.Contains(result, peer => peer.Host == otherHost.Host && peer.Port == otherHost.Port);
        Assert.Contains(result, peer => peer.Host == otherPort.Host && peer.Port == otherPort.Port);
    }

    [Fact]
    public void RelayIdentityFilterExcludesMatchingSupportIdButKeepsSameNamedDifferentPeer()
    {
        const string localSupportId = "RD-0123-4567-89AB-CDEF";
        var localPeer = new Peer("PC-PATRICE", "relay-route", 443, "", localSupportId);
        var sameNameDifferentPeer = new Peer("PC-PATRICE", "RD-9876-5432-10FE-DCBA", 443, "", "RD-9876-5432-10FE-DCBA");

        Assert.True(PeerDiscovery.IsLocalPeer(localPeer, localSupportId));
        Assert.False(PeerDiscovery.IsLocalPeer(sameNameDifferentPeer, localSupportId));

        var visible = new[] { localPeer, sameNameDifferentPeer }
            .Where(peer => !PeerDiscovery.IsLocalPeer(peer, localSupportId))
            .ToArray();
        Assert.Equal(new[] { sameNameDifferentPeer }, visible);
    }

    [Fact]
    public void RelayIdentityFilterAlsoRecognizesTheLocalSupportIdInHost()
    {
        const string localSupportId = "RD-0123-4567-89AB-CDEF";
        var localPeer = new Peer("PC-PATRICE", localSupportId, 443, "");

        Assert.True(PeerDiscovery.IsLocalPeer(localPeer, localSupportId.ToLowerInvariant()));
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
    public async Task PrivateDiscoveryAlsoProbesLanWhenTheRelaySucceeds()
    {
        bool lanCalled = false;
        var relayPeer = new Peer("Relay PC", "RD-0123-4567-89AB-CDEF", 443, "", "RD-0123-4567-89AB-CDEF");
        var result = await PeerDiscovery.FindAsync(
            privateInternet: true,
            lanDiscovery: _ => { lanCalled = true; return Task.FromResult(new List<Peer>()); },
            relayDiscovery: _ => Task.FromResult(new List<Peer> { relayPeer }));

        Assert.False(result.UsedLanFallback);
        Assert.True(lanCalled);
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
    public async Task LanPeerWinsOverRelayAndRebindsAfterAddressAndInvitationChange()
    {
        var lan = new Peer("PC", "192.168.1.20", 45832, new string('a', 64), "RD-0123-4567-89AB-CDEF");
        var relay = lan with { Host = lan.SupportId, Port = 443, Fingerprint = "" };
        var result = await PeerDiscovery.FindAsync(true, _ => Task.FromResult(new List<Peer> { lan }), _ => Task.FromResult(new List<Peer> { relay }));
        Assert.Equal(lan, Assert.Single(result.Peers));
        var changed = lan with { Host = "192.168.1.21", SupportId = "RD-9876-5432-10FE-DCBA" };
        Assert.Equal(changed, PeerDiscovery.Rebind(lan, [changed]));
        Assert.Null(PeerDiscovery.Rebind(lan, [lan with { Fingerprint = new string('b', 64) }]));
    }

    [Fact]
    public async Task FailedDirectRouteFallsBackOnceAndIsNotRetriedByFollowingCalls()
    {
        var attempts = new List<string>();
        var target = new Connection("RD-0123-4567-89AB-CDEF", 443, new string('a', 64), "token", "https://relay.example", new string('b', 64), "127.0.0.1", 45832);
        var client = new RemoteClient(target, (route, _) =>
        {
            attempts.Add(route.DirectHost.Length > 0 ? "direct" : "relay");
            if (route.DirectHost.Length > 0) throw new IOException("Stale direct endpoint");
            return Task.FromResult<Stream>(new ScriptedInputStream());
        });
        Assert.True((await client.CallAsync("status")).Ok);
        Assert.True((await client.CallAsync("status")).Ok);
        Assert.Equal(new[] { "direct", "relay", "relay" }, attempts);
        Assert.Equal("Relay", client.ActiveRoute);
        Assert.Empty(client.Connection.DirectHost);
    }

    [Theory]
    [InlineData("session.end", false)]
    [InlineData("revoke", false)]
    [InlineData("update.cancel", false)]
    [InlineData("update.resume", false)]
    [InlineData("file.info", true)]
    [InlineData("ui.input", true)]
    public void CleanupAndRecoveryBypassSynchronization(string operation, bool expected) =>
        Assert.Equal(expected, Program.RequiresSynchronization(operation));

    [Fact]
    public void PeerSerializationPreservesThePrivateSupportIdentity()
    {
        var peer = new Peer("Nearby", "192.168.1.20", 45832, new string('a', 64), "RD-0123-4567-89AB-CDEF");
        var roundTrip = JsonSerializer.Deserialize<Peer>(JsonSerializer.SerializeToUtf8Bytes(peer, Json.Options), Json.Options);

        Assert.NotNull(roundTrip);
        Assert.Equal(peer, roundTrip);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public async Task RelayPairedConnectionSwitchesToAnAuthenticatedDirectEndpoint(string directHost)
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
            var pairTarget = directHost == "localhost"
                ? new Connection("RD-0123-4567-89AB-CDEF", 443, server.Fingerprint, "", "https://relay.example", new string('a', 64),
                    "127.0.0.1", ReserveTcpPort(), new(directHost, port))
                : new Connection("127.0.0.1", port, server.Fingerprint, "");
            var paired = await PairingTransport.PairAsync(pairTarget, code, CancellationToken.None);
            if (directHost == "localhost") Assert.Equal(directHost, paired.DirectHost);
            var client = new RemoteClient(new Connection(
                "RD-0123-4567-89AB-CDEF",
                443,
                paired.Fingerprint,
                paired.Token,
                "https://relay.example",
                new string('a', 64), WanEndpoint: directHost == "localhost" ? new(directHost, port) : null));

            Assert.True(await client.TryPreferDirectAsync([new DirectEndpoint(directHost, port)]));
            Assert.True(client.UsesDirectTransport);
            Assert.Equal(directHost, client.Connection.DirectHost);
            Assert.Equal(port, client.Connection.DirectPort);
            Assert.Equal("https://relay.example", client.Connection.RelayUrl);
            Assert.Equal(client.Connection, JsonSerializer.Deserialize<Connection>(
                JsonSerializer.SerializeToUtf8Bytes(client.Connection, Json.Options), Json.Options));
            Assert.True((await client.HeartbeatAsync()).GetProperty("session").GetProperty("connected").GetBoolean());
            var wrongIdentity = new RemoteClient(client.Connection with { Fingerprint = new string('f', 64), DirectHost = "", DirectPort = 0 });
            Assert.False(await wrongIdentity.TryPreferDirectAsync([new DirectEndpoint(directHost, port)]));
        }
        finally
        {
            server?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PersistentHeartbeatReusesOneAuthenticatedStream()
    {
        var stream = new ScriptedInputStream { SupportsPersistentHeartbeat = true };
        int opens = 0;
        var client = new RemoteClient(new Connection("127.0.0.1", 45832, new string('a', 64), "token"), (_, _) =>
        {
            opens++;
            return Task.FromResult<Stream>(stream);
        });

        JsonElement first = await client.HeartbeatAsync();
        JsonElement second = await client.HeartbeatAsync();

        Assert.True(first.GetProperty("persistent").GetBoolean());
        Assert.True(second.GetProperty("persistent").GetBoolean());
        Assert.Equal(1, opens);
        Assert.Collection(stream.Requests,
            open =>
            {
                Assert.Equal("session.heartbeat", open.Operation);
                Assert.True(open.Args.GetProperty("keepAlive").GetBoolean());
            },
            heartbeat =>
            {
                Assert.Equal("session.heartbeat", heartbeat.Operation);
                Assert.False(heartbeat.Args.TryGetProperty("keepAlive", out _));
            });
    }

    [Fact]
    public async Task LegacyHeartbeatReturnsProbeReplyAndRenegotiatesAfterClose()
    {
        var probe = new ScriptedInputStream();
        var legacy = new ScriptedInputStream();
        var reopened = new ScriptedInputStream { SupportsPersistentHeartbeat = true };
        int opens = 0;
        var client = new RemoteClient(new Connection("127.0.0.1", 45832, new string('a', 64), "token"), (_, _) =>
        {
            opens++;
            return Task.FromResult<Stream>(opens switch { 1 => probe, 2 => legacy, _ => reopened });
        });

        JsonElement first = await client.HeartbeatAsync();
        JsonElement second = await client.HeartbeatAsync();

        Assert.True(first.GetProperty("legacy").GetBoolean());
        Assert.True(second.GetProperty("legacy").GetBoolean());
        Assert.Equal(2, opens);
        Assert.True(probe.Disposed);
        Assert.Collection(probe.Requests,
            open =>
            {
                Assert.Equal("session.heartbeat", open.Operation);
                Assert.True(open.Args.GetProperty("keepAlive").GetBoolean());
            });
        Assert.Collection(legacy.Requests,
            heartbeat =>
            {
                Assert.Equal("session.heartbeat", heartbeat.Operation);
                Assert.False(heartbeat.Args.TryGetProperty("keepAlive", out _));
            });

        await client.CloseHeartbeatChannelAsync();
        JsonElement renegotiated = await client.HeartbeatAsync();

        Assert.True(renegotiated.GetProperty("persistent").GetBoolean());
        Assert.Equal(3, opens);
        Assert.Collection(reopened.Requests,
            open =>
            {
                Assert.Equal("session.heartbeat", open.Operation);
                Assert.True(open.Args.GetProperty("keepAlive").GetBoolean());
            });
        await client.CloseHeartbeatChannelAsync();
        Assert.True(reopened.Disposed);
    }

    [Fact]
    public async Task FailedPersistentHeartbeatDisposesItsStreamBeforeTheNextCall()
    {
        var failed = new ScriptedInputStream { SupportsPersistentHeartbeat = true, FailAfterRequests = 1 };
        var recovered = new ScriptedInputStream { SupportsPersistentHeartbeat = true };
        int opens = 0;
        var client = new RemoteClient(new Connection("127.0.0.1", 45832, new string('a', 64), "token"), (_, _) =>
        {
            opens++;
            return Task.FromResult<Stream>(opens == 1 ? failed : recovered);
        });

        await client.HeartbeatAsync();
        await Assert.ThrowsAsync<IOException>(() => client.HeartbeatAsync());
        await client.HeartbeatAsync();

        Assert.True(failed.Disposed);
        Assert.Equal(2, opens);
        Assert.Collection(recovered.Requests,
            open =>
            {
                Assert.Equal("session.heartbeat", open.Operation);
                Assert.True(open.Args.GetProperty("keepAlive").GetBoolean());
            });
    }

    [Fact]
    public async Task ClosingHeartbeatChannelDisposesThePersistentStream()
    {
        var stream = new ScriptedInputStream { SupportsPersistentHeartbeat = true };
        var client = new RemoteClient(new Connection("127.0.0.1", 45832, new string('a', 64), "token"), (_, _) => Task.FromResult<Stream>(stream));

        await client.HeartbeatAsync();
        await client.CloseHeartbeatChannelAsync();

        Assert.True(stream.Disposed);
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
    public async Task BinaryUpdateChunkUsesNegotiatedRawBytesAndCount()
    {
        var stream = new ScriptedInputStream { SupportsBinaryChunks = true };
        var client = new RemoteClient(new Connection("127.0.0.1", 45832, new string('a', 64), "token"), (_, _) => Task.FromResult<Stream>(stream));
        byte[] bytes = [0, 1, 2, 255];
        string transactionId = new string('e', 32);

        Assert.True((await client.SendUpdateAsync(
            "update.chunk.binary",
            new { transactionId, offset = 0L, count = bytes.Length },
            seconds: 60,
            binaryChunk: bytes)).Ok);

        Assert.Collection(stream.Requests,
            open => Assert.Equal("update.open", open.Operation),
            chunk =>
            {
                Assert.Equal("update.chunk.binary", chunk.Operation);
                Assert.Equal(bytes.Length, chunk.Args.Int("count"));
            });
        Assert.Equal(bytes, Assert.Single(stream.BinaryChunks));
    }

    [Fact]
    public async Task LegacyUpdatePeerConvertsBinaryChunkToBase64WithoutWritingRawBytes()
    {
        var stream = new ScriptedInputStream();
        var client = new RemoteClient(new Connection("127.0.0.1", 45832, new string('a', 64), "token"), (_, _) => Task.FromResult<Stream>(stream));
        byte[] bytes = [0, 1, 2, 255];
        string transactionId = new string('f', 32);

        Assert.True((await client.SendUpdateAsync(
            "update.chunk.binary",
            new { transactionId, offset = 0L, count = bytes.Length },
            seconds: 60,
            binaryChunk: bytes)).Ok);

        Assert.Collection(stream.Requests,
            open => Assert.Equal("update.open", open.Operation),
            chunk =>
            {
                Assert.Equal("update.chunk", chunk.Operation);
                Assert.Equal(transactionId, chunk.Args.Str("transactionId"));
                Assert.Equal(Convert.ToBase64String(bytes), chunk.Args.Str("data"));
                Assert.False(chunk.Args.TryGetProperty("count", out _));
            });
        Assert.Empty(stream.BinaryChunks);
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

    private static X509Certificate2 CreateTestCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=RemoteDebugger", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
        return new X509Certificate2(generated.Export(X509ContentType.Pfx), (string?)null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    private sealed class LoopbackSocket : WebSocket
    {
        private readonly Channel<byte[]> incoming;
        private LoopbackSocket peer = null!;
        private byte[]? current;
        private int currentOffset;
        private WebSocketState state = WebSocketState.Open;

        private LoopbackSocket()
        {
            incoming = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = true
            });
        }

        public List<byte[]> Sent { get; } = [];

        public static (LoopbackSocket Client, LoopbackSocket Server) CreatePair()
        {
            var client = new LoopbackSocket();
            var server = new LoopbackSocket();
            client.peer = server;
            server.peer = client;
            return (client, server);
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;
        public override void Abort() { state = WebSocketState.Aborted; incoming.Writer.TryComplete(); }
        public override void Dispose() { state = WebSocketState.Closed; incoming.Writer.TryComplete(); }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            state = WebSocketState.Closed;
            peer.incoming.Writer.TryComplete();
            return Task.CompletedTask;
        }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            SendAsync(buffer.AsMemory(), messageType, endOfMessage, cancellationToken).AsTask();
        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            byte[] copy = buffer.ToArray();
            Sent.Add(copy);
            if (messageType == WebSocketMessageType.Close) peer.incoming.Writer.TryComplete();
            else if (!peer.incoming.Writer.TryWrite(copy)) throw new IOException("Loopback WebSocket is closed.");
            return ValueTask.CompletedTask;
        }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            ReceiveLegacyAsync(buffer, cancellationToken);
        private async Task<WebSocketReceiveResult> ReceiveLegacyAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var result = await ReceiveAsync(buffer.AsMemory(), cancellationToken);
            return new WebSocketReceiveResult(result.Count, result.MessageType, result.EndOfMessage);
        }
        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (buffer.Length == 0) return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Binary, true);
            while (current == null || currentOffset == current.Length)
            {
                current = await incoming.Reader.ReadAsync(cancellationToken);
                currentOffset = 0;
            }
            int count = Math.Min(buffer.Length, current.Length - currentOffset);
            current.AsMemory(currentOffset, count).CopyTo(buffer);
            currentOffset += count;
            return new ValueWebSocketReceiveResult(count, WebSocketMessageType.Binary, currentOffset == current.Length);
        }
    }

    private sealed class ScriptedInputStream : Stream
    {
        private readonly List<byte> pending = [];
        private readonly List<byte> incoming = [];
        private readonly List<byte> binaryPending = [];
        private int expectedBinaryBytes;
        private string? pendingBinaryReplyId;
        private bool persistentHeartbeatEstablished;
        public List<Request> Requests { get; } = [];
        public List<byte[]> BinaryChunks { get; } = [];
        public int FailAfterRequests { get; init; } = int.MaxValue;
        public bool RejectUpdateOpen { get; init; }
        public bool SupportsPersistentHeartbeat { get; init; }
        public bool SupportsBinaryChunks { get; init; }
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
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
            if (expectedBinaryBytes > 0)
            {
                int count = Math.Min(expectedBinaryBytes - binaryPending.Count, buffer.Length);
                binaryPending.AddRange(buffer[..count].ToArray());
                if (binaryPending.Count == expectedBinaryBytes)
                {
                    BinaryChunks.Add(binaryPending.ToArray());
                    binaryPending.Clear(); expectedBinaryBytes = 0;
                    QueueReply(Reply.Success(pendingBinaryReplyId!, new { sent = true }));
                    pendingBinaryReplyId = null;
                }
                if (count != buffer.Length) throw new InvalidDataException("scripted binary chunk contained trailing bytes");
                return ValueTask.CompletedTask;
            }
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
                else if (request.Operation == "session.heartbeat")
                {
                    if (SupportsPersistentHeartbeat && request.Args.TryGetProperty("keepAlive", out var keepAlive) && keepAlive.ValueKind == JsonValueKind.True)
                        persistentHeartbeatEstablished = true;
                    QueueReply(persistentHeartbeatEstablished
                        ? Reply.Success(request.Id, new { sent = true, persistent = true })
                        : Reply.Success(request.Id, new { sent = true, legacy = true }));
                }
                else if (request.Operation == "update.open")
                    QueueReply(SupportsBinaryChunks
                        ? Reply.Success(request.Id, new { sent = true, binaryChunks = true })
                        : Reply.Success(request.Id, new { sent = true }));
                else if (request.Operation == "update.chunk.binary")
                {
                    expectedBinaryBytes = request.Args.Int("count");
                    pendingBinaryReplyId = request.Id;
                    if (expectedBinaryBytes < 1) QueueReply(Reply.Failure(request.Id, "invalid_chunk", "scripted binary chunk length is invalid."));
                }
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
