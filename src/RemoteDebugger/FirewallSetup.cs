namespace RemoteDebugger;

public static class FirewallSetup
{
    public static async Task<object> ConfigurePrivateAsync(string root, CancellationToken ct)
    {
        _ = root; // Kept for source compatibility; the broker owns protected state.
        var status = await EnsurePrivateAsync(ct);
        if (!status.Available || !status.FirewallReady) throw new InvalidOperationException(status.Message);
        return new { exitCode = 0, changed = true, status.Message };
    }

    public static async Task<SupportPlatformStatus> EnsurePrivateAsync(CancellationToken ct = default) =>
        await SupportPlatform.PrepareAsync(requireFirewall: true, ct);
}
