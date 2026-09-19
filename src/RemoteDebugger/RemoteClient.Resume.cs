using System.Security.Cryptography;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

public sealed partial class RemoteClient
{
    internal async Task<bool> TryResumeSavedConnectionAsync(CancellationToken ct)
    {
        if (Connection.RelayUrl.Length == 0 || !PairingExchange.ValidHash(Connection.Fingerprint)) return false;
        Connection? saved;
        try { saved = JsonSerializer.Deserialize<Connection>(Vault.Read(Path.Combine(AdminRoot, "controller.connection")), Json.Options); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException) { return false; }
        if (saved == null || !PairingExchange.ValidHash(saved.Fingerprint) ||
            !Safety.Equal(saved.Fingerprint.ToUpperInvariant(), Connection.Fingerprint.ToUpperInvariant()) || !PairingExchange.ValidHash(saved.Token)) return false;

        // A paired agent closes its pairing gate. Reuse its still-valid grant
        // instead of starting another exchange against that closed gate.
        Connection target = Connection;
        bool resumed = false;
        Connection = target with { Token = saved.Token };
        try
        {
            Reply reply = await CallAsync("session.heartbeat", ct: ct, seconds: 10).ConfigureAwait(false);
            if (reply.Error == "access_denied") return false;
            var heartbeat = Require(reply);
            resumed = heartbeat.TryGetProperty("session", out var session) &&
                session.TryGetProperty("connected", out var connected) && connected.ValueKind == JsonValueKind.True;
            return resumed;
        }
        finally { if (!resumed) Connection = target; }
    }
}
