using RemoteDebugger.Core;
using Xunit;

public sealed class SessionClipboardTests
{
    [Fact]
    public void PriorContentsDisconnectedChangesAndRebootBaselinesAreNeverCaptured()
    {
        var state = new SessionClipboard();
        Assert.Null(state.Capture(10, "before support"));
        state.Begin(10);
        Assert.Null(state.Capture(10, "old clipboard"));
        Assert.Equal("in session", state.Capture(11, "in session")!.Text);
        state.Pause();
        Assert.Null(state.Latest);
        Assert.Null(state.Capture(12, "while offline"));
        state.Begin(12);
        Assert.Null(state.Capture(12, "while offline"));
        state.Pause(); state.Begin(1); // Windows sequence restarts on a different boot.
        Assert.Null(state.Capture(1, "restored by Windows"));
        Assert.Equal(1, state.Capture(2, "new copy")!.Version);
    }

    [Fact]
    public void AppliedRemoteTextDoesNotEchoAndOversizedTextIsNotRetained()
    {
        var state = new SessionClipboard(); state.Begin(100);
        state.Suppress(101);
        Assert.Null(state.Capture(101, "received from peer"));
        Assert.Null(state.Capture(102, new string('x', SessionClipboard.MaximumTextLength + 1)));
        Assert.Null(state.Latest);
        Assert.Equal("local copy", state.Capture(103, "local copy")!.Text);
    }
}
