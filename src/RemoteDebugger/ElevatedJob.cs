namespace RemoteDebugger;

/// <summary>
/// Compatibility facade for callers that previously launched a UAC helper for
/// every command. Provisioned builds route the command through the authenticated
/// local broker and never claim success when that broker is unavailable.
/// </summary>
public static class ElevatedJob
{
    public static async Task<object> RunAsync(string file, string[] args, string root, CancellationToken ct)
    {
        using var session = new MaintenanceSession(root);
        await session.StartAsync(ct);
        return await session.RunAsync(file, args, ct);
    }

    public static Task<int> ExecuteAsync(string path)
    {
        _ = path;
        // Old per-operation elevation jobs are intentionally unsupported. The
        // only UAC entry point is the explicit one-time service provisioner.
        return Task.FromResult(3);
    }
}
