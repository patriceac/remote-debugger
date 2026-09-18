namespace RemoteDebugger.Core;

public static class StreamPolicy
{
    public const int MaximumFps = 5;
    public const int H264SlowFrameCount = 3;
    public const double H264EncodeBudgetRatio = 0.75;
    public const double H264MinimumEncodeBudgetMs = 50;
    public static int ClampFps(int requested) => Math.Clamp(requested, 1, MaximumFps);

    public static double H264EncodeBudgetMs(int fps) =>
        Math.Max(H264MinimumEncodeBudgetMs, 1000d / ClampFps(fps) * H264EncodeBudgetRatio);

    public static bool IsH264EncodeSlow(double encodeMs, int fps) => encodeMs > H264EncodeBudgetMs(fps);

    public static bool ShouldFallbackToJpeg(int consecutiveSlowFrames) => consecutiveSlowFrames >= H264SlowFrameCount;
}
