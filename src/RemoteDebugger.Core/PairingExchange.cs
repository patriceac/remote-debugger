using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Agreement.JPake;
using Org.BouncyCastle.Math;

namespace RemoteDebugger.Core;

public sealed record PakeMessage(string ParticipantId, string[] Values);

/// <summary>
/// Bouncy Castle J-PAKE with mandatory mutual key confirmation, then HKDF/HMAC
/// binding to the actual TLS certificate and controller binary identity.
/// The displayed code or private installer secret is never transmitted.
/// </summary>
public sealed class PairingExchange : IDisposable
{
    public const string Protocol = "RemoteDebugger.Pair.JPAKE.v2";
    private readonly JPakeParticipant participant;
    private readonly string role, fingerprint, binaryHash;
    private string? peer;
    private BigInteger? material;
    private byte[]? key;
    public PairingExchange(string role, string code, string fingerprint, string binaryHash)
    {
        if (role is not ("agent" or "controller")) throw new ArgumentException("Invalid pairing role.");
        if (!ValidSecret(code)) throw new ArgumentException("Invalid pairing secret.");
        if (!ValidHash(fingerprint) || !ValidHash(binaryHash)) throw new ArgumentException("Invalid pairing identity.");
        this.role = role; this.fingerprint = fingerprint.ToUpperInvariant(); this.binaryHash = binaryHash.ToUpperInvariant();
        char[] secret = code.ToCharArray();
        try { participant = new JPakeParticipant(role + "-" + Guid.NewGuid().ToString("N"), secret); }
        finally { Array.Clear(secret); }
    }
    public static bool ValidHash(string? hash) => hash?.Length == 64 && hash.All(Uri.IsHexDigit);
    public static bool ValidSecret(string code) => code.Length == 6 && code.All(char.IsAsciiDigit) || ValidHash(code);
    private static string Hex(BigInteger value) => value.ToString(16);
    private static BigInteger[] Decode(PakeMessage message, int count)
    {
        if (message.ParticipantId == null || message.ParticipantId.Length > 80 || message.Values?.Length != count)
            throw new InvalidDataException("Invalid pairing frame.");
        return message.Values.Select(value =>
        {
            if (string.IsNullOrEmpty(value) || value.Length > 800 || !value.TrimStart('-').All(Uri.IsHexDigit))
                throw new InvalidDataException("Invalid pairing value.");
            return new BigInteger(value, 16);
        }).ToArray();
    }
    private void CheckPeer(PakeMessage message)
    {
        string prefix = role == "agent" ? "controller-" : "agent-";
        if (!message.ParticipantId.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(message.ParticipantId[prefix.Length..], "N", out _) ||
            (peer != null && message.ParticipantId != peer)) throw new InvalidDataException("Pairing participant changed.");
        peer = message.ParticipantId;
    }
    public PakeMessage Round1()
    {
        var p = participant.CreateRound1PayloadToSend();
        return new(p.ParticipantId, [Hex(p.Gx1), Hex(p.Gx2), .. p.KnowledgeProofForX1.Select(Hex), .. p.KnowledgeProofForX2.Select(Hex)]);
    }
    public void ReceiveRound1(PakeMessage message)
    {
        var n = Decode(message, 6); CheckPeer(message);
        participant.ValidateRound1PayloadReceived(new JPakeRound1Payload(message.ParticipantId, n[0], n[1], [n[2], n[3]], [n[4], n[5]]));
    }
    public PakeMessage Round2()
    {
        var p = participant.CreateRound2PayloadToSend();
        return new(p.ParticipantId, [Hex(p.A), .. p.KnowledgeProofForX2s.Select(Hex)]);
    }
    public void ReceiveRound2(PakeMessage message)
    {
        var n = Decode(message, 3); CheckPeer(message);
        participant.ValidateRound2PayloadReceived(new JPakeRound2Payload(message.ParticipantId, n[0], [n[1], n[2]]));
    }
    public PakeMessage Round3()
    {
        material = participant.CalculateKeyingMaterial();
        var p = participant.CreateRound3PayloadToSend(material);
        return new(p.ParticipantId, [Hex(p.MacTag)]);
    }
    public void ReceiveRound3(PakeMessage message)
    {
        var n = Decode(message, 1); CheckPeer(message);
        if (material == null) throw new InvalidOperationException("Missing pairing key.");
        participant.ValidateRound3PayloadReceived(new JPakeRound3Payload(message.ParticipantId, n[0]), material);
        byte[] raw = material.ToByteArrayUnsigned();
        try { key = HKDF.DeriveKey(HashAlgorithmName.SHA256, raw, 32, info: Encoding.UTF8.GetBytes(Protocol + "|" + fingerprint + "|" + binaryHash)); }
        finally { CryptographicOperations.ZeroMemory(raw); material = null; }
    }
    public string Proof(string sender, string token = "")
    {
        if (key == null) throw new InvalidOperationException("Pairing was not mutually confirmed.");
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Protocol + "|" + sender + "|" + fingerprint + "|" + binaryHash + "|" + token)));
    }
    public bool VerifyProof(string proof, string sender, string token = "") => PairingExchange.ValidHash(proof) && Safety.Equal(Proof(sender, token), proof);
    public void Dispose() { if (key != null) CryptographicOperations.ZeroMemory(key); key = null; material = null; }
}
