using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using RemoteDebugger;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal static class Program
{
    [STAThread] public static void Main(string[] args) { Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2); Forms.Application.EnableVisualStyles(); Forms.Application.Run(new LabForm(args[0], args[1])); }
}
internal sealed class LabForm : Forms.Form
{
    private readonly string role, output, application;
    private readonly Forms.TextBox log = new() { Dock = Forms.DockStyle.Fill, Multiline = true, ScrollBars = Forms.ScrollBars.Vertical, ReadOnly = true };
    private readonly List<object> steps = [];
    private readonly CancellationTokenSource stop = new(TimeSpan.FromMinutes(12));
    private Process? app;
    private int serial;
    private bool IsLoopback => role is "local" or "ui-local";
    public LabForm(string role, string output)
    {
        this.role = role; this.output = output; Directory.CreateDirectory(output);
        application = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../release/RemoteDebugger.exe"));
        Text = "Remote Debugger Lab — " + role; Width = 820; Height = 480; Controls.Add(log);
        Shown += async (_, _) => { try { if (role is "agent" or "agent-local" or "agent-observe") await AgentAsync(); else { if (IsLoopback) { var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false }; psi.ArgumentList.Add("agent-local"); psi.ArgumentList.Add(Path.Combine(output, "local-agent")); Process.Start(psi); await Task.Delay(3000); } await ControllerAsync(); } } catch (Exception ex) { Step("fatal", false, new { error = ex.ToString() }); await FinishAsync(false); } };
        FormClosed += (_, _) => stop.Cancel();
    }
    private void Step(string name, bool passed, object? data = null)
    {
        if (InvokeRequired) { Invoke(() => Step(name, passed, data)); return; }
        steps.Add(new { name, passed, utc = DateTimeOffset.UtcNow, data }); log.AppendText(name + ": " + (passed ? "PASS" : "FAIL") + Environment.NewLine);
        File.WriteAllText(Path.Combine(output, "progress.json"), Json.Text(new { role, steps }));
        if (!passed && name != "fatal") throw new InvalidOperationException("Assertion failed: " + name + " " + Json.Text(data));
    }
    private async Task FinishAsync(bool passed)
    {
        await using var release = File.OpenRead(application);
        string marker = Path.Combine(output, "lab-result.json");
        await File.WriteAllTextAsync(marker + ".tmp", Json.Text(new { passed, role, machine = Environment.MachineName, steps, appReleaseSha256 = Convert.ToHexString(await SHA256.HashDataAsync(release)) })); File.Move(marker + ".tmp", marker, true);
    }
    private Process Launch(params string[] args)
    {
        var psi = new ProcessStartInfo(application) { UseShellExecute = false }; foreach (string arg in args) psi.ArgumentList.Add(arg); return Process.Start(psi)!;
    }
    private AutomationElement Root()
    {
        app!.Refresh(); if (app.MainWindowHandle == IntPtr.Zero) throw new InvalidOperationException("App has no main window yet."); return AutomationElement.FromHandle(app.MainWindowHandle);
    }
    private async Task WaitUiAsync()
    {
        for (int n = 0; n < 120; n++) { app!.Refresh(); if (app.HasExited) throw new IOException("Release process exited during startup: " + app.ExitCode); if (app.MainWindowHandle != IntPtr.Zero) { await Task.Delay(1000); return; } await Task.Delay(250, stop.Token); } throw new TimeoutException("No release window.");
    }
    private AutomationElement Element(string id)
    {
        for (int attempt = 0; attempt < 20; attempt++) { var element = Root().FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id)); if (element != null) return element; Thread.Sleep(100); }
        throw new InvalidOperationException("Missing UI id " + id + " | " + string.Join(", ", Root().FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().Select(e => e.Current.AutomationId + ":" + e.Current.Name).Take(80)));
    }
    private AutomationElement Named(string name) => Root().FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name)) ?? throw new InvalidOperationException("Missing UI name " + name);
    private new void Click(string id) => ((InvokePattern)Element(id).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    private void Set(string id, string value) => ((ValuePattern)Element(id).GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);
    private void SelectTab(string name)
    {
        Native.FocusWindow(app!.Id);
        var tab = Root().FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.NameProperty, name), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))) ?? throw new InvalidOperationException("Missing navigation button " + name);
        var tabInventory = Root().FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>().Select(t => new { name = t.Current.Name, bounds = t.Current.BoundingRectangle.ToString() }).ToArray();
        File.WriteAllText(Path.Combine(output, "tab-inventory.json"), Json.Text(tabInventory));
        ((InvokePattern)tab.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }
    private string[] UiTexts() => Root().FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().Select(e => e.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && !e.Current.IsPassword ? ((ValuePattern)value).Current.Value : e.Current.Name).ToArray();
    private async Task WaitForUiAsync(Func<bool> ready)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(30)) { if (ready()) return; await Task.Delay(250, stop.Token); }
        throw new TimeoutException("The expected UI result did not appear: " + string.Join(" | ", UiTexts().Take(30)));
    }
    private async Task AgentAsync()
    {
        app = Launch("--agent", "--data-root", Path.Combine(output, "agent-data"), role == "agent-local" ? "--loopback-only" : "--lan"); await WaitUiAsync(); WindowState = Forms.FormWindowState.Minimized;
        Click("openPairing"); Step("release_agent_visible", true);
        if (role != "agent-local") await DismissFirewallPromptAsync();
        if (role == "agent-observe") { Click("revokeAccess"); Step("observe_agent_desktop", true, new { elevated = Native.IsElevated() }); await FinishAsync(true); return; }
        using var udp = new UdpClient(new IPEndPoint(role == "agent-local" ? IPAddress.Loopback : IPAddress.Any, 45835));
        while (!stop.IsCancellationRequested)
        {
            var r = await udp.ReceiveAsync(stop.Token); string request = Encoding.UTF8.GetString(r.Buffer); object response;
            if (request == "RD_LAB_BOOTSTRAP")
            {
                // Lab-only OOB operator simulation; this endpoint is absent from the product.
                string all = string.Join("\n", UiTexts()); var fp = Regex.Match(all, "[A-F0-9]{64}").Value; var code = Regex.Match(all, "Code temporaire : ([0-9]{8})").Groups[1].Value;
                response = new { fingerprint = fp, code, machine = Environment.MachineName };
            }
            else if (request == "RD_LAB_RESTART")
            {
                Click("agentStart"); await Task.Delay(600); Click("agentStart"); await Task.Delay(700); Step("agent_local_disconnect_reconnect", true); response = new { restarted = true };
            }
            else if (request == "RD_LAB_REOPEN") { Click("openPairing"); response = new { reopened = true }; }
            else if (request == "RD_LAB_DONE")
            {
                Step("remote_controller_completed", true); response = new { completed = true }; await udp.SendAsync(Encoding.UTF8.GetBytes(Json.Text(response)), r.RemoteEndPoint, stop.Token); await FinishAsync(true); return;
            }
            else continue;
            await udp.SendAsync(Encoding.UTF8.GetBytes(Json.Text(response)), r.RemoteEndPoint, stop.Token);
        }
    }
    private async Task DismissFirewallPromptAsync()
    {
        var seen = new HashSet<string>();
        for (int attempt = 0; attempt < 100; attempt++)
        {
            foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
            {
                try
                {
                    string description = window.Current.ProcessId + " " + window.Current.ClassName + " " + window.Current.Name;
                    if (seen.Add(description)) File.AppendAllText(Path.Combine(output, "window-inventory.txt"), description + Environment.NewLine);
                    if (!(window.Current.Name.Contains("curité") || window.Current.Name.Contains("Security") || window.Current.Name.Contains("pare-feu") || window.Current.Name.Contains("Firewall"))) continue;
                    string text = string.Join(" ", window.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().Select(e => e.Current.Name));
                    if (!text.Contains("RemoteDebugger") || !(text.Contains("pare-feu") || text.Contains("firewall", StringComparison.OrdinalIgnoreCase))) continue;
                    var cancel = window.FindFirst(TreeScope.Descendants, new OrCondition(new PropertyCondition(AutomationElement.NameProperty, "Annuler"), new PropertyCondition(AutomationElement.NameProperty, "Cancel")));
                    if (cancel != null && cancel.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) { ((InvokePattern)invoke).Invoke(); Step("local_firewall_prompt_cancelled_no_network_permission_added", true); return; }
                }
                catch (ElementNotAvailableException) { }
            }
            if (attempt % 10 == 0) { File.AppendAllText(Path.Combine(output, "desktop-state.jsonl"), Json.Text(DesktopCapture.State()) + Environment.NewLine); File.WriteAllText(Path.Combine(output, "native-windows.json"), Json.Text(Native.NativeWindows())); }
            await Task.Delay(250);
        }
    }
    private async Task<JsonElement> LabMessageAsync(string host, string text)
    {
        using var udp = new UdpClient { EnableBroadcast = true }; using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(5000);
        await udp.SendAsync(Encoding.UTF8.GetBytes(text), new IPEndPoint(IPAddress.Parse(host), 45835), timeout.Token);
        var response = await udp.ReceiveAsync(timeout.Token); return JsonSerializer.Deserialize<JsonElement>(response.Buffer);
    }
    private async Task<JsonElement> CliAsync(string[] args, string? stdin = null, bool requireSuccess = true)
    {
        var psi = new ProcessStartInfo(application) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true }; psi.ArgumentList.Add("cli"); foreach (string arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!; if (stdin != null) await p.StandardInput.WriteLineAsync(stdin); p.StandardInput.Close(); var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(stop.Token); string text = await stdout; string err = await stderr;
        if (text.Trim().Length == 0) throw new IOException("CLI produced no JSON: " + err);
        var result = JsonSerializer.Deserialize<JsonElement>(text);
        if (requireSuccess && p.ExitCode != 0) throw new IOException("CLI failed " + Json.Text(args) + ": " + text + err); return result;
    }
    private async Task<JsonElement> CallAsync(string operation, object? args = null, bool requireSuccess = true, int seconds = 60, string? id = null)
    {
        string file = Path.Combine(output, "request-" + ++serial + ".json"); await File.WriteAllTextAsync(file, Json.Text(new { operation, args = args ?? new { }, timeoutSeconds = seconds, id = id ?? Guid.NewGuid().ToString() }));
        var result = await CliAsync(["call", "--request", file], requireSuccess: requireSuccess);
        File.Delete(file); return result;
    }
    private static JsonElement Data(JsonElement reply) => reply.GetProperty("data");
    private async Task ControllerAsync()
    {
        app = Launch(); await WaitUiAsync(); WindowState = Forms.FormWindowState.Minimized;
        JsonElement? peer = IsLoopback ? Json.Element(new { host = "127.0.0.1" }) : null;
        for (int n = 0; n < 60 && peer == null; n++)
        {
            var discovery = await CliAsync(["discover"]); var list = discovery.GetProperty("peers"); if (list.GetArrayLength() > 0) peer = list[0]; else await Task.Delay(1500, stop.Token);
        }
        if (peer == null) throw new TimeoutException("No second-machine agent discovered.");
        string host = peer.Value.Str("host"); JsonElement bootstrap = default;
        for (int retry = 0; retry < 30; retry++)
        {
            try { bootstrap = await LabMessageAsync(host, "RD_LAB_BOOTSTRAP"); break; }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !stop.IsCancellationRequested) { await Task.Delay(500, stop.Token); }
        }
        if (bootstrap.ValueKind == JsonValueKind.Undefined) throw new TimeoutException("Lab agent bootstrap is not ready.");
        var localIps = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address.ToString()).ToArray();
        Step(IsLoopback ? "single_vm_loopback_only_NOT_network_acceptance" : "discovery_remote_network_endpoint", IsLoopback || (!localIps.Contains(host) && !IPAddress.IsLoopback(IPAddress.Parse(host))), new { controller = Environment.MachineName, agent = bootstrap.Str("machine"), host, localIps });
        // The GUI's real pairing controls establish the same DPAPI connection used by CLI.
        if (bootstrap.Str("code").Length != 8 || bootstrap.Str("fingerprint").Length != 64) throw new IOException("Lab could not read pairing identity from real agent UI.");
        Set("host", host); Set("fingerprint", bootstrap.Str("fingerprint")); Set("pairCode", bootstrap.Str("code")); ((TogglePattern)Element("fingerprintVerified").GetCurrentPattern(TogglePattern.Pattern)).Toggle(); Click("pair");
        for (int attempt = 0; attempt < 40 && !File.Exists(RemoteClient.DefaultPath); attempt++) await Task.Delay(250);
        if (!File.Exists(RemoteClient.DefaultPath)) throw new IOException("GUI pairing did not save a connection: " + string.Join(" | ", UiTexts().Where(t => t.StartsWith("Échec") || t.StartsWith("{\n"))));
        var status = Data(await CallAsync("status")); string workspace = status.Str("workspace");
        Step("gui_pairing_then_cli_status", status.Str("machine") == bootstrap.Str("machine"));
        Click("status"); await Task.Delay(700);
        var maintenance = Data(await CallAsync("maintenance.status")); Step("admin_session_disabled_until_local_consent", !maintenance.GetProperty("active").GetBoolean());
        var noConsent = await CallAsync("maintenance.session", new { file = "whoami.exe", arguments = Array.Empty<string>() }, false); Step("admin_command_without_local_session_denied", !noConsent.GetProperty("ok").GetBoolean());
        if (role == "ui-local")
        {
            SelectTab("Processus & système"); await Task.Delay(300); Click("refreshResources"); await WaitForUiAsync(() => Element("processList").FindAll(TreeScope.Descendants, Condition.TrueCondition).Count > 10); Step("gui_process_list_populated", true);
            await File.WriteAllBytesAsync(Path.Combine(output, "pilot-resources.jpg"), Convert.FromBase64String(DesktopCapture.Capture(0, 1920, 85).Data));
            SelectTab("Écran distant"); await Task.Delay(300); Click("liveStream"); await WaitForUiAsync(() => UiTexts().Any(t => t.Contains("Mbit/s"))); await Task.Delay(8000); Step("gui_live_stream_rendered", UiTexts().Any(t => t.Contains("i/s") && t.Contains("Mbit/s")), new { status = UiTexts().FirstOrDefault(t => t.Contains("Mbit/s")) });
            await File.WriteAllBytesAsync(Path.Combine(output, "pilot-live.jpg"), Convert.FromBase64String(DesktopCapture.Capture(0, 1920, 85).Data)); Click("liveStream");
            await LabMessageAsync(host, "RD_LAB_DONE"); await FinishAsync(true); return;
        }
        // Failed token and pin tests use a separate controller profile; product CLI still owns transport.
        string invalid = Path.Combine(output, "invalid.connection"); new RemoteClient(new Connection(host, 45832, bootstrap.Str("fingerprint"), "invalid-fixture-token")).Save(invalid);
        string req = Path.Combine(output, "unauthorized.json"); await File.WriteAllTextAsync(req, Json.Text(new { operation = "status", args = new { } }));
        var denied = await CliAsync(["call", "--request", req, "--connection", invalid], requireSuccess: false); Step("unpaired_controller_denied", denied.Str("error") == "access_denied");
        new RemoteClient(new Connection(host, 45832, new string('0', 64), "invalid")).Save(invalid);
        var pin = await CliAsync(["call", "--request", req, "--connection", invalid], requireSuccess: false); Step("wrong_certificate_pin_denied", pin.Str("error") == "transport_or_input"); File.Delete(invalid); File.Delete(req);
        var versionHashes = new List<string>(); int target = 0;
        for (int version = 1; version <= 2; version++)
        {
            string source = Path.Combine(AppContext.BaseDirectory, "fixtures", "v" + version, "Fixture.exe"), relative = "deployments/fixture/v" + version + "/Fixture.exe";
            var deployment = Data(await CliAsync(["upload", "--file", source, "--path", relative])); versionHashes.Add(deployment.Str("sha256"));
            var launch = Data(await CallAsync("start", new { path = relative, arguments = new[] { Path.Combine(workspace, "fixture-output-v" + version) } })); target = launch.Int("pid"); await Task.Delay(1600);
            var actual = Data(await CallAsync("process.info", new { pid = target })); Step("deployed_and_running_v" + version, actual.GetProperty("binary").Str("sha256") == deployment.Str("sha256") && actual.GetProperty("binary").Str("fileVersion") == version + ".0.0.0", new { pid = target, binary = actual.GetProperty("binary") });
            var tree = Data(await CallAsync("ui.inspect", new { pid = target })); Step("fixture_controls_visible_v" + version, tree.EnumerateArray().Any(e => e.Str("automationId") == "saveButton"));
            if (version == 1) await CliAsync(["screenshot", "--file", Path.Combine(output, "before-input.jpg")]);
            await CallAsync("ui.text", new { pid = target, text = "Remote round trip v" + version }); await CallAsync("ui.click", new { pid = target, automationId = "saveButton" }); await Task.Delay(400);
            string result = Path.Combine(output, "fixture-result-v" + version + ".json"); await CliAsync(["download", "--path", Path.Combine(workspace, "fixture-output-v" + version, "result.json"), "--file", result]);
            var saved = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(result)); Step("interaction_and_download_v" + version, saved.Str("text") == "Remote round trip v" + version && saved.Str("version") == version + ".0.0.0");
            string logPath = Path.Combine(output, "fixture-v" + version + ".log"); await CliAsync(["download", "--path", Path.Combine(workspace, "fixture-output-v" + version, "fixture.log"), "--file", logPath]); Step("log_collection_v" + version, (await File.ReadAllTextAsync(logPath)).Contains("saved " + version + ".0.0.0"));
            var screenshot = await CliAsync(["screenshot", "--file", Path.Combine(output, "remote-v" + version + ".jpg")]); Step("fresh_capture_v" + version, screenshot.GetProperty("roundTripMs").GetDouble() > 0, screenshot);
            if (version == 1) await CallAsync("stop", new { pid = target });
        }
        Step("version_replaced_with_different_binary", versionHashes.Distinct().Count() == 2);
        var debug = Data(await CallAsync("debug.attach", new { pid = target, seconds = 3 })); Step("real_debugger_attach_breakpoint_detach", debug.GetProperty("attached").GetBoolean() && debug.GetProperty("breakpointObserved").GetBoolean() && debug.GetProperty("detached").GetBoolean(), debug);
        var dump = Data(await CallAsync("debug.dump", new { pid = target })); Step("minidump_written", dump.Long("size") > 1024, new { size = dump.Long("size"), path = dump.Str("path") });
        var processes = Data(await CallAsync("processes")); var sample = processes.GetProperty("processes").EnumerateArray().First(p => p.Int("pid") == target); Step("process_cpu_memory_sample", sample.GetProperty("cpuPercentTotalMachine").ValueKind == JsonValueKind.Number && sample.Long("workingSetBytes") > 0, new { intervalMs = processes.GetProperty("intervalMs"), sample });
        var system = Data(await CallAsync("system")); Step("system_cpu_ram_volumes", system.GetProperty("cpuPercentTotalMachine").ValueKind == JsonValueKind.Number && system.GetProperty("volumes").GetArrayLength() > 0, system);
        foreach (string operation in new[] { "network", "services", "events" }) { var value = Data(await CallAsync(operation)); Step("diagnostic_" + operation, value.Int("exitCode") == 0, new { stdoutCharacters = value.Str("stdout").Length }); }
        var files = Data(await CallAsync("files", new { path = "deployments/fixture" })); Step("browse_remote_files", files.GetArrayLength() == 2);
        var command = Data(await CallAsync("command", new { file = "cmd.exe", arguments = new[] { "/d", "/c", "echo maintenance-fixture-ok" } })); Step("maintenance_command", command.Int("exitCode") == 0 && command.Str("stdout").Contains("maintenance-fixture-ok"));
        // Actual CLI stream plus simultaneous actual CLI mouse/key input.
        var streaming = CliAsync(["stream", "--seconds", "12", "--fps", "5", "--report", Path.Combine(output, "stream-metrics.json"), "--last-frame", Path.Combine(output, "stream-last.jpg")]); await Task.Delay(2000);
        await CallAsync("ui.key", new { pid = target, key = "TAB" }); await CallAsync("ui.key", new { pid = target, key = "TAB" });
        var fresh = Data(await CallAsync("screenshot")); string layout = fresh.GetProperty("geometry").Str("layoutId");
        var stale = await CallAsync("ui.input", new { kind = "move", x = 1, y = 1, layoutId = "stale" }, false); Step("stale_display_geometry_rejected", !stale.GetProperty("ok").GetBoolean());
        await CallAsync("ui.input", new { kind = "move", x = fresh.GetProperty("geometry").Int("x") + 100, y = fresh.GetProperty("geometry").Int("y") + 100, layoutId = layout });
        var streamLoad = Data(await CallAsync("processes")); var agentLoad = streamLoad.GetProperty("processes").EnumerateArray().FirstOrDefault(p => p.Int("pid") == status.Int("processId"));
        var stream = await streaming; Step("continuous_stream_with_concurrent_input", stream.Int("frames") >= 10 && stream.Int("distinctFrames") >= 5, new { metrics = stream, requestedTargetFps = 5, fluidityTargetMet = stream.GetProperty("receivedFps").GetDouble() >= 4.5, agentLoad });
        // Human pilot renders the continuous stream itself; harness screenshots retain its actual window.
        SelectTab("Processus & système"); await Task.Delay(300); Click("refreshResources"); await WaitForUiAsync(() => Element("processList").FindAll(TreeScope.Descendants, Condition.TrueCondition).Count > 10); Step("gui_process_list_populated", UiTexts().Contains("Fixture"));
        var resourceFrame = DesktopCapture.Capture(0, 1920, 85); await File.WriteAllBytesAsync(Path.Combine(output, "pilot-resources.jpg"), Convert.FromBase64String(resourceFrame.Data));
        SelectTab("Écran distant"); await Task.Delay(300); Click("liveStream"); await WaitForUiAsync(() => UiTexts().Any(t => t.Contains("Mbit/s"))); await Task.Delay(8000); Step("gui_live_stream_rendered", UiTexts().Any(t => t.Contains("i/s") && t.Contains("Mbit/s")), new { status = UiTexts().FirstOrDefault(t => t.Contains("Mbit/s")) });
        if (role != "local")
        {
            var controls = Data(await CallAsync("ui.inspect", new { pid = target })); var inputControl = controls.EnumerateArray().First(c => c.Str("automationId") == "fixtureInput").GetProperty("bounds");
            var picture = Element("remoteScreen").Current.BoundingRectangle; var geo = fresh.GetProperty("geometry"); double scale = Math.Min(picture.Width / geo.Int("width"), picture.Height / geo.Int("height"));
            double x = picture.X + (picture.Width - geo.Int("width") * scale) / 2 + (inputControl.GetProperty("x").GetDouble() + 20 - geo.Int("x")) * scale;
            double y = picture.Y + (picture.Height - geo.Int("height") * scale) / 2 + (inputControl.GetProperty("y").GetDouble() + 10 - geo.Int("y")) * scale;
            ((TogglePattern)Named("Souris et clavier distants").GetCurrentPattern(TogglePattern.Pattern)).Toggle(); Native.Mouse(app!.Id, (int)x, (int)y); await Task.Delay(500);
            Native.Key(app.Id, "CTRL+A"); Native.Key(app.Id, "G"); Native.Key(app.Id, "U"); Native.Key(app.Id, "I"); Native.Key(app.Id, "ENTER"); await Task.Delay(1200);
            string guiResult = Path.Combine(output, "gui-input-result.json"); await CliAsync(["download", "--path", Path.Combine(workspace, "fixture-output-v2/result.json"), "--file", guiResult]);
            Step("human_gui_mouse_keyboard_through_live_pilot", JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(guiResult)).Str("text").Equals("gui", StringComparison.OrdinalIgnoreCase));
        }
        var pilotFrame = DesktopCapture.Capture(0, 1920, 85); await File.WriteAllBytesAsync(Path.Combine(output, "pilot-live.jpg"), Convert.FromBase64String(pilotFrame.Data)); Click("liveStream");
        // Deadline and cancellation semantics. Timeout kills only the test command process tree.
        var timeout = await CallAsync("command", new { file = "ping.exe", arguments = new[] { "-n", "20", "127.0.0.1" } }, false, 1); Step("command_deadline", timeout.Str("error") == "cancelled_or_timeout");
        string cancelId = Guid.NewGuid().ToString(); var cancellable = CallAsync("command", new { file = "ping.exe", arguments = new[] { "-n", "20", "127.0.0.1" } }, false, 30, cancelId); await Task.Delay(800); await CallAsync("cancel", new { id = cancelId }); var cancelled = await cancellable; Step("explicit_command_cancellation", cancelled.Str("error") == "cancelled_or_timeout");
        string transfer = Guid.NewGuid().ToString("N"); byte[] resumedData = Encoding.UTF8.GetBytes("first block / second block"); string resumeHash = Convert.ToHexString(SHA256.HashData(resumedData));
        await CallAsync("upload.begin", new { transfer, path = "resume-proof.txt", size = resumedData.Length, sha256 = resumeHash }); await CallAsync("upload.chunk", new { transfer, offset = 0, data = Convert.ToBase64String(resumedData[..10]) });
        await LabMessageAsync(host, "RD_LAB_RESTART"); await Task.Delay(1000); Step("reconnect_persisted_pairing", Data(await CallAsync("status")).Str("machine") == bootstrap.Str("machine"));
        Step("upload_offset_survives_agent_restart", Data(await CallAsync("upload.status", new { transfer })).Long("offset") == 10); await CallAsync("upload.chunk", new { transfer, offset = 10, data = Convert.ToBase64String(resumedData[10..]) }); var resumed = Data(await CallAsync("upload.commit", new { transfer })); Step("resumed_upload_hash_matches", resumed.Str("sha256") == resumeHash);
        var traversal = await CallAsync("upload.begin", new { transfer = Guid.NewGuid().ToString("N"), path = "../escape.txt", size = 0, sha256 = new string('0', 64) }, false); Step("remote_upload_traversal_denied", !traversal.GetProperty("ok").GetBoolean());
        string restartId = Guid.NewGuid().ToString(); var replay1 = Data(await CallAsync("start", new { path = "deployments/fixture/v1/Fixture.exe", arguments = new[] { Path.Combine(workspace, "replay-output") } }, id: restartId)); var replay2 = Data(await CallAsync("start", new { path = "deployments/fixture/v1/Fixture.exe", arguments = new[] { Path.Combine(workspace, "replay-output") } }, id: restartId)); Step("uncertain_start_replay_does_not_duplicate", replay1.Int("pid") == replay2.Int("pid")); await Task.Delay(700); var restarted = Data(await CallAsync("restart", new { pid = replay1.Int("pid"), arguments = new[] { Path.Combine(workspace, "replay-output") } })); Step("application_restart_new_pid", restarted.Int("pid") != replay1.Int("pid")); await Task.Delay(700); await CallAsync("stop", new { pid = restarted.Int("pid") });
        var hang = CallAsync("ui.click", new { pid = target, automationId = "hangButton" }, false, 25); await Task.Delay(6000); var hung = Data(await CallAsync("process.info", new { pid = target })); Step("deliberate_hang_detected", !hung.GetProperty("responding").GetBoolean()); await hang; await Task.Delay(16000); Step("hung_application_recovers", Data(await CallAsync("process.info", new { pid = target })).GetProperty("responding").GetBoolean());
        int crashedPid = target; await CallAsync("ui.click", new { pid = target, automationId = "crashButton" }, false, 20); await Task.Delay(1500);
        var afterCrash = Data(await CallAsync("processes")); Step("deliberate_crash_process_absent", !afterCrash.GetProperty("processes").EnumerateArray().Any(p => p.Int("pid") == crashedPid));
        target = Data(await CallAsync("start", new { path = "deployments/fixture/v2/Fixture.exe", arguments = new[] { Path.Combine(workspace, "fixture-output-v2") } })).Int("pid"); await Task.Delay(1200);
        Step("application_relaunched_after_crash", target != crashedPid && Data(await CallAsync("process.info", new { pid = target })).GetProperty("responding").GetBoolean());
        var history = Data(await CallAsync("history")); Step("intervention_history", history.GetArrayLength() > 10 && !history.GetRawText().Contains("Remote round trip") && !history.GetRawText().Contains("token"));
        await CallAsync("stop", new { pid = target }); await CallAsync("revoke"); var revoked = await CallAsync("status", requireSuccess: false); Step("revocation_denies_previous_controller", revoked.Str("error") == "access_denied");
        await LabMessageAsync(host, "RD_LAB_DONE"); await FinishAsync(true);
    }
}
