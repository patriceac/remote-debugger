using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

public static class ExecutableIdentity
{
    private static readonly Lazy<string> runningHash = new(() =>
    {
        using var executable = File.OpenRead(Environment.ProcessPath ?? throw new IOException("No process executable."));
        return Convert.ToHexString(SHA256.HashData(executable));
    });
    public static string Sha256 => runningHash.Value;
}

internal static class PairingTransport
{
    public static async Task<Connection> PairAsync(Connection target, string code, CancellationToken ct)
    {
        if (code.Length != 6 || !code.All(char.IsAsciiDigit)) throw new ArgumentException("Enter exactly six digits.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(target.Host, target.Port, deadline.Token).ConfigureAwait(false);
        string fingerprint = "";
        // This channel is provisional until the code exchange confirms its actual
        // certificate. No reusable credential or plaintext pairing code is sent.
        using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
        {
            if (certificate == null) return false;
            fingerprint = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
            return true;
        });
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "RemoteDebugger", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, deadline.Token).ConfigureAwait(false);
        if (target.Fingerprint.Length != 0 && !Safety.Equal(fingerprint, target.Fingerprint.ToUpperInvariant().Replace(":", "")))
            throw new AuthenticationException("The selected PC identity changed. Refresh discovery and pair again.");
        using var exchange = new PairingExchange("controller", code, fingerprint, ExecutableIdentity.Sha256);
        string id = Guid.NewGuid().ToString();
        await Wire.WriteAsync(tls, new Request(id, "", "pair.v2", Json.Element(new { protocol = PairingExchange.Protocol, binarySha256 = ExecutableIdentity.Sha256, round = exchange.Round1() })), deadline.Token).ConfigureAwait(false);
        var one = RemoteClient.Require(await Wire.ReadAsync<Reply>(tls, deadline.Token).ConfigureAwait(false));
        exchange.ReceiveRound1(one.Deserialize<PakeMessage>(Json.Options)!);
        await Wire.WriteAsync(tls, exchange.Round2(), deadline.Token).ConfigureAwait(false);
        exchange.ReceiveRound2(await Wire.ReadAsync<PakeMessage>(tls, deadline.Token).ConfigureAwait(false));
        await Wire.WriteAsync(tls, exchange.Round3(), deadline.Token).ConfigureAwait(false);
        exchange.ReceiveRound3(await Wire.ReadAsync<PakeMessage>(tls, deadline.Token).ConfigureAwait(false));
        await Wire.WriteAsync(tls, new { proof = exchange.Proof("controller") }, deadline.Token).ConfigureAwait(false);
        var grant = RemoteClient.Require(await Wire.ReadAsync<Reply>(tls, deadline.Token).ConfigureAwait(false));
        string token = grant.Str("token");
        if (!PairingExchange.ValidHash(token) || !exchange.VerifyProof(grant.Str("proof"), "agent", token))
            throw new AuthenticationException("Pairing channel authentication failed.");
        return target with { Fingerprint = fingerprint, Token = token };
    }
    public static async Task<(string Token, string ControllerHash)> AcceptAsync(SslStream tls, Request request,
        PairingGate gate, string fingerprint, Action<string, string> registerGrant, CancellationToken ct)
    {
        if (request.Args.Str("protocol") != PairingExchange.Protocol || !PairingExchange.ValidHash(request.Args.Str("binarySha256")))
            throw new AuthenticationException("Unsupported pairing protocol. Update this installation.");
        string code = gate.BeginAttempt() ?? throw new AuthenticationException("Pairing is unavailable. Check the agent code and retry after its countdown.");
        string controllerHash = request.Args.Str("binarySha256").ToUpperInvariant();
        using var exchange = new PairingExchange("agent", code, fingerprint, controllerHash);
        var first = exchange.Round1(); exchange.ReceiveRound1(request.Args.GetProperty("round").Deserialize<PakeMessage>(Json.Options)!);
        await Wire.WriteAsync(tls, Reply.Success(request.Id, first), ct).ConfigureAwait(false);
        var second = exchange.Round2(); exchange.ReceiveRound2(await Wire.ReadAsync<PakeMessage>(tls, ct).ConfigureAwait(false));
        await Wire.WriteAsync(tls, second, ct).ConfigureAwait(false);
        var third = exchange.Round3(); exchange.ReceiveRound3(await Wire.ReadAsync<PakeMessage>(tls, ct).ConfigureAwait(false));
        await Wire.WriteAsync(tls, third, ct).ConfigureAwait(false);
        var proof = await Wire.ReadAsync<JsonElement>(tls, ct).ConfigureAwait(false);
        if (!exchange.VerifyProof(proof.Str("proof"), "controller") || !gate.CompleteAttempt(code))
            throw new AuthenticationException("Pairing code is incorrect or expired.");
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        registerGrant(token, controllerHash);
        await Wire.WriteAsync(tls, Reply.Success(request.Id, new { token, proof = exchange.Proof("agent", token) }), ct).ConfigureAwait(false);
        return (token, controllerHash);
    }
}
