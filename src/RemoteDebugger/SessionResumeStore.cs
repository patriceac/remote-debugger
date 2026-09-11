using System.Security.Cryptography;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

internal sealed record ResumableSession(string TicketHash, string GrantHash, string ControllerHash,
    string[] AllowedBinaryHashes, DateTimeOffset ExpiresUtc);

/// <summary>A bounded exception to fresh-launch pairing, explicitly armed for one update.</summary>
internal sealed class SessionResumeStore(string root)
{
    private readonly string path = Path.Combine(root, "update-session.resume");
    public (string Ticket, DateTimeOffset ExpiresUtc) Create(string grantHash, string controllerHash,
        string currentBinaryHash, string replacementBinaryHash, DateTimeOffset deadline)
    {
        if (!PairingExchange.ValidHash(grantHash) || !PairingExchange.ValidHash(controllerHash) ||
            !PairingExchange.ValidHash(currentBinaryHash) || !PairingExchange.ValidHash(replacementBinaryHash))
            throw new ArgumentException("Invalid update session identity.");
        var expires = deadline < DateTimeOffset.UtcNow.AddMinutes(10) ? deadline : DateTimeOffset.UtcNow.AddMinutes(10);
        if (expires <= DateTimeOffset.UtcNow) throw new ArgumentException("Update reconnect deadline expired.");
        string ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Vault.Save(path, JsonSerializer.SerializeToUtf8Bytes(new ResumableSession(Safety.Hash(ticket), grantHash, controllerHash,
            [currentBinaryHash, replacementBinaryHash], expires), Json.Options));
        return (ticket, expires);
    }
    public ResumableSession? Restore(string? ticket)
    {
        if (!PairingExchange.ValidHash(ticket) || !File.Exists(path)) return null;
        try
        {
            var saved = JsonSerializer.Deserialize<ResumableSession>(Vault.Read(path), Json.Options);
            if (saved == null || saved.ExpiresUtc <= DateTimeOffset.UtcNow || saved.ExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(10) ||
                !Safety.Equal(saved.TicketHash, Safety.Hash(ticket!)) ||
                !saved.AllowedBinaryHashes.Contains(ExecutableIdentity.Sha256, StringComparer.OrdinalIgnoreCase)) return null;
            return saved;
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or JsonException) { return null; }
    }
    public void Clear() { if (File.Exists(path)) File.Delete(path); }
}
