namespace RemoteDebugger.Core;

public sealed record FileTransferMetrics(int Percent, double BytesPerSecond, TimeSpan? Remaining)
{
    public static FileTransferMetrics Calculate(long transferred, long total, long bytesThisAttempt, TimeSpan elapsed)
    {
        int percent = total == 0 ? 100 : (int)Math.Clamp(100d * transferred / total, 0, 100);
        double rate = elapsed.TotalSeconds >= .25 ? bytesThisAttempt / elapsed.TotalSeconds : 0;
        TimeSpan? remaining = transferred >= total ? TimeSpan.Zero : rate > 0
            ? TimeSpan.FromSeconds(Math.Min(TimeSpan.MaxValue.TotalSeconds - 1, Math.Ceiling((total - transferred) / rate))) : null;
        return new(percent, rate, remaining);
    }
}
