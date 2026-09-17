using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RemoteDebugger;
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
}
