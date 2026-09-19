using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private async Task PrivilegedInputReviewAsync()
    {
        if (brokerProvisioning == null) throw new IOException("Privileged input requires the broker's verified provisioning receipt.");
        loopbackAgent = product = LaunchLoopbackProduct(true, productData);
        try
        {
            await WaitUiAsync();
            ProbeProductIdentity("input.agent_medium", "The visible agent remains at medium integrity");
            string code = await WaitPairingCodeAsync();
            await CliAsync(["pair", "--host", "127.0.0.1"], stdin: code);
            var connection = JsonSerializer.Deserialize<Connection>(Vault.Read(RemoteClient.DefaultPath), Json.Options)!;
            var remote = new RemoteClient(connection, await HashFileAsync(application));
            var ready = Stopwatch.StartNew();
            while (!RemoteClient.Require(await remote.CallAsync("maintenance.status", ct: stop.Token)).GetProperty("active").GetBoolean())
            {
                if (ready.Elapsed > TimeSpan.FromSeconds(90)) throw new IOException("Administrator maintenance did not start.");
                await Task.Delay(300, stop.Token);
            }
            WindowState = Forms.FormWindowState.Minimized;
            using var launched = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "Taskmgr.exe")) { UseShellExecute = true });
            Native.NativeWindow? window = null;
            var waiting = Stopwatch.StartNew();
            while (window == null && waiting.Elapsed < TimeSpan.FromSeconds(30))
            {
                window = Native.NativeWindows().FirstOrDefault(w => w.Width > 100 && Process.GetProcessById(w.Pid).ProcessName.Equals("Taskmgr", StringComparison.OrdinalIgnoreCase));
                if (window == null) await Task.Delay(250, stop.Token);
            }
            if (window == null) throw new IOException("Task Manager did not open on the interactive desktop.");
            using var manager = Process.GetProcessById(window.Pid);
            var identity = ProvisioningEvidence.InspectProduct(manager, Path.Combine(Environment.SystemDirectory, "Taskmgr.exe"), WindowsIdentity.GetCurrent().User!.Value);
            if (identity.IntegrityLevel != "High") throw new IOException("Task Manager was not elevated; this guest cannot qualify elevated-window clicks.");
            CaptureDesktop("task-manager-before-input.png", focusProduct: false);
            string layoutId = DesktopCapture.LayoutId();
            int x = window.X + window.Width - 25, y = window.Y + 16;
            RemoteClient.Require(await remote.SendInputAsync(new { kind = "down", x, y, button = "left", layoutId }, stop.Token, 30));
            RemoteClient.Require(await remote.SendInputAsync(new { kind = "up", x, y, button = "left", layoutId }, stop.Token));
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                while (Native.NativeWindows().Any(w => w.Pid == window.Pid && w.Width > 100)) await Task.Delay(100, deadline.Token);
            }
            Pass("input.elevated_task_manager", "A click sent through the remote input channel closes high-integrity Task Manager", new { identity, x, y, route = remote.ActiveRoute });
            await remote.DisconnectAsync(stop.Token);
            var teardown = Stopwatch.StartNew();
            while (Process.GetProcessesByName("RemoteDebugger.Support").Any(p => p.SessionId > 0))
            {
                if (teardown.Elapsed > TimeSpan.FromSeconds(8)) throw new IOException("The privileged input helper survived input-channel closure.");
                await Task.Delay(100, stop.Token);
            }
            Pass("input.helper_teardown", "Closing the controller input channel removes its interactive privileged helper");
            RemoteClient.Require(await remote.CallAsync("session.end", ct: stop.Token));
            await FinishAsync();
        }
        finally { await CleanupLoopbackProcessesAsync(); }
    }
}
