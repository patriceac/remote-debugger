using System.IO.Pipes;
using System.ServiceProcess;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

internal static class SupportService
{
    public static int Run()
    {
        if (Environment.UserInteractive) return 3;
        ServiceBase.Run(new WindowsSupportService());
        return 0;
    }

    private sealed class WindowsSupportService : ServiceBase
    {
        private CancellationTokenSource? lifetime;
        private SupportBrokerHost? host;

        public WindowsSupportService()
        {
            ServiceName = SupportPlatformPaths.ServiceName;
            CanStop = true;
            CanShutdown = true;
            AutoLog = true;
        }

        protected override void OnStart(string[] args)
        {
            lifetime = new CancellationTokenSource();
            host = new SupportBrokerHost(lifetime.Token, () =>
            {
                try { Stop(); } catch (InvalidOperationException) { }
            });
            host.Start();
        }

        protected override void OnStop() => StopHost();
        protected override void OnShutdown() => StopHost();

        private void StopHost()
        {
            lifetime?.Cancel();
            host?.Dispose();
            lifetime?.Dispose();
            host = null;
            lifetime = null;
        }
    }
}

internal sealed class SupportBrokerHost : IDisposable
{
    private readonly CancellationToken lifetime;
    private readonly SupportConfiguration configuration;
    private readonly PrivilegedUpdateManager updates;
    private readonly Action requestStop;
    private readonly SemaphoreSlim clients = new(16, 16);
    private Task? server;
    private long lastActivity = DateTime.UtcNow.Ticks;
    private int activeClients;
    private int disposed;

    public SupportBrokerHost(CancellationToken lifetime, Action requestStop)
    {
        this.lifetime = lifetime;
        this.requestStop = requestStop;
        configuration = JsonSerializer.Deserialize<SupportConfiguration>(File.ReadAllText(SupportPlatformPaths.ConfigurationPath), Json.Options)
            ?? throw new InvalidDataException("Support service configuration is empty.");
        if (configuration.ProtocolVersion != SupportPlatformPaths.ProtocolVersion)
            throw new InvalidDataException("Support service configuration protocol is incompatible.");
        if (!string.Equals(Path.GetFullPath(Environment.ProcessPath!), Path.GetFullPath(SupportPlatformPaths.ServiceExecutable), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Support service executable is not running from its protected provisioned path.");
        _ = AuthenticodeVerifier.VerifyPinnedTrusted(Environment.ProcessPath!, configuration.PublisherThumbprint);
        updates = new PrivilegedUpdateManager(configuration, lifetime);
    }

    public void Start()
    {
        server = Task.Run(AcceptAsync);
        _ = Task.Run(WatchIdleAsync);
    }

    private async Task WatchIdleAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), lifetime);
                if (Volatile.Read(ref activeClients) == 0 && !updates.HasActiveWork &&
                    DateTime.UtcNow - new DateTime(Volatile.Read(ref lastActivity), DateTimeKind.Utc) >= TimeSpan.FromMinutes(1))
                {
                    requestStop();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task AcceptAsync()
    {
        var security = SupportPipeSecurity.Create(configuration.RegisteredUserSid);
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await clients.WaitAsync(lifetime);
                NamedPipeServerStream pipe;
                try
                {
                    pipe = NamedPipeServerStreamAcl.Create(SupportPlatformPaths.PipeName, PipeDirection.InOut, 16,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                    await pipe.WaitForConnectionAsync(lifetime);
                }
                catch
                {
                    clients.Release();
                    throw;
                }
                Interlocked.Increment(ref activeClients);
                _ = HandleAsync(pipe);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        string? requestId = null;
        using (pipe)
        {
            try
            {
                var caller = SupportPipeIdentity.VerifyClient(pipe, configuration);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(35));
                var request = await Wire.ReadAsync<Request>(pipe, requestTimeout.Token);
                requestId = request.Id;
                if (!Guid.TryParse(request.Id, out _)) throw new ArgumentException("Broker request id must be a UUID.");
                if (request.Operation == "maintenance.open")
                {
                    var lease = caller.CreateLease();
                    lease.Validate();
                    await Wire.WriteAsync(pipe, Reply.Success(request.Id, new { active = true, leaseId = lease.LeaseId, processId = lease.ProcessId, sessionId = lease.SessionId }), requestTimeout.Token);
                    await ServeMaintenanceAsync(pipe, lease, lifetime);
                    return;
                }

                requestTimeout.CancelAfter(request.Operation switch
                {
                    "update.stage" => TimeSpan.FromMinutes(3),
                    "update.arm" => TimeSpan.FromMinutes(1),
                    "firewall.ensure" => TimeSpan.FromMinutes(1),
                    _ => TimeSpan.FromSeconds(35)
                });

                object result = request.Operation switch
                {
                    "platform.status" => await GetStatusAsync(caller, requestTimeout.Token),
                    "firewall.ensure" => await FirewallManager.EnsureAsync(configuration.RegisteredApplicationPath, requestTimeout.Token),
                    "update.stage" => await updates.StageAsync(caller, request.Args, requestTimeout.Token),
                    "update.arm" => await updates.ArmAsync(caller, request.Args, requestTimeout.Token),
                    "update.startupHealthy" => await updates.ReportStartupHealthyAsync(caller, request.Args, requestTimeout.Token),
                    "update.remoteHealthy" => await updates.ReportRemoteHealthyAsync(caller, request.Args, requestTimeout.Token),
                    "update.status" => updates.Status(request.Args),
                    "update.cancel" => await updates.CancelAsync(request.Args, requestTimeout.Token),
                    _ => throw new ArgumentException("Unsupported privileged broker operation.")
                };
                await Wire.WriteAsync(pipe, Reply.Success(request.Id, result), requestTimeout.Token);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or IOException or OperationCanceledException)
            {
                try
                {
                    string code = ex switch
                    {
                        UnauthorizedAccessException => "broker_identity_rejected",
                        OperationCanceledException => "broker_cancelled",
                        ArgumentException or InvalidDataException => "broker_invalid_request",
                        _ => "broker_operation_failed"
                    };
                    await Wire.WriteAsync(pipe, Reply.Failure(requestId ?? Guid.NewGuid().ToString(), code, ex.Message), CancellationToken.None);
                }
                catch { }
            }
            finally
            {
                Volatile.Write(ref lastActivity, DateTime.UtcNow.Ticks);
                Interlocked.Decrement(ref activeClients);
                clients.Release();
            }
        }
    }

    private async Task<object> GetStatusAsync(VerifiedProcessIdentity caller, CancellationToken ct) => new
    {
        protocolVersion = SupportPlatformPaths.ProtocolVersion,
        registeredApplicationPath = configuration.RegisteredApplicationPath,
        publisherThumbprint = configuration.PublisherThumbprint,
        serviceVersion = configuration.ServiceVersion,
        identityVerified = true,
        processId = caller.ProcessId,
        sessionId = caller.SessionId,
        firewallReady = await FirewallManager.IsReadyAsync(configuration.RegisteredApplicationPath, ct),
        message = "Privileged local support is available for this signed interactive application."
    };

    private static async Task ServeMaintenanceAsync(NamedPipeServerStream pipe, MaintenanceLease lease, CancellationToken lifetime)
    {
        while (!lifetime.IsCancellationRequested && pipe.IsConnected)
        {
            Request request;
            try { request = await Wire.ReadAsync<Request>(pipe, lifetime); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { return; }
            try { MaintenanceLease.ValidateCommand(request); }
            catch (ArgumentException ex)
            {
                await Wire.WriteAsync(pipe, Reply.Failure(request.Id, "maintenance_invalid_request", ex.Message), lifetime);
                continue;
            }

            using var job = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            job.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
            using var disconnectWatch = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            byte[] marker = new byte[1];
            Task<int> disconnected = pipe.ReadAsync(marker.AsMemory(), disconnectWatch.Token).AsTask();
            Task<object> running = Operations.RunAsync(request.Args.Str("file"), request.Args.Strings("arguments"), job.Token);
            Task first = await Task.WhenAny(running, disconnected);
            if (first == disconnected)
            {
                job.Cancel();
                try { await running; } catch { }
                return;
            }
            disconnectWatch.Cancel();
            try { await disconnected; } catch (OperationCanceledException) { }
            Reply reply;
            try { reply = Reply.Success(request.Id, await running); }
            catch (OperationCanceledException) { reply = Reply.Failure(request.Id, "maintenance_cancelled", "Privileged command was cancelled or reached its deadline."); }
            catch (Exception ex) { reply = Reply.Failure(request.Id, "maintenance_failed", ex.Message); }
            try { await Wire.WriteAsync(pipe, reply, lifetime); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { return; }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        updates.Dispose();
        clients.Dispose();
    }
}
