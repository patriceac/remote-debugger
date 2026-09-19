using System.IO.Pipes;
using RemoteDebugger.Core;

namespace RemoteDebugger;

public sealed record MaintenanceSessionStatus(
    bool Active,
    bool BrokerAvailable,
    bool RequiresProvisioning,
    string Message,
    string? LeaseId = null);

/// <summary>
/// One process-bound broker connection. Its lifetime is the paired agent
/// session; closing it cancels every privileged job started through it.
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

    public const string DisabledMessage = "Administrator maintenance is disabled for this support session.";
    public bool Enabled => Volatile.Read(ref enabled) != 0;
    public MaintenanceSessionStatus CurrentStatus => Volatile.Read(ref status);
    public object Status => CurrentStatus;

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
                    "Administrator maintenance is enabled and will start when the support session is paired."));
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
            DisposePipe();
            var platform = await SupportPlatform.GetStatusAsync(ct);
            privilegedInputAvailable = platform.InteractiveInputAvailable;
            if (!platform.Available)
            {
                Volatile.Write(ref status, new(false, false, platform.RequiresAdministratorConsent, platform.Message));
                throw new InvalidOperationException(platform.Message);
            }
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
            connect.CancelAfter(TimeSpan.FromSeconds(10));
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
                            ? "Administrator maintenance is active until the paired agent session ends."
                            : "Administrator maintenance is active. Install the current setup to enable control of elevated windows.", data.Str("leaseId")));
                }
            }
            catch { candidate.Dispose(); throw; }
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
                await Wire.WriteAsync(current, request, timeout.Token);
                return RemoteClient.Require(await Wire.ReadAsync<Reply>(current, timeout.Token));
            }
            catch
            {
                // Closing the lifetime pipe is the cancellation signal observed by
                // the service, including controller cancellation and app shutdown.
                DisposePipe();
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public void End() => DisposePipe();
    internal void EndInput() => input.End();

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

    private void DisposePipe()
    {
        input.End();
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
