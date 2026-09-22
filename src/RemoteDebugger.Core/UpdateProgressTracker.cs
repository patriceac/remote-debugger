namespace RemoteDebugger.Core;

public sealed record UpdateStepProgress(int? Percent, TimeSpan? Remaining, bool Estimated, TimeSpan Elapsed = default);

/// <summary>Byte-based transfer progress; explicitly estimated timing for opaque remote steps.</summary>
public sealed class UpdateProgressTracker(IDictionary<string, double> timings, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private long started;
    private double expectedSeconds;
    private TransferRateTracker transferRate = new(clock);
    private readonly List<string> completed = [];
    public IReadOnlyList<string> CompletedStages => completed;
    public string Stage { get; private set; } = "";
    public TimeSpan Elapsed => time.GetElapsedTime(started);

    public static bool IsEstimatedStage(string stage) => stage is
        "hashing" or "checking" or "preparing" or "verifying" or "restarting" or "finalizing";
    public static bool IsActiveStage(string stage) => stage == "transferring" || IsEstimatedStage(stage);

    public void Report(string stage, long transferredBytes = 0, long totalBytes = 0)
    {
        if (stage != Stage)
        {
            double elapsed = Elapsed.TotalSeconds;
            if (CompletedStep(Stage, stage) && !completed.Contains(Stage)) completed.Add(Stage);
            if (IsEstimatedStage(Stage) && CompletedStep(Stage, stage) && elapsed >= .25 && elapsed < 3600)
                timings[Stage] = timings.TryGetValue(Stage, out double previous) && Valid(previous)
                    ? (previous + elapsed) / 2 : elapsed;
            Stage = stage;
            started = time.GetTimestamp();
            transferRate = new(time);
            expectedSeconds = timings.TryGetValue(stage, out double learned) && Valid(learned)
                ? learned : 0;
        }
        if (stage == "transferring") transferRate.Report(transferredBytes, totalBytes);
    }

    public UpdateStepProgress Snapshot()
    {
        if (Stage is "complete" or "current") return new(100, TimeSpan.Zero, false);
        if (Stage == "transferring")
        {
            var metrics = transferRate.Snapshot();
            return new(metrics.Percent, metrics.Remaining, false);
        }
        if (!IsEstimatedStage(Stage)) return new(0, null, false);
        double elapsed = Math.Max(0, Elapsed.TotalSeconds);
        // Time is not work completed. A past duration can suggest an ETA, but
        // once exceeded it provides no evidence for a remaining-time countdown.
        TimeSpan? remaining = expectedSeconds > elapsed ? TimeSpan.FromSeconds(expectedSeconds - elapsed) : null;
        return new(null, remaining, true, Elapsed);
    }

    private static bool Valid(double seconds) => double.IsFinite(seconds) && seconds >= .25 && seconds < 3600;
    private static bool CompletedStep(string previous, string next) => previous switch
    {
        "hashing" => next is "queued" or "preparing",
        "checking" => next is "available" or "legacy" or "busy" or "current" or "newer" or "conflict",
        "preparing" => next is "transferring" or "verifying" or "finalizing" or "complete" or "current",
        "transferring" => next is "verifying" or "restarting" or "finalizing" or "complete",
        "verifying" => next is "restarting" or "finalizing" or "complete",
        "restarting" => next is "finalizing" or "complete",
        "finalizing" => next is "complete" or "current",
        _ => false
    };
}
