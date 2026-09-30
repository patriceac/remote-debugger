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
            host = new SupportBrokerHost(lifetime.Token);
            host.Start();
        }

        protected override void OnStop() => StopHost();
        protected override void OnShutdown() => StopHost(windowsShutdown: true);

        private void StopHost(bool windowsShutdown = false)
        {
            lifetime?.Cancel();
            host?.Dispose(windowsShutdown);
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
    private readonly PrivilegedPowerManager power;
    private readonly UnattendedSupportHost unattended;
    private readonly SemaphoreSlim clients = new(16, 16);
    private Task? server;
    private int disposed;

    public SupportBrokerHost(CancellationToken lifetime)
    {
        this.lifetime = lifetime;
        configuration = JsonSerializer.Deserialize<SupportConfiguration>(File.ReadAllText(SupportPlatformPaths.ConfigurationPath), Json.Options)
            ?? throw new InvalidDataException("Support service configuration is empty.");
        if (configuration.ProtocolVersion != SupportPlatformPaths.ProtocolVersion)
            throw new InvalidDataException("Support service configuration protocol is incompatible.");
        if (!string.Equals(Path.GetFullPath(Environment.ProcessPath!), Path.GetFullPath(SupportPlatformPaths.ServiceExecutable), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Support service executable is not running from its protected provisioned path.");
        unattended = new UnattendedSupportHost(configuration, lifetime);
        unattended.Initialize();
        updates = new PrivilegedUpdateManager(configuration, lifetime, unattended.LaunchReplacement);
        unattended.UpdateActive = () => updates.HasActiveWork;
        power = new PrivilegedPowerManager(configuration, lifetime);
    }

    public void Start()
    {
        power.Start();
        server = Task.Run(AcceptAsync);
        unattended.Start();
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
                var caller = SupportPipeIdentity.VerifyClient(pipe, configuration, unattended.Owns);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                while (true)
                {
                    requestTimeout.CancelAfter(TimeSpan.FromSeconds(35));
                    var request = await Wire.ReadAsync<Request>(pipe, requestTimeout.Token);
                    requestId = request.Id;
                    if (!Guid.TryParse(request.Id, out _)) throw new ArgumentException("Broker request id must be a UUID.");
                    if (request.Operation == "input.open")
                    {
                        if (unattended.Owns(caller)) throw new ArgumentException("The signed-out agent handles input in its own desktop session.");
                        await InteractiveInputBroker.ServeAsync(pipe, caller, request, lifetime);
                        return;
                    }
                    if (request.Operation == "maintenance.open")
                    {
                        var lease = caller.CreateLease();
                        lease.Validate();
                        await Wire.WriteAsync(pipe, Reply.Success(request.Id, new { active = true, leaseId = lease.LeaseId, processId = lease.ProcessId, sessionId = lease.SessionId, protocolVersion = SupportPlatformPaths.ProtocolVersion, interactiveInput = !unattended.Owns(caller) }), requestTimeout.Token);
                        await ServeMaintenanceAsync(pipe, lease, lifetime);
                        return;
                    }

                    requestTimeout.CancelAfter(request.Operation switch
                    {
                        "update.stage" => TimeSpan.FromSeconds(SupportOperationTimeouts.UpdateStageSeconds),
                        "update.arm" => TimeSpan.FromMinutes(1),
                        "platform.status" => TimeSpan.FromSeconds(SupportOperationTimeouts.PlatformStatusExecutionSeconds),
                        "firewall.ensure" => TimeSpan.FromSeconds(SupportOperationTimeouts.FirewallEnsureExecutionSeconds),
                        _ => TimeSpan.FromSeconds(35)
                    });

                    object result = request.Operation switch
                    {
                        "unattended.configure" => unattended.Configure(caller, request.Args),
                        "unattended.claim" => unattended.Claim(caller),
                        "unattended.attach" => unattended.Attach(caller, request.Args),
                        "unattended.secureAttention" when unattended.Owns(caller) => SendSecureAttention(pipe),
                        "platform.status" => await GetStatusAsync(caller, requestTimeout.Token),
                        "firewall.ensure" => await FirewallManager.EnsureAsync(configuration.RegisteredApplicationPath, requestTimeout.Token),
                        "update.stage" => await updates.StageAsync(caller, request.Args, requestTimeout.Token),
                        "update.arm" => await updates.ArmAsync(caller, request.Args, requestTimeout.Token),
                        "update.startupHealthy" => await updates.ReportStartupHealthyAsync(caller, request.Args, requestTimeout.Token),
                        "update.remoteHealthy" => await updates.ReportRemoteHealthyAsync(caller, request.Args, requestTimeout.Token),
                        "update.status" => updates.Status(request.Args),
                        "update.cancel" => await updates.CancelAsync(request.Args, requestTimeout.Token),
                        "power.preflight" or "power.validateLogin" or "power.issue" or "power.cancel" or "power.returned" => power.Dispatch(caller, request.Operation, request.Args),
                        _ => throw new ArgumentException("Unsupported privileged broker operation.")
                    };
                    bool keepAlive = request.KeepAlive && request.Operation != "update.arm";
                    await Wire.WriteAsync(pipe, Reply.Success(request.Id, result) with { KeepAlive = keepAlive }, requestTimeout.Token);
                    if (!keepAlive) return;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
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
        unattendedSupport = unattended.Enabled,
        interactiveInput = !unattended.Owns(caller),
        identityVerified = true,
        processId = caller.ProcessId,
        sessionId = caller.SessionId,
        firewallReady = await FirewallManager.IsReadyAsync(configuration.RegisteredApplicationPath, ct),
        message = "Privileged local support is available for this registered interactive application."
    };

    private static object SendSecureAttention(NamedPipeServerStream pipe)
    {
        SecureAttention.Send(pipe);
        return new { sent = true };
    }

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

    public void Dispose() => Dispose(windowsShutdown: false);
    internal void Dispose(bool windowsShutdown)
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        power.Stop(windowsShutdown);
        unattended.Dispose();
        updates.Dispose();
        // Active pipe handlers still release their slots while service cancellation
        // unwinds. The managed semaphore is collected after those handlers finish.
    }
}
