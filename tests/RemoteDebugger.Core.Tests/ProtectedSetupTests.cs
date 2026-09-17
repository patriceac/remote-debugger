using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteDebugger.Core;
using Xunit;

namespace RemoteDebugger.Core.Tests;

public sealed class ProtectedSetupTests
{
    [Fact]
    public void RoundTripDoesNotStorePasswordOrCredentialsAndUsesFreshRandomness()
    {
        const string password = "six random words belong in this passphrase";
        byte[] plain = Encoding.UTF8.GetBytes("secret connection credentials");
        string id = Guid.NewGuid().ToString("N");
        var first = ProtectedSetup.Seal(plain, password, id);
        var second = ProtectedSetup.Seal(plain, password, id);
        string json = JsonSerializer.Serialize(first, Json.Options);
        Assert.DoesNotContain(password, json); Assert.DoesNotContain(Encoding.UTF8.GetString(plain), json);
        Assert.NotEqual(first.Salt, second.Salt); Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.Equal(plain, ProtectedSetup.Read(Encoding.UTF8.GetBytes(json)).Open(password));
    }

    [Fact]
    public void WrongPasswordTamperingAndSubstitutedIdentityAreRejected()
    {
        const string password = "a long enough test passphrase";
        var envelope = ProtectedSetup.Seal([1, 2, 3], password, Guid.NewGuid().ToString("N"));
        Assert.Throws<CryptographicException>(() => envelope.Open("different long password"));
        Assert.Throws<CryptographicException>(() => (envelope with { Ciphertext = "BAUG" }).Open(password));
        Assert.Throws<CryptographicException>(() => (envelope with { ProfileId = Guid.NewGuid().ToString("N") }).Open(password));
    }

    [Fact]
    public void MalformedAndOversizedInputsFailBeforeKeyDerivation()
    {
        Assert.Throws<ArgumentException>(() => ProtectedSetup.Seal([1], "short", Guid.NewGuid().ToString("N")));
        Assert.Throws<InvalidDataException>(() => ProtectedSetup.Read(new byte[ProtectedSetup.MaximumBytes + 1]));
        Assert.Throws<InvalidDataException>(() => new ProtectedSetup("unknown", "", "", "", "", "").Open("test"));
    }
}
