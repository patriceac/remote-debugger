namespace RemoteDebugger.Core;

public sealed record MaintenanceLease(int ParentPid, long ParentStartTicks, string PipeName, DateTimeOffset ExpiresUtc)
{
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromHours(1);
    public void Validate(DateTimeOffset now)
    {
        if (ParentPid <= 0 || ParentStartTicks <= 0) throw new ArgumentException("Invalid parent identity.");
        if (!PipeName.StartsWith("RemoteDebugger.Admin.", StringComparison.Ordinal) || !Guid.TryParseExact(PipeName[21..], "N", out _)) throw new ArgumentException("Invalid maintenance pipe.");
        if (ExpiresUtc <= now || ExpiresUtc - now > MaximumDuration) throw new ArgumentException("Maintenance authorization expired or exceeds one hour.");
    }
    public static void ValidateCommand(Request request)
    {
        if (request.Operation != "command" || !Guid.TryParse(request.Id, out _) || request.TimeoutSeconds is < 1 or > 300 || string.IsNullOrWhiteSpace(request.Args.Str("file"))) throw new ArgumentException("Only bounded explicit maintenance commands are accepted.");
    }
}
