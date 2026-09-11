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
    private NamedPipeClientStream? pipe;
    private CancellationTokenSource lifetime = new();
    private MaintenanceSessionStatus status = new(false, false, true, "Privileged maintenance has not started.");
    private int disposed;

    public MaintenanceSessionStatus CurrentStatus => Volatile.Read(ref status);
    public object Status => CurrentStatus;

    public async Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await gate.WaitAsync(ct);
        try
        {
            if (pipe is { IsConnected: true }) return;
            DisposePipe();
            var platform = await SupportPlatform.GetStatusAsync(ct);
            if (!platform.Available)
            {
                Volatile.Write(ref status, new(false, false, platform.RequiresAdministratorConsent, platform.Message));
                throw new InvalidOperationException(platform.Message);
            }
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
            connect.CancelAfter(TimeSpan.FromSeconds(10));
            var candidate = await SupportPlatform.OpenMaintenancePipeAsync(connect.Token);
            try
            {
                string id = Guid.NewGuid().ToString();
                await Wire.WriteAsync(candidate, new Request(id, "", "maintenance.open", Json.Element(new { dataRoot })), connect.Token);
                var reply = await Wire.ReadAsync<Reply>(candidate, connect.Token);
                var data = RemoteClient.Require(reply);
                pipe = candidate;
                Volatile.Write(ref status, new(true, true, false,
                    "Administrator maintenance is active until the paired agent session ends.", data.Str("leaseId")));
            }
            catch { candidate.Dispose(); throw; }
        }
        finally { gate.Release(); }
    }

    public async Task<object> RunAsync(string file, string[] arguments, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await gate.WaitAsync(ct);
        try
        {
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

    private void DisposePipe()
    {
        var current = Interlocked.Exchange(ref pipe, null);
        try { current?.Dispose(); } catch { }
        if (Volatile.Read(ref disposed) == 0)
            Volatile.Write(ref status, new(false, CurrentStatus.BrokerAvailable, CurrentStatus.RequiresProvisioning, "Administrator maintenance ended."));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        DisposePipe();
        lifetime.Dispose();
        gate.Dispose();
        Volatile.Write(ref status, new(false, false, false, "Administrator maintenance ended with the agent session."));
    }
}
