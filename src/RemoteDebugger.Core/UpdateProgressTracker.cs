namespace RemoteDebugger.Core;

public sealed record UpdateStepProgress(int Percent, TimeSpan? Remaining, bool Estimated, bool Overdue = false);

/// <summary>Byte-based transfer progress; explicitly estimated timing for opaque remote steps.</summary>
public sealed class UpdateProgressTracker(IDictionary<string, double> timings, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private long started, baseline, transferred, total;
    private double expectedSeconds;
    public string Stage { get; private set; } = "";
    public TimeSpan Elapsed => time.GetElapsedTime(started);

    // First-run heuristics, not deadlines or claims of measured work. Successful runs replace them.
    private static double InitialSeconds(string stage) => stage switch
    {
        "hashing" => 10, "checking" => 10, "preparing" => 20,
        "verifying" => 60, "restarting" => 45, "finalizing" => 30, _ => 0
    };

    public static bool IsEstimatedStage(string stage) => InitialSeconds(stage) > 0;
    public static bool IsActiveStage(string stage) => stage == "transferring" || IsEstimatedStage(stage);

    public void Report(string stage, long transferredBytes = 0, long totalBytes = 0)
    {
        if (stage != Stage)
        {
            double elapsed = Elapsed.TotalSeconds;
            if (IsEstimatedStage(Stage) && CompletedStep(Stage, stage) && elapsed >= .25 && elapsed < 3600)
                timings[Stage] = timings.TryGetValue(Stage, out double previous) && Valid(previous)
                    ? (previous + elapsed) / 2 : elapsed;
            Stage = stage;
            started = time.GetTimestamp();
            baseline = transferredBytes;
            expectedSeconds = timings.TryGetValue(stage, out double learned) && Valid(learned)
                ? learned : InitialSeconds(stage);
        }
        transferred = transferredBytes;
        total = totalBytes;
    }

    public UpdateStepProgress Snapshot()
    {
        if (Stage is "complete" or "current") return new(100, TimeSpan.Zero, false);
        if (Stage == "transferring")
        {
            if (total <= 0) return new(0, null, false);
            var metrics = FileTransferMetrics.Calculate(transferred, total, Math.Max(0, transferred - baseline), Elapsed);
            return new(metrics.Percent, metrics.Remaining, false);
        }
        if (!IsEstimatedStage(Stage)) return new(0, null, false);
        double elapsed = Math.Max(0, Elapsed.TotalSeconds);
        return new((int)Math.Min(95, elapsed / expectedSeconds * 100),
            elapsed < expectedSeconds ? TimeSpan.FromSeconds(Math.Ceiling(expectedSeconds - elapsed)) : null,
            true, elapsed >= expectedSeconds);
    }

    private static bool Valid(double seconds) => double.IsFinite(seconds) && seconds >= .25 && seconds < 3600;
    private static bool CompletedStep(string previous, string next) => previous switch
    {
        "hashing" => next is "queued" or "preparing",
        "checking" => next is "available" or "legacy" or "busy" or "current" or "newer" or "conflict",
        "preparing" => next is "transferring" or "verifying" or "finalizing" or "complete" or "current",
        "verifying" => next is "restarting" or "finalizing" or "complete",
        "restarting" => next is "finalizing" or "complete",
        "finalizing" => next is "complete" or "current",
        _ => false
    };
}
