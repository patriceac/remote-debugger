using System.Windows.Automation;
using RemoteDebugger;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private async Task ProbeAgentTypographyAsync(bool paired)
    {
        foreach (var size in new[] { new System.Drawing.Size(1060, 720), new System.Drawing.Size(1280, 860) })
        {
            ResizeProductWindow(size.Width, size.Height);
            await Task.Delay(600, stop.Token);
            var labels = new[]
            {
                (Id: "headerTitle", Points: 22f, Bold: true),
                (Id: "headerSubtitle", Points: 10.5f, Bold: false),
                (Id: "agentEyebrow", Points: 9.5f, Bold: true),
                (Id: "agentHeading", Points: 32f, Bold: true),
                (Id: "agentSubtitle", Points: 13f, Bold: false),
                (Id: "agentPairCode", Points: paired ? 24f : 42f, Bold: true)
            }.Select(spec =>
            {
                var label = FindVisibleId(spec.Id) ?? throw new InvalidOperationException("Missing typography label " + spec.Id);
                var bounds = label.Current.BoundingRectangle;
                using var font = new System.Drawing.Font(spec.Id == "agentPairCode" && !paired ? "Consolas" : "Segoe UI", spec.Points,
                    spec.Bold ? System.Drawing.FontStyle.Bold : System.Drawing.FontStyle.Regular);
                var required = System.Windows.Forms.TextRenderer.MeasureText(Value(label), font,
                    new System.Drawing.Size((int)Math.Floor(bounds.Width), int.MaxValue),
                    System.Windows.Forms.TextFormatFlags.NoPadding | System.Windows.Forms.TextFormatFlags.NoPrefix | System.Windows.Forms.TextFormatFlags.WordBreak);
                return new { spec.Id, bounds.Left, bounds.Top, bounds.Width, bounds.Height, requiredHeight = required.Height, textFits = bounds.Height >= required.Height };
            }).ToArray();
            bool aligned = labels.All(label => Math.Abs(label.Left - labels[0].Left) <= 1);
            bool fits = labels.All(label => label.textFits);
            string state = paired ? "connected" : "pairing";
            string id = $"loopback.typography_{state}_{size.Width}";
            if (aligned && fits) Pass(id, "Agent and header text share a left edge and have room for every rendered line", new { size.Width, size.Height, aligned, fits, labels });
            else Fail(id, "Agent and header text share a left edge and have room for every rendered line", new { size.Width, size.Height, aligned, fits, labels });
            CaptureDesktop($"agent-{state}-typography-{size.Width}.png");
        }
    }

    private async Task VerifyContinuousViewingAsync()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var initial = await WaitForLiveEvidenceAsync(30);
        if (!initial.BadgeVisible || !initial.TelemetryVisible) throw new InvalidOperationException("Live viewing was not ready before the continuity check.");
        Pass("loopback.continuous_view_started", "Live viewing is present before waiting across the five-minute transport boundary", new { initial.TelemetryText }, required: false);
        while (started.Elapsed < TimeSpan.FromSeconds(310)) await Task.Delay(1000, stop.Token);
        var renewed = await WaitForLiveEvidenceAsync(30);
        bool live = renewed.BadgeVisible && renewed.TelemetryVisible;
        var evidence = new { elapsedSeconds = started.Elapsed.TotalSeconds, renewed.BadgeVisible, renewed.BadgeText, renewed.TelemetryText, freshnessRequirementSeconds = 3 };
        if (live) Pass("loopback.continuous_view_renewal", "The visible viewer receives fresh frames beyond its five-minute transport boundary without manual resume", evidence);
        else Fail("loopback.continuous_view_renewal", "The visible viewer receives fresh frames beyond its five-minute transport boundary without manual resume", evidence);
        CaptureDesktop("controller-live-after-five-minutes.png");
    }

    private async Task CaptureLoopbackMinimumSizeAsync()
    {
        if (loopbackController == null || loopbackAgent == null) throw new InvalidOperationException("The visual review needs both Release processes.");
        try
        {
            product = loopbackController;
            ResizeProductWindow(1060, 720);
            await Task.Delay(500, stop.Token);
            CaptureDesktop("controller-files-minimum.png");
            Click("navProcesses");
            await Task.Delay(500, stop.Token);
            CaptureDesktop("controller-processes-minimum.png");
            Click("navScreen");
            var resumed = await WaitForLiveEvidenceAsync(30);
            if (!resumed.BadgeVisible || !resumed.TelemetryVisible) throw new InvalidOperationException("Viewing did not resume on returning to the screen page.");
            CaptureDesktop("controller-live-minimum.png");
            ResizeProductWindow(1280, 860);

            product = loopbackAgent;
            ResizeProductWindow(1060, 720);
            await Task.Delay(500, stop.Token);
            CaptureDesktop("agent-connected-minimum.png");
            ResizeProductWindow(1280, 860);
            Pass("loopback.minimum_size_navigation", "The Release windows resize and page navigation resumes live viewing at minimum size", new { width = 1060, height = 720, visualApproval = "requires separate screenshot inspection" });
        }
        finally
        {
            product = loopbackController;
            Native.FocusWindow(loopbackController.Id);
        }
    }

    private void ResizeProductWindow(double width, double height)
    {
        var window = Root();
        if (!window.TryGetCurrentPattern(TransformPattern.Pattern, out var pattern) || pattern is not TransformPattern transform || !transform.Current.CanResize)
            throw new InvalidOperationException("The Release window did not expose native resize support.");
        transform.Resize(width, height);
    }
}
