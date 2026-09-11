using System.Collections.Concurrent;
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
public sealed record Peer(string Name, string Host, int Port, string Fingerprint);
public sealed record Connection(string Host, int Port, string Fingerprint, string Token);

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
                try { var p = JsonSerializer.Deserialize<Peer>(r.Buffer, Json.Options); if (p != null && p.Port is > 0 and < 65536 && p.Fingerprint.Length == 64) peers[r.RemoteEndPoint.Address.ToString()] = p with { Host = r.RemoteEndPoint.Address.ToString() }; } catch (JsonException) { }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        return peers.Values.ToList();
    }
}

public sealed class AgentServer : IDisposable
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
    public PairingGate Pairing { get; } = new();
    public Operations Operations { get; }
    public string Fingerprint => certificate.GetCertHashString(HashAlgorithmName.SHA256);
    public int Port { get; }
    public bool IsListening => Volatile.Read(ref listening) != 0 && Volatile.Read(ref disposed) == 0;
    public bool Paired { get { lock (authLock) return tokenHash.Length != 0; } }
    public event Action<string>? Status;
    public event Action? TerminationRequested;
    public SupportSessionSnapshot Session => session.Snapshot;
    public AgentServer(string root, int port = 45832, bool loopbackOnly = false)
    {
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
            session.Pair(Safety.Equal(controllerBinaryHash, ExecutableIdentity.Sha256));
        }
        else resumeStore.Clear();
        Operations = new Operations(root);
        updates = new AgentUpdateService(root, (context, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var reconnect = PrepareUpdateReconnect(context.Controller.Sha256, DateTimeOffset.UtcNow.AddMinutes(10));
            return Task.FromResult(new UpdateReconnectGrant(reconnect.Ticket, reconnect.ExpiresUtc));
        });
        updates.UpdateRestartRequested += () =>
        {
            // Planned replacement preserves the bounded reconnect grant. Explicit
            // termination takes the cancellation path below instead.
            if (Interlocked.CompareExchange(ref terminating, 2, 0) != 0) return;
            Dispose(); TerminationRequested?.Invoke();
        };
        privateNetworkEnabled = !loopbackOnly;
        listener = new TcpListener(loopbackOnly ? IPAddress.Loopback : IPAddress.Any, port);
        discovery = new UdpClient(new IPEndPoint(loopbackOnly ? IPAddress.Loopback : IPAddress.Any, Discovery.Port));
    }
    public void Start()
    {
        _ = ExecutableIdentity.Sha256;
        lock (transportLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (Volatile.Read(ref started) != 0) throw new InvalidOperationException("The agent listener has already started.");
            if (!Paired) Pairing.Open();
            listener.Start();
            Volatile.Write(ref started, 1);
            StartTransportLoopsLocked();
        }
        _ = Task.Run(WatchSessionAsync);
        Status?.Invoke("Agent ready for pairing; access is visible in this window.");
        if (Paired) _ = StartMaintenanceAsync();
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
            return resumeStore.Create(tokenHash, controllerBinaryHash, ExecutableIdentity.Sha256, replacementHash, deadline);
        }
    }
    private async Task WatchSessionAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(500, stop.Token); _ = Pairing.CurrentCode;
                if (session.ShouldExit)
                {
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
            Revoke(); Dispose(); TerminationRequested?.Invoke();
        }
        finally { updateGate.Release(); }
    }
    public void Revoke()
    {
        resumeStore.Clear(); resumed = null; resumeAccepted = false;
        lock (authLock) { tokenHash = ""; grantLifetime.Cancel(); grantLifetime = new(); Vault.Save(authPath, []); Pairing.Close(); }
        foreach (var job in running.Values) job.Cancel();
        Native.ReleaseAllInput();
        Operations.Maintenance.Dispose();
        requests.Clear(); Status?.Invoke("Access revoked. Active operations cancelled.");
    }
    private async Task DiscoverAsync(UdpClient activeDiscovery)
    {
        try { while (!stop.IsCancellationRequested) { var r = await activeDiscovery.ReceiveAsync(stop.Token); if (r.Buffer.Length == Discovery.Query.Length && Encoding.UTF8.GetString(r.Buffer) == Discovery.Query) await activeDiscovery.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new Peer(Environment.MachineName, "", Port, Fingerprint), Json.Options), r.RemoteEndPoint, stop.Token); } }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }
    private async Task AcceptAsync(TcpListener activeListener, long generation)
    {
        try { while (!stop.IsCancellationRequested) { var tcp = await activeListener.AcceptTcpClientAsync(stop.Token); if (tcp.Client.RemoteEndPoint is not IPEndPoint remote || !IsLocalPeer(remote.Address) || !slots.Wait(0)) { tcp.Dispose(); continue; } tcp.NoDelay = true; _ = ServeAsync(tcp); } }
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
    private async Task ServeAsync(TcpClient tcp)
    {
        using (tcp)
        using (var tls = new SslStream(tcp.GetStream(), false))
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(6));
            try
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token); handshake.CancelAfter(10000);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, ClientCertificateRequired = false }, handshake.Token);
                var r = await Wire.ReadAsync<Request>(tls, handshake.Token);
                Reply reply;
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
                    else if (resumePending && r.Operation is not ("update.resume" or "session.end" or "revoke"))
                        reply = Reply.Failure(r.Id, "resume_required", "Present the bounded update reconnect ticket before resuming support.");
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
                            Revoke(); Dispose(); TerminationRequested?.Invoke();
                        }
                        finally { updateGate.Release(); }
                        return;
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
                        return;
                    }
                    else if (!Session.BinaryMatched && r.Operation is not ("status" or "maintenance.status" or "cancel"))
                        reply = Reply.Failure(r.Id, "binary_mismatch", "Synchronize the agent with the controller executable before starting support.");
                    else if (r.Operation == "screen.stream") { using var streamGrant = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, grant); await StreamAsync(tls, r, streamGrant.Token); return; }
                    else if (r.Operation == "cancel") { if (running.TryGetValue(r.Args.Str("id"), out var job)) job.Cancel(); reply = Reply.Success(r.Id, new { cancellationRequested = true }); }
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
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Status?.Invoke("TLS/session error: " + ex.GetType().Name + ": " + ex.Message); }
            finally { slots.Release(); }
        }
    }
    private async Task StreamAsync(SslStream tls, Request request, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, 300))); running[request.Id] = cts;
        int fps = StreamPolicy.ClampFps(request.Args.Int("fps", StreamPolicy.MaximumFps)); long sequence = 0;
        Status?.Invoke("Live screen stream active (encrypted). Target " + fps + " fps.");
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                var frame = DesktopCapture.Capture(request.Args.Int("monitor"), request.Args.Int("maxWidth", 1920), request.Args.Int("quality", 65), sequence++);
                await Wire.WriteAsync(tls, Reply.Success(request.Id, frame), cts.Token);
                // One frame in flight: presenter acknowledgement prevents a stale video queue.
                using var ackTimeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token); ackTimeout.CancelAfter(5000);
                var ack = await Wire.ReadAsync<System.Text.Json.JsonElement>(tls, ackTimeout.Token);
                if (ack.Long("sequence", -1) != frame.Sequence) throw new InvalidDataException("Invalid stream acknowledgement.");
                session.Observe();
                double wait = 1000.0 / fps - started.Elapsed.TotalMilliseconds; if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), cts.Token);
            }
        }
        catch (Exception ex) when (ex is not (IOException or OperationCanceledException)) { await Wire.WriteAsync(tls, Reply.Failure(request.Id, "stream_failed", ex.Message), ct); }
        finally { running.TryRemove(request.Id, out _); Native.ReleaseAllInput(); Status?.Invoke("Live screen stream ended."); }
    }
    private async Task<Reply> ExecuteAsync(Request r, CancellationToken grant)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, grant); cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(r.TimeoutSeconds, 1, 300)));
        running[r.Id] = cts; var begin = DateTimeOffset.UtcNow; Reply reply;
        bool noisy = r.Operation is "ui.input" or "upload.chunk" or "file.read";
        if (!noisy) Status?.Invoke($"{begin:HH:mm:ss}  {r.Operation}  {r.Id[..8]}");
        try { cts.Token.ThrowIfCancellationRequested(); reply = Reply.Success(r.Id, await Operations.ExecuteAsync(r.Operation, r.Args, cts.Token)); }
        catch (OperationCanceledException) { reply = Reply.Failure(r.Id, "cancelled_or_timeout", "Operation cancelled or its deadline expired. Inspect state before retrying a mutation."); }
        catch (UnauthorizedAccessException) { reply = Reply.Failure(r.Id, "permission_denied", "Windows denied access. An elevated operation may be required."); }
        catch (Exception ex) { reply = Reply.Failure(r.Id, "operation_failed", ex.Message); }
        finally { running.TryRemove(r.Id, out _); }
        if (!noisy) Operations.Record(r.Id, r.Operation, begin, reply.Ok, reply.Error, reply.Data); return reply;
    }
    private async Task StartMaintenanceAsync()
    {
        try { await Operations.Maintenance.StartAsync(stop.Token); Status?.Invoke("Administrator maintenance active for this session."); }
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
        Pairing.Close(); stop.Cancel(); Operations.Maintenance.Dispose(); Native.ReleaseAllInput();
        try { activeListener.Stop(); } catch { }
        try { activeDiscovery.Dispose(); } catch { }
        updates.Dispose();
    }
}

public sealed class RemoteClient(Connection connection)
{
    public Connection Connection { get; private set; } = connection;
    public static string DefaultPath => Path.Combine(Vault.DefaultRoot, "controller.connection");
    public static RemoteClient Load(string? path = null) => new(JsonSerializer.Deserialize<Connection>(Vault.Read(path ?? DefaultPath), Json.Options)!);
    public void Save(string? path = null) => Vault.Save(path ?? DefaultPath, JsonSerializer.SerializeToUtf8Bytes(Connection, Json.Options));
    public async Task PairAsync(string code, CancellationToken ct = default)
    {
        Connection = await PairingTransport.PairAsync(Connection, code, ct).ConfigureAwait(false);
    }
    public async Task<JsonElement> HeartbeatAsync(CancellationToken ct = default) => Require(await CallAsync("session.heartbeat", ct: ct, seconds: 5).ConfigureAwait(false));
    public async Task EndSessionAsync(CancellationToken ct = default)
    {
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
    public async Task DisconnectAsync(CancellationToken ct = default) { Require(await CallAsync("session.disconnect", ct: ct, seconds: 5).ConfigureAwait(false)); }
    public async Task<Reply> CallAsync(string operation, object? args = null, CancellationToken ct = default, string? id = null, int seconds = 60)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 300) + 15));
        using var tcp = new TcpClient { NoDelay = true }; await tcp.ConnectAsync(Connection.Host, Connection.Port, timeout.Token);
        using var tls = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => cert != null && Safety.Equal(Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())), Connection.Fingerprint.ToUpperInvariant().Replace(":", "")));
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "RemoteDebugger", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, timeout.Token);
        var request = new Request(id ?? Guid.NewGuid().ToString(), Connection.Token, operation, args is JsonElement e ? e : Json.Element(args ?? new { }), seconds, ExecutableIdentity.Sha256);
        await Wire.WriteAsync(tls, request, timeout.Token); return await Wire.ReadAsync<Reply>(tls, timeout.Token);
    }
    public async Task StreamAsync(Func<ScreenFrame, Task> present, int fps = StreamPolicy.MaximumFps, int monitor = 0, int seconds = 300, CancellationToken ct = default)
    {
        using var tcp = new TcpClient(); await tcp.ConnectAsync(Connection.Host, Connection.Port, ct); tcp.NoDelay = true;
        using var tls = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => cert != null && Safety.Equal(Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())), Connection.Fingerprint.ToUpperInvariant().Replace(":", "")));
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "RemoteDebugger", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, ct);
        await Wire.WriteAsync(tls, new Request(Guid.NewGuid().ToString(), Connection.Token, "screen.stream", Json.Element(new { fps, monitor, maxWidth = 1920, quality = 65 }), seconds, ExecutableIdentity.Sha256), ct);
        while (!ct.IsCancellationRequested)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(10000);
            var reply = await Wire.ReadAsync<Reply>(tls, timeout.Token); Require(reply);
            var frame = reply.Data.Deserialize<ScreenFrame>(Json.Options) ?? throw new IOException("Missing frame.");
            await present(frame); await Wire.WriteAsync(tls, new { sequence = frame.Sequence }, ct);
        }
    }
    public static JsonElement Require(Reply reply) => reply.Ok ? reply.Data : throw new InvalidOperationException($"{reply.Error}: {reply.Message}");
    private sealed record UploadState(string Transfer, string Sha256, long Size, string Path);
    public async Task<JsonElement> UploadAsync(string file, string relativePath, CancellationToken ct = default)
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
        string transfer = state.Transfer; input.Position = offset; byte[] buffer = new byte[256 * 1024]; int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0) { Require(await CallAsync("upload.chunk", new { transfer, offset, data = Convert.ToBase64String(buffer, 0, n) }, ct)); offset += n; }
        var committed = Require(await CallAsync("upload.commit", new { transfer }, ct)); File.Delete(resume); return committed;
    }
    public async Task DownloadAsync(string remote, string local, CancellationToken ct = default)
    {
        var info = Require(await CallAsync("file.info", new { path = remote }, ct)); long length = info.Long("size");
        string temp = local + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var output = File.Create(temp))
            {
                for (long offset = 0; offset < length;)
                {
                    var chunk = Require(await CallAsync("file.read", new { path = remote, offset }, ct)); var bytes = Convert.FromBase64String(chunk.Str("data"));
                    if (bytes.Length == 0) throw new IOException("Remote file changed during download."); await output.WriteAsync(bytes, ct); offset += bytes.Length;
                }
            }
            await using (var verify = File.OpenRead(temp)) if (!Safety.Equal(Convert.ToHexString(await SHA256.HashDataAsync(verify, ct)), info.Str("sha256"))) throw new IOException("Download hash mismatch; source may have changed.");
            File.Move(temp, local, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
