using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using RemoteDebugger.Core;

namespace RemoteDebugger;

public sealed record AuthenticodeSignatureInfo(
    string SignerThumbprint,
    string Subject,
    bool WindowsTrusted,
    bool CryptographicallyValid,
    int TrustStatus);

/// <summary>
/// Validates both parts WinVerifyTrust alone does not separate for an initially
/// untrusted self-signed publisher: the PE Authenticode digest and the CMS
/// signature over that digest. Provisioning pins the exact leaf certificate;
/// later checks additionally require that pin.
/// </summary>
internal static class AuthenticodeVerifier
{
    private const int Success = 0;
    private const int CertEUntrustedRoot = unchecked((int)0x800B0109);
    private const string CodeSigningOid = "1.3.6.1.5.5.7.3.3";
    private const string Sha256Oid = "2.16.840.1.101.3.4.2.1";
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public static AuthenticodeSignatureInfo InspectForEnrollment(string path)
    {
        var validated = ValidateEmbeddedSignature(Path.GetFullPath(path));
        int status = VerifyTrust(path);
        if (status is not (Success or CertEUntrustedRoot))
            throw new InvalidDataException($"Authenticode trust failed with an unsupported status (0x{status:X8}); only a trusted chain or an otherwise-valid self-signed leaf may be provisioned.");
        return Build(validated.Certificate, status);
    }

    public static AuthenticodeSignatureInfo VerifyPinnedTrusted(string path, string expectedThumbprint)
    {
        UpdatePolicy.ValidateSha256(expectedThumbprint, "pinned publisher certificate SHA-256");
        var validated = ValidateEmbeddedSignature(Path.GetFullPath(path));
        string thumbprint = validated.Certificate.GetCertHashString(HashAlgorithmName.SHA256);
        if (!UpdatePolicy.FixedHexEquals(thumbprint, expectedThumbprint))
            throw new UnauthorizedAccessException("Executable publisher does not match the provisioned publisher certificate.");
        int status = VerifyTrust(path);
        // A cryptographically verified, exact pinned self-signed leaf is the
        // explicit trust root accepted by the one-time administrator action.
        // No broad machine root is installed. Any other Windows trust failure is
        // rejected even when the thumbprint happens to match.
        if (status is not (Success or CertEUntrustedRoot))
            throw new UnauthorizedAccessException($"Authenticode trust failed (0x{status:X8}).");
        return Build(validated.Certificate, status);
    }

    private static (X509Certificate2 Certificate, byte[] Digest) ValidateEmbeddedSignature(string path)
    {
        byte[] image = File.ReadAllBytes(path);
        if (image.LongLength is < 256 or > ExecutableSnapshot.MaximumSize)
            throw new InvalidDataException("Executable size is outside Authenticode validation bounds.");
        var layout = ReadPeLayout(image);
        byte[] signature = ReadPkcs7Certificate(image, layout.CertificateOffset, layout.CertificateSize);
        var cms = new SignedCms();
        try
        {
            cms.Decode(signature);
            cms.CheckSignature(verifySignatureOnly: true);
        }
        catch (CryptographicException ex) { throw new InvalidDataException("The embedded Authenticode CMS signature is invalid.", ex); }
        if (cms.SignerInfos.Count < 1 || cms.SignerInfos[0].Certificate is not X509Certificate2 certificate)
            throw new InvalidDataException("Authenticode signature has no signer certificate.");
        ValidateCodeSigningCertificate(certificate);
        (string algorithm, byte[] signedDigest) = ReadIndirectDataDigest(cms.ContentInfo.Content);
        if (algorithm != Sha256Oid) throw new InvalidDataException("Only SHA-256 Authenticode signatures are accepted.");
        byte[] actualDigest = ComputeAuthenticodeDigest(image, layout);
        if (!CryptographicOperations.FixedTimeEquals(signedDigest, actualDigest))
            throw new InvalidDataException("Authenticode PE image digest does not match the signed digest.");
        return (certificate, actualDigest);
    }

    private static PeLayout ReadPeLayout(ReadOnlySpan<byte> image)
    {
        if (BinaryPrimitives.ReadUInt16LittleEndian(image) != 0x5A4D) throw new InvalidDataException("Executable has no DOS header.");
        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(image[0x3C..]);
        if (peOffset < 0x40 || peOffset > image.Length - 256 || BinaryPrimitives.ReadUInt32LittleEndian(image[peOffset..]) != 0x00004550)
            throw new InvalidDataException("Executable has no valid PE header.");
        int optional = checked(peOffset + 24);
        ushort optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(image[(peOffset + 20)..]);
        if (optional + optionalSize > image.Length) throw new InvalidDataException("PE optional header exceeds the executable.");
        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(image[optional..]);
        int directories = magic switch { 0x10B => optional + 96, 0x20B => optional + 112, _ => throw new InvalidDataException("Unsupported PE optional-header format.") };
        int checksum = optional + 64;
        int certificateDirectory = directories + 8 * 4;
        if (certificateDirectory + 8 > optional + optionalSize) throw new InvalidDataException("PE security directory is missing.");
        uint certificateOffset = BinaryPrimitives.ReadUInt32LittleEndian(image[certificateDirectory..]);
        uint certificateSize = BinaryPrimitives.ReadUInt32LittleEndian(image[(certificateDirectory + 4)..]);
        if (certificateOffset == 0 || certificateSize < 8 || (ulong)certificateOffset + certificateSize > (ulong)image.Length)
            throw new InvalidDataException("PE Authenticode certificate table is missing or outside the executable.");
        return new(checksum, certificateDirectory, checked((int)certificateOffset), checked((int)certificateSize));
    }

    private static byte[] ReadPkcs7Certificate(ReadOnlySpan<byte> image, int certificateOffset, int certificateTableSize)
    {
        int cursor = certificateOffset, end = checked(certificateOffset + certificateTableSize);
        while (cursor + 8 <= end)
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(image[cursor..]);
            ushort revision = BinaryPrimitives.ReadUInt16LittleEndian(image[(cursor + 4)..]);
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(image[(cursor + 6)..]);
            if (length < 8 || cursor + length > end) throw new InvalidDataException("Invalid WIN_CERTIFICATE record.");
            if (revision == 0x0200 && type == 0x0002) return image.Slice(cursor + 8, length - 8).ToArray();
            cursor = checked(cursor + ((length + 7) & ~7));
        }
        throw new InvalidDataException("PE certificate table has no PKCS#7 Authenticode signature.");
    }

    private static (string Algorithm, byte[] Digest) ReadIndirectDataDigest(ReadOnlyMemory<byte> content)
    {
        try
        {
            var reader = new AsnReader(content, AsnEncodingRules.BER);
            var indirect = reader.ReadSequence();
            _ = indirect.ReadEncodedValue();
            var digestInfo = indirect.ReadSequence();
            var algorithm = digestInfo.ReadSequence();
            string oid = algorithm.ReadObjectIdentifier();
            if (algorithm.HasData) _ = algorithm.ReadEncodedValue();
            algorithm.ThrowIfNotEmpty();
            byte[] digest = digestInfo.ReadOctetString();
            digestInfo.ThrowIfNotEmpty();
            indirect.ThrowIfNotEmpty();
            reader.ThrowIfNotEmpty();
            return (oid, digest);
        }
        catch (AsnContentException ex) { throw new InvalidDataException("Authenticode indirect-data digest is malformed.", ex); }
    }

    private static byte[] ComputeAuthenticodeDigest(byte[] image, PeLayout layout)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(0, layout.ChecksumOffset);
        Append(layout.ChecksumOffset + 4, layout.CertificateDirectoryOffset - (layout.ChecksumOffset + 4));
        Append(layout.CertificateDirectoryOffset + 8, layout.CertificateOffset - (layout.CertificateDirectoryOffset + 8));
        int afterCertificate = checked(layout.CertificateOffset + layout.CertificateSize);
        Append(afterCertificate, image.Length - afterCertificate);
        return hash.GetHashAndReset();

        void Append(int offset, int count)
        {
            if (offset < 0 || count < 0 || offset + count > image.Length) throw new InvalidDataException("Invalid PE Authenticode hash span.");
            if (count > 0) hash.AppendData(image, offset, count);
        }
    }

    private static void ValidateCodeSigningCertificate(X509Certificate2 certificate)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < certificate.NotBefore || now > certificate.NotAfter)
            throw new InvalidDataException("The publisher certificate is outside its validity period.");
        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (eku == null || !eku.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == CodeSigningOid))
            throw new InvalidDataException("The Authenticode certificate is not valid for code signing.");
        var constraints = certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
        if (constraints?.CertificateAuthority == true)
            throw new InvalidDataException("A certificate-authority certificate cannot be provisioned as the application publisher leaf.");
    }

    private static AuthenticodeSignatureInfo Build(X509Certificate2 certificate, int status) =>
        new(certificate.GetCertHashString(HashAlgorithmName.SHA256), certificate.Subject, status == Success, true, status);

    private static int VerifyTrust(string path)
    {
        IntPtr pathPointer = Marshal.StringToCoTaskMemUni(Path.GetFullPath(path));
        IntPtr filePointer = IntPtr.Zero;
        try
        {
            var file = new WinTrustFileInfo { StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(), FilePath = pathPointer };
            filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(file, filePointer, false);
            var data = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(), UiChoice = 2, RevocationChecks = 0, UnionChoice = 1,
                FileInfo = filePointer, StateAction = 1, ProviderFlags = 0x00000004 | 0x00001000
            };
            int result = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
            data.StateAction = 2;
            _ = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
            return result;
        }
        finally
        {
            if (filePointer != IntPtr.Zero) Marshal.FreeHGlobal(filePointer);
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    private sealed record PeLayout(int ChecksumOffset, int CertificateDirectoryOffset, int CertificateOffset, int CertificateSize);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo { public uint StructSize; public IntPtr FilePath; public IntPtr FileHandle; public IntPtr KnownSubject; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize; public IntPtr PolicyCallbackData; public IntPtr SipClientData; public uint UiChoice;
        public uint RevocationChecks; public uint UnionChoice; public IntPtr FileInfo; public uint StateAction;
        public IntPtr StateData; public IntPtr UrlReference; public uint ProviderFlags; public uint UiContext; public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr window, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WinTrustData data);
}
