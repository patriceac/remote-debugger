using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

// This scenario runs only inside two disposable, broker-connected guests. Native
// clipboard and Explorer interactions are deliberately absent from host unit tests.
internal sealed partial class LabForm
{
    private async Task WorkflowAgentAsync()
    {
        product = LaunchProduct(true); await WaitUiAsync();
        string code = await WaitPairingCodeAsync();
        string files = Path.Combine(output, "remote-files");
        Directory.CreateDirectory(files);
        MakeTransferFixture(files, "remote-tree"); MakeTransferFixture(files, "remote-viewer-tree");
        Forms.Clipboard.SetText("remote-before-session");
        WindowState = Forms.FormWindowState.Minimized;
        using var animation = new Forms.Form { Bounds = new(70, 60, 620, 380), TopMost = true, Text = "Viewer cadence fixture" };
        int tick = 0;
        animation.Paint += (_, e) => { e.Graphics.Clear(Color.DarkSlateBlue); e.Graphics.FillRectangle(Brushes.White, tick % 450, 80, 90, 90); };
        using var animationTimer = new Forms.Timer { Interval = 20 };
        animationTimer.Tick += (_, _) => { tick += 7; animation.Invalidate(); };
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, CoordinationPort));
        while (!stop.IsCancellationRequested)
        {
            var received = await udp.ReceiveAsync(stop.Token);
            string request = Encoding.UTF8.GetString(received.Buffer);
            object response;
            try
            {
                if (request == "RD_LAB_BOOTSTRAP") response = new { code, files, binarySha256 = await HashFileAsync(application) };
                else if (request.StartsWith("CLIP:", StringComparison.Ordinal)) { Forms.Clipboard.SetText(request[5..]); response = new { written = true }; }
                else if (request == "CLIP_HASH") response = new { hash = Safety.Hash(Forms.Clipboard.ContainsText() ? Forms.Clipboard.GetText() : "") };
                else if (request == "ANIMATE") { animation.Show(); animationTimer.Start(); response = new { running = true }; }
                else if (request == "EXPLORER")
                {
                    animationTimer.Stop(); animation.Hide();
                    var explorer = await OpenWorkflowExplorerAsync(files, new(50, 40, 1100, 740));
                    var item = ExplorerItem(explorer, "remote-viewer-tree").Current.BoundingRectangle;
                    response = new { x = (int)(item.Left + item.Width / 2), y = (int)(item.Top + item.Height / 2), dropX = 900, dropY = 650 };
                }
                else if (request == "VERIFY")
                {
                    RequireFixture(files, "local-tree"); RequireFixture(files, "local-viewer-tree");
                    RequireFixture(files, "remote-tree"); RequireFixture(files, "remote-viewer-tree");
                    response = new { verified = true };
                }
                else if (request == "DONE")
                {
                    Pass("workflow.agent", "The real agent served clipboard and Explorer transfers on an isolated peer desktop");
                    await SendUdpAsync(udp, new { done = true }, received.RemoteEndPoint);
                    await FinishAsync(); return;
                }
                else continue;
            }
            catch (Exception ex) { response = new { error = ex.ToString() }; }
            await SendUdpAsync(udp, response, received.RemoteEndPoint);
        }
    }

    private async Task WorkflowControllerAsync()
    {
        product = LaunchProduct(false); await WaitUiAsync();
        WindowState = Forms.FormWindowState.Minimized;
        var localAddresses = ProvisioningEvidence.LocalIPv4Addresses();
        var peer = (await DiscoverAsync(180)).FirstOrDefault(p => ProvisioningEvidence.IsRemoteCoordinationAddress(p.Host, localAddresses, out _))
            ?? throw new IOException("The support-workflow peer was not discovered.");
        peerHost = peer.Host;
        var bootstrap = await LabMessageAsync(peerHost, "RD_LAB_BOOTSTRAP");
        Forms.Clipboard.SetText("controller-before-session");
        Set("host", peerHost); Set("pairCode", bootstrap.Str("code")); FocusAndEnter("pairCode");
        if (!await WaitForTextAsync("connectionStatus", IsConnected, 60)) throw new IOException("Workflow pairing failed.");
        await WaitForLiveEvidenceAsync(45); await Task.Delay(1500, stop.Token);
        var remote = new RemoteClient(RemoteClient.Load().Connection, await HashFileAsync(application));
        try
        {
            if (Forms.Clipboard.GetText() != "controller-before-session" ||
                (await LabMessageAsync(peerHost, "CLIP_HASH")).Str("hash") != Safety.Hash("remote-before-session"))
                throw new IOException("Pre-session clipboard contents were replayed.");
            Forms.Clipboard.SetText("controller-connected-event");
            await WaitWorkflowAsync(async () => (await LabMessageAsync(peerHost, "CLIP_HASH")).Str("hash") == Safety.Hash("controller-connected-event"));
            await LabMessageAsync(peerHost, "CLIP:remote-connected-event");
            await WaitWorkflowAsync(() => Task.FromResult(Forms.Clipboard.GetText() == "remote-connected-event"));
            Pass("workflow.clipboard", "Only new connected clipboard events cross between the two real desktops in both directions");

            // Disable/re-enable creates a fresh baseline and discards intervening events.
            var share = Find("shareClipboard", 5000) ?? throw new IOException("Clipboard sharing control missing.");
            ((TogglePattern)share.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
            await Task.Delay(600, stop.Token);
            await LabMessageAsync(peerHost, "CLIP:remote-paused-event"); Forms.Clipboard.SetText("controller-paused-event");
            ((TogglePattern)share.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
            await Task.Delay(1500, stop.Token);
            if (Forms.Clipboard.GetText() != "controller-paused-event") throw new IOException("Paused clipboard event was replayed.");
            Pass("workflow.clipboard_pause", "Re-enabling clipboard sharing starts a new baseline without replaying paused contents");

            await ProbeWorkflowFrameRateAsync(remote);
            await ProbeWorkflowExplorerAsync(remote, bootstrap.Str("files"));
            await ProbeAuditRegressionsAsync(); // Existing focused bulk cancel/resume and hash assertions.
            await ProbeWorkflowPowerCancellationAsync(remote);
            CaptureDesktop("support-workflow-complete.png");
            await remote.EndSessionAsync(stop.Token);
            await LabMessageAsync(peerHost, "CLIP:after-session-end");
            await Task.Delay(1500, stop.Token);
            if (Forms.Clipboard.GetText() == "after-session-end") throw new IOException("Clipboard continued after session end.");
            Pass("workflow.clipboard_end", "Ending the support session stops native clipboard propagation");
        }
        finally
        {
            await remote.CloseHeartbeatChannelAsync();
            await LabMessageAsync(peerHost, "DONE");
        }
        await ProbeWorkflowReportsAsync();
        await FinishAsync();
    }

    private async Task ProbeWorkflowReportsAsync()
    {
        product!.Kill(); await product.WaitForExitAsync(stop.Token); product.Dispose();
        product = LaunchProduct(false); await WaitUiAsync();
        await WaitWorkflowAsync(() => Task.FromResult(new IncidentLog(productData).Read()
            .Any(report => report.Failure.Name == "previous_process_ended_unexpectedly")));
        string request = Path.Combine(output, "local-reports-request.json");
        await File.WriteAllTextAsync(request, Json.Text(new { operation = "reports" }), stop.Token);
        var result = Data(await CliAsync(["connected", "--request", request, "--data-root", productData,
            "--connection", Path.Combine(output, "missing.connection")]));
        if (!result.GetProperty("reports").EnumerateArray().Any(report => report.Str("name") == "previous_process_ended_unexpectedly"))
            throw new IOException("Automatic incidents are unavailable without a remote connection profile.");
        foreach (string path in Directory.EnumerateFiles(Path.Combine(productData, "support-reports")))
        {
            string contents = await File.ReadAllTextAsync(path, stop.Token);
            if (contents.Contains("controller-connected-event", StringComparison.Ordinal) || contents.Contains("remote-paused-event", StringComparison.Ordinal))
                throw new IOException("Clipboard payload entered support reports.");
        }
        Pass("workflow.local_reports", "An unexpected process exit produces an automatic incident, readable without a remote profile; clipboard payloads are absent");
    }

    private async Task ProbeWorkflowPowerCancellationAsync(RemoteClient remote)
    {
        await WaitWorkflowAsync(async () => RemoteClient.Require(await remote.CallAsync("maintenance.status", ct: stop.Token)).GetProperty("active").GetBoolean(), 90);
        var preflight = RemoteClient.Require(await remote.CallAsync("power.preflight", ct: stop.Token, seconds: 30));
        if (preflight.Str("machine").Length == 0 || preflight.Int("waitSeconds") != 3600) throw new IOException("Incomplete restart preflight.");
        // Baseline guest autologon must be detected and preserved, never replaced.
        if (preflight.GetProperty("oneTimeLoginAvailable").GetBoolean()) throw new IOException("Existing guest autologon was not detected.");
        Pass("workflow.restart_preflight", "Preflight reports the target, one-hour deadline, and unavailable one-use sign-in over existing autologon", preflight);
        var button = Find("shutdownRemotePc", 5000) ?? throw new IOException("Shutdown button missing.");
        InvokeElement(button);
        var confirmation = await WorkflowDialogAsync("#32770");
        string text = string.Join(" ", confirmation.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>().Select(e => e.Current.Name));
        if (!text.Contains(preflight.Str("machine"), StringComparison.OrdinalIgnoreCase)) throw new IOException("Shutdown confirmation omitted the target PC.");
        CaptureDesktop("shutdown-confirmation.png");
        InvokeElement(confirmation.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "2")) ?? throw new IOException("Confirmation cancel missing."));
        await Task.Delay(300, stop.Token);
        InvokeElement(button); confirmation = await WorkflowDialogAsync("#32770");
        InvokeElement(confirmation.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "1")) ?? throw new IOException("Confirmation accept missing."));
        var progress = await WorkflowDialogAsync("powerProgressWindow");
        await Task.Delay(1500, stop.Token);
        CaptureDesktop("shutdown-countdown.png");
        var cancel = progress.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>().First(e => e.Current.IsEnabled);
        InvokeElement(cancel);
        await Task.Delay(11000, stop.Token);
        _ = RemoteClient.Require(await remote.CallAsync("status", ct: stop.Token));
        Pass("workflow.shutdown_cancel", "The controller confirms the named PC and cancels an accepted Windows shutdown before its countdown ends");
    }

    private async Task ProbeWorkflowFrameRateAsync(RemoteClient remote)
    {
        Click("navScreen"); Click("pauseViewing");
        await LabMessageAsync(peerHost!, "ANIMATE");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); limit.CancelAfter(TimeSpan.FromSeconds(12));
        int frames = 0; var watch = new Stopwatch(); string codec = ""; double oldest = 0;
        try
        {
            await remote.StreamAdaptiveAsync(frame =>
            {
                if (!watch.IsRunning) watch.Start(); frames++;
                oldest = Math.Max(oldest, (DateTimeOffset.UtcNow - frame.CapturedUtc).TotalSeconds);
                frame.Dispose(); return Task.CompletedTask;
            }, (value, _) => codec = value, seconds: 20, ct: limit.Token);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
        double fps = frames / Math.Max(1, watch.Elapsed.TotalSeconds);
        if (fps <= 5.5 || oldest > 5) throw new IOException($"Viewer did not exceed the old frame ceiling with fresh frames: {fps:F1} FPS, {oldest:F1}s.");
        Pass("workflow.viewer_fps", "A real remote desktop streams fresh frames above the former 5 FPS ceiling", new { frames, fps, oldest, codec });
        Click("pauseViewing"); await WaitForLiveEvidenceAsync(30);
    }

    private async Task ProbeWorkflowExplorerAsync(RemoteClient remote, string remoteFiles)
    {
        string local = Path.Combine(output, "local-files"), received = Path.Combine(output, "received");
        Directory.CreateDirectory(local); Directory.CreateDirectory(received);
        MakeTransferFixture(local, "local-tree"); MakeTransferFixture(local, "local-viewer-tree");
        var desktop = Forms.Screen.PrimaryScreen!.Bounds;
        if (desktop.Width < 1700) throw new IOException("Explorer drag acceptance needs the configured 1920px guest desktop.");
        _ = MoveWorkflowWindow(product!.MainWindowHandle, 0, 0, 1060, Math.Min(900, desktop.Height - 40), true);
        var explorer = await OpenWorkflowExplorerAsync(local, new(1080, 30, desktop.Width - 1100, 760));
        Click("navFiles"); Set("remoteDirectory", remoteFiles); FocusAndEnter("remoteDirectory");
        await WaitForRowsAsync(Element("remoteFiles"), 2, 20);
        await DragWorkflowAsync(Center(ExplorerItem(explorer, "local-tree")), Center(Element("remoteFiles")), 1400);
        await WaitWorkflowAsync(async () => (await remote.CallAsync("file.info", new { path = Path.Combine(remoteFiles, "local-tree", "nested", "évidence.txt") }, stop.Token)).Ok);
        Pass("workflow.explorer_to_files", "A real Explorer folder drop uploads its nested contents through the Files pane");
        CloseTransferWindow();

        explorer = await OpenWorkflowExplorerAsync(received, new(1080, 30, desktop.Width - 1100, 760));
        var table = Element("remoteFiles");
        var row = table.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem), new PropertyCondition(AutomationElement.NameProperty, "remote-tree")))
            ?? throw new IOException("Remote source folder missing from Files pane.");
        await DragWorkflowAsync(Center(row), new(desktop.Width - 150, 630), 2200);
        await WaitFixtureAsync(received, "remote-tree");
        if (!await WaitForTextAsync("fileTransferDetails", text => text == UiText.DownloadVerified, 10)) throw new IOException("Completed Explorer copy is still shown as unverified.");
        CaptureDesktop("files-to-explorer-complete.png");
        Pass("workflow.files_to_explorer", "A virtual folder drag from the Files pane lands in real Explorer with nested and empty folders and exact content");
        CloseTransferWindow();

        var context = await LabMessageAsync(peerHost!, "EXPLORER");
        if (context.TryGetProperty("error", out var error)) throw new IOException(error.GetString());
        Click("navScreen"); await WaitForLiveEvidenceAsync(30); await Task.Delay(1000, stop.Token);
        var frame = RemoteClient.Require(await remote.CallAsync("screenshot", new { monitor = 0 }, stop.Token)).Deserialize<ScreenFrame>(Json.Options)!;
        var screenBounds = Element("remoteScreen").Current.BoundingRectangle;
        Point Map(int x, int y)
        {
            double scale = Math.Min(screenBounds.Width / frame.Geometry.Width, screenBounds.Height / frame.Geometry.Height);
            return new((int)(screenBounds.Left + (screenBounds.Width - frame.Geometry.Width * scale) / 2 + (x - frame.Geometry.X) * scale),
                (int)(screenBounds.Top + (screenBounds.Height - frame.Geometry.Height * scale) / 2 + (y - frame.Geometry.Y) * scale));
        }
        explorer = await OpenWorkflowExplorerAsync(local, new(1080, 30, desktop.Width - 1100, 760));
        await DragWorkflowAsync(Center(ExplorerItem(explorer, "local-viewer-tree")), Map(context.Int("dropX"), context.Int("dropY")), 2000);
        await WaitWorkflowAsync(async () => (await remote.CallAsync("file.info", new { path = Path.Combine(remoteFiles, "local-viewer-tree", "nested", "évidence.txt") }, stop.Token)).Ok);
        Pass("workflow.explorer_to_viewer", "Dropping a real Explorer folder over the remote Explorer view selects the remote destination and uploads it");
        CloseTransferWindow();
        // Adding a folder changes Explorer's sorted item positions.
        context = await LabMessageAsync(peerHost!, "EXPLORER");
        if (context.TryGetProperty("error", out error)) throw new IOException(error.GetString());
        await Task.Delay(500, stop.Token);
        explorer = await OpenWorkflowExplorerAsync(received, new(1080, 30, desktop.Width - 1100, 760));
        await DragWorkflowAsync(Map(context.Int("x"), context.Int("y")), new(desktop.Width - 150, 630), 2600);
        await WaitFixtureAsync(received, "remote-viewer-tree");
        RequireFixture(local, "local-tree"); RequireFixture(local, "local-viewer-tree");
        var verified = await LabMessageAsync(peerHost!, "VERIFY");
        if (!verified.TryGetProperty("verified", out var valid) || !valid.GetBoolean()) throw new IOException("Remote folder integrity/source preservation failed: " + verified);
        Pass("workflow.viewer_to_explorer", "Dragging a folder from the remote desktop to local Explorer preserves both source trees and verifies exact nested contents");
        CaptureDesktop("explorer-bidirectional-complete.png");
    }

    private static void MakeTransferFixture(string parent, string name)
    {
        Directory.CreateDirectory(Path.Combine(parent, name, "empty")); Directory.CreateDirectory(Path.Combine(parent, name, "nested"));
        File.WriteAllText(Path.Combine(parent, name, "nested", "évidence.txt"), "Remote Debugger " + name + " — αβγ\n");
    }
    private static void RequireFixture(string parent, string name)
    {
        if (!Directory.Exists(Path.Combine(parent, name, "empty")) || File.ReadAllText(Path.Combine(parent, name, "nested", "évidence.txt")) != "Remote Debugger " + name + " — αβγ\n")
            throw new IOException("Folder structure or contents differ: " + name);
    }
    private Task WaitFixtureAsync(string parent, string name) => WaitWorkflowAsync(() =>
    {
        try { RequireFixture(parent, name); return Task.FromResult(true); }
        catch (IOException) { return Task.FromResult(false); } // Explorer may still own its destination handle.
    });
    private async Task WaitWorkflowAsync(Func<Task<bool>> ready, int seconds = 30, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(ready))] string? expectation = null)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < seconds) { if (await ready()) return; await Task.Delay(250, stop.Token); }
        throw new TimeoutException("Support workflow observation timed out: " + expectation);
    }
    private async Task<AutomationElement> WorkflowDialogAsync(string id)
    {
        AutomationElement? found = null;
        await WaitWorkflowAsync(() =>
        {
            found = AutomationElement.RootElement.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ProcessIdProperty, product!.Id))
                .Cast<AutomationElement>().FirstOrDefault(e => e.Current.AutomationId == id || e.Current.ClassName == id);
            return Task.FromResult(found != null);
        });
        return found!;
    }
    private static Point Center(AutomationElement element) { var r = element.Current.BoundingRectangle; return new((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2)); }
    private static AutomationElement ExplorerItem(AutomationElement window, string name) => window.FindFirst(TreeScope.Descendants,
        new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem), new PropertyCondition(AutomationElement.NameProperty, name)))
        ?? throw new IOException("Explorer item unavailable: " + name);
    private async Task<AutomationElement> OpenWorkflowExplorerAsync(string path, Rectangle bounds)
    {
        IntPtr FindExisting()
        {
            IntPtr found = IntPtr.Zero;
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            dynamic windows = shell.Windows();
            try
            {
                for (int i = 0; i < (int)windows.Count; i++)
                {
                    dynamic browser = windows.Item(i);
                    try { if (string.Equals((string)browser.Document.Folder.Self.Path, path, StringComparison.OrdinalIgnoreCase)) { found = new IntPtr((long)browser.HWND); break; } }
                    catch (Exception) { }
                    finally { Marshal.ReleaseComObject(browser); }
                }
            }
            finally { Marshal.ReleaseComObject(windows); Marshal.ReleaseComObject(shell); }
            return found;
        }
        IntPtr handle = FindExisting();
        if (handle == IntPtr.Zero)
        {
            using var launched = Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = '"' + path + '"' });
            await WaitWorkflowAsync(() => { handle = FindExisting(); return Task.FromResult(handle != IntPtr.Zero); });
        }
        _ = MoveWorkflowWindow(handle, bounds.X, bounds.Y, bounds.Width, bounds.Height, true);
        var result = AutomationElement.FromHandle(handle); result.SetFocus(); await Task.Delay(600, stop.Token); return result;
    }
    private async Task DragWorkflowAsync(Point start, Point end, int holdMilliseconds)
    {
        Forms.Cursor.Position = start; await Task.Delay(150, stop.Token);
        mouse_event(2, 0, 0, 0, UIntPtr.Zero);
        try
        {
            await Task.Delay(450, stop.Token);
            for (int i = 1; i <= 20; i++)
            { Forms.Cursor.Position = new(start.X + (end.X - start.X) * i / 20, start.Y + (end.Y - start.Y) * i / 20); await Task.Delay(70, stop.Token); }
            await Task.Delay(holdMilliseconds, stop.Token);
        }
        finally { mouse_event(4, 0, 0, 0, UIntPtr.Zero); }
        await Task.Delay(700, stop.Token);
    }
    private void CloseTransferWindow()
    {
        var window = AutomationElement.RootElement.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ProcessIdProperty, product!.Id))
            .Cast<AutomationElement>().FirstOrDefault(e => e.Current.AutomationId == "transferQueueWindow");
        if (window?.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern) == true) ((WindowPattern)pattern).Close();
    }
    [DllImport("user32.dll", EntryPoint = "MoveWindow")] private static extern bool MoveWorkflowWindow(IntPtr window, int x, int y, int width, int height, bool repaint);
}
