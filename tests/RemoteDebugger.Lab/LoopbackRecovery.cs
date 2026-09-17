using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using RemoteDebugger;
using RemoteDebugger.Core;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private async Task ProbeLatestFramesAsync()
    {
        Click("pauseViewing");
        try
        {
            var report = await CliAsync(["stream", "--seconds", "6", "--fps", "5", "--present-delay-ms", "1200",
                "--report", Path.Combine(output, "slow-presentation-stream.json")]);
            long[] sequences = report.GetProperty("presentedSequences").EnumerateArray().Select(value => value.GetInt64()).ToArray();
            bool skipped = report.Int("frames") >= 3 && report.Long("framesSkipped") >= 2 &&
                sequences.Zip(sequences.Skip(1), (previous, current) => current - previous).Any(gap => gap > 1);
            if (skipped) Pass("loopback.latest_frame", "A slow presenter skips stale frames while the Release receiver keeps receiving", report);
            else Fail("loopback.latest_frame", "A slow presenter skips stale frames while the Release receiver keeps receiving", report);
        }
        finally
        {
            Click("pauseViewing");
            await WaitForLiveEvidenceAsync(30);
        }
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostWindowMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    private async Task ProbeInputPreferenceAsync()
    {
        var checkbox = FindVisibleId("mouseKeyboard") ?? throw new InvalidOperationException("Input preference missing.");
        var toggle = (TogglePattern)checkbox.GetCurrentPattern(TogglePattern.Pattern);
        Click("pauseViewing");
        await Task.Delay(400, stop.Token);
        bool pausePreserved = toggle.Current.ToggleState == ToggleState.On;
        Click("navFiles"); Click("navScreen");
        await WaitForLiveEvidenceAsync(30);
        bool tabPreserved = toggle.Current.ToggleState == ToggleState.On;
        toggle.Toggle();
        Click("pauseViewing"); Click("pauseViewing");
        await WaitForLiveEvidenceAsync(30);
        bool offPreserved = toggle.Current.ToggleState == ToggleState.Off;
        toggle.Toggle();
        if (pausePreserved && tabPreserved && offPreserved) Pass("loopback.input_preference", "Pausing and changing pages preserve both enabled and disabled input preferences", new { pausePreserved, tabPreserved, offPreserved });
        else Fail("loopback.input_preference", "Pausing and changing pages preserve both enabled and disabled input preferences", new { pausePreserved, tabPreserved, offPreserved });
    }

    private async Task ProbeAgentTrayAsync()
    {
        product = loopbackAgent ?? throw new InvalidOperationException("Agent missing.");
        try
        {
            product.Refresh();
            IntPtr handle = product.MainWindowHandle;
            PostWindowMessage(handle, 0x0112, new IntPtr(0xF060), IntPtr.Zero);
            await Task.Delay(600, stop.Token);
            // A repeated close while hidden must not become an implicit Quit.
            PostWindowMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero);
            await Task.Delay(21000, stop.Token);
            bool hiddenAlive = !product.HasExited && !IsControllerWindowVisible();
            var tray = await OpenTrayContextAsync();
            if (tray.OpenItem == null) throw new InvalidOperationException("Agent tray Open missing.");
            InvokeElement(tray.OpenItem);
            await WaitForUiAsync(IsControllerWindowVisible, 15);
            bool connected = IsConnected(TryValue("connectionStatus"));
            if (hiddenAlive && connected) Pass("loopback.agent_tray", "Closing the agent twice preserves the session and its tray can restore it", new { hiddenAlive, connected, pid = product.Id });
            else Fail("loopback.agent_tray", "Closing the agent twice preserves the session and its tray can restore it", new { hiddenAlive, connected });
            CaptureDesktop("agent-restored.png");
        }
        finally { product = loopbackController; if (product != null) Native.FocusWindow(product.Id); }
    }

    private async Task ProbeSecondSessionAsync()
    {
        if (loopbackAgent == null || loopbackController == null) throw new InvalidOperationException("Processes missing.");
        product = loopbackAgent;
        Native.FocusWindow(product.Id);
        CaptureDesktop("agent-session-ended.png");
        string newCode = await WaitPairingCodeAsync();
        if (IsWorkspaceAudit)
        {
            bool automatic = SixDigits.IsMatch(newCode) && !TryValue("agentHeading").Equals("Assistance terminée", StringComparison.Ordinal);
            if (automatic) Pass("ui.support_auto_restart", "Support automatically returns to a fresh pairing state after the previous session ends", new { restartedCode = CodeEvidence(newCode) });
            else Fail("ui.support_auto_restart", "Support automatically returns to a fresh pairing state after the previous session ends", new { restartedCode = CodeEvidence(newCode), heading = TryValue("agentHeading") });
        }
        product = loopbackController;
        Native.FocusWindow(product.Id);
        Set("host", "127.0.0.1"); Set("pairCode", newCode); FocusAndEnter("pairCode");
        bool connected = await WaitForTextAsync("connectionStatus", IsConnected, 40);
        var live = await WaitForLiveEvidenceAsync(30);
        if (connected && live.BadgeVisible) Pass("loopback.second_session", "The same agent and controller processes can establish a new live session after termination", new { connected, live.BadgeVisible, agentPid = loopbackAgent.Id, controllerPid = loopbackController.Id });
        else Fail("loopback.second_session", "The same agent and controller processes can establish a new live session after termination", new { connected, live.BadgeVisible });
        product = loopbackAgent;
        Click("terminateSession");
        string restartedAfterAgentEnd = await WaitPairingCodeAsync();
        product = loopbackController;
        bool controllerReset = await WaitForTextAsync("connectionStatus", text => !IsConnected(text), 35);
        bool alive = !loopbackAgent.HasExited && !loopbackController.HasExited;
        bool restarted = SixDigits.IsMatch(restartedAfterAgentEnd);
        if (restarted && controllerReset && alive) Pass("loopback.agent_ends_session", "Ending support on the agent keeps both processes open and automatically returns it to a fresh pairing state", new { restarted, restartedCode = CodeEvidence(restartedAfterAgentEnd), controllerReset, alive });
        else Fail("loopback.agent_ends_session", "Ending support on the agent keeps both processes open and automatically returns it to a fresh pairing state", new { restarted, restartedCode = CodeEvidence(restartedAfterAgentEnd), controllerReset, alive });
    }
}
