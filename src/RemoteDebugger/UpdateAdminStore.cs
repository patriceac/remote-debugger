using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

internal sealed class UpdateAdminStore(string root, string trustedPublicKey = UpdateAdminProof.TrustedPublicKey)
{
    private sealed record Recovery(string Kind, string PrivateKey, InternetSettings? Settings);
    private const string RecoveryKind = "RemoteDebugger.AdminRecovery.v1";
    private string KeyPath => Path.Combine(root, "update-admin.dpapi");
    internal string PendingPath => Path.Combine(root, "admin.pending.rdadmin");

    public bool IsAdmin
    {
        get
        {
            try { using var key = Open(); return true; }
            catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException) { return false; }
        }
    }

    private ECDsa Open()
    {
        byte[] bytes = Vault.Read(KeyPath);
        try { return ReadKey(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private ECDsa ReadKey(byte[] bytes)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(bytes, out int consumed);
            if (consumed != bytes.Length || Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) != trustedPublicKey)
                throw new CryptographicException("This recovery key is not the enrolled update administrator.");
            return key;
        }
        catch { key.Dispose(); throw; }
    }

    public string Sign(string nonce, string target, string operation, string binary)
    {
        using var key = Open();
        return UpdateAdminProof.Sign(key, nonce, target, operation, binary);
    }

    public void RequireController()
    {
        if (!IsAdmin) throw new UnauthorizedAccessException("Only an enrolled controller can discover or support another computer.");
    }

    public X509Certificate2 CreateClientCertificate()
    {
        RequireController();
        using var key = Open();
        var request = new CertificateRequest("CN=RemoteDebugger Controller", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        byte[] pfx = generated.Export(X509ContentType.Pfx);
        try { return new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.UserKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    // Existing duplex streams must lose authority too, even while no new RPC is sent.
    public async void WatchAuthority(Stream stream)
    {
        try
        {
            while (stream.CanRead)
            {
                if (!IsAdmin) { stream.Dispose(); return; }
                await Task.Delay(500).ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException) { }
    }

    internal static bool IsControllerCertificate(X509Certificate? certificate,
        string trustedPublicKey = UpdateAdminProof.TrustedPublicKey)
    {
        if (certificate == null) return false;
        try
        {
            using var parsed = new X509Certificate2(certificate);
            using var key = parsed.GetECDsaPublicKey();
            return key != null && parsed.NotBefore.ToUniversalTime() <= DateTime.UtcNow &&
                parsed.NotAfter.ToUniversalTime() > DateTime.UtcNow &&
                Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) == trustedPublicKey;
        }
        catch (CryptographicException) { return false; }
    }

    public void Export(string path, string password)
    {
        using var key = Open();
        byte[] privateKey = key.ExportPkcs8PrivateKey();
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(new Recovery(RecoveryKind,
            Convert.ToBase64String(privateKey), InternetSettings.Load(root)), Json.Options);
        try
        {
            var envelope = ProtectedSetup.Seal(plain, password, Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(envelope, Json.Options));
        }
        finally { CryptographicOperations.ZeroMemory(privateKey); CryptographicOperations.ZeroMemory(plain); }
    }

    public void Restore(string path, string password)
    {
        if (new FileInfo(path).Length > ProtectedSetup.MaximumBytes) throw new InvalidDataException("Invalid admin recovery file.");
        byte[] plain = ProtectedSetup.Read(File.ReadAllBytes(path)).Open(password);
        byte[]? privateKey = null;
        try
        {
            var recovery = JsonSerializer.Deserialize<Recovery>(plain, Json.Options);
            if (recovery?.Kind != RecoveryKind) throw new InvalidDataException("This file does not contain admin recovery access.");
            privateKey = Convert.FromBase64String(recovery.PrivateKey);
            using var key = ReadKey(privateKey);
            recovery.Settings?.Validate(true);
            string pendingSetup = new SecurityMigrationStore(root).PendingSetupPath;
            bool completedSetup = recovery.Settings?.SecurityId.Length > 0 && File.Exists(pendingSetup) &&
                ProtectedSetup.Read(File.ReadAllBytes(pendingSetup)).ProfileId == recovery.Settings.SecurityId;
            // Authenticate and validate the entire recovery before changing saved access.
            if (!IsAdmin) AdminMaintenancePreference.Save(root, false);
            recovery.Settings?.Save(root);
            Vault.Save(KeyPath, privateKey);
            if (completedSetup) File.Delete(pendingSetup);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (privateKey != null) CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    public void Import(string path)
    {
        if (IsAdmin) return; // Reinstallation keeps the existing authority.
        if (new FileInfo(path).Length > ProtectedSetup.MaximumBytes) throw new InvalidDataException("Invalid admin credential.");
        byte[] bytes = File.ReadAllBytes(path);
        _ = ProtectedSetup.Read(bytes);
        Directory.CreateDirectory(root);
        File.WriteAllBytes(PendingPath, bytes);
    }

    public bool Disable()
    {
        bool changed = File.Exists(KeyPath) || File.Exists(PendingPath);
        File.Delete(KeyPath);
        File.Delete(PendingPath);
        return changed;
    }
}
