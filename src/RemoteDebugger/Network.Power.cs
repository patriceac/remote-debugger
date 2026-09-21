using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

public sealed partial class AgentServer
{
    private RestartResumeStore restartStore = null!;
    private RestartAuthorization? restartAuthorization;
    private string? completedRestartTicketHash;
    private readonly SemaphoreSlim powerGate = new(1, 1);
    private string? powerOperationId;
    public bool PowerRestartArmed { get; private set; }
    public bool PowerShutdownArmed { get; private set; }

    private async Task<Reply> HandlePowerAsync(Request request, CancellationToken ct)
    {
        await powerGate.WaitAsync(ct);
        try
        {
            if (request.Operation == "power.resume")
            {
                if (completedRestartTicketHash != null && Safety.Equal(completedRestartTicketHash, Safety.Hash(request.Args.Str("ticket"))))
                    return Reply.Success(request.Id, new { resumed = true, binaryMatched = Session.BinaryMatched, ready = true });
                if (restartAuthorization == null || restartAuthorization.ExpiresUtc <= DateTimeOffset.UtcNow ||
                    !Safety.Equal(restartAuthorization.TicketHash, Safety.Hash(request.Args.Str("ticket"))))
                    return Reply.Failure(request.Id, "resume_denied", "Restart reconnect authorization is invalid or expired.");
                // The ticket remains usable until the controller acknowledges a fresh, controllable desktop.
                resumeAccepted = true; session.Observe();
                return Reply.Success(request.Id, new { resumed = true, binaryMatched = Session.BinaryMatched });
            }
            if (request.Operation == "power.ready")
            {
                if (completedRestartTicketHash != null) return Reply.Success(request.Id, new { ready = true });
                if (restartAuthorization == null || !resumeAccepted) throw new InvalidOperationException("No resumed restart session.");
                _ = await SupportPlatform.BrokerCallAsync("power.returned", new { }, ct);
                completedRestartTicketHash = restartAuthorization.TicketHash;
                restartStore.Clear(); restartAuthorization = null; session.CompleteRestart();
                PowerRestartArmed = false; powerOperationId = null;
                return Reply.Success(request.Id, new { ready = true });
            }
            if (request.Operation == "power.cancelWait")
            {
                if (powerOperationId != null)
                    RecordPowerCancellation(await SupportPlatform.BrokerCallAsync("power.cancel", new { operationId = powerOperationId }, ct));
                restartStore.Clear(); restartAuthorization = null; PowerRestartArmed = false; PowerShutdownArmed = false; powerOperationId = null;
                session.End(); Operations.Clipboard?.Pause();
                return Reply.Success(request.Id, new { waitingStopped = true });
            }
            if (!Operations.Maintenance.Enabled || !Operations.Maintenance.CurrentStatus.Active)
                throw new InvalidOperationException("Administrator maintenance must be active to manage this PC's power.");
            if (request.Operation == "power.cancel")
            {
                var cancelled = await SupportPlatform.BrokerCallAsync("power.cancel", request.Args, ct);
                if (cancelled.TryGetProperty("cancelled", out var value) && value.ValueKind == JsonValueKind.True)
                {
                    restartStore.Clear(); PowerRestartArmed = false; PowerShutdownArmed = false; powerOperationId = null; session.CompleteRestart();
                    RecordPowerCancellation(cancelled);
                }
                return Reply.Success(request.Id, cancelled);
            }
            if (request.Operation is "power.preflight" or "power.validateLogin")
                return Reply.Success(request.Id, await SupportPlatform.BrokerCallAsync(request.Operation, request.Args, ct));
            if (request.Operation != "power.issue") throw new ArgumentException("Unsupported power operation.");
            if (PowerRestartArmed || PowerShutdownArmed) throw new InvalidOperationException("A power request is already active.");
            string action = request.Args.Str("action");
            (string Ticket, DateTimeOffset ExpiresUtc)? reconnect = null;
            try
            {
                if (action == "restart")
                {
                    lock (authLock) reconnect = restartStore.Create(tokenHash, controllerBinaryHash, ExecutableIdentity.Sha256);
                    restartStore.RegisterStartup(); session.AwaitRestart(reconnect.Value.ExpiresUtc);
                }
                var issued = await SupportPlatform.BrokerCallAsync("power.issue", request.Args, ct);
                powerOperationId = request.Args.Str("operationId");
                PowerRestartArmed = action == "restart"; PowerShutdownArmed = action == "shutdown";
                Operations.Clipboard?.Pause();
                Operations.Diagnostics.PlannedExit(new(Environment.MachineName, "agent", typeof(AgentServer).Assembly.GetName().Version?.ToString() ?? ""));
                return Reply.Success(request.Id, new { issued, reconnectTicket = reconnect?.Ticket, reconnectExpiresUtc = reconnect?.ExpiresUtc });
            }
            catch
            {
                restartStore.Clear(); session.CompleteRestart(); throw;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or IOException or ArgumentException)
        { return Reply.Failure(request.Id, "power_failed", ex.Message); }
        finally { powerGate.Release(); }
    }

    private async Task CancelPlannedPowerAsync(CancellationToken ct)
    {
        if (powerOperationId != null)
        {
            RecordPowerCancellation(await SupportPlatform.BrokerCallAsync("power.cancel", new { operationId = powerOperationId }, ct));
            powerOperationId = null;
        }
        restartStore.Clear(); restartAuthorization = null; PowerRestartArmed = false; PowerShutdownArmed = false;
    }

    private void RecordPowerCancellation(JsonElement result)
    {
        if (result.TryGetProperty("cancelled", out var cancelled) && cancelled.ValueKind == JsonValueKind.True)
            Operations.Diagnostics.CancelPlannedExit(new(Environment.MachineName, "agent", typeof(AgentServer).Assembly.GetName().Version?.ToString() ?? ""));
    }
}
