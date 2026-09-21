namespace RemoteDebugger.Core;

public sealed record SupportSessionSnapshot(bool Connected, bool HasPaired, DateTimeOffset? DisconnectDeadlineUtc,
    string State, bool BinaryMatched, DateTimeOffset? StartedUtc);

/// <summary>One logical session across many RPC sockets and rolling screen streams.</summary>
public sealed class SupportSession(TimeProvider? clock = null)
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DisconnectGrace = TimeSpan.FromMinutes(10);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object sync = new();
    private bool paired, matched, ended;
    private long lastSeen;
    private long? disconnected;
    private DateTimeOffset? startedUtc, disconnectUtc;
    private DateTimeOffset? restartDeadline;
    public void Pair(bool binaryMatched)
    {
        lock (sync) { paired = true; ended = false; matched = binaryMatched; lastSeen = time.GetTimestamp(); disconnected = null; disconnectUtc = null; restartDeadline = null; startedUtc = time.GetUtcNow(); }
    }
    public void Observe()
    {
        lock (sync) { if (!paired || ended || IsExpired()) return; lastSeen = time.GetTimestamp(); disconnected = null; disconnectUtc = null; }
    }
    public void SetBinaryMatched(bool value) { lock (sync) matched = value; }
    public void AwaitRestart(DateTimeOffset deadline)
    {
        lock (sync)
        {
            if (!paired || ended || deadline <= time.GetUtcNow() || deadline > time.GetUtcNow().AddHours(1))
                throw new InvalidOperationException("Restart recovery requires a live session and a deadline within one hour.");
            restartDeadline = deadline;
        }
    }
    public void CompleteRestart() { lock (sync) { restartDeadline = null; lastSeen = time.GetTimestamp(); disconnected = null; disconnectUtc = null; } }
    public void Disconnect()
    {
        lock (sync) { if (paired && !ended && disconnected == null) { disconnected = time.GetTimestamp(); disconnectUtc = time.GetUtcNow(); } }
    }
    public void End() { lock (sync) { ended = true; paired = false; matched = false; } }
    public void ResetForPairing()
    {
        lock (sync) { paired = false; matched = false; ended = false; disconnected = null; disconnectUtc = null; startedUtc = null; restartDeadline = null; }
    }
    private void DetectDisconnect()
    {
        if (paired && !ended && disconnected == null && time.GetElapsedTime(lastSeen) >= HeartbeatTimeout)
        {
            // Base the deadline on the first missed heartbeat, even if the UI thread
            // or scheduler did not run for a while.
            disconnected = lastSeen + (long)(HeartbeatTimeout.TotalSeconds * time.TimestampFrequency);
            disconnectUtc = time.GetUtcNow() - time.GetElapsedTime(disconnected.Value);
        }
    }
    private bool IsExpired() { DetectDisconnect(); return restartDeadline is { } deadline ? time.GetUtcNow() >= deadline : disconnected != null && time.GetElapsedTime(disconnected.Value) >= DisconnectGrace; }
    public bool ShouldExit { get { lock (sync) return ended || IsExpired(); } }
    public SupportSessionSnapshot Snapshot
    {
        get
        {
            lock (sync)
            {
                DetectDisconnect(); bool expired = IsExpired(); bool connected = paired && !ended && disconnected == null;
                string state = ended || expired ? "ended" : !paired ? "pairing" : !connected ? "reconnecting" : matched ? "connected" : "synchronizing";
                return new(connected, paired, restartDeadline ?? disconnectUtc + DisconnectGrace, state, matched, startedUtc);
            }
        }
    }
}
