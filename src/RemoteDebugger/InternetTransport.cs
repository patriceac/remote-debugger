using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

public sealed record InternetSettings(string RelayUrl, string AccessKey)
{
    public static InternetSettings? Load(string root)
    {
        string path = Path.Combine(root, "internet.dpapi");
        if (!File.Exists(path)) return null;
        var settings = JsonSerializer.Deserialize<InternetSettings>(Vault.Read(path), Json.Options)
            ?? throw new IOException("Invalid internet settings.");
        settings.Validate(); return settings;
    }

    public void Validate()
    {
        if (!Uri.TryCreate(RelayUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("The relay must be an HTTPS origin without a path or credentials.");
        if (AccessKey.Length != 64 || !AccessKey.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid private relay access key.");
    }

    public static void Import(string file, string root)
    {
        if (new FileInfo(file).Length > 4096) throw new IOException("Internet setup file is too large.");
        var settings = JsonSerializer.Deserialize<InternetSettings>(File.ReadAllBytes(file), Json.Options)
            ?? throw new IOException("Invalid internet setup file.");
        settings.Validate();
        Vault.Save(Path.Combine(root, "internet.dpapi"), JsonSerializer.SerializeToUtf8Bytes(settings, Json.Options));
    }

    public static bool IsSupportId(string value) => value.Trim().StartsWith("RD-", StringComparison.OrdinalIgnoreCase);
    public static string SessionId(string value)
    {
        string id = value.Trim().Replace("-", "").ToUpperInvariant();
        if (id.StartsWith("RD", StringComparison.Ordinal)) id = id[2..];
        if (id.Length != 16 || !id.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid support ID.");
        return id;
    }
    public static string DisplayId(string id) => "RD-" + string.Join("-", Enumerable.Range(0, 4).Select(i => id.Substring(i * 4, 4)));

    public static Connection Target(string address, int port, string fingerprint, string root)
    {
        if (!IsSupportId(address)) return new(address, port, fingerprint, "");
        var settings = Load(root) ?? throw new InvalidOperationException(UiText.InternetSetupRequired);
        return new(DisplayId(SessionId(address)), 443, fingerprint, "", settings.RelayUrl, settings.AccessKey);
    }

    internal ClientWebSocket Socket(string? sessionKey = null)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + AccessKey);
        if (sessionKey != null) socket.Options.SetRequestHeader("X-Session-Key", sessionKey);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        return socket;
    }
    internal Uri Address(string id, string suffix) => new(new UriBuilder(RelayUrl) { Scheme = "wss", Port = new Uri(RelayUrl).Port,
        Path = "/v1/sessions/" + SessionId(id) + "/" + suffix }.Uri.AbsoluteUri);
}

public static class ConnectionTransport
{
    public static async Task<Stream> OpenAsync(Connection target, CancellationToken ct)
    {
        if (target.RelayUrl.Length == 0)
        {
            var tcp = new TcpClient { NoDelay = true };
            try { await tcp.ConnectAsync(target.Host, target.Port, ct).ConfigureAwait(false); return tcp.GetStream(); }
            catch { tcp.Dispose(); throw; }
        }
        var settings = new InternetSettings(target.RelayUrl, target.RelayAccessKey); settings.Validate();
        var socket = settings.Socket();
        try
        {
            await socket.ConnectAsync(settings.Address(target.Host, "connect"), ct).ConfigureAwait(false);
            string ready = await WebSocketStream.ReadTextAsync(socket, ct).ConfigureAwait(false);
            if (ready != "ready") throw new IOException("Internet session unavailable.");
            return new WebSocketStream(socket);
        }
        catch (WebSocketException ex) { socket.Dispose(); throw new IOException("Internet session unavailable.", ex); }
        catch { socket.Dispose(); throw; }
    }
}

// Carries opaque inner TLS records. Outer WSS authenticates the hosting service;
// existing endpoint TLS/J-PAKE independently authenticates and encrypts the PC.
public sealed class WebSocketStream(WebSocket socket) : Stream
{
    private int disposed;
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.Length == 0) return 0;
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return 0;
                if (result.MessageType != WebSocketMessageType.Binary) throw new IOException("Invalid encrypted relay frame.");
                if (result.Count != 0) return result.Count;
            }
        }
        catch (WebSocketException ex) { throw new IOException("Internet connection interrupted.", ex); }
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        try
        {
            while (buffer.Length != 0)
            {
                int size = Math.Min(buffer.Length, 65536);
                await socket.SendAsync(buffer[..size], WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                buffer = buffer[size..];
            }
        }
        catch (WebSocketException ex) { throw new IOException("Internet connection interrupted.", ex); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref disposed, 1) == 0) { socket.Abort(); socket.Dispose(); }
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try
        {
            // A normal WebSocket close is ordered after the last data frame.
            // Aborting immediately after a large TLS reply can discard its tail.
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or InvalidOperationException) { }
        finally { socket.Abort(); socket.Dispose(); GC.SuppressFinalize(this); }
    }
    internal static async Task<string> ReadTextAsync(WebSocket socket, CancellationToken ct)
    {
        byte[] buffer = new byte[2048]; int used = 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(used), ct).ConfigureAwait(false);
            if (result.MessageType != WebSocketMessageType.Text) throw new IOException("Internet connection closed.");
            used += result.Count;
            if (result.EndOfMessage) return Encoding.UTF8.GetString(buffer, 0, used);
            if (used == buffer.Length) throw new IOException("Relay message is too large.");
        }
    }
}

public sealed class InternetAgent : IDisposable
{
    private sealed record Invitation(string Id, string Key);
    private readonly InternetSettings settings;
    private readonly Invitation invitation;
    private readonly Func<Stream, Task> accept;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim channels = new(12);
    private ClientWebSocket? control;
    public string SupportId => InternetSettings.DisplayId(invitation.Id);
    public bool Connected { get; private set; }
    public string PublicIp { get; private set; } = "";
    public string Error { get; private set; } = "";
    public InternetAgent(string root, InternetSettings settings, bool resume, Func<Stream, Task> accept)
    {
        this.settings = settings; this.accept = accept;
        string path = Path.Combine(root, "internet-invitation.dpapi");
        invitation = resume && File.Exists(path)
            ? JsonSerializer.Deserialize<Invitation>(Vault.Read(path), Json.Options) ?? throw new IOException("Invalid saved internet invitation.")
            : new(Convert.ToHexString(RandomNumberGenerator.GetBytes(8)), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        Vault.Save(path, JsonSerializer.SerializeToUtf8Bytes(invitation, Json.Options));
    }
    public void Start() => _ = RunAsync();
    private async Task RunAsync()
    {
        int delay = 1;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var socket = settings.Socket(invitation.Key); control = socket;
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); connect.CancelAfter(TimeSpan.FromSeconds(20));
                await socket.ConnectAsync(settings.Address(invitation.Id, "agent"), connect.Token).ConfigureAwait(false);
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                Task pulse = PulseAsync(socket, heartbeat.Token);
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); idle.CancelAfter(TimeSpan.FromSeconds(50));
                        string text = await WebSocketStream.ReadTextAsync(socket, idle.Token).ConfigureAwait(false);
                        if (text == "pong") continue;
                        using var message = JsonDocument.Parse(text); var data = message.RootElement;
                        if (data.Str("type") == "registered") { PublicIp = data.Str("publicIp"); Connected = true; Error = ""; delay = 1; }
                        else if (data.Str("type") == "open" && channels.Wait(0)) _ = OpenChannelAsync(data.Str("channel"));
                    }
                }
                finally { heartbeat.Cancel(); try { await pulse.ConfigureAwait(false); } catch (OperationCanceledException) { } }
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or JsonException)
            { Error = ex is OperationCanceledException ? "Connection timed out" : ex.Message; }
            finally { Connected = false; control = null; }
            try { await Task.Delay(TimeSpan.FromSeconds(delay), stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            delay = Math.Min(delay * 2, 30);
        }
    }
    private static async Task PulseAsync(ClientWebSocket socket, CancellationToken ct)
    {
        while (true) { await Task.Delay(20000, ct).ConfigureAwait(false); await socket.SendAsync("ping"u8.ToArray(), WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
    }
    private async Task OpenChannelAsync(string channel)
    {
        try
        {
            if (channel.Length != 32 || !channel.All(Uri.IsHexDigit)) return;
            var socket = settings.Socket(invitation.Key);
            await using var stream = new WebSocketStream(socket);
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); connect.CancelAfter(TimeSpan.FromSeconds(20));
            await socket.ConnectAsync(settings.Address(invitation.Id, "channels/" + channel), connect.Token).ConfigureAwait(false);
            using var abort = stop.Token.Register(socket.Abort);
            await accept(stream).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException) { /* Controller retry owns recovery. */ }
        finally { channels.Release(); }
    }
    public void Dispose() { stop.Cancel(); control?.Abort(); Connected = false; }
}
