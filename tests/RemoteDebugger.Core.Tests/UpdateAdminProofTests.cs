using System.Security.Cryptography;
using RemoteDebugger.Core;
using Xunit;

public sealed class UpdateAdminProofTests
{
    private static string Hash(char c) => new(c, 64);

    [Fact]
    public void ProofIsBoundToNonceDeviceOperationAndExecutable()
    {
        using var admin = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(admin.ExportSubjectPublicKeyInfo());
        string proof = UpdateAdminProof.Sign(admin, Hash('A'), Hash('B'), "admin.connect", Hash('C'));
        UpdateAdminProof.Verify(proof, Hash('A'), Hash('B'), "admin.connect", Hash('C'), publicKey);
        foreach (var (nonce, device, operation, binary) in new[]
        {
            (Hash('D'), Hash('B'), "admin.connect", Hash('C')),
            (Hash('A'), Hash('D'), "admin.connect", Hash('C')),
            (Hash('A'), Hash('B'), "admin.inspect", Hash('C')),
            (Hash('A'), Hash('B'), "admin.connect", Hash('D'))
        }) Assert.Throws<UnauthorizedAccessException>(() => UpdateAdminProof.Verify(proof, nonce, device, operation, binary, publicKey));
        Assert.Throws<UnauthorizedAccessException>(() => UpdateAdminProof.Verify(proof, Hash('A'), Hash('B'), "admin.connect", Hash('C'), Convert.ToBase64String(stranger.ExportSubjectPublicKeyInfo())));
    }
}
