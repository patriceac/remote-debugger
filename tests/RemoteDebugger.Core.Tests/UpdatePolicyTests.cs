using RemoteDebugger.Core;
using Xunit;

public sealed class UpdatePolicyTests
{
    private const string HashA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string HashB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private static ExecutableSnapshot Snapshot(string hash = HashA, long size = 1234, string signer = HashB) =>
        new(@"C:\RemoteDebugger.exe", size, hash, "1.0.0", signer);

    [Fact]
    public void ExactBytesWinEvenWhenVersionLabelsAreEqual()
    {
        var controller = Snapshot(HashA);
        var differentBytes = Snapshot(HashB, signer: HashB);
        Assert.Throws<InvalidOperationException>(() => UpdatePolicy.RequireExactControllerBinary(controller, differentBytes));
    }

    [Theory]
    [InlineData("1.0.0", "9.0.0")]
    [InlineData("1.0.0", "1.0.0.0")]
    [InlineData("1.0.0", null)]
    [InlineData(null, "1.0.0")]
    public void UpdatesRejectDowngradesSameVersionAndUnknownVersions(string? candidate, string? installed)
    {
        Assert.Throws<InvalidOperationException>(() => UpdatePolicy.RequireNewerRelease(
            Snapshot() with { FileVersion = candidate }, Snapshot() with { FileVersion = installed }));
    }

    [Fact]
    public void NewerReleaseIsAccepted() => UpdatePolicy.RequireNewerRelease(
        Snapshot() with { FileVersion = "0.4.14" }, Snapshot() with { FileVersion = "0.4.13.0" });

    [Fact]
    public void PublisherPinCannotBeReplacedByMatchingVersionMetadata() =>
        Assert.Throws<InvalidOperationException>(() => UpdatePolicy.RequirePinnedPublisher(Snapshot(signer: HashA), HashB));

    [Theory]
    [InlineData(UpdateTransactionState.Receiving, UpdateTransactionState.Staged)]
    [InlineData(UpdateTransactionState.Staged, UpdateTransactionState.Armed)]
    [InlineData(UpdateTransactionState.Replacing, UpdateTransactionState.RolledBack)]
    [InlineData(UpdateTransactionState.AwaitingStartupHealth, UpdateTransactionState.RolledBack)]
    [InlineData(UpdateTransactionState.RunningPendingRemoteHealth, UpdateTransactionState.Completed)]
    public void ValidTransactionTransitionsAreAccepted(UpdateTransactionState current, UpdateTransactionState next) =>
        UpdatePolicy.RequireTransition(current, next);

    [Theory]
    [InlineData(UpdateTransactionState.Receiving, UpdateTransactionState.Completed)]
    [InlineData(UpdateTransactionState.Armed, UpdateTransactionState.Completed)]
    [InlineData(UpdateTransactionState.RolledBack, UpdateTransactionState.Completed)]
    [InlineData(UpdateTransactionState.Completed, UpdateTransactionState.RolledBack)]
    public void UnsafeTransactionTransitionsAreRejected(UpdateTransactionState current, UpdateTransactionState next) =>
        Assert.Throws<InvalidOperationException>(() => UpdatePolicy.RequireTransition(current, next));

    [Fact]
    public void ReconnectTicketMustBeHighEntropyAndShortLived()
    {
        var now = new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);
        new UpdateReconnectGrant(Convert.ToBase64String(new byte[32]), now.AddMinutes(5)).Validate(now);
        Assert.Throws<ArgumentException>(() => new UpdateReconnectGrant("short", now.AddMinutes(5)).Validate(now));
        Assert.Throws<ArgumentException>(() => new UpdateReconnectGrant(Convert.ToBase64String(new byte[32]), now.AddMinutes(11)).Validate(now));
    }

    [Theory]
    [InlineData(@"C:\Program Files\RemoteDebugger\RemoteDebugger.exe")]
    [InlineData(@"C:\Program Files\RemoteDebugger\Support\RemoteDebugger.Support.exe")]
    public void ProtectedTargetsRemainUnderManagedRoot(string path) =>
        Assert.Equal(path, UpdatePolicy.RequirePathUnderRoot(path, @"C:\Program Files\RemoteDebugger"));

    [Theory]
    [InlineData(@"C:\Program Files\RemoteDebugger-evil\RemoteDebugger.exe")]
    [InlineData(@"C:\Windows\System32\kernel32.dll")]
    [InlineData(@"C:\Program Files\RemoteDebugger\..\Elsewhere\RemoteDebugger.exe")]
    public void ProtectedTargetsCannotEscapeManagedRoot(string path) =>
        Assert.Throws<UnauthorizedAccessException>(() => UpdatePolicy.RequirePathUnderRoot(path, @"C:\Program Files\RemoteDebugger"));
}
