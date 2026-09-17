using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace RemoteDebugger.Core;

/// <summary>Portable setup ciphertext. The passphrase and derived key are never serialized.</summary>
public sealed record ProtectedSetup(string Format, string ProfileId, string Salt, string Nonce, string Ciphertext, string Tag)
{
    public const string FormatName = "RemoteDebugger.ProtectedSetup.v1";
    public const int MaximumBytes = 16384;
    private byte[] AssociatedData => Encoding.UTF8.GetBytes(FormatName + "|" + ProfileId);

    public static ProtectedSetup Seal(byte[] plaintext, string passphrase, string profileId)
    {
        if (passphrase.Length is < 16 or > 1024) throw new ArgumentException("Use a passphrase of at least 16 characters.");
        if (plaintext.Length is < 1 or > 8192 || !Guid.TryParseExact(profileId, "N", out _)) throw new ArgumentException("Invalid setup data.");
        byte[] salt = RandomNumberGenerator.GetBytes(16), nonce = RandomNumberGenerator.GetBytes(12);
        byte[] key = Derive(passphrase, salt), cipher = new byte[plaintext.Length], tag = new byte[16];
        var envelope = new ProtectedSetup(FormatName, profileId, Convert.ToBase64String(salt), Convert.ToBase64String(nonce), "", "");
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plaintext, cipher, tag, envelope.AssociatedData);
            return envelope with { Ciphertext = Convert.ToBase64String(cipher), Tag = Convert.ToBase64String(tag) };
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public byte[] Open(string passphrase)
    {
        Validate();
        if (passphrase.Length is < 1 or > 1024) throw new CryptographicException("Incorrect passphrase or damaged setup file.");
        byte[] key = Derive(passphrase, Convert.FromBase64String(Salt));
        byte[] plaintext = new byte[Convert.FromBase64String(Ciphertext).Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(Convert.FromBase64String(Nonce), Convert.FromBase64String(Ciphertext), Convert.FromBase64String(Tag), plaintext, AssociatedData);
            return plaintext;
        }
        catch (CryptographicException) { CryptographicOperations.ZeroMemory(plaintext); throw new CryptographicException("Incorrect passphrase or damaged setup file."); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public void Validate()
    {
        if (Format != FormatName || !Guid.TryParseExact(ProfileId, "N", out _) ||
            !ValidBase64(Salt, 16, 16) || !ValidBase64(Nonce, 12, 12) ||
            !ValidBase64(Tag, 16, 16) || !ValidBase64(Ciphertext, 1, 8192))
            throw new InvalidDataException("Invalid protected setup file.");
    }

    public static ProtectedSetup Read(byte[] data)
    {
        if (data.Length > MaximumBytes) throw new InvalidDataException("Setup file is too large.");
        var envelope = JsonSerializer.Deserialize<ProtectedSetup>(data, Json.Options) ?? throw new InvalidDataException("Invalid protected setup file.");
        envelope.Validate(); return envelope;
    }

    private static bool ValidBase64(string? value, int min, int max)
    {
        if (value == null || value.Length > (max + 2) / 3 * 4) return false;
        Span<byte> bytes = stackalloc byte[max];
        return Convert.TryFromBase64String(value, bytes, out int written) && written >= min && written <= max;
    }

    private static byte[] Derive(string passphrase, byte[] salt)
    {
        // Fixed, bounded parameters avoid attacker-controlled KDF resource use.
        var parameters = new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
            .WithVersion(Argon2Parameters.Version13).WithSalt(salt)
            .WithMemoryAsKB(65536).WithIterations(3).WithParallelism(1).Build();
        var generator = new Argon2BytesGenerator(); generator.Init(parameters);
        byte[] password = Encoding.UTF8.GetBytes(passphrase), key = new byte[32];
        try { generator.GenerateBytes(password, key); return key; }
        finally { CryptographicOperations.ZeroMemory(password); parameters.Clear(); }
    }
}
