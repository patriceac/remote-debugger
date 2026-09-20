using System.Security.Cryptography;
using System.Text;

namespace RemoteDebugger.Core;

public static class UpdateAdminProof
{
    // Fixture builds use a public, disposable test authority, never real credentials.
#if REMOTEDEBUGGER_UPDATE_ACCEPTANCE
    public const string TrustedPublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE19IJpleMRXsNXWgvgqOigzVOON1teOo3sLF+M7fPnL2kbAWXbnLTaYlL4vipc57+ElM30kB2bg84XoYDWWc2Zw==";
#else
    // Public authority only. The matching private key never ships with the app.
    public const string TrustedPublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEsl7SJeEx4X++vk1jndocVn8NaUgENs8ubXHassqM3Ji9aibaf7etWfDRuvZpWs/JaJ2D33Q8R/omWooWhxR8HA==";
#endif

    public static string Sign(ECDsa key, string nonce, string target, string operation, string binary) =>
        Convert.ToBase64String(key.SignData(Message(nonce, target, operation, binary), HashAlgorithmName.SHA256));

    public static void Verify(string proof, string nonce, string target, string operation, string binary,
        string publicKey = TrustedPublicKey)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            if (proof.Length > 256 || !key.VerifyData(Message(nonce, target, operation, binary),
                Convert.FromBase64String(proof), HashAlgorithmName.SHA256))
                throw new CryptographicException();
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        { throw new UnauthorizedAccessException("This computer is not an authorized update administrator."); }
    }

    private static byte[] Message(string nonce, string target, string operation, string binary)
    {
        UpdatePolicy.ValidateSha256(nonce, "update challenge");
        UpdatePolicy.ValidateSha256(target, "target identity");
        UpdatePolicy.ValidateSha256(binary, "controller executable");
        if (operation is not ("admin.inspect" or "admin.connect" or "admin.wake" or "update.begin"))
            throw new ArgumentException("Invalid administrator operation.");
        return Encoding.UTF8.GetBytes($"RemoteDebugger.UpdateAdmin.v1|{nonce.ToUpperInvariant()}|{target.ToUpperInvariant()}|{operation}|{binary.ToUpperInvariant()}");
    }
}
