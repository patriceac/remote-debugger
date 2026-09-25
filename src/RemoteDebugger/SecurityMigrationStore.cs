using System.Security.Cryptography;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

public sealed record SecurityDevice(string Name, string LegacyHost, string Fingerprint = "", string NewHost = "",
    string State = "pending", string Error = "", Connection? Connection = null, Connection? LegacyConnection = null);
public sealed record SecurityMigrationState(InternetSettings Previous, InternetSettings Current, List<SecurityDevice> Devices);
public sealed record RelaySecurityPreparation(string RelayUrl, string AccessKey);

public sealed class SecurityMigrationStore(string root)
{
    public string ProtectedSetupPath => Path.Combine(root, "RemoteDebugger-Protected.rdrelay");
    public string PendingSetupPath => Path.Combine(root, "internet.pending.rdrelay");
    private string StatePath => Path.Combine(root, "security-migration.dpapi");
    public SecurityMigrationState? Load() => File.Exists(StatePath)
        ? JsonSerializer.Deserialize<SecurityMigrationState>(Vault.Read(StatePath), Json.Options) ?? throw new IOException("Invalid migration state.") : null;
    public void Save(SecurityMigrationState state) => Vault.Save(StatePath, JsonSerializer.SerializeToUtf8Bytes(state, Json.Options));

    public Connection? PreviousConnection(InternetSettings previous, SecurityDevice device)
    {
        string path = Path.Combine(root, "controller.connection");
        if (!File.Exists(path)) return null;
        try
        {
            var connection = RemoteClient.Load(path).Connection;
            return connection.Host == device.LegacyHost && connection.RelayUrl == previous.RelayUrl &&
                Safety.Equal(connection.RelayAccessKey, previous.AccessKey) && connection.Token.Length > 0 &&
                PairingExchange.ValidHash(connection.Fingerprint) &&
                (device.Fingerprint.Length == 0 || Safety.Equal(device.Fingerprint, connection.Fingerprint))
                ? connection : null;
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or JsonException) { return null; }
    }

    public RelaySecurityPreparation? Preparation()
    {
        string path = Path.Combine(root, "security-relay-prepared.dpapi");
        return File.Exists(path) ? JsonSerializer.Deserialize<RelaySecurityPreparation>(Vault.Read(path), Json.Options) : null;
    }

    public async Task<SecurityMigrationState> CreateAsync(string passphrase, CancellationToken ct)
    {
        if (Load() != null) throw new InvalidOperationException("Security setup already exists. Resume the saved migration.");
        var previous = InternetSettings.Load(root) ?? throw new InvalidOperationException("Existing internet settings are required.");
        var preparation = Preparation() ?? throw new InvalidOperationException("The relay must be prepared for security migration first.");
        if (preparation.RelayUrl != previous.RelayUrl || Safety.Equal(preparation.AccessKey, previous.AccessKey))
            throw new InvalidOperationException("Invalid relay security preparation.");
        var current = new InternetSettings(previous.RelayUrl, preparation.AccessKey,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), Guid.NewGuid().ToString("N"));
        current.Validate(true);
        // Verify the new relay credential before changing any local authorization.
        _ = await current.FindAsync(ct, root).ConfigureAwait(false);
        var peers = await previous.FindAsync(ct, root).ConfigureAwait(false);
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(current, Json.Options);
        ProtectedSetup envelope;
        try { envelope = await Task.Run(() => ProtectedSetup.Seal(plaintext, passphrase, current.SecurityId), ct).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        Directory.CreateDirectory(root);
        string temporary = ProtectedSetupPath + ".tmp";
        await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(envelope, Json.Options), ct).ConfigureAwait(false);
        File.Move(temporary, ProtectedSetupPath, true);
        var state = new SecurityMigrationState(previous, current, peers.Select(p => new SecurityDevice(p.Name, p.Host)).ToList());
        Save(state); // Retain the old credential before switching the controller.
        current.Save(root);
        return state;
    }

    public void Unlock(string passphrase)
    {
        var envelope = ProtectedSetup.Read(File.ReadAllBytes(PendingSetupPath));
        byte[] plain = envelope.Open(passphrase);
        try
        {
            var settings = JsonSerializer.Deserialize<InternetSettings>(plain, Json.Options) ?? throw new IOException("Invalid internet settings.");
            settings.Validate(true);
            if (settings.SecurityId != envelope.ProfileId) throw new CryptographicException("Invalid setup identity.");
            settings.Save(root);
            File.Move(PendingSetupPath, ProtectedSetupPath, true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
