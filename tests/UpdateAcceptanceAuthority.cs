using System.IO;
using RemoteDebugger;

namespace RemoteDebugger.Testing;

// Public test material, deliberately rejected by normal release builds.
// Only the Lab/test assemblies include the private half, never the product.
internal static class UpdateAcceptanceAuthority
{
    internal const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE19IJpleMRXsNXWgvgqOigzVOON1teOo3sLF+M7fPnL2kbAWXbnLTaYlL4vipc57+ElM30kB2bg84XoYDWWc2Zw==";
    private const string PrivateKey = "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQg6MIKQCb9hd+6mwrTH6xla5DJkkCUn/dWn2VfBTxwQIyhRANCAATX0gmmV4xFew1daC+Co6KDNU443W146jewsX4zt8+cvaRsBZductNpiUvi+Klznv4SUzfSQHZuDzhehgNZZzZn";

    internal static void Enroll(string root) =>
        Vault.Save(Path.Combine(root, "update-admin.dpapi"), Convert.FromBase64String(PrivateKey));
}
