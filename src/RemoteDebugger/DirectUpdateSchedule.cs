namespace RemoteDebugger;

internal sealed class DirectUpdateSchedule
{
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);
    private HashSet<string> pending = new(StringComparer.OrdinalIgnoreCase);
    internal DateTimeOffset? DueUtc { get; private set; }

    internal void Refresh(IEnumerable<string> keys, DateTimeOffset now)
    {
        var next = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (next.Count == 0) DueUtc = null;
        else if (DueUtc == null || next.Except(pending, StringComparer.OrdinalIgnoreCase).Any()) DueUtc = now;
        pending = next;
    }

    internal void Defer(DateTimeOffset now) => DueUtc = pending.Count == 0 ? null : now + RetryInterval;
}
