using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using Microsoft.Win32;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    // Only the initial phase launches the product. A declared harness continuation
    // must find the process started by the product's own Windows RunOnce entry.
    private async Task PowerAgentAsync(int boot, string? credentialPath)
    {
        if (brokerProvisioning == null) throw new IOException("Power acceptance requires verified guest provisioning.");
        var credentialBinding = credentialPath == null ? null : PowerCredentialFixture.Read(credentialPath).Identity;
        string code = "";
        if (boot == 0)
        {
            product = LaunchProduct(true); await WaitUiAsync();
            code = await WaitPairingCodeAsync();
        }
        else
        {
            int session = Process.GetCurrentProcess().SessionId;
            await WaitWorkflowAsync(() =>
            {
                foreach (var candidate in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(application)))
                {
                    bool selected = false;
                    try
                    {
                        if (candidate.SessionId == session && string.Equals(candidate.MainModule?.FileName, application, StringComparison.OrdinalIgnoreCase))
                        { product = candidate; selected = true; return Task.FromResult(true); }
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                    finally { if (!selected) candidate.Dispose(); }
                }
                return Task.FromResult(false);
            }, 120);
        }
        ProbeProductIdentity($"power.agent_boot_{boot}", boot == 0
            ? "The initial power-test agent runs as the registered interactive user"
            : "Windows resumed the same managed product without a Lab relaunch");
        string bootId = WindowsBootIdentity.Read();
        string userSid = WindowsIdentity.GetCurrent().User?.Value ?? "";
        Pass($"power.boot_{boot}", "The Lab records the actual boot and interactive account", new { boot, bootId, userSid });
        using var input = new Forms.TextBox { Name = "powerInputProbe", Dock = Forms.DockStyle.Top, Height = 36, Font = new("Segoe UI", 14) };
        Controls.Add(input); input.BringToFront();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, CoordinationPort));
        while (!stop.IsCancellationRequested)
        {
            using var receive = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            receive.CancelAfter(TimeSpan.FromMinutes(10));
            var received = await udp.ReceiveAsync(receive.Token);
            string request = Encoding.UTF8.GetString(received.Buffer);
            object response;
            try
            {
                if (request is "RD_LAB_BOOTSTRAP" or "POWER_STATUS")
                    response = new { code, boot, bootId, userSid, account = WindowsIdentity.GetCurrent().Name, credentialBinding,
                        binarySha256 = await HashFileAsync(application), productStartedUtc = product!.StartTime.ToUniversalTime(),
                        utc = DateTimeOffset.UtcNow, reports = new IncidentLog(productData).Read().Select(x => x.Failure.Name).ToArray() };
                else if (request == "POWER_INPUT_CLEAR")
                {
                    WindowState = Forms.FormWindowState.Normal; TopMost = true; Show(); Activate(); input.Clear();
                    var point = input.PointToScreen(new Point(input.Width / 2, input.Height / 2));
                    response = new { x = point.X, y = point.Y, layoutId = DesktopCapture.LayoutId() };
                }
                else if (request == "POWER_INPUT_READ") response = new { text = input.Text };
                else if (request == "CLIP_HASH") response = new { hash = Safety.Hash(Forms.Clipboard.ContainsText() ? Forms.Clipboard.GetText() : "") };
                else if (request.StartsWith("CLIP:", StringComparison.Ordinal))
                { Forms.Clipboard.SetText(request[5..]); response = new { written = true }; }
                else if (request == "POWER_BEFORE_RESTART")
                {
                    if (!File.Exists(Path.Combine(productData, "restart-session.resume"))) throw new IOException("The product did not persist its restart grant.");
                    using var startup = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce");
                    string expected = $"\"{application}\" --startup --resume-restart --data-root \"{Path.GetFullPath(productData).TrimEnd('\\')}\"";
                    if (!string.Equals(startup?.GetValue(RestartResumeStore.RunOnceName) as string, expected, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The product did not register its exact Windows restart continuation.");
                    Pass($"power.before_boot_{boot + 1}", "The controller accepted restart and the product saved its reconnect grant", new { bootId });
                    await FinishAsync(markerName: $"power-before-boot-{boot + 1}.json");
                    response = new { prepared = true, bootId };
                }
                else if (request == "POWER_BEFORE_SHUTDOWN")
                {
                    Pass("power.before_shutdown", "The controller observed the product accepting shutdown before the pre-power-off marker", new { bootId });
                    await FinishAsync();
                    response = new { prepared = true, bootId };
                }
                else if (request == "POWER_DONE")
                {
                    Pass("power.completed", "The controller completed the declared native power assertions", new { boot, bootId });
                    await FinishAsync();
                    await SendUdpAsync(udp, new { done = true }, received.RemoteEndPoint); return;
                }
                else continue;
            }
            catch (Exception ex) { response = new { error = ex.ToString() }; }
            await SendUdpAsync(udp, response, received.RemoteEndPoint);
        }
    }

    private static AutomationElement PowerControl(AutomationElement window, string id) => window.FindFirst(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.AutomationIdProperty, id)) ?? throw new IOException("Power control missing: " + id);

    private async Task<JsonElement> PowerMessageAsync(string message, int seconds = 30)
    {
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        overall.CancelAfter(TimeSpan.FromSeconds(seconds));
        while (true)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(overall.Token); attempt.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                using var udp = new UdpClient();
                await udp.SendAsync(Encoding.UTF8.GetBytes(message), new IPEndPoint(IPAddress.Parse(peerHost!), CoordinationPort), attempt.Token);
                var received = await udp.ReceiveAsync(attempt.Token);
                var response = JsonSerializer.Deserialize<JsonElement>(received.Buffer);
                if (response.TryGetProperty("error", out var error)) throw new IOException(error.GetString());
                return response;
            }
            catch (OperationCanceledException) when (!overall.IsCancellationRequested) { }
            catch (SocketException) when (!overall.IsCancellationRequested) { }
        }
    }

    private async Task PowerControllerAsync(string scenario, PowerCredentialFixture? credential = null)
    {
        if (scenario is not ("once" or "cancel" or "expiry" or "shutdown")) throw new ArgumentException("Unknown power scenario.");
        product = LaunchProduct(false); await WaitUiAsync(); WindowState = Forms.FormWindowState.Minimized;
        var addresses = ProvisioningEvidence.LocalIPv4Addresses();
        var peer = (await DiscoverAsync(180)).FirstOrDefault(p => ProvisioningEvidence.IsRemoteCoordinationAddress(p.Host, addresses, out _))
            ?? throw new IOException("Power-test target was not discovered.");
        peerHost = peer.Host;
        var initial = await PowerMessageAsync("RD_LAB_BOOTSTRAP");
        Action? enterPassword = credential == null ? null : () => credential.EnterPassword(initial.GetProperty("credentialBinding"));
        Set("host", peerHost); Set("pairCode", initial.Str("code")); FocusAndEnter("pairCode");
        if (!await WaitForTextAsync("connectionStatus", IsConnected, 60)) throw new IOException("Power-test pairing failed.");
        var connection = RemoteClient.Load().Connection;
        var remote = new RemoteClient(connection, await HashFileAsync(application));
        await WaitWorkflowAsync(async () => RemoteClient.Require(await remote.CallAsync("maintenance.status", ct: stop.Token)).GetProperty("active").GetBoolean(), 90);
        string machine = (scenario == "shutdown"
            ? RemoteClient.Require(await remote.CallAsync("power.preflight", ct: stop.Token, seconds: 30))
            : await RequireCleanPowerPreflightAsync(remote)).Str("machine");
        if (machine.Length == 0) throw new IOException("Power preflight omitted the target PC.");

        if (scenario == "shutdown")
        {
            var progress = await AcceptPowerFromControllerAsync(false, false, machine);
            _ = await PowerMessageAsync("POWER_BEFORE_SHUTDOWN", 4);
            await WaitWorkflowAsync(() => Task.FromResult(progress.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "powerState")) is { } state &&
                Value(state) == UiText.PowerOffNotConfirmed), 30);
            if (File.Exists(RemoteClient.DefaultPath)) throw new IOException("The controller retained the shutdown session for reconnection.");
            CaptureDesktop("power-shutdown-accepted.png");
            Pass("power.shutdown_controller", "The controller accepts shutdown, forgets reconnection and does not claim physical power-off");
            await FinishAsync(); return;
        }

        if (scenario == "once")
        {
            if (enterPassword == null) throw new IOException("The protected target-credential reader is unavailable.");
            var countdown = await AcceptPowerFromControllerAsync(true, true, machine, enterPassword);
            InvokeElement(PowerControl(countdown, "cancelPowerWait"));
            await WaitWorkflowAsync(() => Task.FromResult(IsConnected(TryValue("connectionStatus"))), 30);
            await Task.Delay(11000, stop.Token);
            if ((await PowerMessageAsync("POWER_STATUS")).Str("bootId") != initial.Str("bootId")) throw new IOException("The cancelled restart still rebooted.");
            _ = await RequireCleanPowerPreflightAsync(remote);
            Pass("power.restart_countdown_cancel", "Cancelling the real one-use restart keeps the session and removes temporary sign-in configuration");
        }

        int boots = scenario == "once" ? 2 : 1;
        string priorBoot = initial.Str("bootId");
        for (int boot = 1; boot <= boots; boot++)
        {
            bool once = scenario == "once" && boot == 1;
            var progress = await AcceptPowerFromControllerAsync(true, once, machine, once ? enterPassword : null);
            _ = await PowerMessageAsync("POWER_BEFORE_RESTART", 4);
            var elapsed = Stopwatch.StartNew();
            Forms.Clipboard.SetText("controller-during-reboot-" + boot);
            await WaitWorkflowAsync(() => Task.FromResult(Value(PowerControl(progress, "cancelPowerWait")) == UiText.CancelReconnectWait), 30);
            string clock = Regex.Match(Value(PowerControl(progress, "powerCountdown")), @"\d{2}:\d{2}:\d{2}").Value;
            if (!TimeSpan.TryParse(clock, CultureInfo.InvariantCulture, out var remaining) || remaining.TotalSeconds is < 3500 or > 3600)
                throw new IOException("The controller did not show the one-hour reconnect countdown.");
            CaptureDesktop($"power-boot-{boot}-waiting.png");

            if (scenario is "cancel" or "expiry")
            {
                if (scenario == "cancel") InvokeElement(PowerControl(progress, "cancelPowerWait"));
                await WaitWorkflowAsync(() => Task.FromResult(UiTexts().Any(x => x.Contains(UiText.RestartWaitStopped, StringComparison.Ordinal))), scenario == "expiry" ? 3700 : 30);
                if (scenario == "expiry" && elapsed.Elapsed.TotalSeconds < 3500) throw new IOException("The reconnect wait expired before one hour.");
                if (File.Exists(RemoteClient.DefaultPath)) throw new IOException("The stopped controller retained its reconnect profile.");
                CaptureDesktop("power-wait-" + scenario + "-stopped.png");
                Pass("power.wait_stopped_" + scenario, "The controller visibly stopped waiting and removed its reconnect profile",
                    new { elapsedSeconds = elapsed.Elapsed.TotalSeconds });
                // Cold boot, manual sign-in and broker continuation startup are
                // outside the controller's already-cancelled/expired wait.
                var returned = await PowerMessageAsync("POWER_STATUS", 600);
                if (returned.Str("bootId") == priorBoot) throw new IOException("The issued restart did not change the Windows boot.");
                await Task.Delay(10000, stop.Token);
                if (File.Exists(RemoteClient.DefaultPath) || IsConnected(TryValue("connectionStatus"))) throw new IOException("The stopped controller reconnected after sign-in.");
                CaptureDesktop("power-wait-" + scenario + ".png");
                Pass("power.wait_" + scenario, scenario == "expiry"
                    ? "The real one-hour deadline stops reconnection even when Windows later signs in"
                    : "Cancelling after the countdown stops reconnection without undoing the issued restart", new { elapsedSeconds = elapsed.Elapsed.TotalSeconds });
                _ = await PowerMessageAsync("POWER_DONE"); await FinishAsync(); return;
            }

            try { await WaitWorkflowAsync(() => Task.FromResult(Value(PowerControl(progress, "powerState")) == UiText.DesktopReady), 300); }
            catch (TimeoutException) { await CapturePowerReturnDiagnosticsAsync(remote); throw; }
            var state = await PowerMessageAsync("POWER_STATUS", 120);
            if (state.Int("boot") != boot || state.Str("bootId") == priorBoot) throw new IOException("The continuation did not observe the expected new boot.");
            if (!Safety.Equal(RemoteClient.Load().Connection.Token, connection.Token)) throw new IOException("Restart silently replaced the authorized support grant.");
            // The previous boot's persistent input socket is no longer usable.
            remote = new RemoteClient(connection, await HashFileAsync(application));
            var heartbeat = RemoteClient.Require(await remote.CallAsync("session.heartbeat", ct: stop.Token));
            if (!heartbeat.GetProperty("binaryMatched").GetBoolean()) throw new IOException("The returning executable does not match the controller.");
            var frame = RemoteClient.Require(await remote.CallAsync("screenshot", new { monitor = 0 }, stop.Token)).Deserialize<ScreenFrame>(Json.Options)!;
            if (frame.CapturedUtc < state.GetProperty("utc").GetDateTimeOffset().AddSeconds(-2)) throw new IOException("The returning desktop frame is stale.");
            if (state.GetProperty("reports").EnumerateArray().Any(x => x.GetString() == "previous_process_ended_unexpectedly"))
                throw new IOException("A planned restart was reported as a process crash.");
            if ((await PowerMessageAsync("CLIP_HASH")).Str("hash") == Safety.Hash("controller-during-reboot-" + boot))
                throw new IOException("A disconnected clipboard event was replayed after reboot.");
            InvokeElement(PowerControl(progress, "cancelPowerWait")); // The finished dialog's Close button.
            var live = await WaitForLiveEvidenceAsync(30);
            if (!live.BadgeVisible || !live.TelemetryVisible) throw new IOException("The controller did not restore live viewing.");
            var input = await PowerMessageAsync("POWER_INPUT_CLEAR");
            foreach (string kind in new[] { "down", "up" })
                RemoteClient.Require(await remote.SendInputAsync(new { kind, x = input.Int("x"), y = input.Int("y"), button = "left", layoutId = input.Str("layoutId") }, stop.Token));
            foreach (string kind in new[] { "keyDown", "keyUp" })
                RemoteClient.Require(await remote.SendInputAsync(new { kind, virtualKey = 65 }, stop.Token));
            await WaitWorkflowAsync(async () => string.Equals((await PowerMessageAsync("POWER_INPUT_READ")).Str("text"), "a", StringComparison.OrdinalIgnoreCase), 10);
            _ = await RequireCleanPowerPreflightAsync(remote);
            CaptureDesktop($"power-boot-{boot}-ready.png");
            Pass($"power.return_boot_{boot}", once ? "One-use sign-in restores the same session with fresh video and working input"
                : "Manual sign-in on the second boot restores the same session without reusing automatic sign-in", new { bootId = state.Str("bootId"), elapsedSeconds = elapsed.Elapsed.TotalSeconds });
            priorBoot = state.Str("bootId");
        }
        await remote.EndSessionAsync(stop.Token);
        _ = await PowerMessageAsync("POWER_DONE"); await FinishAsync();
    }

    private async Task CapturePowerReturnDiagnosticsAsync(RemoteClient remote)
    {
        var observations = new Dictionary<string, object>();
        try { observations["target"] = await PowerMessageAsync("POWER_STATUS", 5); }
        catch (Exception ex) { observations["targetError"] = ex.GetType().Name + ": " + ex.Message; }
        try { observations["heartbeat"] = await remote.CallAsync("session.heartbeat", ct: stop.Token, seconds: 5); }
        catch (Exception ex) { observations["heartbeatError"] = ex.GetType().Name + ": " + ex.Message; }
        await File.WriteAllTextAsync(Path.Combine(output, "power-return-diagnostics.json"), Json.Text(observations), stop.Token);
    }

    private async Task<JsonElement> RequireCleanPowerPreflightAsync(RemoteClient remote)
    {
        var preflight = RemoteClient.Require(await remote.CallAsync("power.preflight", ct: stop.Token, seconds: 30));
        if (!preflight.GetProperty("oneTimeLoginAvailable").GetBoolean() || preflight.Str("loginConstraint") != "" ||
            preflight.Str("expectedReturn") != "manual_sign_in" || preflight.Int("waitSeconds") != 3600)
            throw new IOException("The target is not a clean, unattended-boot-capable manual-sign-in fixture: " + preflight.Str("loginConstraint"));
        return preflight;
    }

    private async Task<AutomationElement> AcceptPowerFromControllerAsync(bool restart, bool once, string machine, Action? enterPassword = null)
    {
        string buttonId = restart ? "restartRemotePc" : "shutdownRemotePc";
        await WaitWorkflowAsync(() => Task.FromResult(Find(buttonId, 100)?.Current.IsEnabled == true));
        InvokeElement(Find(buttonId, 5000) ?? throw new IOException("Controller power button missing."));
        if (restart)
        {
            var options = await WorkflowDialogAsync("restartOptions");
            var choice = PowerControl(options, "oneTimeLogin");
            if (once && !choice.Current.IsEnabled) throw new IOException("The clean guest did not offer one-use sign-in.");
            CaptureDesktop(once ? "power-once-options.png" : "power-manual-options.png");
            if (once)
            {
                if (enterPassword == null) throw new IOException("The protected guest password fixture is unavailable.");
                ((TogglePattern)choice.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
                PowerControl(options, "oneTimePassword").SetFocus();
                enterPassword();
            }
            InvokeElement(PowerControl(options, "continueRestart"));
        }
        var confirmation = await WorkflowDialogAsync("#32770");
        string text = string.Join(" ", confirmation.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>().Select(x => x.Current.Name));
        if (!text.Contains(machine, StringComparison.OrdinalIgnoreCase)) throw new IOException("Power confirmation omitted the target PC.");
        CaptureDesktop(restart ? "power-restart-confirmation.png" : "power-shutdown-confirmation.png");
        InvokeElement(PowerControl(confirmation, "1"));
        var progress = await WorkflowDialogAsync("powerProgressWindow");
        await WaitWorkflowAsync(() => Task.FromResult(Value(PowerControl(progress, "powerState")) == (restart ? UiText.RestartCountdown : UiText.ShutdownCountdown)));
        CaptureDesktop(restart ? "power-restart-countdown.png" : "power-shutdown-countdown.png");
        return progress;
    }
}
