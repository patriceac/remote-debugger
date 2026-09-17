using System.Buffers.Binary;
using RemoteDebugger.Core;
using Xunit;

namespace RemoteDebugger.Core.Tests;

public sealed class ProtocolTests
{
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    [Fact] public void PairingIsClosedByDefault() => Assert.False(new PairingGate().TryConsume("000000"));
    [Fact] public void PrivateSupportHasNoDisplayedCodeAndItsGrantIsSingleUseAndRevocable()
    {
        var gate = new PairingGate(); string secret = new('A', 64);
        Assert.False(gate.TryConsume(secret)); gate.OpenPrivate(secret);
        Assert.Null(gate.CurrentCode); Assert.False(gate.TryConsume("000000"));
        Assert.True(gate.TryConsume(secret)); Assert.False(gate.TryConsume(secret));
        gate.OpenPrivate(secret); gate.Close(); Assert.False(gate.TryConsume(secret));
    }
    [Fact] public void PairingIsSingleUse() { var gate = new PairingGate(); string code = gate.Open(); Assert.Equal(6, code.Length); Assert.All(code, c => Assert.True(char.IsAsciiDigit(c))); Assert.True(gate.TryConsume(code)); Assert.False(gate.TryConsume(code)); }
    [Fact] public void PairingExpiresAndRotates() { var clock = new Clock(); var gate = new PairingGate(clock); string code = gate.Open(); clock.Now += TimeSpan.FromMinutes(5); Assert.False(gate.TryConsume(code)); Assert.NotEqual(code, gate.CurrentCode); Assert.True(gate.TryConsume(gate.CurrentCode!)); }
    [Fact] public void ExchangeCannotFinishAfterCodeExpires() { var clock = new Clock(); var gate = new PairingGate(clock); gate.Open(); string code = gate.BeginAttempt()!; clock.Now += TimeSpan.FromMinutes(5); Assert.False(gate.CompleteAttempt(code)); }
    [Fact] public void RotationDoesNotBypassRollingAttemptLimit() { var gate = new PairingGate(); for (int batch = 0; batch < 3; batch++) { gate.Open(); for (int attempt = 0; attempt < 5; attempt++) Assert.NotNull(gate.BeginAttempt()); } gate.Open(); Assert.Null(gate.BeginAttempt()); }
    [Fact] public void FiveFailuresLockPairing() { var gate = new PairingGate(); string code = gate.Open(); for (int i = 0; i < 5; i++) Assert.False(gate.TryConsume("wrong")); Assert.False(gate.TryConsume(code)); }
    [Fact] public void CloseRevokesPairingCode() { var gate = new PairingGate(); string code = gate.Open(); gate.Close(); Assert.False(gate.TryConsume(code)); }
    [Fact] public void ReopenInvalidatesOldCode() { var gate = new PairingGate(); string old = gate.Open(); string current = gate.Open(); if (old != current) Assert.False(gate.TryConsume(old)); Assert.True(gate.TryConsume(current)); }
    [Theory] [InlineData("../escape")] [InlineData("a/../../escape")] [InlineData("C:\\escape")] [InlineData("file:stream")] [InlineData("")]
    public void RejectsEscapingPaths(string path) => Assert.Throws<ArgumentException>(() => Safety.UnderRoot(Path.Combine(Path.GetTempPath(), "rd-unit-root"), path));
    [Fact] public void AcceptsNestedPath() { string root = Path.Combine(Path.GetTempPath(), "rd-unit-root"); Assert.Equal(Path.Combine(root, "versions", "one.exe"), Safety.UnderRoot(root, "versions/one.exe")); }
    [Theory] [InlineData(0)] [InlineData(-1)] [InlineData(Wire.MaxFrame + 1)]
    public async Task RejectsBadFrameBeforeAllocating(int length) { byte[] data = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(data, length); await Assert.ThrowsAsync<InvalidDataException>(() => Wire.ReadAsync<Request>(new MemoryStream(data), default)); }
    [Fact] public async Task RejectsTruncatedFrame() { byte[] data = new byte[5]; BinaryPrimitives.WriteInt32BigEndian(data, 10); await Assert.ThrowsAsync<EndOfStreamException>(() => Wire.ReadAsync<Request>(new MemoryStream(data), default)); }
    [Fact] public async Task FrameRoundTrip() { var expected = new Request(Guid.NewGuid().ToString(), "test-only", "status", Json.Element(new { value = "é日本" })); using var ms = new MemoryStream(); await Wire.WriteAsync(ms, expected, default); ms.Position = 0; var actual = await Wire.ReadAsync<Request>(ms, default); Assert.Equal(expected.Id, actual.Id); Assert.Equal("é日本", actual.Args.Str("value")); }
    [Fact] public void HashComparisonRejectsDifferentLength() { Assert.True(Safety.Equal(Safety.Hash("a"), Safety.Hash("a"))); Assert.False(Safety.Equal("a", "aa")); Assert.False(Safety.Equal(Safety.Hash("a"), Safety.Hash("b"))); }
}
