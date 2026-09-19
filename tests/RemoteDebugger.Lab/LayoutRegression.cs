using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using RemoteDebugger;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private void AuditControlAlignment(string tab, string phase, uint dpi)
    {
        string[][] rows = tab switch
        {
            "connection" => [["pairCode", "pair"]],
            "screen" => [["monitorLabel", "monitor", "mouseKeyboard", "pauseViewing"], ["remoteText", "typeText", "enterKey"]],
            "processes" => [["refreshResources", "resourceState"]],
            "files" => [["remoteDirectory", "openRemoteFolder", "parentFolder", "browseFiles"], ["selectionLabel", "remotePath"], ["fileTransferStatus", "cancelTransfer"]],
            _ => [["operationLabel", "operation", "pidLabel", "targetPid", "execute", "cancel"]]
        };
        var evidence = rows.Select(ids =>
        {
            var bounds = ids.Select(id => Element(id).Current.BoundingRectangle).ToArray();
            double[] centers = bounds.Select(rect => rect.Top + rect.Height / 2).ToArray();
            return new { ids, bounds, aligned = centers.Max() - centers.Min() <= 2, unclipped = ids.All(id => FitsVisibleAncestors(Element(id))) };
        }).ToArray();
        var navigation = Find("navConnection") ?? throw new InvalidOperationException("Navigation missing.");
        var subtitle = Find("headerSubtitle") ?? throw new InvalidOperationException("Header subtitle missing.");
        var title = Find("headerTitle") ?? throw new InvalidOperationException("Header title missing.");
        var shellEvidence = new { navigationWidth = navigation.Current.BoundingRectangle.Width,
            minimumNavigationWidth = 176 * dpi / 96d, titleFits = FitsVisibleAncestors(title), subtitleFits = FitsVisibleAncestors(subtitle) };
        bool shellFits = shellEvidence.navigationWidth >= shellEvidence.minimumNavigationWidth && shellEvidence.titleFits && shellEvidence.subtitleFits;
        bool metricFits = true;
        if (tab == "processes")
        {
            var volumes = Element("volumeSummary").Current.BoundingRectangle;
            var metrics = new[] { "cpuSummary", "ramSummary", "processSummary", "resourceMeasuredAt" }.Select(id =>
            {
                var label = Element(id); var bounds = label.Current.BoundingRectangle;
                using var font = new System.Drawing.Font("Segoe UI", 13 * dpi / 96f, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
                // Measure against a 96-DPI bitmap to avoid inheriting the Lab's DPI.
                using var bitmap = new System.Drawing.Bitmap(1, 1); bitmap.SetResolution(96, 96);
                using var graphics = System.Drawing.Graphics.FromImage(bitmap);
                var measured = Forms.TextRenderer.MeasureText(graphics, Value(label), font, new System.Drawing.Size((int)bounds.Width, int.MaxValue),
                    Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.NoPrefix | Forms.TextFormatFlags.WordBreak);
                return new { id, bounds, measured, fits = bounds.Height >= measured.Height && bounds.Bottom <= volumes.Top && FitsVisibleAncestors(label) };
            }).ToArray();
            metricFits = metrics.All(metric => metric.fits);
            File.WriteAllText(Path.Combine(output, $"metrics-{phase}-{dpi}.json"), JsonSerializer.Serialize(metrics));
        }
        string check = $"ui.alignment_{phase}_{tab}_{dpi}";
        if (evidence.All(row => row.aligned && row.unclipped) && metricFits && shellFits) Pass(check, "The shell scales with DPI, toolbar controls align and metric text remains fully visible", new { evidence, metricFits, shellEvidence });
        else Fail(check, "The shell scales with DPI, toolbar controls align and metric text remains fully visible", new { evidence, metricFits, shellEvidence });
    }

    private bool FitsVisibleAncestors(AutomationElement control)
    {
        var bounds = control.Current.BoundingRectangle;
        var parent = TreeWalker.ControlViewWalker.GetParent(control);
        while (parent != null && parent.Current.ProcessId == product!.Id)
        {
            var area = parent.Current.BoundingRectangle;
            if (!area.IsEmpty && area.Width > 0 && area.Height > 0 &&
                (bounds.Left < area.Left - 1 || bounds.Right > area.Right + 1 || bounds.Top < area.Top - 1 || bounds.Bottom > area.Bottom + 1)) return false;
            parent = TreeWalker.ControlViewWalker.GetParent(parent);
        }
        return true;
    }

    private static readonly (string Nav, string Id)[] Tables = [("navConnection", "peers"), ("navProcesses", "processList"), ("navFiles", "remoteFiles")];
    private sealed record HeaderObservation(string Name, double Width, System.Windows.Rect Bounds);
    private sealed record SavedHeader(string Name, double Width);
    private readonly Dictionary<string, SavedHeader[]> changedColumns = [];

    private HeaderObservation[] ReadHeaders(string id)
    {
        string[] sourceColumns = id switch
        {
            "peers" => ["Nom", "Adresse", "État"],
            "processList" => ["PID", "Application", "CPU %", "RAM Mio", "Réponse", "Fenêtre"],
            _ => ["Nom", "Type", "Taille", "Modifié"]
        };
        var list = Element(id);
        return list.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.HeaderItem))
            .Cast<AutomationElement>().Select(header =>
            {
                string name = header.Current.Name.TrimEnd(' ', '▲', '▼');
                int sourceIndex = Array.IndexOf(sourceColumns, name);
                if (sourceIndex < 0) throw new InvalidOperationException("Unknown native column: " + name);
                // UIA exposes the caption rectangle, whose theme padding is
                // excluded. LVM_GETCOLUMNWIDTH reports the actual column width.
                int width = SendMessage(list.Current.NativeWindowHandle, 0x101D, new IntPtr(sourceIndex), IntPtr.Zero).ToInt32();
                return new HeaderObservation(name, width, header.Current.BoundingRectangle);
            }).OrderBy(header => header.Bounds.Left).ToArray();
    }

    private async Task DragHeaderAsync(double x, double y, double destinationX)
    {
        Forms.Cursor.Position = new System.Drawing.Point((int)x, (int)y);
        await Task.Delay(150, stop.Token);
        mouse_event(2, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(100, stop.Token);
        try
        {
            for (int i = 1; i <= 12; i++)
            {
                Forms.Cursor.Position = new System.Drawing.Point((int)(x + (destinationX - x) * i / 12), (int)y);
                await Task.Delay(30, stop.Token);
            }
        }
        finally { mouse_event(4, 0, 0, 0, UIntPtr.Zero); }
        await Task.Delay(350, stop.Token);
    }

    private async Task ChangeTableLayoutsAsync()
    {
        product = loopbackController; Native.FocusWindow(product!.Id);
        ResizeProductWindow(1280, 860);
        await Task.Delay(300, stop.Token);
        uint dpi = GetDpiForWindow(Root().Current.NativeWindowHandle);
        foreach (var table in Tables)
        {
            Click(table.Nav); await Task.Delay(300, stop.Token);
            var before = ReadHeaders(table.Id);
            if (before.Length < 2) throw new InvalidOperationException("Missing native column headers for " + table.Id);
            var first = before[0].Bounds;
            double divider = Element(table.Id).Current.BoundingRectangle.Left + before[0].Width;
            await DragHeaderAsync(divider, first.Top + first.Height / 2, divider + 37 * dpi / 96d);
            var resized = ReadHeaders(table.Id);
            await DragHeaderAsync(resized[0].Bounds.Left + 20, first.Top + first.Height / 2, resized[1].Bounds.Right - 10);
            var after = ReadHeaders(table.Id);
            bool widthChanged = Math.Abs(after.First(header => header.Name == before[0].Name).Width - before[0].Width) > 20 * dpi / 96d;
            bool orderChanged = after[0].Name == before[1].Name;
            changedColumns[table.Id] = after.Select(header => new SavedHeader(header.Name, header.Width * 96d / dpi)).ToArray();
            string path = Path.Combine(output, "loopback", "controller-data", "tables", table.Id + ".json");
            bool saved = File.Exists(path);
            if (widthChanged && orderChanged && saved) Pass("ui.columns_changed_" + table.Id, "Native header dragging changes and saves column width and order", new { before, after, saved });
            else Fail("ui.columns_changed_" + table.Id, "Native header dragging changes and saves column width and order", new { before, after, saved, widthChanged, orderChanged });
            CaptureDesktop("columns-changed-" + table.Id + ".png");
        }
    }

    private async Task VerifyTableLayoutsAsync(string phase)
    {
        product = loopbackController; Native.FocusWindow(product!.Id);
        uint dpi = GetDpiForWindow(Root().Current.NativeWindowHandle);
        foreach (var table in Tables)
        {
            Click(table.Nav); await Task.Delay(300, stop.Token);
            var actual = ReadHeaders(table.Id); var expected = changedColumns[table.Id];
            bool matches = actual.Length == expected.Length && actual.Zip(expected).All(pair => pair.First.Name == pair.Second.Name && Math.Abs(pair.First.Width * 96d / dpi - pair.Second.Width) <= 2);
            if (matches) Pass($"ui.columns_{phase}_{table.Id}", "Each table retains its independent order and logical widths", new { dpi, actual, expected });
            else Fail($"ui.columns_{phase}_{table.Id}", "Each table retains its independent order and logical widths", new { dpi, actual, expected });
            CaptureDesktop($"columns-{phase}-{table.Id}.png");
        }
    }

    private async Task RestartControllerForColumnsAsync()
    {
        product = loopbackController; Native.FocusWindow(product!.Id);
        // Put the window in the same hidden state as the existing tray tests.
        // This also prevents a live-view surface from receiving shell clicks.
        product.CloseMainWindow();
        await Task.Delay(700, stop.Token);
        var menu = await OpenTrayContextAsync();
        AutomationElement? quit = null;
        for (int attempt = 0; menu.OpenItem != null && attempt < 20 && quit == null; attempt++)
        {
            quit = FindLoopbackTrayMenuItem("Quitter");
            if (quit == null) await Task.Delay(250, stop.Token);
        }
        if (quit == null)
        {
            string attempts = Path.Combine(output, "tray-attempts.json");
            if (File.Exists(attempts)) File.Copy(attempts, Path.Combine(output, "restart-tray-attempts.json"), true);
            var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>()
                .Where(window => window.Current.ProcessId == product.Id).Select(window => new
                {
                    window = DescribeTrayElement(window),
                    children = window.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().Take(250).Select(DescribeTrayElement).ToArray()
                }).ToArray();
            File.WriteAllText(Path.Combine(output, "restart-menus.json"), Json.Text(windows));
            CaptureDesktop("restart-tray-failure.png", focusProduct: false);
            throw new InvalidOperationException("Controller Quit missing.");
        }
        InvokeElement(quit);
        await WaitForProcessExitAsync(product, TimeSpan.FromSeconds(15));
        loopbackController = LaunchLoopbackProduct(false, Path.Combine(output, "loopback", "controller-data"));
        product = loopbackController; await WaitUiAsync();
        await VerifyTableLayoutsAsync("restarted");
        await AuditTabsAsync("restarted", false);
    }

    private async Task VerifySessionExitAsync(bool waitForExit = true)
    {
        product = loopbackAgent ?? throw new InvalidOperationException("Agent missing.");
        Native.FocusWindow(product.Id);
        string countdown = TryValue("pairingCountdown");
        bool visible = countdown.Contains("Fermeture automatique dans") && TimeText.IsMatch(countdown);
        CaptureDesktop("agent-exit-countdown.png");
        if (visible) Pass("ui.exit_countdown_started", "Ending the session starts the assisted PC's visible ten-minute exit countdown", new { countdown });
        else Fail("ui.exit_countdown_started", "Ending the session starts the assisted PC's visible ten-minute exit countdown", new { countdown });
        if (!waitForExit) { product = loopbackController; return; }
        var watch = Stopwatch.StartNew();
        // Observe real production time, including its tray behavior. No test
        // timeout override is injected into the shipped application.
        product.CloseMainWindow();
        while (!product.HasExited && watch.Elapsed < TimeSpan.FromSeconds(620)) await Task.Delay(1000, stop.Token);
        bool exited = product.HasExited && product.ExitCode == 0;
        bool controllerAlive = loopbackController is { HasExited: false };
        if (visible && exited && controllerAlive) Pass("ui.exit_countdown_completed", "The assisted PC exits cleanly after its countdown while the controller stays open", new { exited, controllerAlive, waitedSeconds = watch.Elapsed.TotalSeconds });
        else Fail("ui.exit_countdown_completed", "The assisted PC exits cleanly after its countdown while the controller stays open", new { exited, controllerAlive, waitedSeconds = watch.Elapsed.TotalSeconds });
        product = loopbackController;
    }

    private async Task LoopbackExitAsync()
    {
        try
        {
            try { File.Delete(RemoteClient.DefaultPath); } catch (IOException) { }
            string data = Path.Combine(output, "loopback"); Directory.CreateDirectory(data);
            loopbackAgent = LaunchLoopbackProduct(true, Path.Combine(data, "agent-data"));
            product = loopbackAgent; await WaitUiAsync(); WindowState = Forms.FormWindowState.Minimized;
            string code = await WaitPairingCodeAsync();
            loopbackController = LaunchLoopbackProduct(false, Path.Combine(data, "controller-data"));
            product = loopbackController; await WaitUiAsync();
            Set("host", "127.0.0.1"); Set("pairCode", code); FocusAndEnter("pairCode");
            bool connected = await WaitForTextAsync("connectionStatus", IsConnected, 60);
            if (!connected) throw new InvalidOperationException("Exit-timer test could not establish a session.");
            Pass("exit.session_started", "The exit test starts a real authenticated Release session", new { sha256 = await HashFileAsync(application) });
            Click("terminateSession");
            await WaitForTextAsync("connectionStatus", text => !IsConnected(text), 30);
            product = loopbackAgent;
            if (!await WaitForTextAsync("agentHeading", text => text == "Assistance terminée", 30)) throw new InvalidOperationException("Agent did not end its session.");
            await VerifySessionExitAsync();
            await FinishAsync();
        }
        finally { await CleanupLoopbackProcessesAsync(); }
    }

    private async Task LoopbackScaleAsync()
    {
        try
        {
            loopbackController = LaunchLoopbackProduct(false, Path.Combine(output, "scale-data"));
            product = loopbackController; await WaitUiAsync(); WindowState = Forms.FormWindowState.Minimized;
            foreach (int percent in new[] { 125, 200, 175, 150 })
            {
                await TrySetGuestScaleAsync(percent);
                CaptureDesktop($"scale-product-{percent}.png");
            }
            await FinishAsync();
        }
        finally { await CleanupLoopbackProcessesAsync(); }
    }

    private async Task LoopbackColumnsAsync()
    {
        try
        {
            try { File.Delete(RemoteClient.DefaultPath); } catch (IOException) { }
            string data = Path.Combine(output, "loopback"); Directory.CreateDirectory(data);
            loopbackAgent = LaunchLoopbackProduct(true, Path.Combine(data, "agent-data"));
            product = loopbackAgent; await WaitUiAsync(); WindowState = Forms.FormWindowState.Minimized;
            string code = await WaitPairingCodeAsync();
            loopbackController = LaunchLoopbackProduct(false, Path.Combine(data, "controller-data"));
            product = loopbackController; await WaitUiAsync();
            Set("host", "127.0.0.1"); Set("pairCode", code); FocusAndEnter("pairCode");
            if (!await WaitForTextAsync("connectionStatus", IsConnected, 60)) throw new InvalidOperationException("Column test could not pair.");
            await ChangeTableLayoutsAsync();
            if (await TrySetGuestScaleAsync(150)) await VerifyTableLayoutsAsync("dpi_changed_150");
            Click("terminateSession");
            await WaitForTextAsync("connectionStatus", text => !IsConnected(text), 30);
            product = loopbackAgent;
            await WaitForTextAsync("agentHeading", text => text == "Assistance terminée", 30);
            await ProbeSecondSessionAsync();
            await RestartControllerForColumnsAsync();
            await VerifySessionExitAsync(waitForExit: false);
            await FinishAsync();
        }
        finally { await CleanupLoopbackProcessesAsync(); }
    }
}
