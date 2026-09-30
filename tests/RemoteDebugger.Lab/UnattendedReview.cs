using System.Drawing;
using System.IO;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    // Disposable pairing material; the test network has no Internet route.
    private static InternetSettings UnattendedFixtureSettings => new("https://unattended.invalid", new string('A', 64), new string('B', 64), "1e5bcf2e61c449179ce9a3d887c1ddea");

    private async Task UnattendedControllerAsync(bool upgrade = false)
    {
        Testing.UpdateAcceptanceAuthority.Enroll(Vault.DefaultRoot);
        UnattendedFixtureSettings.Save(Vault.DefaultRoot);
        string hash = await HashFileAsync(application);
        var local = ProvisioningEvidence.LocalIPv4Addresses();
        Peer? peer = null;
        await WaitWorkflowAsync(async () =>
        {
            peer = (await Discovery.FindAsync(1500, stop.Token)).FirstOrDefault(p =>
                ProvisioningEvidence.IsRemoteCoordinationAddress(p.Host, local, out _));
            return peer != null;
        }, 150);
        peerHost = peer!.Host;
        var initial = await PowerMessageAsync("POWER_STATUS");
        if (upgrade) await UpgradeAgentFromControllerAsync(peer, hash);
        await CliAsync(["pair", "--host", peerHost, "--fingerprint", peer.Fingerprint]);
        await CliAsync(["sync"]);
        var remote = new RemoteClient(RemoteClient.Load().Connection, hash);
        var before = RemoteClient.Require(await remote.CallAsync("status", ct: stop.Token));
        if (before.GetProperty("signedOut").GetBoolean() || before.GetProperty("elevated").GetBoolean())
            throw new IOException("The initial desktop agent is not unelevated.");
        Pass("unattended.interactive_before", "The original desktop agent remains unelevated before reboot");
        await ProbeUnattendedInteractiveInputAsync(remote);

        await WaitWorkflowAsync(async () =>
        {
            return RemoteClient.Require(await remote.CallAsync("maintenance.status", ct: stop.Token)).GetProperty("active").GetBoolean();
        }, 90);
        await Task.Delay(3000, stop.Token);
        _ = await PowerMessageAsync("POWER_UNATTENDED_RESTART");
        RemoteClient.Require(await remote.CallAsync("maintenance.elevated", new
        {
            file = Path.Combine(Environment.SystemDirectory, "shutdown.exe"), arguments = new[] { "/r", "/t", "10" }
        }, stop.Token));
        await Task.Delay(20000, stop.Token);

        bool connected = false;
        await WaitWorkflowAsync(async () =>
        {
            try
            {
                var found = (await Discovery.FindAsync(1000, stop.Token)).FirstOrDefault(p => p.Fingerprint == peer.Fingerprint);
                if (found == null) return false;
                await CliAsync(["pair", "--host", found.Host, "--fingerprint", peer.Fingerprint]);
                await CliAsync(["sync"]);
                remote = new RemoteClient(RemoteClient.Load().Connection, hash);
                var state = RemoteClient.Require(await remote.CallAsync("status", ct: stop.Token));
                connected = state.GetProperty("signedOut").GetBoolean();
                return connected;
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                await File.WriteAllTextAsync(Path.Combine(output, "unattended-reconnect-error.txt"), ex.GetType().Name + ": " + ex.Message, stop.Token);
                return false;
            }
        }, 240);
        if (!connected) throw new IOException("The signed-out service agent never became reachable.");
        var signedOut = RemoteClient.Require(await remote.CallAsync("status", ct: stop.Token));
        if (!signedOut.GetProperty("elevated").GetBoolean() || signedOut.Int("sessionId") <= 0)
            throw new IOException("The sign-in worker is not in the console session.");
        string probe = "$s = Get-CimInstance Win32_Service -Filter \"Name='RemoteDebuggerSupport'\"; " +
            "$p = Get-CimInstance Win32_Process -Filter \"ProcessId=" + signedOut.Int("processId") + "\"; " +
            "$owner = Invoke-CimMethod -InputObject $p -MethodName GetOwnerSid; " +
            "[pscustomobject]@{ServiceStart=$s.StartMode;ServiceState=$s.State;ServiceAccount=$s.StartName;" +
            "AgentPid=$p.ProcessId;ServicePid=$s.ProcessId;AgentSid=$owner.Sid;AgentSession=$p.SessionId;AgentPath=$p.ExecutablePath;" +
            "CallerSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value} | ConvertTo-Json -Compress";
        var inspected = RemoteClient.Require(await remote.CallAsync("command", new
        {
            file = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            arguments = new[] { "-NoProfile", "-NonInteractive", "-Command", probe }
        }, stop.Token));
        var os = JsonSerializer.Deserialize<JsonElement>(inspected.Str("stdout"));
        if (os.Str("ServiceStart") != "Auto" || os.Str("ServiceState") != "Running" ||
            os.Str("ServiceAccount") != "LocalSystem" || os.Str("CallerSid") != UnattendedSupport.SystemSid ||
            os.Str("AgentSid") != UnattendedSupport.SystemSid || os.Int("AgentSession") != signedOut.Int("sessionId") ||
            os.Int("AgentPid") != signedOut.Int("processId") || os.Int("AgentPid") == os.Int("ServicePid") ||
            !string.Equals(os.Str("AgentPath"), SupportPlatformPaths.ApplicationExecutable, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Windows did not attest automatic LocalSystem service operation.");
        Pass("unattended.service_at_boot", "Windows reports automatic LocalSystem service operation before sign-in", os);

        var frame = RemoteClient.Require(await remote.CallAsync("screenshot", new { quality = 90 }, stop.Token)).Deserialize<ScreenFrame>(Json.Options)!;
        byte[] pixels = Convert.FromBase64String(frame.Data);
        await File.WriteAllBytesAsync(Path.Combine(output, "unattended-sign-in-before.jpg"), pixels, stop.Token);
        using (var stream = new MemoryStream(pixels))
        using (var bitmap = new Bitmap(stream))
        {
            var colors = new HashSet<int>();
            for (int y = 0; y < bitmap.Height; y += 16)
                for (int x = 0; x < bitmap.Width; x += 16) colors.Add(bitmap.GetPixel(x, y).ToArgb());
            if (colors.Count < 16) throw new IOException("The sign-in capture is blank.");
        }
        int xPos = frame.Geometry.X + frame.Geometry.Width / 2, yPos = frame.Geometry.Y + 60;
        var moved = RemoteClient.Require(await remote.CallAsync("ui.input", new { kind = "move", x = xPos, y = yPos, layoutId = frame.Geometry.LayoutId }, stop.Token));
        if (moved.GetProperty("cursor").Int("x") != xPos || moved.GetProperty("cursor").Int("y") != yPos)
            throw new IOException("The Windows sign-in pointer did not move.");
        RemoteClient.Require(await remote.CallAsync("ui.input", new { kind = "keyDown", virtualKey = 16 }, stop.Token));
        await Task.Delay(4500, stop.Token);
        RemoteClient.Require(await remote.CallAsync("ui.input", new { kind = "keyUp", virtualKey = 16 }, stop.Token));
        foreach (string kind in new[] { "keyDown", "keyUp" })
            RemoteClient.Require(await remote.CallAsync("ui.input", new { kind, virtualKey = 13 }, stop.Token));
        await Task.Delay(1500, stop.Token);
        var afterKey = RemoteClient.Require(await remote.CallAsync("screenshot", new { quality = 90 }, stop.Token)).Deserialize<ScreenFrame>(Json.Options)!;
        await File.WriteAllBytesAsync(Path.Combine(output, "unattended-sign-in-after-enter.jpg"), Convert.FromBase64String(afterKey.Data), stop.Token);
        if (afterKey.Data == frame.Data) throw new IOException("Enter did not reveal Windows sign-in controls.");
        RemoteClient.Require(await remote.CallAsync("ui.input", new { kind = "text", text = "rd-input-probe" }, stop.Token));
        await Task.Delay(500, stop.Token);
        var afterText = RemoteClient.Require(await remote.CallAsync("screenshot", new { quality = 90 }, stop.Token)).Deserialize<ScreenFrame>(Json.Options)!;
        await File.WriteAllBytesAsync(Path.Combine(output, "unattended-sign-in-typed.jpg"), Convert.FromBase64String(afterText.Data), stop.Token);
        foreach (var key in new[] { ("keyDown", 17), ("keyDown", 65), ("keyUp", 65), ("keyUp", 17), ("keyDown", 8), ("keyUp", 8) })
            RemoteClient.Require(await remote.CallAsync("ui.input", new { kind = key.Item1, virtualKey = key.Item2 }, stop.Token));
        if (afterText.Data == afterKey.Data) throw new IOException("Windows sign-in did not display the test keyboard input.");
        Pass("unattended.sign_in_input", "The real sign-in screen is nonblank and receives pointer and keyboard input");

        // Keep the session active until the broker performs its declared Windows sign-in.
        await WaitWorkflowAsync(async () =>
        {
            try
            {
                await remote.HeartbeatAsync(stop.Token);
                var state = RemoteClient.Require(await remote.CallAsync("status", ct: stop.Token));
                return !state.GetProperty("signedOut").GetBoolean() && !state.GetProperty("elevated").GetBoolean();
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                await File.WriteAllTextAsync(Path.Combine(output, "unattended-handoff-error.txt"), ex.GetType().Name + ": " + ex.Message, stop.Token);
                return false;
            }
        }, 240);
        var returned = await PowerMessageAsync("POWER_STATUS", 120);
        if (returned.Str("bootId") == initial.Str("bootId")) throw new IOException("The test did not cross a real reboot.");
        await ProbeUnattendedInteractiveInputAsync(remote);
        Pass("unattended.handoff", "The same authenticated connection returns to the unelevated desktop agent after Windows sign-in");
        _ = await PowerMessageAsync("POWER_DONE");
        await FinishAsync();
    }

    private async Task ProbeUnattendedInteractiveInputAsync(RemoteClient remote)
    {
        var field = await PowerMessageAsync("POWER_INPUT_CLEAR");
        foreach (string kind in new[] { "down", "up" })
            RemoteClient.Require(await remote.SendInputAsync(new { kind, x = field.Int("x"), y = field.Int("y"), layoutId = field.Str("layoutId"), button = "left" }, stop.Token));
        RemoteClient.Require(await remote.SendInputAsync(new { kind = "text", text = "desktop input retained" }, stop.Token));
        string observed = "";
        try
        {
            await WaitWorkflowAsync(async () => (observed = (await PowerMessageAsync("POWER_INPUT_READ")).Str("text")) == "desktop input retained", 10);
        }
        catch (TimeoutException ex)
        {
            var frame = RemoteClient.Require(await remote.CallAsync("screenshot", ct: stop.Token)).Deserialize<ScreenFrame>(Json.Options)!;
            await File.WriteAllBytesAsync(Path.Combine(output, "unattended-desktop-input-failure.jpg"), Convert.FromBase64String(frame.Data), stop.Token);
            throw new IOException("Desktop input readback was: " + JsonSerializer.Serialize(observed), ex);
        }
    }
}
