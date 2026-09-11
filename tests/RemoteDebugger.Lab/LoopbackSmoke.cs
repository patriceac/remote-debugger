using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using RemoteDebugger;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private Process? loopbackAgent;
    private Process? loopbackController;

    /// <summary>
    /// Runs a single-guest product smoke test. Both product processes are real
    /// Release binaries and bind only to loopback; this deliberately supplies
    /// no LAN-discovery, firewall, service, or UAC acceptance claim.
    /// </summary>
    private async Task LoopbackSmokeAsync()
    {
        try
        {
            await LoopbackSmokeCoreAsync();
        }
        finally
        {
            await CleanupLoopbackProcessesAsync();
        }
    }

    private async Task LoopbackSmokeCoreAsync()
    {
        if (scope != "runtime" || IsUpdateVariant) throw new ArgumentException("Loopback smoke only accepts the Runtime/None configuration.");

        string root = Path.Combine(output, "loopback");
        string agentRoot = Path.Combine(root, "agent-data");
        string controllerRoot = Path.Combine(root, "controller-data");
        Directory.CreateDirectory(agentRoot);
        Directory.CreateDirectory(controllerRoot);

        // A fresh Lab run must not inherit a token from a previous guest run.
        // Removing the encrypted saved connection is test isolation; pairing
        // below still obtains its token through the shipped PAKE flow.
        try { if (File.Exists(RemoteClient.DefaultPath)) File.Delete(RemoteClient.DefaultPath); } catch (IOException) { }

        string releaseHash = await HashFileAsync(application);
        loopbackAgent = LaunchLoopbackProduct(true, agentRoot);
        product = loopbackAgent;
        await WaitUiAsync();
        WindowState = Forms.FormWindowState.Minimized;

        string agentState = TryValue("agentState");
        string agentCode = await WaitPairingCodeAsync();
        string countdown = TryValue("pairingCountdown");
        guestElevated = Native.IsElevated();
        Pass("loopback.agent_elevation_context", "The loopback Lab records the actual Windows elevation context supplied to the guest", new { elevated = guestElevated, user = Environment.UserName }, required: false);
        if (IsAgentReadyState(agentState) && UiTexts().Any(x => x.Contains("Donner le contrôle", StringComparison.OrdinalIgnoreCase) || x.Contains("Donner le controle", StringComparison.OrdinalIgnoreCase)))
            Pass("loopback.agent_default_role", "The default Release launch opens the agent role and starts pairing", new { state = agentState, code = CodeEvidence(agentCode), launchArguments = new[] { "--loopback-only", "--data-root", "<agent-data>" } });
        else
            Fail("loopback.agent_default_role", "The default Release launch opens the agent role and starts pairing", new { state = agentState, code = CodeEvidence(agentCode), visible = UiTexts().Take(35).ToArray() });
        if (SixDigits.IsMatch(agentCode)) Pass("loopback.agent_pairing_code", "The loopback agent exposes six pairing digits", new { code = CodeEvidence(agentCode) });
        else Fail("loopback.agent_pairing_code", "The loopback agent exposes six pairing digits", new { code = CodeEvidence(agentCode) });
        if (TimeText.IsMatch(countdown)) Pass("loopback.agent_pairing_countdown", "The loopback agent exposes its pairing expiry countdown", new { countdown });
        else Fail("loopback.agent_pairing_countdown", "The loopback agent exposes its pairing expiry countdown", new { countdown });
        await ProbeSleepRequestAsync();
        var platformStatus = await TryPlatformStatusAsync();
        if (platformStatus.HasValue) Pass("loopback.platform_status", "The Release CLI exposes the local platform status without changing the loopback setup", platformStatus.Value, required: false);
        else Block("loopback.platform_status", "The Release CLI exposes the local platform status without changing the loopback setup", "The artifact did not return a local platform-status response.", required: false);
        Block("loopback.lan_scope", "LAN discovery, private firewall, installed broker, and UAC remain outside the loopback smoke claim", "Both product processes are intentionally bound to 127.0.0.1; the two-VM Provisioned/Full run owns LAN and broker evidence.", new { network = "loopback-only", discoveryClaimed = false, firewallClaimed = false, serviceClaimed = false }, required: false);
        CaptureDesktop("loopback-agent-pairing.png");

        loopbackController = LaunchLoopbackProduct(false, controllerRoot);
        product = loopbackController;
        await WaitUiAsync();
        WindowState = Forms.FormWindowState.Minimized;

        bool fingerprintGateVisible = FindVisibleId("fingerprintVerified") != null || FindVisibleId("fingerprint") != null;
        if (fingerprintGateVisible)
            Fail("loopback.no_fingerprint_gate", "Loopback pairing has no visible fingerprint checkbox or fingerprint entry step", new { fingerprintGateVisible });
        else
            Pass("loopback.no_fingerprint_gate", "Loopback pairing has no visible fingerprint checkbox or fingerprint entry step", new { fingerprintGateVisible });
        Set("host", "127.0.0.1");
        Set("pairCode", agentCode);
        FocusAndEnter("pairCode");
        string pairStatusBeforeWait = TryValue("connectionStatus");
        bool connected = await WaitForTextAsync("connectionStatus", IsConnected, 60);
        string pairStatus = TryValue("connectionStatus");
        if (connected) sawPairing = true;
        if (connected) Pass("loopback.code_enter_pairing", "The controller pairs to the loopback agent with the six-digit code and Enter", new { host = "127.0.0.1", pairStatusBeforeWait, pairStatus, codeLength = agentCode.Length });
        else Fail("loopback.code_enter_pairing", "The controller pairs to the loopback agent with the six-digit code and Enter", new { host = "127.0.0.1", pairStatusBeforeWait, pairStatus, visible = UiTexts().Take(45).ToArray() });
        if (!connected) throw new InvalidOperationException("Loopback controller did not connect after code entry.");

        var statusReply = await CallAsync("status");
        var status = Data(statusReply);
        workspace = status.Str("workspace");
        string remoteHash = FindString(status, "agentBinarySha256", "binarySha256", "releaseSha256", "executableSha256", "sha256");
        var heartbeat = await WaitForBinaryMatchAsync(releaseHash, 30);
        string heartbeatHash = FindString(heartbeat, "agentBinarySha256", "binarySha256", "releaseSha256", "executableSha256", "sha256");
        bool matched = remoteHash.Equals(releaseHash, StringComparison.OrdinalIgnoreCase) && heartbeatHash.Equals(releaseHash, StringComparison.OrdinalIgnoreCase) && (!heartbeat.TryGetProperty("binaryMatched", out var binaryMatched) || binaryMatched.GetBoolean());
        if (matched) Pass("loopback.sync_exact_release", "The loopback agent reports the controller's exact Release bytes before live viewing", new { releaseHash, remoteHash, heartbeatHash, heartbeat });
        else Fail("loopback.sync_exact_release", "The loopback agent reports the controller's exact Release bytes before live viewing", new { releaseHash, remoteHash, heartbeatHash, heartbeat });

        LiveEvidence liveEvidence = await WaitForLiveEvidenceAsync(60);
        var firstFrame = await CliAsync(["screenshot", "--file", Path.Combine(output, "loopback-screen.jpg")]);
        bool freshFrame = firstFrame.TryGetProperty("ok", out var firstFrameOk) && firstFrameOk.ValueKind == JsonValueKind.True && firstFrame.TryGetProperty("roundTripMs", out var roundTrip) && roundTrip.ValueKind == JsonValueKind.Number && roundTrip.GetDouble() > 0;
        bool live = liveEvidence.BadgeVisible && liveEvidence.TelemetryVisible && freshFrame;
        if (live) Pass("loopback.live_auto_start", "The loopback controller starts live viewing after a fresh frame", new { liveBadge = new { visible = liveEvidence.BadgeVisible, text = liveEvidence.BadgeText }, streamStatus = liveEvidence.TelemetryText, waitedSeconds = liveEvidence.WaitedSeconds, frame = firstFrame });
        else Fail("loopback.live_auto_start", "The loopback controller starts live viewing after a fresh frame", new { live, liveBadge = new { visible = liveEvidence.BadgeVisible, text = liveEvidence.BadgeText }, streamStatus = liveEvidence.TelemetryText, waitedSeconds = liveEvidence.WaitedSeconds, freshFrame, frame = firstFrame });
        CaptureDesktop("loopback-controller-live.png");

        ProbeDefaultInput();
        ProbeLoopbackRemoteScreenInput();
        await ProbeAutoDataAsync();
        await CaptureLoopbackMinimumSizeAsync();
        await RunRegressionScenarioAsync(status);
        await ProbeLoopbackTrayAndTerminateAsync();
        await FinishAsync();
    }

    private async Task CleanupLoopbackProcessesAsync()
    {
        foreach (Process? process in new[] { loopbackController, loopbackAgent })
        {
            if (process == null) continue;
            try
            {
                process.Refresh();
                if (!process.HasExited)
                {
                    process.CloseMainWindow();
                    await WaitForProcessExitAsync(process, TimeSpan.FromSeconds(8));
                }
            }
            catch (Exception ex)
            {
                Record("loopback.cleanup", "Loopback product cleanup attempted graceful process shutdown", "blocked", false, new { pid = SafeProcessId(process), error = ex.Message });
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static int? SafeProcessId(Process process)
    {
        try { return process.Id; } catch (InvalidOperationException) { return null; }
    }

    private sealed record TrayContext(AutomationElement? OpenItem, string IconName, bool OverflowOpened);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extraInfo);

    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;

    private static AutomationElement? FindSystemTrayIcon()
    {
        try
        {
            return AutomationElement.RootElement.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().FirstOrDefault(element =>
            {
                try
                {
                    var current = element.Current;
                    string name = current.Name;
                    string type = current.ControlType.ProgrammaticName;
                    return !current.IsOffscreen && (type == "ControlType.Button" || type == "ControlType.ListItem")
                        && name.Contains("Remote Debugger", StringComparison.OrdinalIgnoreCase);
                }
                catch (ElementNotAvailableException) { return false; }
            });
        }
        catch (Exception) { return null; }
    }

    private static AutomationElement? FindTrayOverflowButton()
    {
        try
        {
            return AutomationElement.RootElement.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().FirstOrDefault(element =>
            {
                try
                {
                    var current = element.Current;
                    string name = current.Name.ToLowerInvariant();
                    string type = current.ControlType.ProgrammaticName;
                    return !current.IsOffscreen && type == "ControlType.Button"
                        && (name.Contains("hidden", StringComparison.Ordinal) || name.Contains("masqu", StringComparison.Ordinal) || name.Contains("icône", StringComparison.Ordinal));
                }
                catch (ElementNotAvailableException) { return false; }
            });
        }
        catch (Exception) { return null; }
    }

    private static bool RightClickTrayIcon(AutomationElement icon)
    {
        try
        {
            var point = icon.GetClickablePoint();
            Forms.Cursor.Position = new System.Drawing.Point((int)Math.Round(point.X), (int)Math.Round(point.Y));
            mouse_event(MouseEventRightDown, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MouseEventRightUp, 0, 0, 0, UIntPtr.Zero);
            return true;
        }
        catch (Exception) { return false; }
    }

    private async Task<TrayContext> OpenTrayContextAsync()
    {
        bool overflowOpened = false;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            AutomationElement? icon = FindSystemTrayIcon();
            if (icon == null && !overflowOpened)
            {
                var overflow = FindTrayOverflowButton();
                if (overflow != null)
                {
                    InvokeElement(overflow);
                    overflowOpened = true;
                    await Task.Delay(500, stop.Token);
                    continue;
                }
            }
            if (icon == null) break;
            string iconName = Safe(() => icon.Current.Name);
            if (!RightClickTrayIcon(icon)) break;
            for (int n = 0; n < 20; n++)
            {
                var open = FindDesktopMenuItem("Ouvrir") ?? FindDesktopMenuItem("Open");
                if (open != null) return new TrayContext(open, iconName, overflowOpened);
                await Task.Delay(250, stop.Token);
            }
            return new TrayContext(null, iconName, overflowOpened);
        }
        return new TrayContext(null, "", overflowOpened);
    }

    private bool IsLoopbackControllerVisible()
    {
        try { return FindVisibleId(ContractId("terminateSession")) != null; }
        catch (Exception) { return false; }
    }

    private Process LaunchLoopbackProduct(bool agent, string dataRoot)
    {
        var psi = new ProcessStartInfo(application)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(application)!,
            CreateNoWindow = false
        };
        if (!agent) psi.ArgumentList.Add("--controller");
        psi.ArgumentList.Add("--loopback-only");
        psi.ArgumentList.Add("--data-root");
        psi.ArgumentList.Add(dataRoot);
        return Process.Start(psi) ?? throw new IOException("Loopback Release process did not start.");
    }

    private async Task ProbeLoopbackTrayAndTerminateAsync()
    {
        if (product == null || loopbackAgent == null) throw new InvalidOperationException("Loopback product processes are missing.");
        Native.FocusWindow(product.Id);
        product.CloseMainWindow();
        await Task.Delay(1400, stop.Token);
        product.Refresh();
        TrayContext tray = await OpenTrayContextAsync();
        bool trayAlive = !product.HasExited && !IsLoopbackControllerVisible() && tray.OpenItem != null;
        JsonElement? heartbeat = null;
        try { heartbeat = Data(await CallAsync("status")); } catch (Exception) { }
        bool heartbeatAlive = heartbeat.HasValue && heartbeat.Value.ValueKind == JsonValueKind.Object;
        if (trayAlive && heartbeatAlive) Pass("loopback.close_to_tray", "Closing the loopback controller keeps its session alive in the tray", new { pid = product.Id, trayIcon = tray.IconName, overflowOpened = tray.OverflowOpened, heartbeat = heartbeat!.Value });
        else Fail("loopback.close_to_tray", "Closing the loopback controller keeps its session alive in the tray", new { trayAlive, heartbeatAlive, trayIcon = tray.IconName, overflowOpened = tray.OverflowOpened, pid = product.Id, exited = product.HasExited });

        var open = tray.OpenItem;
        if (open != null)
        {
            InvokeElement(open);
            await WaitForUiAsync(IsLoopbackControllerVisible, 15);
        }
        else Fail("loopback.tray_restore", "The loopback controller tray Open action restores its window", new { menu = "Ouvrir missing", trayIcon = tray.IconName, overflowOpened = tray.OverflowOpened });

        var terminate = FindVisibleId(ContractId("terminateSession"));
        if (terminate == null)
        {
            Fail("loopback.terminate", "The controller terminate action ends the loopback session and exits the agent", new { id = ContractId("terminateSession") });
            return;
        }
        InvokeElement(terminate);
        bool disconnected = await WaitForTextAsync("connectionStatus", text => !IsConnected(text), 30);
        bool agentExited = false;
        for (int n = 0; n < 30; n++)
        {
            loopbackAgent.Refresh();
            if (loopbackAgent.HasExited) { agentExited = true; break; }
            await Task.Delay(1000, stop.Token);
        }
        if (disconnected && agentExited)
        {
            sawTermination = true;
            Pass("loopback.terminate", "The controller terminate action ends the loopback session and exits the agent", new { disconnected, agentPid = loopbackAgent.Id, agentExited });
        }
        else Fail("loopback.terminate", "The controller terminate action ends the loopback session and exits the agent", new { disconnected, agentExited, agentPid = loopbackAgent.Id });
        JsonElement denied = Json.Element(new { ok = false, error = "transport_after_termination" });
        try { denied = await CallAsync("status", requireSuccess: false, seconds: 15); } catch (Exception ex) { denied = Json.Element(new { ok = false, error = ex.Message }); }
        bool accessClosed = !denied.TryGetProperty("ok", out var accessOk) || accessOk.ValueKind != JsonValueKind.True;
        if (accessClosed) Pass("loopback.terminated_access_denied", "The loopback support token no longer authorizes requests after termination", denied);
        else Fail("loopback.terminated_access_denied", "The loopback support token no longer authorizes requests after termination", denied);
        await ProbeSleepReleasedAsync();
        CaptureDesktop("loopback-controller-terminated.png");
    }

    private void ProbeLoopbackRemoteScreenInput()
    {
        AutomationElement? screen = FindVisibleId(ContractId("remoteScreen"));
        if (screen == null)
        {
            Fail("loopback.remote_screen_input", "The live remote screen is a focusable input surface", new { control = ContractId("remoteScreen"), visible = false });
            return;
        }

        try
        {
            bool keyboardFocusable = screen.Current.IsKeyboardFocusable;
            screen.SetFocus();
            bool hasFocus = screen.Current.HasKeyboardFocus;
            if (keyboardFocusable && hasFocus)
                Pass("loopback.remote_screen_input", "The live remote screen accepts focus for the enabled mouse and keyboard forwarding", new { control = ContractId("remoteScreen"), keyboardFocusable, hasFocus });
            else
                Fail("loopback.remote_screen_input", "The live remote screen accepts focus for the enabled mouse and keyboard forwarding", new { control = ContractId("remoteScreen"), keyboardFocusable, hasFocus });
        }
        catch (Exception ex)
        {
            Fail("loopback.remote_screen_input", "The live remote screen accepts focus for the enabled mouse and keyboard forwarding", new { control = ContractId("remoteScreen"), error = ex.Message });
        }
    }
}
