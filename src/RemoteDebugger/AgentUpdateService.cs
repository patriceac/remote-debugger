using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

public delegate Task<UpdateReconnectGrant> UpdateReconnectGrantFactory(UpdateCommitContext context, CancellationToken ct);

internal sealed record AgentUpdateTransfer(
    ExecutableSnapshot Candidate,
    DateTimeOffset CreatedUtc,
    bool BrokerStaged = false,
    UpdateReconnectGrant? Reconnect = null);

internal enum UpdateRemoteHealthReadiness
{
    PendingStartupHealth,
    Ready
}

public sealed class AgentUpdateService : IDisposable
{
    private static readonly HashSet<string> Operations = new(StringComparer.Ordinal)
    {
        "update.snapshot", "update.begin", "update.status", "update.chunk", "update.stage",
        "update.commit", "update.health", "update.cancel", "update.confirm"
    };

    private readonly string transferRoot;
    private readonly UpdateReconnectGrantFactory reconnectGrantFactory;
    private readonly SemaphoreSlim transferGate = new(1, 1);
    private int controllerSynchronized;
    private int disposed;
    private string? activeTransactionId;
    private UpdateExitPlan? pendingExitPlan;
    private AgentUpdateProgress progress = new("idle", 0, 0);

    public event Action? UpdateRestartRequested;

    public AgentUpdateService(string root, UpdateReconnectGrantFactory reconnectGrantFactory)
    {
        transferRoot = Path.Combine(Path.GetFullPath(root), "updates");
        this.reconnectGrantFactory = reconnectGrantFactory ?? throw new ArgumentNullException(nameof(reconnectGrantFactory));
        Directory.CreateDirectory(transferRoot);
        string[] launch = Environment.GetCommandLineArgs();
        int transactionIndex = Array.IndexOf(launch, "--update-transaction");
        if (transactionIndex >= 0 && transactionIndex + 1 < launch.Length && Guid.TryParseExact(launch[transactionIndex + 1], "N", out _))
        {
            activeTransactionId = launch[transactionIndex + 1];
            try
            {
                string meta = MetaPath(activeTransactionId);
                if (File.Exists(meta))
                {
                    var transfer = JsonSerializer.Deserialize<AgentUpdateTransfer>(File.ReadAllText(meta), Json.Options);
                    if (transfer != null) SetProgress("restarting", transfer.Candidate.Size, transfer.Candidate.Size);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
    }

    public bool ControllerSynchronized => Volatile.Read(ref controllerSynchronized) != 0;
    public UpdateExitPlan? PendingExitPlan => Volatile.Read(ref pendingExitPlan);
    public AgentUpdateProgress Progress => Volatile.Read(ref progress);
    public bool IsOperation(string operation) => Operations.Contains(operation);
    public void ResetControllerSynchronization()
    {
        Volatile.Write(ref controllerSynchronized, 0);
        SetProgress("idle", 0, 0);
    }

    public async Task<Reply> DispatchAsync(Request request, CancellationToken ct)
    {
        try
        {
            if (!IsOperation(request.Operation)) return Reply.Failure(request.Id, "unknown_update_operation", "Unknown update operation.");
            object result = request.Operation switch
            {
                "update.snapshot" => await SnapshotAsync(ct),
                "update.begin" => await WithTransferGateAsync(() => BeginAsync(request.Args, ct), ct),
                "update.status" => await WithTransferGateAsync(() => TransferStatusAsync(request.Args, ct), ct),
                "update.chunk" => await WithTransferGateAsync(() => ChunkAsync(request.Args, ct), ct),
                "update.stage" => await WithTransferGateAsync(() => StageAsync(request.Args, ct), ct),
                "update.commit" => await CommitAsync(request.Args, ct),
                "update.health" => await HealthAsync(request.Args, ct),
                "update.cancel" => await WithTransferGateAsync(() => CancelAsync(request.Args, ct), ct),
                "update.confirm" => await ConfirmAsync(request.Args, ct),
                _ => throw new ArgumentException("Unknown update operation.")
            };
            return Reply.Success(request.Id, result);
        }
        catch (OperationCanceledException) { return Reply.Failure(request.Id, "update_cancelled", "Update operation was cancelled."); }
        catch (UnauthorizedAccessException ex) { return Reply.Failure(request.Id, "update_trust_rejected", ex.Message); }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or IOException or JsonException)
        {
            return Reply.Failure(request.Id, "update_failed", ex.Message);
        }
    }

    /// <summary>Call after the successful update.commit reply has reached the controller.</summary>
    public void NotifyReplySent(Request request, Reply reply)
    {
        if (request.Operation != "update.commit" || !reply.Ok) return;
        string transactionId = reply.Data.Str("transactionId");
        var deadline = reply.Data.GetProperty("plannedDisconnectDeadlineUtc").GetDateTimeOffset();
        Volatile.Write(ref pendingExitPlan, new UpdateExitPlan(transactionId, deadline));
        UpdateRestartRequested?.Invoke();
    }

    private async Task<object> SnapshotAsync(CancellationToken ct)
    {
        var agent = await SupportPlatform.CaptureCurrentExecutableAsync(ct);
        var platform = await SupportPlatform.GetStatusAsync(ct);
        object? transaction = null;
        if (platform.Available)
        {
            try { transaction = await SupportPlatform.BrokerCallAsync("update.status", new { }, ct); }
            catch (InvalidOperationException) { }
        }
        return new { agent, platform, transaction, controllerSynchronized = ControllerSynchronized, actualRunningSha256 = agent.Sha256 };
    }

    private async Task<object> BeginAsync(JsonElement args, CancellationToken ct)
    {
        string transactionId = ValidateTransactionId(args.Str("transactionId"));
        var candidate = args.GetProperty("candidate").Deserialize<ExecutableSnapshot>(Json.Options) ?? throw new InvalidDataException("Missing controller executable snapshot.");
        candidate.Validate();
        string meta = MetaPath(transactionId), partial = PartialPath(transactionId);
        if (File.Exists(meta))
        {
            var existing = await LoadTransferAsync(transactionId, ct);
            if (existing.Candidate.Size != candidate.Size || !UpdatePolicy.FixedHexEquals(existing.Candidate.Sha256, candidate.Sha256) ||
                !UpdatePolicy.FixedHexEquals(existing.Candidate.SignerThumbprint, candidate.SignerThumbprint))
                throw new IOException("Transaction id already identifies different executable bytes.");
            Volatile.Write(ref activeTransactionId, transactionId);
            long offset = new FileInfo(partial).Length;
            SetProgress("transferring", offset, candidate.Size);
            return new { transactionId, offset, candidate.Size };
        }
        string temp = meta + ".new";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new AgentUpdateTransfer(candidate, DateTimeOffset.UtcNow), Json.Options), ct);
        File.Move(temp, meta, false);
        using (File.Create(partial)) { }
        Volatile.Write(ref activeTransactionId, transactionId);
        SetProgress("transferring", 0, candidate.Size);
        return new { transactionId, offset = 0L, candidate.Size };
    }

    private async Task<object> TransferStatusAsync(JsonElement args, CancellationToken ct)
    {
        string transactionId = ValidateTransactionId(args.Str("transactionId"));
        var transfer = await LoadTransferAsync(transactionId, ct);
        long offset = new FileInfo(PartialPath(transactionId)).Length;
        SetProgress("transferring", offset, transfer.Candidate.Size);
        return new { transactionId, offset, transfer.Candidate.Size };
    }

    private async Task<object> ChunkAsync(JsonElement args, CancellationToken ct)
    {
        string transactionId = ValidateTransactionId(args.Str("transactionId"));
        var transfer = await LoadTransferAsync(transactionId, ct);
        byte[] data = Convert.FromBase64String(args.Str("data"));
        if (data.Length is < 1 or > 512 * 1024) throw new ArgumentException("Update chunk must contain at most 512 KiB.");
        long offset = args.Long("offset");
        await using var stream = File.Open(PartialPath(transactionId), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (offset < 0 || offset + data.Length > transfer.Candidate.Size) throw new ArgumentException("Update chunk is outside the declared executable size.");
        if (offset < stream.Length)
        {
            if (offset + data.Length > stream.Length) throw new IOException("Retry chunk overlaps the current transfer boundary.");
            stream.Position = offset;
            byte[] existing = new byte[data.Length];
            await stream.ReadExactlyAsync(existing, ct);
            if (!existing.SequenceEqual(data)) throw new IOException("Retry chunk conflicts with received bytes.");
        }
        else
        {
            if (offset != stream.Length) throw new IOException("Unexpected update offset; query update.status and resume.");
            stream.Position = offset;
            await stream.WriteAsync(data, ct);
            await stream.FlushAsync(ct);
        }
        long acknowledged = stream.Length;
        SetProgress("transferring", acknowledged, transfer.Candidate.Size);
        return new { transactionId, offset = acknowledged };
    }

    private async Task<object> StageAsync(JsonElement args, CancellationToken ct)
    {
        string transactionId = ValidateTransactionId(args.Str("transactionId"));
        var transfer = await LoadTransferAsync(transactionId, ct);
        string partial = PartialPath(transactionId);
        SetProgress("verifying", new FileInfo(partial).Length, transfer.Candidate.Size);
        var actual = await SnapshotTransferredFileAsync(partial, ct);
        RequireTransferMatch(actual, transfer.Candidate);
        var result = await SupportPlatform.BrokerCallAsync("update.stage", new { transactionId, sourcePath = partial, candidate = transfer.Candidate }, ct, 120);
        string metaTemp = MetaPath(transactionId) + ".new";
        await File.WriteAllTextAsync(metaTemp, JsonSerializer.Serialize(transfer with { BrokerStaged = true }, Json.Options), ct);
        File.Move(metaTemp, MetaPath(transactionId), true);
        return result;
    }

    private async Task<object> CommitAsync(JsonElement args, CancellationToken ct)
    {
        string transactionId = ValidateTransactionId(args.Str("transactionId"));
        var transfer = await LoadTransferAsync(transactionId, ct);
        if (!transfer.BrokerStaged) throw new InvalidOperationException("Update bytes must be verified and staged before commit.");
        var agent = await SupportPlatform.CaptureCurrentExecutableAsync(ct);
        DateTimeOffset plannedDeadline = DateTimeOffset.UtcNow.AddSeconds(15);
        var context = new UpdateCommitContext(transactionId, transfer.Candidate, agent, plannedDeadline);
        var reconnect = transfer.Reconnect;
        if (reconnect == null || reconnect.ExpiresUtc <= DateTimeOffset.UtcNow)
        {
            reconnect = await reconnectGrantFactory(context, ct);
            reconnect.Validate(DateTimeOffset.UtcNow);
            transfer = transfer with { Reconnect = reconnect };
            await SaveTransferAsync(transactionId, transfer, ct);
        }
        else reconnect.Validate(DateTimeOffset.UtcNow);
        string[] arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var armed = await SupportPlatform.BrokerCallAsync("update.arm", new
        {
            transactionId,
            reconnect,
            arguments,
            workingDirectory = Environment.CurrentDirectory
        }, ct, 60);
        SetProgress("restarting", transfer.Candidate.Size, transfer.Candidate.Size);
        return new
        {
            transactionId,
            reconnectTicket = reconnect.Ticket,
            reconnectExpiresUtc = reconnect.ExpiresUtc,
            plannedDisconnectDeadlineUtc = armed.GetProperty("plannedDisconnectDeadlineUtc").GetDateTimeOffset(),
            disconnectKind = "planned_update",
            candidate = transfer.Candidate
        };
    }

    private async Task<object> HealthAsync(JsonElement args, CancellationToken ct)
    {
        string transactionId = ValidateTransactionId(args.Str("transactionId"));
        string ticket = args.Str("ticket");
        var status = await SupportPlatform.BrokerCallAsync("update.status", new { transactionId }, ct, 15);
        if (ClassifyRemoteHealthReadiness(status) == UpdateRemoteHealthReadiness.PendingStartupHealth)
            return new { transactionId, ready = false, state = status.Str("state") };
        var broker = await SupportPlatform.BrokerCallAsync("update.remoteHealthy", new { transactionId, ticket }, ct, 30);
        var running = await SupportPlatform.CaptureCurrentExecutableAsync(ct);
        return new { transactionId, ready = true, ticketAccepted = true, actualRunningSha256 = running.Sha256, running, transaction = broker };
    }

    internal static UpdateRemoteHealthReadiness ClassifyRemoteHealthReadiness(JsonElement status)
    {
        string state = status.Str("state");
        return state switch
        {
            nameof(UpdateTransactionState.AwaitingStartupHealth) => UpdateRemoteHealthReadiness.PendingStartupHealth,
            nameof(UpdateTransactionState.RunningPendingRemoteHealth) or nameof(UpdateTransactionState.Completed) => UpdateRemoteHealthReadiness.Ready,
            _ => throw new InvalidOperationException($"Update cannot complete remote health from broker state '{state}': {status.Str("lastError", "no broker error was reported")}")
        };
    }

    private async Task<object> ConfirmAsync(JsonElement args, CancellationToken ct)
    {
        string expected = args.Str("sha256", args.Str("controllerBinarySha256"));
        UpdatePolicy.ValidateSha256(expected, "controller executable SHA-256");
        var running = await SupportPlatform.CaptureCurrentExecutableAsync(ct);
        if (!UpdatePolicy.FixedHexEquals(expected, running.Sha256))
            throw new InvalidOperationException("Agent running executable still differs from the authenticated controller executable.");
        Volatile.Write(ref controllerSynchronized, 1);
        SetProgress("complete", running.Size, running.Size);
        string transactionId = args.Str("transactionId");
        if (Guid.TryParseExact(transactionId, "N", out _))
        {
            DeleteIfExists(PartialPath(transactionId));
            DeleteIfExists(MetaPath(transactionId));
            Volatile.Write(ref activeTransactionId, null);
            Volatile.Write(ref pendingExitPlan, null);
        }
        return new { confirmed = true, actualRunningSha256 = running.Sha256, running };
    }

    private async Task<object> CancelAsync(JsonElement args, CancellationToken ct)
    {
        string transactionId = ValidateTransactionId(args.Str("transactionId"));
        var transfer = await LoadTransferAsync(transactionId, ct);
        object broker = transfer.BrokerStaged
            ? await SupportPlatform.BrokerCallAsync("update.cancel", new { transactionId }, ct)
            : new { cancelled = false, reason = "Transfer had not crossed the privilege boundary." };
        DeleteIfExists(PartialPath(transactionId));
        DeleteIfExists(MetaPath(transactionId));
        if (string.Equals(Volatile.Read(ref activeTransactionId), transactionId, StringComparison.OrdinalIgnoreCase))
            Volatile.Write(ref activeTransactionId, null);
        SetProgress("idle", 0, 0);
        return new { transactionId, cancelled = true, broker };
    }

    public async Task CancelActiveAsync(CancellationToken ct)
    {
        string? transactionId = Volatile.Read(ref activeTransactionId);
        if (transactionId == null) return;
        AgentUpdateTransfer? transfer = File.Exists(MetaPath(transactionId)) ? await LoadTransferAsync(transactionId, ct) : null;
        bool brokerHasTransaction = transfer?.BrokerStaged == true;
        if (!brokerHasTransaction && transfer == null)
        {
            var status = await SupportPlatform.BrokerCallAsync("update.status", new { transactionId }, ct, 10);
            brokerHasTransaction = status.TryGetProperty("active", out var active) && active.GetBoolean();
        }
        if (!brokerHasTransaction)
        {
            DeleteIfExists(PartialPath(transactionId));
            DeleteIfExists(MetaPath(transactionId));
            Volatile.Write(ref activeTransactionId, null);
            Volatile.Write(ref pendingExitPlan, null);
            SetProgress("idle", 0, 0);
            return;
        }
        var result = await SupportPlatform.BrokerCallAsync("update.cancel", new { transactionId, relaunchPrevious = false }, ct, 25);
        string state = result.Str("state");
        if (state == nameof(UpdateTransactionState.Failed))
            throw new InvalidOperationException("Active update could not be cancelled and rolled back safely: " + result.Str("lastError"));
        if (state is not (nameof(UpdateTransactionState.Cancelled) or nameof(UpdateTransactionState.RolledBack) or nameof(UpdateTransactionState.Completed)))
            throw new InvalidOperationException("Active update did not reach a safe terminal state: " + state);
        DeleteIfExists(PartialPath(transactionId));
        DeleteIfExists(MetaPath(transactionId));
        Volatile.Write(ref activeTransactionId, null);
        Volatile.Write(ref pendingExitPlan, null);
        SetProgress("idle", 0, 0);
    }

    private async Task<ExecutableSnapshot> SnapshotTransferredFileAsync(string path, CancellationToken ct)
    {
        var signature = AuthenticodeVerifier.InspectForEnrollment(path);
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new(Path.GetFullPath(path), stream.Length, Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)),
            FileVersionInfo.GetVersionInfo(path).FileVersion, signature.SignerThumbprint);
    }

    private static void RequireTransferMatch(ExecutableSnapshot actual, ExecutableSnapshot expected)
    {
        if (actual.Size != expected.Size || !UpdatePolicy.FixedHexEquals(actual.Sha256, expected.Sha256) ||
            !UpdatePolicy.FixedHexEquals(actual.SignerThumbprint, expected.SignerThumbprint))
            throw new InvalidDataException("Completed transfer does not match the controller executable snapshot.");
    }

    private async Task<AgentUpdateTransfer> LoadTransferAsync(string transactionId, CancellationToken ct) =>
        JsonSerializer.Deserialize<AgentUpdateTransfer>(await File.ReadAllTextAsync(MetaPath(transactionId), ct), Json.Options)
        ?? throw new InvalidDataException("Update transfer metadata is empty.");

    private async Task SaveTransferAsync(string transactionId, AgentUpdateTransfer transfer, CancellationToken ct)
    {
        string temp = MetaPath(transactionId) + ".new";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(transfer, Json.Options), ct);
        File.Move(temp, MetaPath(transactionId), true);
    }

    private async Task<object> WithTransferGateAsync(Func<Task<object>> action, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await transferGate.WaitAsync(ct);
        try { return await action(); }
        finally { transferGate.Release(); }
    }

    private static string ValidateTransactionId(string value) =>
        Guid.TryParseExact(value, "N", out _) ? value : throw new ArgumentException("Update transaction id must be a UUID in N format.");
    private void SetProgress(string stage, long transferredBytes, long totalBytes) =>
        Volatile.Write(ref progress, new AgentUpdateProgress(stage, transferredBytes, totalBytes));
    private string MetaPath(string transactionId) => Path.Combine(transferRoot, transactionId + ".json");
    private string PartialPath(string transactionId) => Path.Combine(transferRoot, transactionId + ".partial.exe");
    private static void DeleteIfExists(string path) { if (File.Exists(path)) File.Delete(path); }
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) transferGate.Dispose(); }
}

internal static class AgentUpdateClient
{
    public static async Task<AgentSynchronizationResult> SynchronizeAgentAsync(
        RemoteClient client,
        CancellationToken ct,
        IProgress<AgentUpdateProgress>? progress = null)
    {
        var controller = await SupportPlatform.CaptureCurrentExecutableAsync(ct);
        var snapshot = RemoteClient.Require(await client.CallAsync("update.snapshot", ct: ct, seconds: 30));
        var agent = snapshot.GetProperty("agent").Deserialize<ExecutableSnapshot>(Json.Options) ?? throw new InvalidDataException("Agent did not return an executable snapshot.");
        agent.Validate();
        if (IsExact(controller, agent))
        {
            RemoteClient.Require(await client.CallAsync("update.confirm", new { sha256 = controller.Sha256 }, ct, seconds: 30));
            progress?.Report(new("complete", controller.Size, controller.Size));
            return new(true, false, controller, agent, null, "Agent already runs the controller's exact executable bytes.");
        }

        string transactionId = controller.Sha256[..32].ToLowerInvariant();
        try
        {
            var begin = RemoteClient.Require(await client.SendUpdateAsync("update.begin", new { transactionId, candidate = controller }, ct, seconds: 30));
            long offset = begin.Long("offset");
            if (offset < 0 || offset > controller.Size) throw new InvalidDataException("Agent returned an invalid update resume offset.");
            progress?.Report(new("transferring", offset, controller.Size));
            await using (var input = File.Open(controller.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                input.Position = offset;
                byte[] buffer = new byte[512 * 1024];
                while (offset < input.Length)
                {
                    int count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, input.Length - offset)), ct);
                    if (count == 0) throw new EndOfStreamException("Controller executable changed while transferring.");
                    var result = RemoteClient.Require(await client.SendUpdateAsync("update.chunk", new { transactionId, offset, data = Convert.ToBase64String(buffer, 0, count) }, ct, seconds: 60));
                    long acknowledged = result.Long("offset");
                    if (acknowledged != offset + count)
                        throw new InvalidDataException("Agent returned an invalid acknowledged update offset.");
                    offset = acknowledged;
                    input.Position = offset;
                    progress?.Report(new("transferring", offset, controller.Size));
                }
            }
            progress?.Report(new("verifying", controller.Size, controller.Size));
            RemoteClient.Require(await client.SendUpdateAsync("update.stage", new { transactionId }, ct, seconds: 180));
            var commit = RemoteClient.Require(await client.SendUpdateAsync("update.commit", new { transactionId }, ct, seconds: 60));
            await client.CloseUpdateChannelAsync().ConfigureAwait(false);
            progress?.Report(new("restarting", controller.Size, controller.Size));
            string ticket = commit.Str("reconnectTicket");
            DateTimeOffset expires = commit.GetProperty("reconnectExpiresUtc").GetDateTimeOffset();
            DateTimeOffset planned = commit.GetProperty("plannedDisconnectDeadlineUtc").GetDateTimeOffset();

            JsonElement resumed = default;
            Exception? lastError = null;
            while (DateTimeOffset.UtcNow < expires)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    resumed = RemoteClient.Require(await client.CallAsync("update.resume", new { transactionId, ticket }, ct, seconds: 10));
                    break;
                }
                catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    lastError = ex;
                    await Task.Delay(750, ct);
                }
            }
            if (resumed.ValueKind == JsonValueKind.Undefined)
                throw new IOException("Updated agent did not reconnect before the authenticated ticket expired.", lastError);

            JsonElement health = default;
            while (DateTimeOffset.UtcNow < expires)
            {
                ct.ThrowIfCancellationRequested();
                var candidateHealth = RemoteClient.Require(await client.CallAsync("update.health", new { transactionId, ticket }, ct, seconds: 45));
                if (candidateHealth.TryGetProperty("ready", out var ready) && ready.GetBoolean())
                {
                    health = candidateHealth;
                    break;
                }
                await Task.Delay(500, ct);
            }
            if (health.ValueKind == JsonValueKind.Undefined)
                throw new IOException("Updated agent did not complete startup health before the authenticated reconnect ticket expired.");
            if (!UpdatePolicy.FixedHexEquals(health.Str("actualRunningSha256"), controller.Sha256))
                throw new InvalidOperationException("Agent rolled back or relaunched bytes that differ from the controller executable.");
            var finalSnapshot = RemoteClient.Require(await client.CallAsync("update.snapshot", ct: ct, seconds: 30));
            agent = finalSnapshot.GetProperty("agent").Deserialize<ExecutableSnapshot>(Json.Options) ?? throw new InvalidDataException("Updated agent did not return an executable snapshot.");
            UpdatePolicy.RequireExactControllerBinary(controller, agent);
            RemoteClient.Require(await client.CallAsync("update.confirm", new { sha256 = controller.Sha256, transactionId, ticket }, ct, seconds: 30));
            progress?.Report(new("complete", controller.Size, controller.Size));
            return new(false, true, controller, agent, planned, "Agent replaced, relaunched, health-checked, and confirmed on the controller's exact executable bytes.");
        }
        finally
        {
            await client.CloseUpdateChannelAsync().ConfigureAwait(false);
        }
    }

    private static bool IsExact(ExecutableSnapshot left, ExecutableSnapshot right) =>
        left.Size == right.Size && UpdatePolicy.FixedHexEquals(left.Sha256, right.Sha256);
}
