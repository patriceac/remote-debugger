using System.Collections.Concurrent;
using System.Drawing;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

public static class Vault
{
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteDebugger");
    public static void Save(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temp, ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser));
        File.Move(temp, path, true);
    }
    public static byte[] Read(string path) => ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
}
public sealed record Peer(string Name, string Host, int Port, string Fingerprint, string SupportId = "");
public sealed record DirectEndpoint(string Host, int Port);
public sealed record Connection(
    string Host,
    int Port,
    string Fingerprint,
    string Token,
    string RelayUrl = "",
    string RelayAccessKey = "",
    string DirectHost = "",
    int DirectPort = 0,
    DirectEndpoint? WanEndpoint = null);

public static class Discovery
{
    public const int Port = 45833;
    public const string Query = "REMOTEDEBUGGER_DISCOVER_V1";
    public static async Task<List<Peer>> FindAsync(int milliseconds = 2500, CancellationToken ct = default)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var endpoints = new HashSet<string> { "255.255.255.255" };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up))
        foreach (var u in nic.GetIPProperties().UnicastAddresses.Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork))
        {
            var ip = u.Address.GetAddressBytes(); var mask = u.IPv4Mask.GetAddressBytes();
            endpoints.Add(new IPAddress(ip.Zip(mask, (a, b) => (byte)(a | ~b)).ToArray()).ToString());
        }
        foreach (string ep in endpoints) try { await udp.SendAsync(Encoding.UTF8.GetBytes(Query), new IPEndPoint(IPAddress.Parse(ep), Port), ct); } catch (SocketException) { }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(milliseconds);
        var peers = new Dictionary<string, Peer>();
        try
        {
            while (true)
            {
                var r = await udp.ReceiveAsync(timeout.Token);
                if (r.Buffer.Length > 2048) continue;
                try
                {
                    var p = JsonSerializer.Deserialize<Peer>(r.Buffer, Json.Options);
                    if (p != null && p.Port is > 0 and < 65536 && p.Fingerprint.Length == 64)
                    {
                        string supportId = p.SupportId;
                        if (supportId.Length > 0)
                        {
                            try { supportId = InternetSettings.DisplayId(InternetSettings.SessionId(supportId)); }
                            catch (ArgumentException) { continue; }
                        }
                        peers[r.RemoteEndPoint.Address.ToString()] = p with { Host = r.RemoteEndPoint.Address.ToString(), SupportId = supportId };
                    }
                }
                catch (JsonException) { }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        return PeerDiscovery.DistinctPeers(peers.Values);
    }
}

internal sealed record PeerDiscoveryResult(IReadOnlyList<Peer> Peers, bool UsedLanFallback);

internal static class PeerDiscovery
{
    internal static bool IsLocalPeer(Peer peer, string? localSupportId)
    {
        if (string.IsNullOrWhiteSpace(localSupportId)) return false;
        return string.Equals(peer.SupportId, localSupportId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(peer.Host, localSupportId, StringComparison.OrdinalIgnoreCase);
    }

    internal static List<Peer> DistinctPeers(IEnumerable<Peer> peers)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Peer>();
        foreach (var peer in peers)
        {
            string identity = peer.Fingerprint.Length == 64
                ? $"fingerprint:{peer.Fingerprint}"
                : $"endpoint:{peer.Host}:{peer.Port}";
            string invitation = "invitation:" + peer.SupportId;
            if (!seen.Contains(identity) && (peer.SupportId.Length == 0 || !seen.Contains(invitation)))
            {
                seen.Add(identity);
                if (peer.SupportId.Length > 0) seen.Add(invitation);
                result.Add(peer);
            }
        }
        return result;
    }

    public static async Task<PeerDiscoveryResult> FindAsync(
        bool privateInternet,
        Func<CancellationToken, Task<List<Peer>>> lanDiscovery,
        Func<CancellationToken, Task<List<Peer>>> relayDiscovery,
        CancellationToken ct = default)
    {
        if (!privateInternet) return new(DistinctPeers(await lanDiscovery(ct).ConfigureAwait(false)), false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        async Task<List<Peer>> Find(Func<CancellationToken, Task<List<Peer>>> discover)
        {
            try { return await discover(deadline.Token).ConfigureAwait(false); }
            catch (Exception) when (!ct.IsCancellationRequested) { return []; }
        }
        var lan = Find(lanDiscovery);
        var relay = Find(relayDiscovery);
        await Task.WhenAll(lan, relay).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return new(DistinctPeers(lan.Result.Concat(relay.Result)), relay.Result.Count == 0);
    }

    internal static Peer? Rebind(Peer previous, IEnumerable<Peer> peers) => peers.FirstOrDefault(peer =>
        previous.Fingerprint.Length == 64
            ? string.Equals(previous.Fingerprint, peer.Fingerprint, StringComparison.OrdinalIgnoreCase)
            : previous.SupportId.Length > 0
                ? string.Equals(previous.SupportId, peer.SupportId, StringComparison.OrdinalIgnoreCase)
                : PeerDiscoveryPolicy.IsSameEndpoint(previous.Host, previous.Port, peer.Host, peer.Port));
}

public sealed partial class AgentServer : IDisposable
{
    private readonly object transportLock = new();
    private TcpListener listener;
    private UdpClient discovery;
    private bool privateNetworkEnabled;
    private long transportGeneration;
    private readonly X509Certificate2 certificate;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim slots = new(12);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> running = new();
    private readonly ConcurrentDictionary<string, (string Signature, Lazy<Task<Reply>> Job)> requests = new();
    private readonly object authLock = new();
    private CancellationTokenSource grantLifetime = new();
    private readonly string authPath;
    private string tokenHash = "";
    private string controllerBinaryHash = "";
    private bool updateOnly;
    private readonly SupportSession session = new();
    private readonly SessionResumeStore resumeStore;
    private readonly AgentUpdateService updates;
    private readonly SemaphoreSlim updateGate = new(1, 1);
    private ResumableSession? resumed;
    private bool resumeAccepted;
    private readonly SemaphoreSlim pairingSlot = new(1, 1);
    private int disposed;
    private int terminating;
    private int started;
    private int listening;
    private long captureEpoch;
    private readonly SemaphoreSlim captureWake = new(0, 1);
    private void WakeCapture(bool force = true)
    {
        if (force) Interlocked.Increment(ref captureEpoch);
        if (captureWake.CurrentCount == 0) try { captureWake.Release(); } catch (SemaphoreFullException) { }
    }
    public PairingGate Pairing { get; } = new();
    public Operations Operations { get; }
    public string Fingerprint => certificate.GetCertHashString(HashAlgorithmName.SHA256);
    public int Port { get; }
    public bool IsListening => Volatile.Read(ref listening) != 0 && Volatile.Read(ref disposed) == 0;
    public bool Paired { get { lock (authLock) return tokenHash.Length != 0; } }
    public AgentUpdateProgress UpdateProgress => updates.Progress;
    public InternetAgent? Internet { get; private set; }
    public bool AdminMaintenanceEnabled => Operations.Maintenance.Enabled;
    public event Action<string>? Status;
    public event Action<AgentStopReason>? TerminationRequested;
    public SupportSessionSnapshot Session => session.Snapshot;
    public AgentServer(string root, int port = 45832, bool loopbackOnly = false, bool enableInternet = true, bool enableAdminMaintenance = true)
    {
        securityRoot = root;
        resumeStore = new(root);
        Directory.CreateDirectory(root); Port = port; authPath = Path.Combine(root, "agent.auth");
        string certPath = Path.Combine(root, "identity.pfx.dpapi");
        if (File.Exists(certPath)) certificate = new X509Certificate2(Vault.Read(certPath), (string?)null, X509KeyStorageFlags.UserKeySet);
        else
        {
            using var rsa = RSA.Create(3072);
            var req = new CertificateRequest("CN=RemoteDebugger", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var generated = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
            var pfx = generated.Export(X509ContentType.Pfx); Vault.Save(certPath, pfx);
            // Schannel needs a Windows user key container; EphemeralKeySet fails TLS on Windows.
            certificate = new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.UserKeySet);
        }
        // A normal launch always starts a new support session. Persisted grants from
        // older versions must never silently grant access on a fresh launch.
        if (File.Exists(authPath)) Vault.Save(authPath, []);
        string[] launchArguments = Environment.GetCommandLineArgs(); int resumeIndex = Array.IndexOf(launchArguments, "--resume-update");
        if (resumeIndex >= 0 && resumeIndex + 1 < launchArguments.Length)
            resumed = resumeStore.Restore(launchArguments[resumeIndex + 1]);
        if (resumed != null)
        {
            tokenHash = resumed.GrantHash; controllerBinaryHash = resumed.ControllerHash;
            updateOnly = resumed.UpdateOnly;
            session.Pair(Safety.Equal(controllerBinaryHash, ExecutableIdentity.Sha256));
        }
        else resumeStore.Clear();
        Operations = new Operations(root);
        Operations.Maintenance.SetEnabled(enableAdminMaintenance);
        updates = new AgentUpdateService(root, (context, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var reconnect = PrepareUpdateReconnect(context.Controller.Sha256, DateTimeOffset.UtcNow.AddMinutes(10));
            return Task.FromResult(new UpdateReconnectGrant(reconnect.Ticket, reconnect.ExpiresUtc));
        }, () => Fingerprint);
        updates.UpdateRestartRequested += () =>
        {
            // Planned replacement preserves the bounded reconnect grant. Explicit
            // termination takes the cancellation path below instead.
            if (Interlocked.CompareExchange(ref terminating, 2, 0) != 0) return;
            Dispose(); TerminationRequested?.Invoke(AgentStopReason.UpdateReplacement);
        };
        privateNetworkEnabled = !loopbackOnly;
        listener = new TcpListener(loopbackOnly ? IPAddress.Loopback : IPAddress.Any, port);
        discovery = new UdpClient(new IPEndPoint(loopbackOnly ? IPAddress.Loopback : IPAddress.Any, Discovery.Port));
        if (enableInternet && InternetSettings.Load(root) is { } internetSettings)
            Internet = new InternetAgent(root, internetSettings, resumed != null, AcceptInternetAsync, Fingerprint);
    }
    public void Start()
    {
        _ = ExecutableIdentity.Sha256;
        lock (transportLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (Volatile.Read(ref started) != 0) throw new InvalidOperationException("The agent listener has already started.");
            if (!Paired)
            {
                if (Internet != null) Pairing.OpenPrivate(Internet.AuthenticationSecret);
                else Pairing.Open();
            }
            listener.Start();
            Volatile.Write(ref started, 1);
            StartTransportLoopsLocked();
        }
        _ = Task.Run(WatchSessionAsync);
        Status?.Invoke("Agent ready for pairing; access is visible in this window.");
        Internet?.Start();
        if (Paired) _ = StartMaintenanceAsync();
    }

    public async Task SetAdminMaintenanceEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        Operations.Maintenance.SetEnabled(enabled);
        if (!enabled) requests.Clear();
        if (enabled && Paired) await StartMaintenanceAsync(ct);
    }
    public void EnablePrivateNetwork()
    {
        Exception? promotionFailure = null;
        lock (transportLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (Volatile.Read(ref started) == 0) throw new InvalidOperationException("Start the agent before enabling its private-network listener.");
            if (privateNetworkEnabled)
            {
                if (Volatile.Read(ref listening) == 0) throw new InvalidOperationException("The private-network listener is not accepting connections.");
                return;
            }

            Volatile.Write(ref listening, 0);
            try { listener.Stop(); } catch (SocketException) { }
            try { discovery.Dispose(); } catch { }

            try
            {
                (listener, discovery) = CreateStartedTransport(IPAddress.Any);
                privateNetworkEnabled = true;
                StartTransportLoopsLocked();
            }
            catch (SocketException ex)
            {
                promotionFailure = ex;
                try
                {
                    (listener, discovery) = CreateStartedTransport(IPAddress.Loopback);
                    privateNetworkEnabled = false;
                    StartTransportLoopsLocked();
                }
                catch (SocketException restoreFailure)
                {
                    throw new IOException("The private-network listener failed and loopback pairing could not be restored.",
                        new AggregateException(ex, restoreFailure));
                }
            }
        }

        if (promotionFailure != null)
        {
            Status?.Invoke("Private-network listening failed; loopback pairing remains available with the same code.");
            throw new IOException("The private-network listener could not start; loopback pairing was restored.", promotionFailure);
        }
        Status?.Invoke("Private-network listening is active; the current pairing code remains valid.");
    }
    private (TcpListener Listener, UdpClient Discovery) CreateStartedTransport(IPAddress address)
    {
        var nextListener = new TcpListener(address, Port);
        UdpClient? nextDiscovery = null;
        try
        {
            nextDiscovery = new UdpClient(new IPEndPoint(address, Discovery.Port));
            nextListener.Start();
            return (nextListener, nextDiscovery);
        }
        catch
        {
            try { nextListener.Stop(); } catch { }
            try { nextDiscovery?.Dispose(); } catch { }
            throw;
        }
    }
    private void StartTransportLoopsLocked()
    {
        TcpListener activeListener = listener;
        UdpClient activeDiscovery = discovery;
        long generation = ++transportGeneration;
        Volatile.Write(ref listening, 1);
        _ = Task.Run(() => AcceptAsync(activeListener, generation));
        _ = Task.Run(() => DiscoverAsync(activeDiscovery));
    }
    public (string Ticket, DateTimeOffset ExpiresUtc) PrepareUpdateReconnect(string replacementHash, DateTimeOffset deadline)
    {
        lock (authLock)
        {
            if (tokenHash.Length == 0 || !Safety.Equal(controllerBinaryHash, replacementHash))
                throw new UnauthorizedAccessException("Only the authenticated controller binary can replace the agent.");
            return resumeStore.Create(tokenHash, controllerBinaryHash, ExecutableIdentity.Sha256, replacementHash, deadline, updateOnly);
        }
    }
    private async Task WatchSessionAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(500, stop.Token); _ = Pairing.CurrentCode;
                if (securityCandidate is { Promoted: false } candidate && candidate.Expires <= DateTimeOffset.UtcNow) candidate.Agent.Dispose();
                if (session.ShouldExit)
                {
                    if (updateOnly && updates.PendingExitPlan == null) { ReleaseUpdateSession(); continue; }
                    try { await TerminateAsync(); return; }
                    catch (Exception ex) when (!stop.IsCancellationRequested)
                    {
                        Status?.Invoke("Session ended; retrying update cleanup: " + ex.Message);
                        await Task.Delay(TimeSpan.FromSeconds(5), stop.Token);
                    }
                }
                if (!Session.Connected && Session.HasPaired) Native.ReleaseAllInput();
            }
        }
        catch (OperationCanceledException) { }
    }
    public async Task TerminateAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        if (Interlocked.CompareExchange(ref terminating, 1, 0) == 2) return;
        session.End(); Native.ReleaseAllInput();
        foreach (var job in running.Values) job.Cancel();
        await updateGate.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref disposed) != 0) return;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(35));
            await updates.CancelActiveAsync(deadline.Token);
            // Retain the listener in an unauthorized idle state so the
            // controller can distinguish termination from a temporary outage.
            Revoke(); Volatile.Write(ref terminating, 2); TerminationRequested?.Invoke(AgentStopReason.SupportEnded);
        }
        finally { updateGate.Release(); }
    }
    public void Revoke()
    {
        if (securityCandidate is { Promoted: false } staged) staged.Agent.Dispose();
        resumeStore.Clear(); resumed = null; resumeAccepted = false;
        lock (authLock) { tokenHash = ""; grantLifetime.Cancel(); grantLifetime = new(); Vault.Save(authPath, []); Pairing.Close(); }
        foreach (var job in running.Values) job.Cancel();
        Native.ReleaseAllInput();
        Operations.Maintenance.Dispose();
        requests.Clear(); Status?.Invoke("Access revoked. Active operations cancelled.");
    }
    private async Task DiscoverAsync(UdpClient activeDiscovery)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var r = await activeDiscovery.ReceiveAsync(stop.Token);
                if (r.Buffer.Length == Discovery.Query.Length && Encoding.UTF8.GetString(r.Buffer) == Discovery.Query)
                    await activeDiscovery.SendAsync(JsonSerializer.SerializeToUtf8Bytes(
                        new Peer(Environment.MachineName, "", Port, Fingerprint, Internet?.SupportId ?? ""), Json.Options),
                        r.RemoteEndPoint, stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }
    private async Task AcceptAsync(TcpListener activeListener, long generation)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var tcp = await activeListener.AcceptTcpClientAsync(stop.Token);
                bool remoteAllowed = privateNetworkEnabled || tcp.Client.RemoteEndPoint is IPEndPoint remote && IsLocalPeer(remote.Address);
                if (!remoteAllowed || !slots.Wait(0)) { tcp.Dispose(); continue; }
                tcp.NoDelay = true; _ = ServeAsync(tcp);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        finally
        {
            lock (transportLock)
            {
                if (Volatile.Read(ref disposed) == 0 && generation == transportGeneration && ReferenceEquals(listener, activeListener))
                    Volatile.Write(ref listening, 0);
            }
        }
    }
    private static bool IsLocalPeer(IPAddress peer)
    {
        if (IPAddress.IsLoopback(peer)) return true;
        byte[] remote = peer.GetAddressBytes(); if (remote.Length != 4) return false;
        return NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up).SelectMany(n => n.GetIPProperties().UnicastAddresses).Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Any(a => a.Address.GetAddressBytes().Zip(a.IPv4Mask.GetAddressBytes(), (ip, mask) => (byte)(ip & mask)).SequenceEqual(remote.Zip(a.IPv4Mask.GetAddressBytes(), (ip, mask) => (byte)(ip & mask))));
    }
    private IReadOnlyList<DirectEndpoint> DirectEndpoints()
    {
        if (!privateNetworkEnabled) return [];
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string value)
        {
            if (IPAddress.TryParse(value, out var address) && address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                hosts.Add(address.ToString());
        }
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up))
            foreach (var address in nic.GetIPProperties().UnicastAddresses.Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork))
                Add(address.Address.ToString());
        if (Internet?.PublicIp is { Length: > 0 } publicIp) Add(publicIp);
        return hosts.Select(host => new DirectEndpoint(host, Port)).ToArray();
    }
    private async Task ServeAsync(TcpClient tcp)
    {
        using (tcp) await ServeAsync(tcp.GetStream());
    }
    private async Task AcceptInternetAsync(Stream transport)
    {
        if (Volatile.Read(ref disposed) != 0 || !slots.Wait(0)) return;
        await ServeAsync(transport);
    }
    private async Task ServeAsync(Stream transport, SecurityCandidate? candidate = null)
    {
        bool persistentInput = false;
        CancellationTokenRegistration persistentInputGrant = default;
        bool persistentUpdate = false;
        CancellationTokenRegistration persistentUpdateGrant = default;
        using (var tls = new SslStream(transport, true))
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(6));
            try
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token); handshake.CancelAfter(10000);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, ClientCertificateRequired = false }, handshake.Token);
                var r = await Wire.ReadAsync<Request>(tls, handshake.Token);
                while (true)
                {
                    if (candidate != null && !candidate.Promoted)
                    {
                        await ServeSecurityCandidateAsync(tls, r, candidate, timeout.Token);
                        return;
                    }
                    Reply reply;
                    if (r.Operation is "admin.inspect" or "admin.connect")
                    {
                        handshake.CancelAfter(TimeSpan.FromSeconds(30));
                        await ServeAdminAsync(tls, r, handshake.Token);
                        return;
                    }
                    if (r.Operation == "pair.v2")
                    {
                        if (Volatile.Read(ref terminating) != 0 || Volatile.Read(ref disposed) != 0)
                            throw new AuthenticationException("This support session has ended.");
                        if (!await pairingSlot.WaitAsync(0, handshake.Token)) throw new AuthenticationException("Another pairing attempt is in progress.");
                        try
                        {
                            handshake.CancelAfter(TimeSpan.FromSeconds(30));
                            await PairingTransport.AcceptAsync(tls, r, Pairing, Fingerprint, (token, controllerHash) =>
                            {
                                lock (authLock)
                                {
                                    tokenHash = Safety.Hash(token); controllerBinaryHash = controllerHash; grantLifetime.Cancel(); grantLifetime = new();
                                    updateOnly = false;
                                    updates.ResetControllerSynchronization();
                                    foreach (var job in running.Values) job.Cancel(); requests.Clear();
                                    session.Pair(Safety.Equal(controllerBinaryHash, ExecutableIdentity.Sha256));
                                }
                            }, handshake.Token);
                            _ = StartMaintenanceAsync(); Status?.Invoke("Controller paired. Synchronizing the support session.");
                        }
                        finally { pairingSlot.Release(); }
                        return;
                    }
                    else
                    {
                        bool authorized; CancellationToken grant; lock (authLock) { authorized = tokenHash.Length != 0 && Safety.Equal(tokenHash, Safety.Hash(r.Token ?? "")); grant = grantLifetime.Token; }
                        if (authorized && !PairingExchange.ValidHash(r.BinarySha256)) authorized = false;
                        if (authorized && Volatile.Read(ref terminating) != 0) authorized = false;
                        if (authorized && !Safety.Equal(controllerBinaryHash, r.BinarySha256!.ToUpperInvariant()))
                        {
                            if (updateOnly) { await Wire.WriteAsync(tls, Reply.Failure(r.Id, "access_denied", "Update authorization is bound to one executable."), timeout.Token); return; }
                            await updateGate.WaitAsync(timeout.Token);
                            try
                            {
                                if (Volatile.Read(ref terminating) != 0 || updates.PendingExitPlan != null) authorized = false;
                                else
                                {
                                    lock (authLock)
                                    {
                                        controllerBinaryHash = r.BinarySha256!.ToUpperInvariant();
                                        updates.ResetControllerSynchronization();
                                        session.SetBinaryMatched(Safety.Equal(controllerBinaryHash, ExecutableIdentity.Sha256));
                                    }
                                    Native.ReleaseAllInput();
                                    foreach (var job in running.Values) job.Cancel();
                                }
                            }
                            finally { updateGate.Release(); }
                        }
                        bool resumePending = resumed != null && !Volatile.Read(ref resumeAccepted);
                        if (resumePending && resumed!.ExpiresUtc <= DateTimeOffset.UtcNow) { session.End(); authorized = false; }
                        if (authorized && !resumePending && r.Operation != "session.disconnect") session.Observe();
                        if (!authorized || session.ShouldExit) reply = Reply.Failure(r.Id, "access_denied", "This support session is not authorized or has ended.");
                        else if (updateOnly && !IsUpdateSessionOperation(r.Operation))
                            reply = Reply.Failure(r.Id, "update_only", "This grant permits software updates only.");
                        else if (r.Operation == "update.release")
                        {
                            await updateGate.WaitAsync(timeout.Token);
                            try
                            {
                                await updates.CancelActiveAsync(timeout.Token);
                                await Wire.WriteAsync(tls, Reply.Success(r.Id, new { released = true }), timeout.Token);
                                ReleaseUpdateSession(); return;
                            }
                            finally { updateGate.Release(); }
                        }
                        else if (resumePending && r.Operation is not ("update.open" or "update.resume" or "update.cancel" or "update.status" or "session.end" or "revoke"))
                            reply = Reply.Failure(r.Id, "resume_required", "Present the bounded update reconnect ticket before resuming support.");
                        else if (r.Operation == "screen.refresh")
                        {
                            WakeCapture(); reply = Reply.Success(r.Id, new { requested = true });
                        }
                        else if (r.Operation == "session.heartbeat")
                        {
                            session.Observe();
                            reply = Reply.Success(r.Id, new { session = Session, agentBinarySha256 = ExecutableIdentity.Sha256, controllerBinarySha256 = controllerBinaryHash, binaryMatched = Session.BinaryMatched, processId = Environment.ProcessId, maintenance = Operations.Maintenance.Status });
                        }
                        else if (r.Operation == "session.disconnect") { Native.ReleaseAllInput(); session.Disconnect(); reply = Reply.Success(r.Id, new { session = Session }); }
                        else if (r.Operation == "update.resume")
                        {
                            if (resumed == null || resumed.ExpiresUtc <= DateTimeOffset.UtcNow ||
                                !Safety.Equal(resumed.TicketHash, Safety.Hash(r.Args.Str("ticket"))))
                                reply = Reply.Failure(r.Id, "resume_denied", "Update reconnect authorization is invalid or expired.");
                            else
                            {
                                Volatile.Write(ref resumeAccepted, true); session.Observe();
                                reply = Reply.Success(r.Id, new { resumed = true, agentBinarySha256 = ExecutableIdentity.Sha256, binaryMatched = Session.BinaryMatched });
                            }
                        }
                        else if (r.Operation is "session.end" or "revoke")
                        {
                            // Keep this socket alive until the controller knows whether
                            // an armed replacement was safely cancelled.
                            if (Interlocked.CompareExchange(ref terminating, 1, 0) == 2) return;
                            session.End(); Native.ReleaseAllInput();
                            foreach (var job in running.Values) job.Cancel();
                            await updateGate.WaitAsync(timeout.Token);
                            try
                            {
                                using var endDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                                await updates.CancelActiveAsync(endDeadline.Token);
                                await Wire.WriteAsync(tls, Reply.Success(r.Id, new { ended = true }), timeout.Token);
                                Revoke(); Volatile.Write(ref terminating, 2); TerminationRequested?.Invoke(AgentStopReason.SupportEnded);
                            }
                            finally { updateGate.Release(); }
                            return;
                        }
                        else if (r.Operation == "update.open" && !Guid.TryParse(r.Id, out _))
                            reply = Reply.Failure(r.Id, "invalid_id", "Request id must be a UUID.");
                        else if (r.Operation == "update.open")
                        {
                            // The controller keeps this authenticated stream open
                            // for the ordered update transfer. Tie its lifetime to
                            // the support grant and extend the normal short RPC
                            // timeout while chunks are being acknowledged.
                            if (!persistentUpdate)
                            {
                                persistentUpdate = true;
                                persistentUpdateGrant = grant.Register(timeout.Cancel);
                            }
                            timeout.CancelAfter(TimeSpan.FromHours(12));
                            reply = Reply.Success(r.Id, new { ready = true });
                        }
                        else if (updates.IsOperation(r.Operation))
                        {
                            await updateGate.WaitAsync(timeout.Token);
                            try
                            {
                                if (Volatile.Read(ref terminating) != 0) reply = Reply.Failure(r.Id, "session_ended", "The support session is ending.");
                                else
                                {
                                    using var updateDeadline = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, grant);
                                    updateDeadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(r.TimeoutSeconds, 1, 120)));
                                    reply = await updates.DispatchAsync(r, updateDeadline.Token);
                                    if (r.Operation == "update.confirm" && reply.Ok)
                                    {
                                        session.SetBinaryMatched(Safety.Equal(controllerBinaryHash, ExecutableIdentity.Sha256));
                                        if (Session.BinaryMatched) { resumeStore.Clear(); resumed = null; }
                                    }
                                    if (r.Operation == "update.cancel" && reply.Ok) { resumeStore.Clear(); resumed = null; }
                                }
                                try
                                {
                                    await Wire.WriteAsync(tls, reply, timeout.Token);
                                }
                                catch when (r.Operation == "update.commit" && reply.Ok)
                                {
                                    resumeStore.Clear(); resumed = null; resumeAccepted = false;
                                    using var abortDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                                    try { await updates.CancelActiveAsync(abortDeadline.Token); }
                                    catch (Exception ex)
                                    {
                                        Interlocked.CompareExchange(ref terminating, 1, 0); session.End();
                                        Status?.Invoke("Update reply failed; retrying protected cancellation: " + ex.Message);
                                    }
                                    throw;
                                }
                                updates.NotifyReplySent(r, reply);
                            }
                            finally { updateGate.Release(); }
                            if (persistentUpdate) { r = await Wire.ReadAsync<Request>(tls, timeout.Token); continue; }
                            return;
                        }
                        else if (r.Operation == "connection.candidates")
                            reply = Reply.Success(r.Id, new { candidates = DirectEndpoints() });
                        else if (!Session.BinaryMatched && r.Operation is not ("status" or "maintenance.status" or "cancel"))
                            reply = Reply.Failure(r.Id, "binary_mismatch", "Synchronize the agent with the controller executable before starting support.");
                        else if (r.Operation == "ui.input.open" && !Guid.TryParse(r.Id, out _))
                            reply = Reply.Failure(r.Id, "invalid_id", "Request id must be a UUID.");
                        else if (r.Operation == "ui.input.open")
                        {
                            // The controller keeps this authenticated stream open for
                            // ordered mouse/keyboard requests. Tie its lifetime to the
                            // support grant so revocation closes the stream and releases
                            // any native input that may still be held.
                            if (!persistentInput)
                            {
                                persistentInput = true;
                                persistentInputGrant = grant.Register(timeout.Cancel);
                            }
                            timeout.CancelAfter(TimeSpan.FromHours(12));
                            reply = Reply.Success(r.Id, new { ready = true });
                        }
                        else if (r.Operation == "security.stage")
                        {
                            await updateGate.WaitAsync(timeout.Token);
                            try { reply = Reply.Success(r.Id, await StageSecurityAsync(r.Args, timeout.Token)); }
                            finally { updateGate.Release(); }
                        }
                        else if (r.Operation == "security.status")
                            reply = Reply.Success(r.Id, new { securityId = InternetSettings.Load(securityRoot)?.SecurityId ?? "", fingerprint = Fingerprint, computer = Environment.MachineName });
                        else if (r.Operation is "security.commit" or "security.release")
                        {
                            var currentSecurity = InternetSettings.Load(securityRoot);
                            if (currentSecurity?.SecurityId.Length is not > 0 || currentSecurity.SecurityId != r.Args.Str("securityId"))
                                reply = Reply.Failure(r.Id, "security_identity_mismatch", "The requested security profile is not active.");
                            else if (r.Operation == "security.commit")
                                reply = Reply.Success(r.Id, new { securityId = currentSecurity.SecurityId, protectedAccess = true });
                            else
                            {
                                await Wire.WriteAsync(tls, Reply.Success(r.Id, new { released = true }), timeout.Token);
                                lock (authLock)
                                {
                                    tokenHash = ""; controllerBinaryHash = ""; grantLifetime.Cancel(); grantLifetime = new();
                                    foreach (var job in running.Values) job.Cancel(); requests.Clear();
                                    Operations.Maintenance.End(); updates.ResetControllerSynchronization();
                                    session.ResetForPairing(); Pairing.OpenPrivate(Internet!.AuthenticationSecret);
                                }
                                return;
                            }
                        }
                        else if (r.Operation is "file.upload" or "file.download")
                        {
                            timeout.CancelAfter(TimeSpan.FromHours(12));
                            using var transferGrant = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, grant);
                            await Operations.TransferAsync(tls, r, session.Observe, transferGrant.Token);
                            return;
                        }
                        else if (r.Operation == "screen.stream") { using var streamGrant = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, grant); await StreamAsync(tls, r, streamGrant.Token); return; }
                        else if (r.Operation == "cancel") { if (running.TryGetValue(r.Args.Str("id"), out var job)) job.Cancel(); reply = Reply.Success(r.Id, new { cancellationRequested = true }); }
                        else if ((r.Operation is "maintenance.session" or "maintenance.elevated") && !Operations.Maintenance.Enabled)
                            reply = Reply.Failure(r.Id, "maintenance_disabled", MaintenanceSession.DisabledMessage);
                        else if (!Guid.TryParse(r.Id, out _)) reply = Reply.Failure(r.Id, "invalid_id", "Request id must be a UUID.");
                        else if (r.Operation is "screenshot" or "monitors" or "status" or "file.info" or "file.read" or "files" or "processes" or "process.info" or "system" or "network" or "services" or "events" or "history" or "windows" or "ui.inspect" or "upload.chunk" or "upload.status" or "ui.input") reply = await ExecuteAsync(r, grant);
                        else if (requests.Count >= 2048 && !requests.ContainsKey(r.Id)) reply = Reply.Failure(r.Id, "session_limit", "Restart the agent to clear its 2048-mutation retry cache.");
                        else
                        {
                            string signature = Safety.Hash(r.Operation + r.Args.GetRawText());
                            var entry = requests.GetOrAdd(r.Id, _ => (signature, new Lazy<Task<Reply>>(() => ExecuteAsync(r, grant))));
                            reply = entry.Signature == signature ? await entry.Job.Value : Reply.Failure(r.Id, "id_conflict", "Request id already used with different arguments.");
                        }
                    }
                    await Wire.WriteAsync(tls, reply, timeout.Token);
                    if (r.Operation == "session.disconnect" || (!persistentInput && !persistentUpdate)) return;
                    r = await Wire.ReadAsync<Request>(tls, timeout.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Status?.Invoke("TLS/session error: " + ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                persistentInputGrant.Dispose();
                persistentUpdateGrant.Dispose();
                if (persistentInput) { Operations.Maintenance.EndInput(); Native.ReleaseAllInput(); }
                slots.Release();
            }
        }
    }
    private async Task StreamAsync(SslStream tls, Request request, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, 300))); running[request.Id] = cts;
        if (request.Args.Str("codec") == "auto") { await StreamAdaptiveAsync(tls, request, cts.Token); return; }
        int fps = StreamPolicy.ClampFps(request.Args.Int("fps", StreamPolicy.MaximumFps)); long sequence = 0; string? previousFingerprint = null;
        using var captureBuffer = new CaptureBuffer();
        long epoch = Volatile.Read(ref captureEpoch); int unchanged = 0;
        Status?.Invoke("Live screen stream active (encrypted). Target " + fps + " fps.");
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                long currentEpoch = Volatile.Read(ref captureEpoch);
                if (currentEpoch != epoch) { previousFingerprint = null; epoch = currentEpoch; unchanged = 0; }
                var capture = DesktopCapture.CaptureStream(request.Args.Int("monitor"), request.Args.Int("maxWidth", 1920), request.Args.Int("quality", 65), sequence, previousFingerprint, captureBuffer);
                previousFingerprint = capture.Fingerprint;
                if (capture.Frame is not { } frame)
                {
                    await captureWake.WaitAsync(Math.Min(1000, 1000 / fps * (1 + ++unchanged / 5)), cts.Token);
                    continue;
                }
                unchanged = 0;
                await Wire.WriteAsync(tls, Reply.Success(request.Id, frame), cts.Token);
                // One frame in flight; the next capture starts only after receipt.
                // The controller independently replaces any unrendered older frame.
                using var ackTimeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token); ackTimeout.CancelAfter(5000);
                var ack = await Wire.ReadAsync<System.Text.Json.JsonElement>(tls, ackTimeout.Token);
                if (ack.Long("sequence", -1) != frame.Sequence) throw new InvalidDataException("Invalid stream acknowledgement.");
                session.Observe();
                sequence++;
                double wait = 1000.0 / fps - started.Elapsed.TotalMilliseconds; if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), cts.Token);
            }
        }
        catch (Exception ex) when (ex is not (IOException or OperationCanceledException)) { await Wire.WriteAsync(tls, Reply.Failure(request.Id, "stream_failed", ex.Message), ct); }
        finally { running.TryRemove(request.Id, out _); Native.ReleaseAllInput(); Status?.Invoke("Live screen stream ended."); }
    }
    private async Task StreamAdaptiveAsync(SslStream tls, Request request, CancellationToken ct)
    {
        int fps = StreamPolicy.ClampFps(request.Args.Int("fps", StreamPolicy.MaximumFps));
        int monitor = request.Args.Int("monitor"); int maxWidth = request.Args.Int("maxWidth", 1920); int quality = request.Args.Int("quality", 65);
        bool controllerCanH264 = !request.Args.TryGetProperty("h264", out var h264Support) || h264Support.ValueKind != JsonValueKind.False;
        long sequence = 0; string? previousFingerprint = null; var state = new AdaptiveStreamState();
        using var captureBuffer = new CaptureBuffer();
        long epoch = Volatile.Read(ref captureEpoch); int unchanged = 0;
        try
        {
            BitmapCaptureResult firstResult;
            try { firstResult = DesktopCapture.CaptureBitmap(monitor, previousFingerprint: null, captureBuffer); }
            catch (Exception ex) { await Wire.WriteAsync(tls, Reply.Failure(request.Id, "stream_failed", ex.Message), ct); return; }
            previousFingerprint = firstResult.Fingerprint;
            if (firstResult.Capture is not { } first)
            {
                await Wire.WriteAsync(tls, Reply.Failure(request.Id, "stream_failed", "Desktop capture returned no frame."), ct); return;
            }

            using (first)
            {
                if (controllerCanH264)
                {
                    try
                    {
                        var size = H264Dimensions(first, maxWidth);
                        state.Encoder = new H264Encoder(first.Bitmap.Width, first.Bitmap.Height, size.Width, size.Height, fps, quality); state.Codec = "h264";
                    }
                    catch (Exception ex) { state.FallbackReason = "x264 unavailable: " + ex.Message; }
                }
                var start = Reply.Success(request.Id, new { type = "stream_started", codec = state.Codec, reason = state.FallbackReason, geometry = first.Geometry });
                await Wire.WriteAsync(tls, start, ct);
                Status?.Invoke($"Live screen stream active ({state.Codec}). Target {fps} fps.");
                await SendAdaptiveFrameAsync(tls, first, state, sequence, fps, maxWidth, quality, ct);
                sequence++;
                double firstWait = 1000.0 / fps; if (firstWait > 0) await Task.Delay(TimeSpan.FromMilliseconds(firstWait), ct);
            }

            while (!ct.IsCancellationRequested)
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                long currentEpoch = Volatile.Read(ref captureEpoch);
                if (currentEpoch != epoch) { previousFingerprint = null; epoch = currentEpoch; unchanged = 0; }
                var capture = DesktopCapture.CaptureBitmap(monitor, previousFingerprint, captureBuffer);
                previousFingerprint = capture.Fingerprint;
                if (capture.Capture is not { } current)
                {
                    await captureWake.WaitAsync(Math.Min(1000, 1000 / fps * (1 + ++unchanged / 5)), ct);
                    continue;
                }
                unchanged = 0;
                using (current)
                {
                    await SendAdaptiveFrameAsync(tls, current, state, sequence, fps, maxWidth, quality, ct);
                    sequence++;
                }
                double wait = 1000.0 / fps - started.Elapsed.TotalMilliseconds; if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), ct);
            }
        }
        catch (Exception ex) when (ex is not (IOException or OperationCanceledException))
        {
            try
            {
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { type = "stream_error", error = "stream_failed", message = ex.Message }, Json.Options);
                await Wire.WriteStreamPacketAsync(tls, new StreamPacket(StreamPacketKind.Control, -1, DateTimeOffset.UtcNow, 0, payload), ct);
            }
            catch { }
        }
        finally
        {
            state.Encoder?.Dispose(); running.TryRemove(request.Id, out _); Native.ReleaseAllInput(); Status?.Invoke("Live screen stream ended.");
        }
    }

    private sealed class AdaptiveStreamState
    {
        public H264Encoder? Encoder;
        public string Codec = "jpeg";
        public string? FallbackReason;
        public int SlowFrames;
        public DesktopGeometry? Geometry;
    }

    private async Task SendAdaptiveFrameAsync(SslStream tls, CapturedDesktop capture, AdaptiveStreamState state, long sequence, int fps, int maxWidth, int quality, CancellationToken ct)
    {
        if (state.Geometry != null && state.Geometry != capture.Geometry)
            throw new IOException("Display layout changed; reopen the stream to negotiate its geometry.");
        state.Geometry = capture.Geometry;
        byte[]? bytes = null; StreamPacketKind kind = StreamPacketKind.Jpeg; double encodeMs = capture.CopyMs;
        if (state.Codec == "h264" && state.Encoder != null)
        {
            try
            {
                var encoded = state.Encoder.Encode(capture.Bitmap, sequence); encodeMs = capture.CopyMs + encoded.EncodeMs;
                if (StreamPolicy.IsH264EncodeSlow(encoded.EncodeMs, fps)) state.SlowFrames++; else state.SlowFrames = 0;
                if (StreamPolicy.ShouldFallbackToJpeg(state.SlowFrames))
                {
                    state.FallbackReason = $"x264 encode exceeded {StreamPolicy.H264EncodeBudgetMs(fps):F0} ms for {StreamPolicy.H264SlowFrameCount} frames";
                    state.Encoder.Dispose(); state.Encoder = null; state.Codec = "jpeg"; state.SlowFrames = 0;
                    await SendCodecChangeAsync(tls, state.Codec, state.FallbackReason, ct);
                }
                else { kind = StreamPacketKind.H264; bytes = encoded.Data; }
            }
            catch (Exception ex)
            {
                state.FallbackReason = "x264 encode failed: " + ex.Message;
                state.Encoder?.Dispose(); state.Encoder = null; state.Codec = "jpeg"; state.SlowFrames = 0;
                await SendCodecChangeAsync(tls, state.Codec, state.FallbackReason, ct);
            }
        }
        if (state.Codec == "jpeg")
        {
            var encoded = DesktopCapture.EncodeJpeg(capture, maxWidth, quality, sequence); bytes = encoded.Bytes; encodeMs = encoded.Frame.CaptureEncodeMs; kind = StreamPacketKind.Jpeg;
        }
        if (bytes is null) throw new InvalidOperationException("Adaptive stream produced no frame.");
        await Wire.WriteStreamPacketAsync(tls, new StreamPacket(kind, sequence, capture.CapturedUtc, encodeMs, bytes), ct);
        using var ackTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct); ackTimeout.CancelAfter(5000);
        var ack = await Wire.ReadAsync<JsonElement>(tls, ackTimeout.Token);
        if (ack.Long("sequence", -1) != sequence) throw new InvalidDataException("Invalid stream acknowledgement.");
        session.Observe();
    }

    private async Task SendCodecChangeAsync(SslStream tls, string codec, string reason, CancellationToken ct)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { type = "codec_changed", codec, reason }, Json.Options);
        await Wire.WriteStreamPacketAsync(tls, new StreamPacket(StreamPacketKind.Control, -1, DateTimeOffset.UtcNow, 0, payload), ct);
        using var ackTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct); ackTimeout.CancelAfter(5000);
        var ack = await Wire.ReadAsync<JsonElement>(tls, ackTimeout.Token);
        if (ack.Long("sequence", -1) != -1) throw new InvalidDataException("Invalid codec change acknowledgement.");
        session.Observe();
    }

    private static (int Width, int Height) H264Dimensions(CapturedDesktop capture, int maxWidth)
    {
        int width = maxWidth <= 0 ? capture.Bitmap.Width : Math.Min(capture.Bitmap.Width, Math.Clamp(maxWidth, 320, 3840));
        width = Math.Max(2, width & ~1);
        int height = Math.Max(2, (int)Math.Round(capture.Bitmap.Height * (double)width / capture.Bitmap.Width) & ~1);
        return (width, height);
    }

    private async Task<Reply> ExecuteAsync(Request r, CancellationToken grant)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, grant); cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(r.TimeoutSeconds, 1, 300)));
        running[r.Id] = cts; var begin = DateTimeOffset.UtcNow; Reply reply;
        bool noisy = r.Operation is "ui.input" or "upload.chunk" or "file.read";
        if (!noisy) Status?.Invoke($"{begin:HH:mm:ss}  {r.Operation}  {r.Id[..8]}");
        try { cts.Token.ThrowIfCancellationRequested(); reply = Reply.Success(r.Id, await Operations.ExecuteAsync(r.Operation, r.Args, cts.Token)); if (r.Operation.StartsWith("ui.", StringComparison.Ordinal)) WakeCapture(false); }
        catch (OperationCanceledException) { reply = Reply.Failure(r.Id, "cancelled_or_timeout", "Operation cancelled or its deadline expired. Inspect state before retrying a mutation."); }
        catch (InputBlockedException ex) { reply = Reply.Failure(r.Id, "input_blocked", ex.Message); }
        catch (UnauthorizedAccessException) { reply = Reply.Failure(r.Id, "permission_denied", "Windows denied access. An elevated operation may be required."); }
        catch (Exception ex) { reply = Reply.Failure(r.Id, "operation_failed", ex.Message); }
        finally { running.TryRemove(r.Id, out _); }
        if (!noisy) Operations.Record(r.Id, r.Operation, begin, reply.Ok, reply.Error, reply.Data); return reply;
    }
    private async Task StartMaintenanceAsync(CancellationToken requested = default)
    {
        if (!Operations.Maintenance.Enabled) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, requested);
        try
        {
            await Operations.Maintenance.StartAsync(linked.Token);
            if (Operations.Maintenance.Enabled) Status?.Invoke("Administrator maintenance active for this session.");
        }
        catch (Exception ex) { if (!stop.IsCancellationRequested) Status?.Invoke("Administrator maintenance unavailable: " + ex.Message); }
    }
    public void Dispose()
    {
        TcpListener activeListener;
        UdpClient activeDiscovery;
        lock (transportLock)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            Volatile.Write(ref listening, 0);
            transportGeneration++;
            activeListener = listener;
            activeDiscovery = discovery;
        }
        Pairing.Close(); stop.Cancel(); Internet?.Dispose(); securityCandidate?.Agent.Dispose(); Operations.Maintenance.Dispose(); Native.ReleaseAllInput();
        try { activeListener.Stop(); } catch { }
        try { activeDiscovery.Dispose(); } catch { }
        updates.Dispose();
    }
}

public sealed partial class RemoteClient
{
    private readonly string controllerBinarySha256 = ExecutableIdentity.Sha256;
    private readonly Func<Connection, CancellationToken, Task<Stream>> transportFactory;
    private readonly SemaphoreSlim inputGate = new(1, 1);
    private readonly SemaphoreSlim updateGate = new(1, 1);
    private PersistentChannel? inputChannel;
    private PersistentChannel? updateChannel;
    private int legacyUpdateOperations;
    public Connection Connection { get; private set; }
    internal string AdminRoot { get; set; } = Vault.DefaultRoot;
    public static string DefaultPath => Path.Combine(Vault.DefaultRoot, "controller.connection");
    public RemoteClient(Connection connection) : this(connection, OpenAuthenticatedTransportOnceAsync) { discoverRoutes = true; }
    internal RemoteClient(Connection connection, string controllerBinarySha256) : this(connection)
    {
        UpdatePolicy.ValidateSha256(controllerBinarySha256, "controller executable");
        this.controllerBinarySha256 = controllerBinarySha256;
    }
    internal RemoteClient(Connection connection, Func<Connection, CancellationToken, Task<Stream>> transportFactory)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        this.transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
    }
    public static RemoteClient Load(string? path = null) => new(JsonSerializer.Deserialize<Connection>(Vault.Read(path ?? DefaultPath), Json.Options)!);
    public void Save(string? path = null) => Vault.Save(path ?? DefaultPath, JsonSerializer.SerializeToUtf8Bytes(Connection, Json.Options));
    public async Task PairAsync(string code, CancellationToken ct = default)
    {
        await CloseInputChannelAsync().ConfigureAwait(false);
        await CloseUpdateChannelAsync().ConfigureAwait(false);
        if (await TryResumeSavedConnectionAsync(ct).ConfigureAwait(false)) return;
        Connection = await PairingTransport.PairAsync(Connection, code, ct).ConfigureAwait(false);
    }
    public bool UsesDirectTransport => Connection.DirectHost.Length > 0;
    internal async Task<IReadOnlyList<DirectEndpoint>> GetDirectEndpointsAsync(CancellationToken ct = default)
    {
        if (Connection.RelayUrl.Length == 0) return [];
        var data = Require(await CallAsync("connection.candidates", ct: ct, seconds: 10).ConfigureAwait(false));
        if (!data.TryGetProperty("candidates", out var candidates)) return [];
        return candidates.Deserialize<List<DirectEndpoint>>(Json.Options)?
            .Where(candidate => IsLanAddress(candidate.Host) && candidate.Port is > 0 and < 65536)
            .DistinctBy(candidate => $"{candidate.Host}:{candidate.Port}", StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToArray() ?? [];
    }
    internal async Task<bool> TryPreferDirectAsync(IEnumerable<DirectEndpoint> candidates, CancellationToken ct = default)
    {
        if (Connection.RelayUrl.Length == 0) return true;
        Connection relay = Connection;
        async Task<DirectEndpoint?> Probe(DirectEndpoint candidate)
        {
            if (!(IsLanAddress(candidate.Host) || candidate == relay.WanEndpoint) ||
                candidate.Port is not (> 0 and < 65536) || CoolingDown(candidate)) return null;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(TimeSpan.FromSeconds(2));
            var direct = relay with
            {
                Host = candidate.Host,
                Port = candidate.Port,
                RelayUrl = "",
                RelayAccessKey = "",
                DirectHost = "",
                DirectPort = 0
            };
            try
            {
                await using var tls = await transportFactory(direct, attempt.Token).ConfigureAwait(false);
                var id = Guid.NewGuid().ToString();
                // Status is intentionally allowed before binary synchronization so
                // a newly paired controller can move the synchronization itself
                // onto the direct endpoint.
                await Wire.WriteAsync(tls, new Request(id, relay.Token, "status", Json.Element(new { }), 5, controllerBinarySha256), attempt.Token).ConfigureAwait(false);
                var reply = await Wire.ReadAsync<Reply>(tls, attempt.Token).ConfigureAwait(false);
                if (reply.Ok) return candidate;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (Exception) when (!ct.IsCancellationRequested) { }
            CoolDown(candidate.Host, candidate.Port);
            return null;
        }
        var endpoints = candidates.Where(c => IsLanAddress(c.Host)).Take(16)
            .Concat(relay.WanEndpoint is { } wan ? [wan] : []).Distinct();
        foreach (var group in endpoints.OrderByDescending(c => IsLanAddress(c.Host)).GroupBy(c => IsLanAddress(c.Host)))
        {
            var results = await Task.WhenAll(group.Select(Probe)).ConfigureAwait(false);
            if (results.FirstOrDefault(candidate => candidate != null) is not { } preferred) continue;
            Connection = Connection with { DirectHost = preferred.Host, DirectPort = preferred.Port };
            return true;
        }
        return false;
    }
    public async Task<JsonElement> HeartbeatAsync(CancellationToken ct = default) => Require(await CallAsync("session.heartbeat", ct: ct, seconds: 5).ConfigureAwait(false));
    public async Task EndSessionAsync(CancellationToken ct = default)
    {
        await CloseInputChannelAsync().ConfigureAwait(false);
        await CloseUpdateChannelAsync().ConfigureAwait(false);
        // A user can press End while the agent is between updater processes.
        // Keep the old session token solely to terminate the resumed listener.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(50));
        while (true)
        {
            try { Require(await CallAsync("session.end", ct: deadline.Token, seconds: 35).ConfigureAwait(false)); return; }
            catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException)
            {
                await Task.Delay(500, deadline.Token).ConfigureAwait(false);
            }
        }
    }
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        try { Require(await CallAsync("session.disconnect", ct: ct, seconds: 5).ConfigureAwait(false)); }
        finally
        {
            await CloseInputChannelAsync().ConfigureAwait(false);
            await CloseUpdateChannelAsync().ConfigureAwait(false);
        }
    }
    public async Task<Reply> CallAsync(string operation, object? args = null, CancellationToken ct = default, string? id = null, int seconds = 60)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 300) + 15));
        await using var tls = await OpenTransportAsync(timeout.Token).ConfigureAwait(false);
        var request = new Request(id ?? Guid.NewGuid().ToString(), Connection.Token, operation, args is JsonElement e ? e : Json.Element(args ?? new { }), seconds, controllerBinarySha256);
        await Wire.WriteAsync(tls, request, timeout.Token); return await Wire.ReadAsync<Reply>(tls, timeout.Token);
    }
    public async Task<Reply> SendInputAsync(object? args = null, CancellationToken ct = default, int seconds = 3)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 300)));
        await inputGate.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            PersistentChannel? channel = inputChannel;
            try
            {
                if (channel == null)
                {
                    Stream transport = await OpenTransportAsync(timeout.Token).ConfigureAwait(false);
                    channel = new PersistentChannel(transport, Connection.Token, controllerBinarySha256, "ui.input.open");
                    await channel.OpenAsync(timeout.Token, seconds).ConfigureAwait(false);
                    inputChannel = channel;
                }
                return await channel.CallAsync("ui.input", args, timeout.Token, seconds).ConfigureAwait(false);
            }
            catch
            {
                if (channel != null && ReferenceEquals(inputChannel, channel)) inputChannel = null;
                if (channel != null)
                {
                    try { await channel.DisposeAsync().ConfigureAwait(false); } catch { }
                }
                throw;
            }
        }
        finally { inputGate.Release(); }
    }

    /// <summary>Send update operations over one authenticated connection for the transfer lifetime.</summary>
    internal async Task<Reply> SendUpdateAsync(string operation, object? args = null, CancellationToken ct = default, int seconds = 60)
    {
        if (string.IsNullOrWhiteSpace(operation)) throw new ArgumentException("An update operation is required.", nameof(operation));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 300)));
        await updateGate.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref legacyUpdateOperations) != 0)
                return await CallAsync(operation, args, timeout.Token, seconds: seconds).ConfigureAwait(false);

            PersistentChannel? channel = updateChannel;
            try
            {
                if (channel == null)
                {
                    Stream transport = await OpenTransportAsync(timeout.Token).ConfigureAwait(false);
                    channel = new PersistentChannel(transport, Connection.Token, controllerBinarySha256, "update.open");
                    await channel.OpenAsync(timeout.Token, seconds).ConfigureAwait(false);
                    updateChannel = channel;
                }
                return await channel.CallAsync(operation, args, timeout.Token, seconds).ConfigureAwait(false);
            }
            catch (RemoteOperationException ex) when (IsLegacyUpdateChannelUnsupported(ex))
            {
                if (channel != null && ReferenceEquals(updateChannel, channel)) updateChannel = null;
                if (channel != null)
                {
                    try { await channel.DisposeAsync().ConfigureAwait(false); } catch { }
                }
                // Agents shipped before the persistent update channel still expose
                // the same authenticated update operations as one-shot RPCs. Keep
                // the transfer compatible without weakening authentication or the
                // update transaction checks.
                Volatile.Write(ref legacyUpdateOperations, 1);
                return await CallAsync(operation, args, timeout.Token, seconds: seconds).ConfigureAwait(false);
            }
            catch
            {
                if (channel != null && ReferenceEquals(updateChannel, channel)) updateChannel = null;
                if (channel != null)
                {
                    try { await channel.DisposeAsync().ConfigureAwait(false); } catch { }
                }
                throw;
            }
        }
        finally { updateGate.Release(); }
    }
    private async Task CloseInputChannelAsync()
    {
        await inputGate.WaitAsync().ConfigureAwait(false);
        try
        {
            PersistentChannel? channel = inputChannel;
            inputChannel = null;
            if (channel != null)
            {
                try { await channel.DisposeAsync().ConfigureAwait(false); } catch { }
            }
        }
        finally { inputGate.Release(); }
    }

    internal async Task CloseUpdateChannelAsync()
    {
        await updateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            PersistentChannel? channel = updateChannel;
            updateChannel = null;
            if (channel != null)
            {
                try { await channel.DisposeAsync().ConfigureAwait(false); } catch { }
            }
        }
        finally
        {
            Volatile.Write(ref legacyUpdateOperations, 0);
            updateGate.Release();
        }
    }

    private static bool IsLegacyUpdateChannelUnsupported(RemoteOperationException ex) =>
        ex.Code == "binary_mismatch" ||
        (ex.Code == "operation_failed" && ex.Message.Contains("update.open", StringComparison.OrdinalIgnoreCase));
    private static async Task<Stream> OpenAuthenticatedTransportOnceAsync(Connection connection, CancellationToken ct)
    {
        Stream transport = await ConnectionTransport.OpenAsync(connection, ct).ConfigureAwait(false);
        SslStream? tls = null;
        try
        {
            tls = new SslStream(transport, false, (_, cert, _, _) => cert != null &&
                Safety.Equal(Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())), connection.Fingerprint.ToUpperInvariant().Replace(":", "")));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "RemoteDebugger",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, ct).ConfigureAwait(false);
            return tls;
        }
        catch
        {
            if (tls != null) await tls.DisposeAsync().ConfigureAwait(false);
            else await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
    private sealed class PersistentChannel(Stream stream, string token, string binarySha256, string openOperation) : IAsyncDisposable
    {
        private readonly Stream stream = stream;
        private readonly string token = token;
        private readonly string binarySha256 = binarySha256;
        private readonly string openOperation = openOperation;
        private int disposed;
        public async Task OpenAsync(CancellationToken ct, int seconds)
        {
            Reply reply = await SendAsync(openOperation, Json.Element(new { }), ct, seconds).ConfigureAwait(false);
            RemoteClient.Require(reply);
        }
        public async Task<Reply> CallAsync(string operation, object? args, CancellationToken ct, int seconds)
            => await SendAsync(operation, args is JsonElement e ? e : Json.Element(args ?? new { }), ct, seconds).ConfigureAwait(false);
        private async Task<Reply> SendAsync(string operation, object? args, CancellationToken ct, int seconds)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var request = new Request(Guid.NewGuid().ToString(), token, operation, args is JsonElement e ? e : Json.Element(args ?? new { }), seconds, binarySha256);
            await Wire.WriteAsync(stream, request, ct).ConfigureAwait(false);
            Reply reply = await Wire.ReadAsync<Reply>(stream, ct).ConfigureAwait(false);
            if (!string.Equals(reply.Id, request.Id, StringComparison.Ordinal))
                throw new InvalidDataException("Reply did not match its request.");
            return reply;
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }
    public async Task StreamAsync(Func<ScreenFrame, Task> present, int fps = StreamPolicy.MaximumFps, int monitor = 0, int seconds = 300, CancellationToken ct = default)
    {
        await LatestFrameStream.RunAsync<ScreenFrame>(
            (publish, receiveToken) => ReceiveFramesAsync(publish, fps, monitor, seconds, receiveToken), present, ct);
    }

    internal async Task StreamAdaptiveAsync(Func<DecodedStreamFrame, Task> present, Action<string, string?>? codecChanged, int fps = StreamPolicy.MaximumFps, int monitor = 0, int seconds = 300, CancellationToken ct = default)
    {
        await LatestFrameStream.RunAsync<DecodedStreamFrame>(
            (publish, receiveToken) => ReceiveAdaptiveFramesAsync(publish, codecChanged, fps, monitor, seconds, receiveToken), present, ct);
    }

    private async Task ReceiveAdaptiveFramesAsync(Action<DecodedStreamFrame> publish, Action<string, string?>? codecChanged, int fps, int monitor, int seconds, CancellationToken ct)
    {
        using var connecting = CancellationTokenSource.CreateLinkedTokenSource(ct); connecting.CancelAfter(TimeSpan.FromSeconds(30));
        await using var tls = await OpenTransportAsync(connecting.Token);
        await Wire.WriteAsync(tls, new Request(Guid.NewGuid().ToString(), Connection.Token, "screen.stream", Json.Element(new { codec = "auto", h264 = FfmpegRuntime.IsAvailable(), fps, monitor, maxWidth = 1920, quality = 65 }), seconds, controllerBinarySha256), ct);
        var start = await Wire.ReadAsync<Reply>(tls, ct); Require(start);
        if (start.Data.Str("type") != "stream_started") throw new InvalidDataException("Missing stream negotiation.");
        string codec = start.Data.Str("codec", "jpeg");
        var geometry = start.Data.TryGetProperty("geometry", out var geometryValue) ? geometryValue.Deserialize<DesktopGeometry>(Json.Options) : null;
        if (geometry == null) throw new InvalidDataException("Missing stream geometry.");
        codecChanged?.Invoke(codec, start.Data.Str("reason"));
        H264Decoder? decoder = codec == "h264" ? new H264Decoder() : null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var packet = await Wire.ReadStreamPacketAsync(tls, ct);
                if (packet.Kind == StreamPacketKind.Control)
                {
                    var control = JsonSerializer.Deserialize<JsonElement>(packet.Payload, Json.Options);
                    string type = control.Str("type");
                    if (type == "codec_changed")
                    {
                        codec = control.Str("codec", "jpeg");
                        decoder?.Dispose(); decoder = codec == "h264" ? new H264Decoder() : null;
                        codecChanged?.Invoke(codec, control.Str("reason"));
                        await Wire.WriteAsync(tls, new { sequence = -1 }, ct);
                        continue;
                    }
                    if (type == "stream_error") throw new IOException(control.Str("message", "Remote stream failed."));
                    throw new InvalidDataException("Unknown adaptive stream control packet.");
                }

                Bitmap? image = null;
                if (packet.Kind == StreamPacketKind.Jpeg)
                {
                    using var imageStream = new MemoryStream(packet.Payload, writable: false);
                    using var source = Image.FromStream(imageStream);
                    image = new Bitmap(source);
                }
                else if (packet.Kind == StreamPacketKind.H264)
                {
                    if (decoder == null) throw new InvalidDataException("H.264 packet received without a decoder.");
                    image = decoder.Decode(packet.Payload);
                }
                else throw new InvalidDataException("Unknown adaptive stream frame packet.");

                await Wire.WriteAsync(tls, new { sequence = packet.Sequence }, ct);
                if (image != null) publish(new DecodedStreamFrame(packet.Sequence, packet.CapturedUtc, geometry, codec, image, packet.CaptureEncodeMs, packet.Payload.Length));
            }
        }
        finally { decoder?.Dispose(); }
    }

    private async Task ReceiveFramesAsync(Action<ScreenFrame> publish, int fps, int monitor, int seconds, CancellationToken ct)
    {
        using var connecting = CancellationTokenSource.CreateLinkedTokenSource(ct); connecting.CancelAfter(TimeSpan.FromSeconds(30));
        await using var tls = await OpenTransportAsync(connecting.Token);
        await Wire.WriteAsync(tls, new Request(Guid.NewGuid().ToString(), Connection.Token, "screen.stream", Json.Element(new { fps, monitor, maxWidth = 1920, quality = 65 }), seconds, controllerBinarySha256), ct);
        while (!ct.IsCancellationRequested)
        {
            var reply = await Wire.ReadAsync<Reply>(tls, ct); Require(reply);
            var frame = reply.Data.Deserialize<ScreenFrame>(Json.Options) ?? throw new IOException("Missing frame.");
            publish(frame); await Wire.WriteAsync(tls, new { sequence = frame.Sequence }, ct);
        }
    }
    public static JsonElement Require(Reply reply) => reply.Ok ? reply.Data : throw new RemoteOperationException(reply.Error ?? "operation_failed", reply.Message ?? "Remote operation failed.");
    private sealed record UploadState(string Transfer, string Sha256, long Size, string Path);
    public async Task<JsonElement> UploadAsync(string file, string relativePath, CancellationToken ct = default, IProgress<FileTransferProgress>? progress = null)
    {
        await using var input = File.OpenRead(file); string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)); input.Position = 0;
        string resume = Path.Combine(Vault.DefaultRoot, "uploads", Safety.Hash(Connection.Fingerprint + "|" + relativePath) + ".state");
        UploadState? state = File.Exists(resume) ? JsonSerializer.Deserialize<UploadState>(Vault.Read(resume), Json.Options) : null; long offset = 0;
        if (state != null && (state.Sha256 != hash || state.Size != input.Length)) { File.Delete(resume); state = null; }
        if (state != null)
        {
            var status = await CallAsync("upload.status", new { transfer = state.Transfer }, ct);
            if (status.Ok) offset = status.Data.Long("offset");
            else
            {
                var target = await CallAsync("file.info", new { path = relativePath }, ct);
                if (target.Ok && target.Data.Str("sha256") == hash && target.Data.Long("size") == input.Length) { File.Delete(resume); return target.Data; }
                state = null;
            }
        }
        if (state == null)
        {
            state = new UploadState(Guid.NewGuid().ToString("N"), hash, input.Length, relativePath); Vault.Save(resume, JsonSerializer.SerializeToUtf8Bytes(state, Json.Options));
            Require(await CallAsync("upload.begin", new { transfer = state.Transfer, path = relativePath, size = input.Length, sha256 = hash }, ct));
        }
        await using var channel = await OpenTransportAsync(ct);
        var request = new Request(Guid.NewGuid().ToString(), Connection.Token, "file.upload", Json.Element(new { transfer = state.Transfer }), 300, controllerBinarySha256);
        await Wire.WriteAsync(channel, request, ct);
        var ready = Require(await Wire.ReadAsync<Reply>(channel, ct));
        offset = ready.Long("offset");
        if (ready.Long("size") != input.Length || offset < 0 || offset > input.Length) throw new IOException("Invalid upload resume offset.");
        input.Position = offset;
        progress?.Report(new(offset, input.Length, 0));
        await BulkTransfer.SendAsync(input, channel, offset, input.Length, value => progress?.Report(new(value, input.Length, value - offset)), ct);
        var committed = Require(await Wire.ReadAsync<Reply>(channel, ct));
        if (!Safety.Equal(committed.Str("sha256"), hash) || committed.Long("size") != input.Length) throw new IOException("Upload verification failed.");
        File.Delete(resume); return committed;
    }
    public async Task DownloadAsync(string remote, string local, CancellationToken ct = default, IProgress<FileTransferProgress>? progress = null)
    {
        string temp = local + ".rd-" + Safety.Hash(Connection.Fingerprint + "|" + remote)[..16] + ".partial";
        string metadata = temp + ".sha256";
        string hash = File.Exists(metadata) ? await File.ReadAllTextAsync(metadata, ct) : "";
        long offset = File.Exists(temp) && hash.Length == 64 ? new FileInfo(temp).Length : 0;
        await using var channel = await OpenTransportAsync(ct);
        var request = new Request(Guid.NewGuid().ToString(), Connection.Token, "file.download", Json.Element(new { path = remote, offset, sha256 = hash }), 300, controllerBinarySha256);
        await Wire.WriteAsync(channel, request, ct);
        var info = Require(await Wire.ReadAsync<Reply>(channel, ct));
        long length = info.Long("size"); offset = info.Long("offset"); hash = info.Str("sha256");
        if (!PairingExchange.ValidHash(hash) || offset < 0 || offset > length) throw new IOException("Invalid download metadata.");
        await using (var output = new FileStream(temp, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, BulkTransfer.ChunkSize, FileOptions.Asynchronous))
        {
            output.SetLength(offset); output.Position = offset;
            await File.WriteAllTextAsync(metadata, hash, ct);
            progress?.Report(new(offset, length, 0));
            await BulkTransfer.ReceiveAsync(channel, output, offset, length, value => progress?.Report(new(value, length, value - offset)), ct);
        }
        Require(await Wire.ReadAsync<Reply>(channel, ct));
        await using (var verify = File.OpenRead(temp))
            if (!Safety.Equal(Convert.ToHexString(await SHA256.HashDataAsync(verify, ct)), hash))
            { File.Delete(metadata); throw new IOException("Download hash mismatch; retry to restart the transfer."); }
        File.Move(temp, local, true); File.Delete(metadata);
    }
}

public sealed record FileTransferProgress(long TransferredBytes, long TotalBytes, long BytesThisAttempt = 0);
