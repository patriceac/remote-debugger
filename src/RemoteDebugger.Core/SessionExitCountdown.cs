namespace RemoteDebugger.Core;

/// <summary>Application lifetime after access has already been revoked.</summary>
public sealed class SessionExitCountdown(TimeProvider? clock = null)
{
    public static readonly TimeSpan Delay = TimeSpan.FromMinutes(10);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private long? started;
    public bool Active => started.HasValue;
    public TimeSpan Remaining => started is { } timestamp ? TimeSpan.FromTicks(Math.Max(0, (Delay - time.GetElapsedTime(timestamp)).Ticks)) : Delay;
    public bool Expired => Active && Remaining == TimeSpan.Zero;
    public void Start() => started ??= time.GetTimestamp();
    public void Cancel() => started = null;
}
