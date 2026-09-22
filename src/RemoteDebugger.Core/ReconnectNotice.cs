namespace RemoteDebugger.Core;

public sealed class ReconnectNotice
{
    private long? interruptedAt;
    public void Interrupt(long now) => interruptedAt ??= now;
    public void Recover() => interruptedAt = null;
    public bool IsVisible(long now) => interruptedAt is { } start && now - start >= 2000;
}
