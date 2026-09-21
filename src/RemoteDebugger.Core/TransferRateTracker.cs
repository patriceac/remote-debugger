namespace RemoteDebugger.Core;

/// <summary>A recent byte-rate window. Resumed bytes and stalled time cannot masquerade as throughput.</summary>
public sealed class TransferRateTracker(TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Queue<(long Timestamp, long Bytes)> samples = new();
    private long transferred, total, lastChange;

    public void Report(long bytes, long totalBytes)
    {
        long now = time.GetTimestamp();
        if (samples.Count == 0 || bytes < transferred || totalBytes != total)
        {
            samples.Clear();
            samples.Enqueue((now, bytes));
            lastChange = now;
        }
        else if (bytes != transferred)
        {
            samples.Enqueue((now, bytes));
            lastChange = now;
        }
        transferred = bytes;
        total = totalBytes;
        // Retain one sample before the window to avoid a zero-width estimate.
        while (samples.Count > 2 && time.GetElapsedTime(samples.ElementAt(1).Timestamp, now).TotalSeconds > 8)
            samples.Dequeue();
    }

    public FileTransferMetrics Snapshot()
    {
        if (samples.Count == 0 || total <= 0) return new(0, 0, null);
        var first = samples.Peek();
        var elapsed = time.GetElapsedTime(first.Timestamp);
        var metrics = FileTransferMetrics.Calculate(transferred, total, Math.Max(0, transferred - first.Bytes), elapsed);
        return transferred < total && time.GetElapsedTime(lastChange).TotalSeconds >= 3
            ? metrics with { BytesPerSecond = 0, Remaining = null } : metrics;
    }
}
