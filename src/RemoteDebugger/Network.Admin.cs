using System.Security.Cryptography;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

public sealed partial class AgentServer
{
    internal static bool IsUpdateSessionOperation(string operation) => operation is
        "update.open" or "update.snapshot" or "update.challenge" or "update.begin" or "update.status" or
        "update.chunk" or "update.chunk.binary" or "update.transfer" or "update.stage" or "update.commit" or "update.resume" or "update.health" or
        "update.confirm" or "update.cancel" or "update.release" or "platform.ensureCurrent" or "session.heartbeat" or "session.disconnect";

    private async Task ServeAdminAsync(Stream stream, Request request, CancellationToken ct)
    {
        try
        {
            if (Volatile.Read(ref terminating) != 0 || !PairingExchange.ValidHash(request.BinarySha256))
                throw new UnauthorizedAccessException("This device is not available for updates.");
            // Fresh challenge on this TLS connection: proofs cannot be replayed on
            // another socket, device, operation or executable.
            string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            await Wire.WriteAsync(stream, Reply.Success(request.Id, new { nonce }), ct);
            var proof = await Wire.ReadAsync<JsonElement>(stream, ct);
            UpdateAdminProof.Verify(proof.Str("authorization"), nonce, Fingerprint, request.Operation, request.BinarySha256!);
            if (request.Operation == "admin.wake")
            {
                var sent = await WakeOnLan.SendAsync(request.Args.Str("macAddress"), "", request.Args.Int("port", 9), ct);
                await Wire.WriteAsync(stream, Reply.Success(request.Id, sent), ct);
                return;
            }
            if (request.Operation == "admin.inspect")
            {
                await Wire.WriteAsync(stream, Reply.Success(request.Id, new
                {
                    snapshot = await updates.SnapshotAsync(ct, request.Args.TryGetProperty("versionOnly", out var versionOnly) && versionOnly.ValueKind == JsonValueKind.True),
                    busy = Session.HasPaired && !updateOnly,
                    fingerprint = Fingerprint, computer = Environment.MachineName
                }), ct);
                return;
            }
            await pairingSlot.WaitAsync(ct);
            try
            {
                ReclaimDisconnectedUpdateSession();
                lock (authLock)
                {
                    if (Session.HasPaired || updates.PendingExitPlan != null)
                        throw new InvalidOperationException("This computer is in use. Retry its update after the current session ends.");
                    string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                    tokenHash = Safety.Hash(token); controllerBinaryHash = request.BinarySha256!;
                    updateOnly = true; grantLifetime.Cancel(); grantLifetime = new();
                    updates.ResetControllerSynchronization();
                    session.Pair(Safety.Equal(controllerBinaryHash, ExecutableIdentity.Sha256));
                    proof = Json.Element(new { token });
                }
                await Wire.WriteAsync(stream, Reply.Success(request.Id, proof), ct);
            }
            finally { pairingSlot.Release(); }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or ArgumentException or IOException)
        { await Wire.WriteAsync(stream, Reply.Failure(request.Id, "admin_rejected", ex.Message), ct); }
    }

    private void ReleaseUpdateSession()
    {
        lock (authLock)
            ReleaseUpdateSessionUnsafe();
    }

    private void ReclaimDisconnectedUpdateSession()
    {
        lock (authLock)
        {
            if (CanReclaimUpdateSession(updateOnly, Session, updates.PendingExitPlan))
                ReleaseUpdateSessionUnsafe();
        }
    }

    internal static bool CanReclaimUpdateSession(bool updateOnly, SupportSessionSnapshot session, UpdateExitPlan? pendingExitPlan) =>
        updateOnly && session.HasPaired && !session.Connected && pendingExitPlan == null;

    private void ReleaseUpdateSessionUnsafe()
    {
        tokenHash = ""; controllerBinaryHash = ""; updateOnly = false;
        grantLifetime.Cancel(); grantLifetime = new(); resumeStore.Clear(); resumed = null;
        Operations.Maintenance.End(); updates.ResetControllerSynchronization();
        session.ResetForPairing();
        if (Internet != null) Pairing.OpenPrivate(Internet.AuthenticationSecret); else Pairing.Open();
    }
}

public sealed partial class RemoteClient
{
    internal async Task<JsonElement> AdminRequestAsync(string operation, CancellationToken ct, object? args = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await using var stream = await OpenTransportAsync(deadline.Token);
        string id = Guid.NewGuid().ToString();
        await Wire.WriteAsync(stream, new Request(id, "", operation, Json.Element(args ?? new { }), 30, controllerBinarySha256), deadline.Token);
        var challenge = Require(await Wire.ReadAsync<Reply>(stream, deadline.Token));
        string authorization = new UpdateAdminStore(AdminRoot).Sign(challenge.Str("nonce"), Connection.Fingerprint, operation, controllerBinarySha256);
        await Wire.WriteAsync(stream, new { authorization }, deadline.Token);
        var result = Require(await Wire.ReadAsync<Reply>(stream, deadline.Token));
        if (operation == "admin.connect") Connection = Connection with { Token = result.Str("token") };
        return result;
    }

    internal async Task ReleaseUpdateAsync(CancellationToken ct)
    {
        try { Require(await CallAsync("update.release", ct: ct, seconds: 35)); }
        finally { await CloseUpdateChannelAsync(); await commands.ClearAsync(); }
    }
}
