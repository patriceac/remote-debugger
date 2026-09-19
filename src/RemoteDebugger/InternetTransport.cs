using System.Net.Sockets;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

public sealed record InternetSettings(string RelayUrl, string AccessKey, string PairingKey = "", string SecurityId = "")
{
    private sealed record OnlineClient(string Id, string Name, string Fingerprint = "");

    public async Task<List<Peer>> FindAsync(CancellationToken ct = default)
    {
        Validate();
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 16384 };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessKey);
        using var response = await http.GetAsync(new Uri(new Uri(RelayUrl), "/v1/clients"), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var clients = JsonSerializer.Deserialize<List<OnlineClient>>(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false), Json.Options)
            ?? throw new IOException("Invalid computer list.");
        if (clients.Count > 32 || clients.Any(c => string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 128 || c.Name.Any(char.IsControl)))
            throw new IOException("Invalid computer list.");
        return clients.Select(c =>
        {
            string supportId = DisplayId(SessionId(c.Id));
            return new Peer(c.Name, supportId, 443, PairingExchange.ValidHash(c.Fingerprint) ? c.Fingerprint.ToUpperInvariant() : "", supportId);
        }).ToList();
    }

    public static InternetSettings? Load(string root)
    {
        string path = Path.Combine(root, "internet.dpapi");
        if (!File.Exists(path)) return null;
        var settings = JsonSerializer.Deserialize<InternetSettings>(Vault.Read(path), Json.Options)
            ?? throw new IOException("Invalid internet settings.");
        settings.Validate(requirePairingKey: true); return settings;
    }

    public void Validate(bool requirePairingKey = false)
    {
        if (!Uri.TryCreate(RelayUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("The relay must be an HTTPS origin without a path or credentials.");
        if (AccessKey.Length != 64 || !AccessKey.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid private relay access key.");
        if (requirePairingKey && !PairingExchange.ValidHash(PairingKey))
            throw new ArgumentException(UiText.InternetSetupRequired);
        if (SecurityId.Length > 0 && !Guid.TryParseExact(SecurityId, "N", out _))
            throw new ArgumentException("Invalid security profile identity.");
    }

    // This separate installer secret is never sent to or stored by the relay.
    // Binding it to the invitation prevents reusing an exchange across sessions.
    public string AuthenticationSecret(string invitationId)
    {
        Validate(requirePairingKey: true);
        byte[] key = Convert.FromHexString(PairingKey);
        try { return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("RemoteDebugger.Private.v1|" + SessionId(invitationId)))); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static void Import(string file, string root)
    {
        if (new FileInfo(file).Length > ProtectedSetup.MaximumBytes) throw new IOException("Internet setup file is too large.");
        byte[] bytes = File.ReadAllBytes(file);
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.TryGetProperty("format", out _))
        {
            var envelope = ProtectedSetup.Read(bytes);
            // Installing an update with the same profile must not prompt again.
            if (Load(root)?.SecurityId == envelope.ProfileId) return;
            Directory.CreateDirectory(root);
            File.WriteAllBytes(Path.Combine(root, "internet.pending.rdrelay"), bytes);
            return;
        }
        var settings = JsonSerializer.Deserialize<InternetSettings>(bytes, Json.Options)
            ?? throw new IOException("Invalid internet setup file.");
        settings.Validate(requirePairingKey: true);
        if (Load(root)?.SecurityId.Length > 0) throw new InvalidOperationException("An unprotected profile cannot replace protected access.");
        settings.Save(root);
    }

    public void Save(string root)
    {
        Validate(requirePairingKey: true);
        Vault.Save(Path.Combine(root, "internet.dpapi"), JsonSerializer.SerializeToUtf8Bytes(this, Json.Options));
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

    internal static Connection Target(Peer peer, string root)
    {
        if (peer.SupportId.Length == 0 || IsSupportId(peer.Host)) return Target(peer.Host, peer.Port, peer.Fingerprint, root);
        var settings = Load(root);
        return settings == null ? new(peer.Host, peer.Port, peer.Fingerprint, "")
            : new(peer.SupportId, 443, peer.Fingerprint, "", settings.RelayUrl, settings.AccessKey, peer.Host, peer.Port);
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
        if (target.DirectHost.Length > 0)
        {
            return await OpenTcpAsync(target.DirectHost, target.DirectPort, ct).ConfigureAwait(false);
        }
        if (target.RelayUrl.Length == 0) return await OpenTcpAsync(target.Host, target.Port, ct).ConfigureAwait(false);
        return await OpenRelayAsync(target, ct).ConfigureAwait(false);
    }

    private static async Task<Stream> OpenTcpAsync(string host, int port, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        try { await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false); return tcp.GetStream(); }
        catch { tcp.Dispose(); throw; }
    }

    private static async Task<Stream> OpenRelayAsync(Connection target, CancellationToken ct)
    {
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
    private long writeWindow = System.Diagnostics.Stopwatch.GetTimestamp();
    private int writeBytes;
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
                // Leave headroom below the relay's 16 MiB/s disconnect limit.
                if (writeBytes + size > 1024 * 1024)
                {
                    var delay = TimeSpan.FromMilliseconds(100) - System.Diagnostics.Stopwatch.GetElapsedTime(writeWindow);
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
                    writeWindow = System.Diagnostics.Stopwatch.GetTimestamp(); writeBytes = 0;
                }
                await socket.SendAsync(buffer[..size], WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                writeBytes += size;
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
    private readonly string fingerprint;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim channels = new(12);
    private ClientWebSocket? control;
    public string SupportId => InternetSettings.DisplayId(invitation.Id);
    public string AuthenticationSecret => settings.AuthenticationSecret(invitation.Id);
    public bool Connected { get; private set; }
    public string PublicIp { get; private set; } = "";
    public string Error { get; private set; } = "";
    public InternetAgent(string root, InternetSettings settings, bool resume, Func<Stream, Task> accept, string fingerprint = "")
    {
        this.settings = settings; this.accept = accept; this.fingerprint = fingerprint;
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
                socket.Options.SetRequestHeader("X-Computer-Name", Uri.EscapeDataString(Environment.MachineName));
                if (fingerprint.Length > 0) socket.Options.SetRequestHeader("X-Computer-Fingerprint", fingerprint);
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
