using System.Windows.Automation;
using RemoteDebugger;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
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
