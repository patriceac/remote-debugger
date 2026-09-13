using System.Globalization;
using System.IO;
using System.Windows.Automation;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private async Task LocalizationReviewAsync()
    {
        var systemLanguage = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var sample in new[]
            {
                (Language: (string?)null, Title: UiText.Get(nameof(UiText.GiveControl), systemLanguage)),
                (Language: "fr-CA", Title: "Donner le contrôle"),
                (Language: "es-MX", Title: "Ceder el control"),
                (Language: "en-GB", Title: "Give control"),
                (Language: "de-DE", Title: "Give control")
            })
            {
                string tag = sample.Language ?? "system";
                CultureInfo.CurrentUICulture = UiCulture.Select(systemLanguage, sample.Language);
                string data = Path.Combine(output, "localization", tag);
                loopbackAgent = LaunchLoopbackProduct(true, Path.Combine(data, "agent"), sample.Language);
                product = loopbackAgent;
                await WaitUiAsync(); WindowState = Forms.FormWindowState.Minimized;
                Native.FocusWindow(product.Id); ResizeProductWindow(1060, 720);
                string code = await WaitPairingCodeAsync();
                CheckLocalizedText(tag + ".agent", "headerTitle", sample.Title);
                CheckLocalizedText(tag + ".agent_heading", "agentHeading", UiText.ShareCode);
                CheckLocalizedText(tag + ".copy", "copyAgentCode", UiText.Copy);
                string countdown = TryValue("pairingCountdown");
                if (TimeText.IsMatch(countdown)) Pass(tag + ".countdown", "The localized pairing countdown renders its time", new { countdown });
                else Fail(tag + ".countdown", "The localized pairing countdown renders its time", new { countdown });
                CaptureDesktop("localization-" + tag + "-agent.png");

                loopbackController = LaunchLoopbackProduct(false, Path.Combine(data, "controller"), sample.Language);
                product = loopbackController; await WaitUiAsync();
                Native.FocusWindow(product.Id); ResizeProductWindow(1060, 720);
                foreach (var tab in new[]
                {
                    (Nav: "navConnection", Name: "connection", Title: UiText.Connection),
                    (Nav: "navScreen", Name: "screen", Title: UiText.RemoteScreen),
                    (Nav: "navProcesses", Name: "processes", Title: UiText.Processes),
                    (Nav: "navFiles", Name: "files", Title: UiText.Files),
                    (Nav: "navDiagnostics", Name: "diagnostics", Title: UiText.Diagnostics)
                })
                {
                    Click(tab.Nav); await Task.Delay(300, stop.Token);
                    CheckLocalizedText(tag + "." + tab.Name, "headerTitle", tab.Title);
                    AuditControlAlignment(tab.Name, "localization-" + tag, GetDpiForWindow(Root().Current.NativeWindowHandle));
                    CaptureDesktop("localization-" + tag + "-" + tab.Name + ".png");
                }
                CheckLocalizedText(tag + ".disconnected_identity", "technicalIdentity", UiText.NoAuthenticatedConnection);
                Click("navConnection"); Set("host", "127.0.0.1"); Set("pairCode", "12"); Click("pair");
                CheckLocalizedText(tag + ".validation", "connectionFormState", UiText.CodeMustBeSixDigits);
                Set("pairCode", code); FocusAndEnter("pairCode");
                bool paired = await WaitForTextAsync("connectionStatus", value => value.StartsWith(UiText.Connected, StringComparison.Ordinal), 60);
                if (!paired) throw new InvalidOperationException(tag + " pairing did not connect: " + TryValue("connectionFormState"));
                CheckLocalizedText(tag + ".connected", "connectionStatus", UiText.Connected);
                CheckLocalizedText(tag + ".end_support", "terminateSession", UiText.EndSupport);
                Click("navProcesses");
                await WaitForTextAsync("resourceState", value => value == UiText.MeasurementComplete, 30);
                CheckLocalizedText(tag + ".measurement", "resourceState", UiText.MeasurementComplete);
                AuditControlAlignment("processes", "localized-connected-" + tag, GetDpiForWindow(Root().Current.NativeWindowHandle));
                CaptureDesktop("localization-" + tag + "-measurements.png");
                Click("navFiles"); await Task.Delay(500, stop.Token);
                AuditControlAlignment("files", "localized-connected-" + tag, GetDpiForWindow(Root().Current.NativeWindowHandle));
                CaptureDesktop("localization-" + tag + "-connected-files.png");
                Click("navDiagnostics");
                CheckLocalizedText(tag + ".ready_to_run", "diagnosticState", UiText.ReadyToRun);
                Set("arguments", "{"); Click("execute");
                CheckLocalizedText(tag + ".invalid_json", "diagnosticState", UiText.InvalidJsonArguments);
                CaptureDesktop("localization-" + tag + "-invalid-json.png");
                Click("navScreen");
                await WaitForTextAsync("liveBadge", value => value.Contains(UiText.Live), 30);
                CaptureDesktop("localization-" + tag + "-live.png");
                Click("pauseViewing");
                CheckLocalizedText(tag + ".resume", "pauseViewing", UiText.Resume);
                CheckLocalizedText(tag + ".paused", "streamOverlay", UiText.ViewingPausedResume);
                Click("terminateSession");
                product = loopbackAgent; Native.FocusWindow(product.Id);
                bool ended = await WaitForTextAsync("agentHeading", value => value == UiText.SupportEnded, 20);
                if (!ended) throw new InvalidOperationException(tag + " session did not end.");
                CheckLocalizedText(tag + ".ended", "agentHeading", UiText.SupportEnded);
                CaptureDesktop("localization-" + tag + "-ended.png");
                await QuitLocalizedProductAsync(tag + ".agent_tray");
                loopbackAgent = null;
                product = loopbackController;
                await QuitLocalizedProductAsync(tag + ".controller_tray");
                loopbackController = null; product = null;
            }
            await FinishAsync();
        }
        finally
        {
            // Only disposable guest processes are involved. The broker owns final cleanup.
            foreach (var process in new[] { loopbackAgent, loopbackController })
                if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            CultureInfo.CurrentUICulture = systemLanguage;
        }
    }

    private void CheckLocalizedText(string id, string control, string expected)
    {
        string actual = Value(Element(control));
        if (actual == expected) Pass("localization." + id, "The Release control uses the selected language", new { control, expected, actual });
        else Fail("localization." + id, "The Release control uses the selected language", new { control, expected, actual });
    }

    private async Task QuitLocalizedProductAsync(string tag)
    {
        Native.FocusWindow(product!.Id); product.CloseMainWindow(); await Task.Delay(500, stop.Token);
        var menu = await OpenTrayContextAsync();
        var quit = FindLoopbackTrayMenuItem(UiText.Quit);
        var end = FindLoopbackTrayMenuItem(UiText.EndSupport);
        if (menu.OpenItem == null || quit == null || end == null) throw new InvalidOperationException("Localized tray menu incomplete: " + tag);
        Pass("localization." + tag, "The actual tray menu exposes localized Open, End support and Quit", new { open = menu.OpenItem.Current.Name, quit = quit.Current.Name, end = end.Current.Name });
        CaptureDesktop("localization-" + tag + ".png", focusProduct: false);
        InvokeElement(quit); await WaitForProcessExitAsync(product, TimeSpan.FromSeconds(15));
    }
}
