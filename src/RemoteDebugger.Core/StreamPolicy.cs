namespace RemoteDebugger.Core;

public static class StreamPolicy
{
    public const int MaximumFps = 5;
    public static int ClampFps(int requested) => Math.Clamp(requested, 1, MaximumFps);
}
