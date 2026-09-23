using System.IO.Pipes;
using RemoteDebugger.Core;

namespace RemoteDebugger;

public sealed record MaintenanceSessionStatus(
    bool Active,
    bool BrokerAvailable,
    bool RequiresProvisioning,
    string Message,
    string? LeaseId = null,
    bool InputReady = false);

/// <summary>
/// Process-bound broker connections stay ready between authorized support
/// sessions. Ending a session releases input and cancels any privileged job.
/// </summary>
public sealed class MaintenanceSession(string root) : IDisposable
{
    private readonly string dataRoot = Path.GetFullPath(root);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object stateLock = new();
    private NamedPipeClientStream? pipe;
    private readonly CancellationTokenSource lifetime = new();
    private readonly PrivilegedInputSession input = new();
    private bool privilegedInputAvailable;
    private MaintenanceSessionStatus status = new(false, false, true, "Privileged maintenance has not started.");
    private int enabled = 1;
    private int disposed;
    private int commandRunning;

    public const string DisabledMessage = "Administrator maintenance is disabled for this support session.";
    public bool Enabled => Volatile.Read(ref enabled) != 0;
    public MaintenanceSessionStatus CurrentStatus => Volatile.Read(ref status);
    public object Status => CurrentStatus with { InputReady = input.Ready };

    internal async Task PrepareAsync(CancellationToken ct)
    {
        if (!Enabled || Volatile.Read(ref disposed) != 0) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(SupportOperationTimeouts.PlatformStatusRoundTripSeconds));
        await StartAsync(deadline.Token);
        if (privilegedInputAvailable)
            await input.PrepareAsync(() => Enabled && Volatile.Read(ref disposed) == 0, deadline.Token);
    }

    public void SetEnabled(bool value)
    {
        Volatile.Write(ref enabled, value ? 1 : 0);
        if (!value)
        {
            DisposePipe();
            return;
        }

        lock (stateLock)
        {
            if (Volatile.Read(ref disposed) == 0 && pipe is null)
                Volatile.Write(ref status, new(false, CurrentStatus.BrokerAvailable, CurrentStatus.RequiresProvisioning,
                    "Administrator maintenance is enabled and preparing for authorized support sessions."));
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (!Enabled)
            {
                SetDisabledStatus();
                return;
            }
            if (pipe is { IsConnected: true }) return;
            DisposePipe(closeInput: false);
            var platform = await SupportPlatform.GetStatusAsync(ct);
            privilegedInputAvailable = platform.InteractiveInputAvailable;
            if (!platform.Available)
            {
                Volatile.Write(ref status, new(false, false, platform.RequiresAdministratorConsent, platform.Message));
                throw new InvalidOperationException(platform.Message);
            }
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
            connect.CancelAfter(TimeSpan.FromSeconds(SupportOperationTimeouts.PlatformStatusRoundTripSeconds));
            var candidate = await SupportPlatform.OpenBrokerPipeAsync(connect.Token);
            try
            {
                string id = Guid.NewGuid().ToString();
                await Wire.WriteAsync(candidate, new Request(id, "", "maintenance.open", Json.Element(new { dataRoot })), connect.Token);
                var reply = await Wire.ReadAsync<Reply>(candidate, connect.Token);
                var data = RemoteClient.Require(reply);
                lock (stateLock)
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                    connect.Token.ThrowIfCancellationRequested();
                    if (!Enabled)
                    {
                        candidate.Dispose();
                        SetDisabledStatus();
                        return;
                    }
                    pipe = candidate;
                    Volatile.Write(ref status, new(true, true, false,
                        privilegedInputAvailable
                            ? "Administrator maintenance is ready for authorized support sessions."
                            : "Administrator maintenance is active. Install the current setup to enable control of elevated windows.", data.Str("leaseId")));
                }
            }
            catch { candidate.Dispose(); throw; }
        }
        catch (Exception ex)
        {
            lock (stateLock)
            {
                if (Enabled && Volatile.Read(ref disposed) == 0 && !ct.IsCancellationRequested)
                    Volatile.Write(ref status, CurrentStatus with { Active = false, Message = ex.Message, LeaseId = null });
            }
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task<object> RunAsync(string file, string[] arguments, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!Enabled) throw new InvalidOperationException(DisabledMessage);
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (!Enabled) throw new InvalidOperationException(DisabledMessage);
            var current = pipe;
            if (current is not { IsConnected: true } || !CurrentStatus.Active)
                throw new InvalidOperationException("Administrator maintenance is unavailable. Pair the session after provisioning local support.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            string id = Guid.NewGuid().ToString();
            var request = new Request(id, "", "maintenance.command", Json.Element(new { file, arguments }), 300);
            MaintenanceLease.ValidateCommand(request);
            try
            {
                Volatile.Write(ref commandRunning, 1);
                await Wire.WriteAsync(current, request, timeout.Token);
                return RemoteClient.Require(await Wire.ReadAsync<Reply>(current, timeout.Token));
            }
            catch
            {
                // Closing the lifetime pipe is the cancellation signal observed by
                // the service, including controller cancellation and app shutdown.
                DisposePipe(closeInput: false);
                throw;
            }
            finally { Volatile.Write(ref commandRunning, 0); }
        }
        finally { gate.Release(); }
    }

    internal async Task<string> QueueSupportRefreshAsync(CancellationToken ct)
    {
        string signals = Path.Combine(dataRoot, "support-refresh");
        Directory.CreateDirectory(signals);
        string signalPath = Path.Combine(signals, Guid.NewGuid().ToString("N") + ".ready");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var refreshPipe = await SupportPlatform.OpenBrokerPipeAsync(timeout.Token);
        string openId = Guid.NewGuid().ToString();
        await Wire.WriteAsync(refreshPipe, new Request(openId, "", "maintenance.open", Json.Element(new { dataRoot })), timeout.Token);
        _ = RemoteClient.Require(await Wire.ReadAsync<Reply>(refreshPipe, timeout.Token));
        string commandId = Guid.NewGuid().ToString();
        var command = new Request(commandId, "", "maintenance.command", Json.Element(new
        {
            file = SupportPlatformPaths.ApplicationExecutable,
            arguments = new[] { "--support-refresh-launch", signalPath }
        }), 30);
        MaintenanceLease.ValidateCommand(command);
        await Wire.WriteAsync(refreshPipe, command, timeout.Token);
        var result = RemoteClient.Require(await Wire.ReadAsync<Reply>(refreshPipe, timeout.Token));
        if (result.Int("exitCode", -1) != 0)
            throw new InvalidOperationException("The protected support refresh worker did not start.");
        return signalPath;
    }

    public void End()
    {
        EndInput();
        if (Volatile.Read(ref commandRunning) != 0) DisposePipe(closeInput: false);
    }
    internal void EndInput() => _ = input.ReleaseAsync();

    internal async Task RecoverInputAsync(CancellationToken ct)
    {
        // Reuse the installed, owner-enabled broker after a failed startup or
        // service refresh. Never provision support or replay a click/key here.
        if (Enabled && !CurrentStatus.Active && File.Exists(SupportPlatformPaths.ConfigurationPath))
            await PrepareAsync(ct);
    }

    internal async Task SendInputAsync(object args, CancellationToken ct)
    {
        if (!Enabled || !CurrentStatus.Active) throw new InputBlockedException(DisabledMessage);
        if (!privilegedInputAvailable)
        {
            try { Native.HandleInput(args is System.Text.Json.JsonElement element ? element : Json.Element(args)); }
            catch (InputBlockedException) { throw new InputBlockedException("Windows blocked input. Install the current Remote Debugger setup on this agent to enable control of elevated windows."); }
            return;
        }
        try { await input.SendAsync(args, () => Enabled && CurrentStatus.Active, ct); }
        catch (RemoteOperationException ex) { throw new InputBlockedException(ex.Message); }
    }

    private void SetDisabledStatus()
    {
        lock (stateLock)
        {
            if (Volatile.Read(ref disposed) == 0)
                Volatile.Write(ref status, new(false, CurrentStatus.BrokerAvailable, CurrentStatus.RequiresProvisioning, DisabledMessage));
        }
    }

    private void DisposePipe(bool closeInput = true)
    {
        if (closeInput) input.End();
        NamedPipeClientStream? current;
        lock (stateLock)
        {
            current = pipe;
            pipe = null;
            if (Volatile.Read(ref disposed) == 0)
                Volatile.Write(ref status, new(false, CurrentStatus.BrokerAvailable, CurrentStatus.RequiresProvisioning,
                    Enabled ? "Administrator maintenance ended." : DisabledMessage));
        }
        try { current?.Dispose(); } catch { }
    }

    public void Dispose()
    {
        input.End();
        NamedPipeClientStream? current;
        lock (stateLock)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            current = pipe;
            pipe = null;
            Volatile.Write(ref status, new(false, false, false, "Administrator maintenance ended with the agent session."));
        }

        // Cancellation and pipe closure interrupt in-flight calls. The semaphore
        // and token source remain owned by this object until it is collected so
        // continuations that already passed their initial disposal check can
        // finish without touching synchronization primitives that were torn down.
        try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
        try { current?.Dispose(); } catch { }
    }
}
