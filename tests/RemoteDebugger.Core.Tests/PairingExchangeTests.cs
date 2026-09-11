using RemoteDebugger.Core;
using Xunit;

public sealed class PairingExchangeTests
{
    private static readonly string Fingerprint = new('A', 64), Binary = new('B', 64);
    private static void ExchangeThroughRound2(PairingExchange a, PairingExchange b)
    {
        var a1 = a.Round1(); var b1 = b.Round1(); a.ReceiveRound1(b1); b.ReceiveRound1(a1);
        var a2 = a.Round2(); var b2 = b.Round2(); a.ReceiveRound2(b2); b.ReceiveRound2(a2);
    }
    [Fact] public void SharedSixDigitCodeAuthenticatesBothRolesAndToken()
    {
        using var a = new PairingExchange("agent", "000123", Fingerprint, Binary);
        using var b = new PairingExchange("controller", "000123", Fingerprint, Binary);
        ExchangeThroughRound2(a, b); var a3 = a.Round3(); var b3 = b.Round3(); a.ReceiveRound3(b3); b.ReceiveRound3(a3);
        Assert.True(a.VerifyProof(b.Proof("controller"), "controller"));
        Assert.True(b.VerifyProof(a.Proof("agent", "token"), "agent", "token"));
        Assert.False(b.VerifyProof(a.Proof("agent", "token"), "agent", "substituted"));
    }
    [Fact] public void WrongCodeFailsMutualConfirmation()
    {
        using var a = new PairingExchange("agent", "000123", Fingerprint, Binary);
        using var b = new PairingExchange("controller", "000124", Fingerprint, Binary);
        ExchangeThroughRound2(a, b); a.Round3(); var b3 = b.Round3(); Assert.ThrowsAny<Exception>(() => a.ReceiveRound3(b3));
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void CertificateOrBinarySubstitutionFailsChannelBinding(bool substituteCertificate)
    {
        using var a = new PairingExchange("agent", "000123", Fingerprint, Binary);
        using var b = new PairingExchange("controller", "000123", substituteCertificate ? new string('C', 64) : Fingerprint, substituteCertificate ? Binary : new string('C', 64));
        ExchangeThroughRound2(a, b); var a3 = a.Round3(); var b3 = b.Round3(); a.ReceiveRound3(b3); b.ReceiveRound3(a3);
        Assert.False(a.VerifyProof(b.Proof("controller"), "controller"));
    }
    [Fact] public void UnconfirmedKeyCannotAuthenticate()
    {
        using var a = new PairingExchange("agent", "000123", Fingerprint, Binary);
        Assert.Throws<InvalidOperationException>(() => a.Proof("agent"));
        Assert.Throws<InvalidDataException>(() => a.ReceiveRound1(new PakeMessage("controller-invalid", [])));
    }
}
