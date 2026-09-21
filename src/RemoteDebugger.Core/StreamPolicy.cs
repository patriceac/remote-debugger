namespace RemoteDebugger.Core;

public static class StreamPolicy
{
    public const int MaximumFps = 30;
    public const int H264SlowFrameCount = 3;
    public const double H264EncodeBudgetRatio = 0.75;
    public const double H264MinimumEncodeBudgetMs = 50;
    public static int ClampFps(int requested) => Math.Clamp(requested, 1, MaximumFps);
    public static (int Fps, int MaxWidth, int Quality) ViewingSettings(bool relayEconomy) =>
        relayEconomy ? (3, 1280, 55) : (MaximumFps, 1920, 65);

    public static double H264EncodeBudgetMs(int fps) =>
        Math.Max(H264MinimumEncodeBudgetMs, 1000d / ClampFps(fps) * H264EncodeBudgetRatio);

    public static bool IsH264EncodeSlow(double encodeMs, int fps) => encodeMs > H264EncodeBudgetMs(fps);

    public static bool ShouldFallbackToJpeg(int consecutiveSlowFrames) => consecutiveSlowFrames >= H264SlowFrameCount;
}

/// <summary>Leave CPU/network headroom for input; recover gradually after pressure subsides.</summary>
public sealed class AdaptiveFrameRate(int maximumFps)
{
    private readonly int maximum = StreamPolicy.ClampFps(maximumFps);
    private double averageWorkMs;
    private int recoveryFrames;
    public int FramesPerSecond { get; private set; } = StreamPolicy.ClampFps(maximumFps);

    public void Observe(TimeSpan captureEncodeAndSend)
    {
        double ms = captureEncodeAndSend.TotalMilliseconds;
        if (!double.IsFinite(ms) || ms <= 0) return;
        averageWorkMs = averageWorkMs == 0 ? ms : averageWorkMs * .8 + ms * .2;
        int sustainable = (int)Math.Clamp(Math.Floor(750 / averageWorkMs), 1, maximum);
        if (sustainable < FramesPerSecond)
        {
            FramesPerSecond = sustainable;
            recoveryFrames = 0;
        }
        else if (sustainable > FramesPerSecond && ++recoveryFrames >= Math.Max(3, FramesPerSecond))
        {
            FramesPerSecond++;
            recoveryFrames = 0;
        }
        else if (sustainable == FramesPerSecond) recoveryFrames = 0;
    }

    public TimeSpan DelayAfter(TimeSpan work) => TimeSpan.FromMilliseconds(Math.Max(0, 1000d / FramesPerSecond - work.TotalMilliseconds));
}
