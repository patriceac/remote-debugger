using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using RemoteDebugger;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private bool IsWorkspaceAudit => role == "loopbackui";
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private async Task AuditScaledAgentAsync()
    {
        product = loopbackAgent ?? throw new InvalidOperationException("Agent missing.");
        Native.FocusWindow(product.Id);
        uint dpi = GetDpiForWindow(Root().Current.NativeWindowHandle);
        var working = Forms.Screen.PrimaryScreen!.WorkingArea;
        ResizeProductWindow(Math.Min(1060 * dpi / 96d, working.Width), Math.Min(720 * dpi / 96d, working.Height));
        await Task.Delay(250, stop.Token);
        var workspace = FindVisibleId("agentWorkspace") ?? throw new InvalidOperationException("Agent workspace missing.");
        var note = Find("agentSessionNote") ?? throw new InvalidOperationException("Agent session explanation missing.");
        SendMessage(workspace.Current.NativeWindowHandle, 0x0115, new IntPtr(7), IntPtr.Zero); // WM_VSCROLL / SB_BOTTOM
        await Task.Delay(250, stop.Token);
        var viewport = workspace.Current.BoundingRectangle;
        var bounds = note.Current.BoundingRectangle;
        var window = Root().Current.BoundingRectangle;
        var footer = Find("footerStatus")!.Current.BoundingRectangle;
        bool readable = !note.Current.IsOffscreen && bounds.Top >= viewport.Top && bounds.Bottom <= viewport.Bottom &&
            viewport.Bottom <= footer.Top && viewport.Left >= window.Left && viewport.Right <= window.Right;
        if (readable) Pass("ui.agent_scaled_scroll", "The agent's final explanation remains reachable at increased DPI", new { viewport, bounds, window, footer });
        else Fail("ui.agent_scaled_scroll", "The agent's final explanation remains reachable at increased DPI", new { viewport, bounds, window, footer });
        CaptureDesktop("ui-agent-connected-scaled.png");
        product = loopbackController;
    }

    private async Task AuditTabsAsync(string phase, bool connected)
    {
        product = loopbackController ?? throw new InvalidOperationException("Controller missing.");
        Native.FocusWindow(product.Id);
        uint dpi = GetDpiForWindow(Root().Current.NativeWindowHandle);
        var working = Forms.Screen.PrimaryScreen!.WorkingArea;
        ResizeProductWindow(Math.Min(1060 * dpi / 96d, working.Width), Math.Min(720 * dpi / 96d, working.Height));
        foreach (var tab in new[]
        {
            (Nav: "navConnection", Name: "connection", Controls: new[] { "host", "pairCode", "pair" }),
            (Nav: "navScreen", Name: "screen", Controls: new[] { "remoteScreen", "pauseViewing", "inputStatus" }),
            (Nav: "navProcesses", Name: "processes", Controls: new[] { "refreshResources", "processList", "resourceState" }),
            (Nav: "navFiles", Name: "files", Controls: new[] { "browseFiles", "remoteFiles", "upload", "download" }),
            (Nav: "navDiagnostics", Name: "diagnostics", Controls: new[] { "execute", "arguments", "argumentsLabel", "resultLabel", "technicalIdentity", "output" })
        })
        {
            Click(tab.Nav);
            await Task.Delay(500, stop.Token);
            if (connected && tab.Name == "screen") await WaitForLiveEvidenceAsync(30);
            var window = Root().Current.BoundingRectangle;
            var controls = tab.Controls.Select(id =>
            {
                var element = FindVisibleId(id) ?? throw new InvalidOperationException("Audit control missing: " + id);
                var bounds = element.Current.BoundingRectangle;
                return new { id, enabled = element.Current.IsEnabled, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom,
                    contained = bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= window.Left && bounds.Top >= window.Top && bounds.Right <= window.Right && bounds.Bottom <= window.Bottom };
            }).ToArray();
            bool actions = tab.Name switch
            {
                "connection" => Element("pair").Current.IsEnabled == !connected && Element("host").Current.IsEnabled == !connected,
                "screen" => Element("pauseViewing").Current.IsEnabled == connected,
                "processes" => Element("refreshResources").Current.IsEnabled == connected,
                "files" => Element("upload").Current.IsEnabled == connected && !Element("download").Current.IsEnabled,
                _ => Element("execute").Current.IsEnabled == connected && !Element("cancel").Current.IsEnabled
            };
            string detail = TryValue("footerDetail");
            bool scopedFooter = tab.Name switch
            {
                "connection" => detail.Contains("PC :") || detail.Contains("adresse"),
                "screen" => !detail.Contains("Fichiers") && !detail.Contains("processus"),
                "processes" => detail.Contains("mesure", StringComparison.OrdinalIgnoreCase) || detail.Contains("mesuré"),
                "files" => detail == "Espace de travail",
                _ => detail.StartsWith("Action :", StringComparison.Ordinal)
            };
            bool identity = tab.Name != "diagnostics" || TryValue("technicalIdentity").StartsWith(connected ? "Identité vérifiée" : "Aucune connexion authentifiée", StringComparison.Ordinal);
            string footerStatus = TryValue("footerStatus");
            bool diagnosticState = tab.Name != "diagnostics" || (footerStatus == TryValue("diagnosticState") && (!connected || !footerStatus.StartsWith("Connectez-vous")));
            var evidence = new { phase, dpi, window.Width, window.Height, controls, actions, scopedFooter, identity, diagnosticState, footerStatus, footer = detail };
            string id = $"ui.{phase}.{tab.Name}.{dpi}";
            if (controls.All(control => control.contained) && actions && scopedFooter && identity && diagnosticState)
                Pass(id, "The tab has visible controls, truthful availability, scoped status and current identity", evidence);
            else Fail(id, "The tab has visible controls, truthful availability, scoped status and current identity", evidence);
            CaptureDesktop($"ui-{phase}-{tab.Name}-{dpi}.png");
            AuditControlAlignment(tab.Name, phase, dpi);
        }
    }

    private async Task AuditPairingErrorAsync()
    {
        Click("navConnection"); Set("host", "127.0.0.1"); Set("pairCode", "12"); Click("pair");
        bool error = await WaitForTextAsync("connectionFormState", text => text.Contains("six chiffres"), 5);
        if (error && Element("pair").Current.IsEnabled) Pass("ui.pairing_validation", "Invalid pairing input remains editable and explains the required code", null);
        else Fail("ui.pairing_validation", "Invalid pairing input remains editable and explains the required code", new { error });
        CaptureDesktop("ui-connection-error.png");
    }

    private async Task AuditDiagnosticsAndFileErrorsAsync()
    {
        Click("navDiagnostics");
        Set("arguments", "{"); Click("execute");
        bool invalid = await WaitForTextAsync("diagnosticState", text => text.StartsWith("Arguments JSON invalides"), 5);
        CaptureDesktop("ui-diagnostics-invalid-json.png");
        Set("arguments", "{}"); Click("execute");
        bool success = await WaitForTextAsync("diagnosticState", text => text.StartsWith("Terminé"), 20);
        bool result = TryValue("output").Contains("\"ok\": true");
        CaptureDesktop("ui-diagnostics-success.png");
        await SelectDiagnosticOperationAsync("file.info");
        bool template = TryValue("arguments").Contains("path");
        Set("arguments", "{\"path\":\"../outside-workspace\"}"); Click("execute");
        bool remoteFailure = await WaitForTextAsync("diagnosticState", text => text.StartsWith("Échec de l’action"), 20);
        CaptureDesktop("ui-diagnostics-remote-error.png");
        await SelectDiagnosticOperationAsync("status");
        bool reset = TryValue("arguments").Trim() == "{}";
        if (invalid && success && result && template && remoteFailure && reset)
            Pass("ui.diagnostics_states", "Diagnostics loads templates, validates JSON, displays results and explains remote errors", new { invalid, success, result, template, remoteFailure, reset });
        else Fail("ui.diagnostics_states", "Diagnostics loads templates, validates JSON, displays results and explains remote errors", new { invalid, success, result, template, remoteFailure, reset });

        await SelectDiagnosticOperationAsync("command");
        Set("arguments", "{\"file\":\"powershell.exe\",\"arguments\":[\"-NoProfile\",\"-NonInteractive\",\"-Command\",\"Start-Sleep -Seconds 20\"]}");
        Click("execute");
        bool running = await WaitForTextAsync("diagnosticState", text => text == "En cours…", 5);
        bool busyControls = running && Element("cancel").Current.IsEnabled && !Element("execute").Current.IsEnabled && !Element("operation").Current.IsEnabled;
        CaptureDesktop("ui-diagnostics-running.png");
        if (running) Click("cancel");
        bool cancelled = await WaitForTextAsync("diagnosticState", text => text.StartsWith("Action annulée"), 10);
        bool restored = Element("execute").Current.IsEnabled && !Element("cancel").Current.IsEnabled;
        CaptureDesktop("ui-diagnostics-cancelled.png");
        if (busyControls && cancelled && restored) Pass("ui.diagnostics_cancellation", "A running action exposes cancellation and restores editable controls afterwards", new { busyControls, cancelled, restored });
        else Fail("ui.diagnostics_cancellation", "A running action exposes cancellation and restores editable controls afterwards", new { busyControls, cancelled, restored });
        await SelectDiagnosticOperationAsync("status");

        Click("navFiles"); Set("remoteDirectory", "../outside-workspace"); FocusAndEnter("remoteDirectory");
        bool fileError = await WaitForTextAsync("fileState", text => text.StartsWith("Lecture impossible"), 20);
        CaptureDesktop("ui-files-error.png");
        Set("remoteDirectory", ""); FocusAndEnter("remoteDirectory");
        bool recovered = await WaitForTextAsync("fileState", text => text.Contains("élément", StringComparison.OrdinalIgnoreCase) || text == "Dossier vide", 20);
        if (fileError && recovered) Pass("ui.files_error_recovery", "An invalid directory has a visible error and a corrected path reloads", new { fileError, recovered });
        else Fail("ui.files_error_recovery", "An invalid directory has a visible error and a corrected path reloads", new { fileError, recovered });
        Click("navScreen"); await WaitForLiveEvidenceAsync(30); Click("pauseViewing");
        CaptureDesktop("ui-screen-paused.png"); Click("pauseViewing"); await WaitForLiveEvidenceAsync(30);
    }

    private async Task SelectDiagnosticOperationAsync(string name)
    {
        var combo = Element("operation");
        ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        await Task.Delay(200, stop.Token);
        var option = AutomationElement.RootElement.FindAll(TreeScope.Descendants,
            new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, product!.Id), new PropertyCondition(AutomationElement.NameProperty, name)))
            .Cast<AutomationElement>().FirstOrDefault(item => item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _));
        if (option == null) throw new InvalidOperationException("Diagnostic operation missing: " + name);
        ((SelectionItemPattern)option.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        // SelectionItem changes the value without dismissing the WinForms popup.
        // Close it before Process.MainWindowHandle can resolve to that popup.
        ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse();
        await Task.Delay(250, stop.Token);
    }

    private async Task<bool> TrySetGuestScaleAsync(int percent = 150)
    {
        // Leave live loopback input before operating Windows Settings; otherwise
        // cursor changes can be forwarded back into the same guest desktop.
        Click("navConnection");
        Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true });
        await Task.Delay(3000, stop.Token);
        AutomationElement? settings = null;
        for (int attempt = 0; attempt < 12 && settings == null; attempt++)
        {
            settings = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>()
                .FirstOrDefault(window => window.Current.Name is "Paramètres" or "Settings");
            if (settings == null) await Task.Delay(500, stop.Token);
        }
        if (settings == null) { Fail($"ui.display_scale_{percent}", "Windows display scaling is exercised", "Windows Settings did not appear."); return false; }
        try
        {
            settings.SetFocus();
            await Task.Delay(500, stop.Token);
            var items = settings.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().ToArray();
            File.WriteAllText(Path.Combine(output, "display-settings-inventory.json"), Json.Text(items.Select(item => new { name = Safe(() => item.Current.Name), id = Safe(() => item.Current.AutomationId), type = Safe(() => item.Current.ControlType.ProgrammaticName) })));
            CaptureDesktop($"ui-display-settings-{percent}.png", focusProduct: false);
            var scale = items.FirstOrDefault(item => item.Current.ControlType == ControlType.ComboBox &&
                (item.Current.Name.Contains("Échelle", StringComparison.OrdinalIgnoreCase) || item.Current.Name.Contains("Scale", StringComparison.OrdinalIgnoreCase)));
            if (scale == null) { Fail($"ui.display_scale_{percent}", "Windows display scaling is exercised", "The display scaling selector was not exposed; inspect the saved Settings inventory."); return false; }
            ((ExpandCollapsePattern)scale.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
            await Task.Delay(1000, stop.Token);
            // The XAML popup can belong to SystemSettings while its window is
            // hosted by ApplicationFrameHost. Do not filter by the frame PID.
            var choices = AutomationElement.RootElement.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Cast<AutomationElement>().ToArray();
            File.WriteAllText(Path.Combine(output, $"scale-choices-{percent}.json"), Json.Text(choices.Select(item => new { name = item.Current.Name, pid = item.Current.ProcessId })));
            var choice = choices.FirstOrDefault(item => item.Current.Name.StartsWith(percent.ToString(), StringComparison.Ordinal) && item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _));
            CaptureDesktop($"scale-popup-{percent}.png", focusProduct: false);
            if (choice == null)
            {
                Block($"ui.display_scale_{percent}", $"Windows display scaling at {percent} percent", "The guest display exposes only the presets recorded in scale-choices; custom scaling requires a new Windows sign-in.",
                    new { available = choices.Select(item => item.Current.Name).Where(name => name.Contains('%')).ToArray() }, required: false);
                return false;
            }
            ((SelectionItemPattern)choice.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            await Task.Delay(2500, stop.Token);
            product = loopbackController;
            uint dpi = GetDpiForWindow(Root().Current.NativeWindowHandle);
            if (dpi == 96 * percent / 100) { Pass($"ui.display_scale_{percent}", "The actual Release window uses the selected Windows display scaling", new { dpi, percent }); return true; }
            Fail($"ui.display_scale_{percent}", "The actual Release window uses the selected Windows display scaling", new { dpi, percent });
            return false;
        }
        finally
        {
            if (settings.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern)) ((WindowPattern)pattern).Close();
            product = loopbackController; if (product != null) Native.FocusWindow(product.Id);
        }
    }
}
