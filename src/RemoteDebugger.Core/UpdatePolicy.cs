namespace RemoteDebugger.Core;

public sealed record ExecutableSnapshot(
    string Path,
    long Size,
    string Sha256,
    string? FileVersion,
    string SignerThumbprint)
{
    public const long MaximumSize = 1024L * 1024 * 1024;

    public void Validate(bool requirePath = true)
    {
        if (requirePath && (string.IsNullOrWhiteSpace(Path) || !System.IO.Path.IsPathFullyQualified(Path)))
            throw new ArgumentException("A fully qualified executable path is required.");
        if (Size is < 1 or > MaximumSize)
            throw new ArgumentException("Executable size is outside the supported update bounds.");
        UpdatePolicy.ValidateSha256(Sha256, "executable SHA-256");
        UpdatePolicy.ValidateSha256(SignerThumbprint, "publisher certificate SHA-256");
    }
}

public sealed record UpdateReconnectGrant(string Ticket, DateTimeOffset ExpiresUtc)
{
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(10);

    public void Validate(DateTimeOffset now)
    {
        if (Ticket.Length is < 43 or > 512)
            throw new ArgumentException("Reconnect ticket is not a bounded high-entropy token.");
        if (ExpiresUtc <= now || ExpiresUtc - now > MaximumLifetime)
            throw new ArgumentException("Reconnect ticket expiry is invalid.");
    }
}

public sealed record UpdateCommitContext(
    string TransactionId,
    ExecutableSnapshot Controller,
    ExecutableSnapshot Agent,
    DateTimeOffset PlannedDisconnectDeadlineUtc);

public enum UpdateTransactionState
{
    Receiving,
    Staged,
    Armed,
    Replacing,
    AwaitingStartupHealth,
    RunningPendingRemoteHealth,
    Completed,
    RolledBack,
    Cancelled,
    Failed
}

public static class UpdatePolicy
{
    private static readonly IReadOnlyDictionary<UpdateTransactionState, UpdateTransactionState[]> Allowed =
        new Dictionary<UpdateTransactionState, UpdateTransactionState[]>
        {
            [UpdateTransactionState.Receiving] = [UpdateTransactionState.Staged, UpdateTransactionState.Cancelled, UpdateTransactionState.Failed],
            [UpdateTransactionState.Staged] = [UpdateTransactionState.Armed, UpdateTransactionState.Cancelled, UpdateTransactionState.Failed],
            [UpdateTransactionState.Armed] = [UpdateTransactionState.Replacing, UpdateTransactionState.Cancelled, UpdateTransactionState.Failed],
            [UpdateTransactionState.Replacing] = [UpdateTransactionState.AwaitingStartupHealth, UpdateTransactionState.RolledBack, UpdateTransactionState.Failed],
            [UpdateTransactionState.AwaitingStartupHealth] = [UpdateTransactionState.RunningPendingRemoteHealth, UpdateTransactionState.RolledBack, UpdateTransactionState.Failed],
            [UpdateTransactionState.RunningPendingRemoteHealth] = [UpdateTransactionState.Completed, UpdateTransactionState.RolledBack, UpdateTransactionState.Failed],
            [UpdateTransactionState.Completed] = [],
            [UpdateTransactionState.RolledBack] = [],
            [UpdateTransactionState.Cancelled] = [],
            [UpdateTransactionState.Failed] = []
        };

    public static void RequireTransition(UpdateTransactionState current, UpdateTransactionState next)
    {
        if (!Allowed[current].Contains(next))
            throw new InvalidOperationException($"Update transaction cannot transition from {current} to {next}.");
    }

    public static void RequireExactControllerBinary(ExecutableSnapshot controller, ExecutableSnapshot agent)
    {
        controller.Validate();
        agent.Validate();
        if (!FixedHexEquals(controller.Sha256, agent.Sha256) || controller.Size != agent.Size)
            throw new InvalidOperationException("The agent does not run the controller's exact executable bytes.");
    }

    public static void RequirePinnedPublisher(ExecutableSnapshot candidate, string pinnedPublisherThumbprint)
    {
        candidate.Validate();
        ValidateSha256(pinnedPublisherThumbprint, "pinned publisher certificate SHA-256");
        if (!FixedHexEquals(candidate.SignerThumbprint, pinnedPublisherThumbprint))
            throw new InvalidOperationException("The update is signed by a different publisher certificate.");
    }

    public static Version ReleaseVersion(string? value)
    {
        if (!Version.TryParse(value, out var version) || version.Major < 0 || version.Minor < 0 || version.Build < 0)
            throw new InvalidOperationException("The software version could not be verified. Install a numbered release before updating.");
        return new Version(version.Major, version.Minor, version.Build, Math.Max(0, version.Revision));
    }

    public static void RequireNewerRelease(ExecutableSnapshot candidate, ExecutableSnapshot installed)
    {
        candidate.Validate(); installed.Validate();
        if (ReleaseVersion(candidate.FileVersion) <= ReleaseVersion(installed.FileVersion))
            throw new InvalidOperationException("Only a newer software version can be installed. Update the controller first; downgrades and replacement builds with the same version are not allowed.");
    }

    public static void ValidateSha256(string value, string name)
    {
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new ArgumentException($"Invalid {name}.");
    }

    public static bool FixedHexEquals(string left, string right)
    {
        if (left.Length != right.Length) return false;
        try
        {
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(left), Convert.FromHexString(right));
        }
        catch (FormatException) { return false; }
    }

    public static string RequirePathUnderRoot(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Protected path and root are required.");
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullPath = Path.GetFullPath(path);
        string prefix = fullRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Privileged update path escapes its protected root.");
        return fullPath;
    }
}
