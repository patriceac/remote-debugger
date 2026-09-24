using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Windows.Automation;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private async Task PrivilegedInputReviewAsync()
    {
        if (brokerProvisioning == null) throw new IOException("Privileged input requires the broker's verified provisioning receipt.");
        var originalCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        var startup = Stopwatch.StartNew();
        loopbackAgent = product = LaunchLoopbackProduct(true, productData, "en");
        try
        {
            await WaitUiAsync();
            ProbeProductIdentity("input.agent_medium", "The visible agent remains at medium integrity");
            var preparation = Stopwatch.StartNew();
            await WaitForUiAsync(() => FindVisibleId("agentMaintenanceState") is { } state && Value(state) == "Ready", 90);
            startup.Stop();
            int helperPid = InputHelperIds().Single();
            CaptureDesktop("input-ready-before-pairing.png");
            Pass("input.prepared_before_pairing", "The authenticated helper is ready before any controller pairs", new { helperPid, preparation.Elapsed.TotalSeconds, startupSeconds = startup.Elapsed.TotalSeconds });
            string code = await WaitPairingCodeAsync();
            await CliAsync(["pair", "--host", "127.0.0.1"], stdin: code);
            var connection = JsonSerializer.Deserialize<Connection>(Vault.Read(RemoteClient.DefaultPath), Json.Options)!;
            string binaryHash = await HashFileAsync(application);
            var remote = new RemoteClient(connection, binaryHash);
            if (!RemoteClient.Require(await remote.CallAsync("maintenance.status", ct: stop.Token)).GetProperty("inputReady").GetBoolean())
                throw new IOException("Pairing lost the prepared input helper.");
            WindowState = Forms.FormWindowState.Minimized;
            await CheckWarmInputAsync(remote, "input.first_input");
            var unauthorized = new RemoteClient(connection with { Token = "" }, binaryHash);
            var denied = await unauthorized.CallAsync("ui.input", new { kind = "release" }, ct: stop.Token);
            if (denied.Ok || denied.Error != "access_denied") throw new IOException("The prepared helper accepted unauthorized input.");
            Pass("input.unauthorized_denied", "A prepared helper still requires controller authorization");
            foreach (string inputKind in new[] { "keyboard", "mouse" })
            {
                using var launched = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "Taskmgr.exe")) { UseShellExecute = true });
                Native.NativeWindow? window = null;
                var waiting = Stopwatch.StartNew();
                while (window == null && waiting.Elapsed < TimeSpan.FromSeconds(30))
                {
                    window = Native.NativeWindows().FirstOrDefault(w => w.Width > 100 && Process.GetProcessById(w.Pid).ProcessName.Equals("Taskmgr", StringComparison.OrdinalIgnoreCase));
                    if (window == null) await Task.Delay(250, stop.Token);
                }
                if (window == null) throw new IOException("Task Manager did not open on the interactive desktop.");
                Pass($"input.task_manager_{inputKind}_opened", "Task Manager is visible before elevated input verification", window);
                using var manager = Process.GetProcessById(window.Pid);
                var identity = ProvisioningEvidence.InspectProduct(manager, Path.Combine(Environment.SystemDirectory, "Taskmgr.exe"), WindowsIdentity.GetCurrent().User!.Value);
                if (identity.IntegrityLevel != "High") throw new IOException("Task Manager was not elevated; this guest cannot qualify elevated-window clicks.");
                await Task.Delay(3500, stop.Token);
                window = Native.NativeWindows().First(w => w.Pid == manager.Id && w.Width > 100);
                if (!window.Foreground) throw new IOException("Task Manager is not foreground; keyboard input cannot be qualified.");
                CaptureDesktop($"task-manager-before-{inputKind}.png", focusProduct: false);
                string layoutId = DesktopCapture.LayoutId();
                int x = window.X + window.Width - 25, y = window.Y + 16;
                if (inputKind == "keyboard")
                {
                    foreach (var key in new[] { ("keyDown", 18), ("keyDown", 115), ("keyUp", 115), ("keyUp", 18) })
                        RemoteClient.Require(await remote.SendInputAsync(new { kind = key.Item1, virtualKey = key.Item2 }, stop.Token));
                }
                else
                {
                    RemoteClient.Require(await remote.SendInputAsync(new { kind = "down", x, y, button = "left", layoutId }, stop.Token));
                    RemoteClient.Require(await remote.SendInputAsync(new { kind = "up", x, y, button = "left", layoutId }, stop.Token));
                }
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    while (Native.NativeWindows().Any(w => w.Pid == window.Pid && w.Width > 100)) await Task.Delay(100, deadline.Token);
                }
                Pass($"input.elevated_task_manager_{inputKind}", "Remote input closes high-integrity Task Manager using the viewer's default deadlines", new { inputKind, identity, x, y, route = remote.ActiveRoute });
            }
            RemoteClient.Require(await remote.SendInputAsync(new { kind = "keyDown", virtualKey = 160 }, stop.Token));
            if ((InputReviewKeyState(160) & 0x8000) == 0) throw new IOException("The held Shift key was not injected.");
            var release = Stopwatch.StartNew();
            await remote.DisconnectAsync(stop.Token);
            while ((InputReviewKeyState(160) & 0x8000) != 0 && release.ElapsedMilliseconds < 1500) await Task.Delay(25, stop.Token);
            if ((InputReviewKeyState(160) & 0x8000) != 0) throw new IOException("Disconnect did not promptly release the held Shift key.");
            if (!InputHelperIds().SequenceEqual([helperPid])) throw new IOException("Disconnect replaced the prepared helper.");
            Pass("input.disconnect_reuses_helper", "Disconnect releases held keys and preserves the same helper", new { helperPid, release.Elapsed.TotalMilliseconds });
            await CheckWarmInputAsync(remote, "input.reconnect");
            await remote.EndSessionAsync(stop.Token);
            await WaitForUiAsync(() => TryValue("agentPairCode").Replace(" ", "") is var next && next.Length == 6 && next.All(char.IsDigit) && next != code, 20);
            denied = await new RemoteClient(connection, binaryHash).CallAsync("ui.input", new { kind = "release" }, ct: stop.Token);
            if (denied.Ok || denied.Error != "access_denied") throw new IOException("The ended session's token still permits input.");
            code = await WaitPairingCodeAsync();
            await CliAsync(["pair", "--host", "127.0.0.1"], stdin: code);
            connection = JsonSerializer.Deserialize<Connection>(Vault.Read(RemoteClient.DefaultPath), Json.Options)!;
            remote = new RemoteClient(connection, binaryHash);
            if (!InputHelperIds().SequenceEqual([helperPid])) throw new IOException("A new support session replaced the prepared helper.");
            await CheckWarmInputAsync(remote, "input.new_session");
            Pass("input.ended_grant_revoked", "A fresh pairing reuses the helper while the previous grant remains revoked");

            var toggle = (TogglePattern)(FindVisibleId("adminMaintenanceToggle") ?? throw new IOException("Maintenance toggle missing.")).GetCurrentPattern(TogglePattern.Pattern);
            toggle.Toggle();
            await WaitForUiAsync(() => InputHelperIds().Length == 0, 8);
            Pass("input.disable_teardown", "Disabling administrator maintenance stops its prepared helper");
            toggle.Toggle();
            await WaitForUiAsync(() => InputHelperIds().Length == 1, 90);
            RemoteClient.Require(await remote.SendInputAsync(new { kind = "release" }, stop.Token));
            await QuitLocalizedProductAsync("input.quit");
            await WaitForUiAsync(() => InputHelperIds().Length == 0, 8);
            Pass("input.quit_teardown", "Quitting the agent stops its prepared helper");
            await FinishAsync();
        }
        catch (Exception ex)
        {
            Fail("input.failure", "Elevated input verification completes without an exception", new { error = ex.ToString() });
            await FinishAsync(ex.ToString());
        }
        finally { CultureInfo.CurrentUICulture = originalCulture; await CleanupLoopbackProcessesAsync(); }
    }

    private async Task CheckWarmInputAsync(RemoteClient remote, string check)
    {
        var latency = Stopwatch.StartNew();
        RemoteClient.Require(await remote.SendInputAsync(new { kind = "release" }, stop.Token));
        if (latency.Elapsed > TimeSpan.FromSeconds(3)) throw new IOException("Prepared input exceeded the normal three-second input deadline.");
        Pass(check, "The prepared helper handles input within the normal input deadline", new { latency.Elapsed.TotalMilliseconds });
    }

    private static int[] InputHelperIds()
    {
        var ids = new List<int>();
        foreach (var process in Process.GetProcessesByName("RemoteDebugger.Support"))
            using (process) { if (process.SessionId > 0) ids.Add(process.Id); }
        return ids.ToArray();
    }

    [DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")]
    private static extern short InputReviewKeyState(int key);
}
