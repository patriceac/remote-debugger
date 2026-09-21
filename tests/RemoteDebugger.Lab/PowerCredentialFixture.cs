using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger.Lab;

internal sealed class PowerCredentialFixture
{
    internal sealed record Binding(string UserName, string UserSid, string PoolBaselineId);
    internal Binding Identity { get; }
    private readonly string protectedPassword;

    private PowerCredentialFixture(Binding identity, string protectedPassword)
    { Identity = identity; this.protectedPassword = protectedPassword; }

    internal static PowerCredentialFixture Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new IOException("The request-private guest credential fixture is missing.");
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is < 1 or > 16384) throw new IOException("The guest credential fixture has an invalid size.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var value = document.RootElement;
        if (value.GetProperty("FormatVersion").GetInt32() != 1 || value.GetProperty("Protection").GetString() != "DPAPI CurrentUser")
            throw new IOException("The guest credential fixture has an unsupported protection format.");
        var binding = new Binding(value.GetProperty("UserName").GetString()!, value.GetProperty("UserSid").GetString()!, value.GetProperty("PoolBaselineId").GetString()!);
        if (string.IsNullOrWhiteSpace(binding.UserName) || !Guid.TryParse(binding.PoolBaselineId, out _) ||
            binding.UserSid != WindowsIdentity.GetCurrent().User?.Value)
            throw new IOException("The guest credential fixture is not bound to this interactive baseline account.");
        return new(binding, value.GetProperty("ProtectedPassword").GetString()!);
    }

    internal static void RequirePeerBinding(Binding local, Binding peer)
    {
        if (string.IsNullOrWhiteSpace(local.UserName) || string.IsNullOrWhiteSpace(local.UserSid) ||
            !Guid.TryParse(local.PoolBaselineId, out var localBaseline) || !Guid.TryParse(peer.PoolBaselineId, out var peerBaseline) ||
            localBaseline != peerBaseline || !string.Equals(local.UserName, peer.UserName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(local.UserSid, peer.UserSid, StringComparison.Ordinal))
            throw new IOException("The target does not share the controller's managed baseline credential binding.");
    }

    internal void EnterPassword(JsonElement peer)
    {
        RequirePeerBinding(Identity, peer.Deserialize<Binding>(Json.Options) ?? throw new IOException("The target credential binding is missing."));
        byte[] encrypted = Convert.FromBase64String(protectedPassword);
        byte[]? clear = null;
        try
        {
            clear = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            if (clear.Length is < 1 or > 8192) throw new IOException("The guest password fixture has an invalid size.");
            Native.TypeText(0, Encoding.UTF8.GetString(clear));
        }
        finally
        {
            if (clear != null) CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }
}
