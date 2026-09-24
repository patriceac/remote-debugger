namespace RemoteDebugger.Core;

/// <summary>
/// Identifies one interactive, registered desktop process for the lifetime of its
/// connection to the privileged local broker. There is deliberately no wall
/// clock expiry: closing the agent, disabling maintenance, disconnecting the
/// pipe, or losing the process ends the lease and cancels its work. Ending a
/// support session cancels its active work while an idle connection stays ready.
/// </summary>
public sealed record MaintenanceLease(
    int ProcessId,
    long ProcessStartTicks,
    int SessionId,
    string UserSid,
    string ExecutablePath,
    string LeaseId)
{
    public void Validate()
    {
        if (ProcessId <= 0 || ProcessStartTicks <= 0 || SessionId <= 0)
            throw new ArgumentException("Invalid interactive process identity.");
        if (!UserSid.StartsWith("S-1-", StringComparison.Ordinal) || UserSid.Length > 184)
            throw new ArgumentException("Invalid Windows user SID.");
        if (string.IsNullOrWhiteSpace(ExecutablePath) || !Path.IsPathFullyQualified(ExecutablePath))
            throw new ArgumentException("A fully qualified executable path is required.");
        if (!Guid.TryParseExact(LeaseId, "N", out _))
            throw new ArgumentException("Invalid maintenance lease id.");
    }

    public static void ValidateCommand(Request request)
    {
        string file = request.Args.Str("file");
        string[] arguments = request.Args.Strings("arguments");
        if (request.Operation != "maintenance.command" ||
            !Guid.TryParse(request.Id, out _) ||
            request.TimeoutSeconds is < 1 or > 300 ||
            string.IsNullOrWhiteSpace(file) ||
            file.Length > 32767 ||
            arguments.Length > 128 ||
            arguments.Any(argument => argument.Length > 32767) ||
            arguments.Sum(argument => (long)argument.Length) > 131072)
            throw new ArgumentException("Only bounded explicit maintenance commands are accepted.");
    }
}
