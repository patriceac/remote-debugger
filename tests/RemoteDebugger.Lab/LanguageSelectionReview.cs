using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Windows.Automation;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private async Task LanguageSelectionReviewAsync()
    {
        var systemLanguage = CultureInfo.CurrentUICulture;
        string agentRoot = Path.Combine(output, "language-agent");
        string controllerRoot = Path.Combine(output, "language-controller");
        try
        {
            UiCulture.Apply(UiCulture.Resolve(systemLanguage));
            loopbackAgent = LaunchLoopbackProduct(true, agentRoot, null);
            product = loopbackAgent; await WaitUiAsync();
            WindowState = Forms.FormWindowState.Minimized;
            Native.FocusWindow(product.Id); ResizeProductWindow(1060, 720);
            string pairingCode = await WaitPairingCodeAsync();
            int agentPid = product.Id;
            await SelectInterfaceLanguageAsync("Español", "es");
            CheckLocalizedText("selector.agent_spanish", "agentHeading", UiText.ShareCode);
            CheckLanguageState("agent_code", product.Id == agentPid && await WaitPairingCodeAsync() == pairingCode,
                "Changing language preserves the assisted process and current pairing code");
            CaptureDesktop("language-selector-agent-spanish.png");

            loopbackController = LaunchLoopbackProduct(false, controllerRoot, null);
            product = loopbackController; await WaitUiAsync();
            UiCulture.Apply(UiCulture.Resolve(systemLanguage));
            Native.FocusWindow(product.Id); ResizeProductWindow(1060, 720);
            int controllerPid = product.Id;
            Set("host", "127.0.0.1"); Set("pairCode", "12"); Click("pair");
            await SelectInterfaceLanguageAsync("English", "en");
            CheckLocalizedText("selector.validation", "connectionFormState", UiText.CodeMustBeSixDigits);
            CheckLanguageState("draft", TryValue("host") == "127.0.0.1" && TryValue("pairCode") == "12",
                "Changing language preserves the typed address and pairing code");
            CaptureDesktop("language-selector-english-connection.png");
            Set("pairCode", pairingCode); FocusAndEnter("pairCode");
            if (!await WaitForTextAsync("connectionStatus", value => value.StartsWith(UiText.Connected, StringComparison.Ordinal), 60))
                throw new InvalidOperationException("Pairing failed: " + TryValue("connectionStatus"));
            await WaitForTextAsync("liveBadge", value => value.Contains(UiText.Live), 30);

            foreach (var language in new[] { (Name: "Français", Code: "fr"), (Name: "Español", Code: "es"), (Name: "English", Code: "en") })
            {
                Click("navScreen");
                await WaitForTextAsync("liveBadge", value => value.Contains(UiText.Live), 30);
                var before = StreamConnections();
                await SelectInterfaceLanguageAsync(language.Name, language.Code);
                await Task.Delay(1100, stop.Token);
                var after = StreamConnections();
                CheckLanguageState(language.Code + ".session", product.Id == controllerPid && !loopbackAgent.HasExited
                    && Value(Element("connectionStatus")) == UiText.Connected && TryValue("headerTitle") == UiText.RemoteScreen,
                    "Language switches immediately in the same process, tab and authenticated session");
                var live = await WaitForLiveEvidenceAsync(10);
                CheckLanguageState(language.Code + ".stream", before.Intersect(after).Any() && live.BadgeVisible && live.TelemetryVisible,
                    "The live stream keeps its established TCP connection and fresh-frame telemetry", new { before, after, live });
                CaptureDesktop("language-selector-" + language.Code + "-live.png");
                Click("navProcesses");
                await WaitForTextAsync("resourceState", value => value == UiText.MeasurementComplete, 30);
                CheckLocalizedText("selector." + language.Code + ".measurements", "resourceState", UiText.MeasurementComplete);
                AuditControlAlignment("processes", "language-selector-" + language.Code, GetDpiForWindow(Root().Current.NativeWindowHandle));
                CaptureDesktop("language-selector-" + language.Code + "-processes.png");
                Click("navFiles");
                Set("destination", "deployments/keep-language-choice");
                CheckLocalizedText("selector." + language.Code + ".upload", "upload", UiText.UploadFile);
                CaptureDesktop("language-selector-" + language.Code + "-files.png");
            }

            Click("navDiagnostics"); Set("arguments", "{"); Click("execute");
            string diagnosticResult = TryValue("output");
            await SelectInterfaceLanguageAsync("Español", "es");
            CheckLocalizedText("selector.invalid_json", "diagnosticState", UiText.InvalidJsonArguments);
            CheckLanguageState("diagnostic_preserved", TryValue("arguments") == "{" && TryValue("output") == diagnosticResult,
                "Language changes preserve the diagnostic editor and original technical error result");
            CaptureDesktop("language-selector-spanish-diagnostic.png");
            Click("navFiles");
            CheckLanguageState("destination_preserved", TryValue("destination") == "deployments/keep-language-choice",
                "The file destination survives a language change and tab navigation");
            Click("navScreen"); await WaitForTextAsync("liveBadge", value => value.Contains(UiText.Live), 30);
            Click("pauseViewing");
            await SelectInterfaceLanguageAsync("Français", "fr");
            CheckLocalizedText("selector.paused", "streamOverlay", UiText.ViewingPausedResume);
            CheckLocalizedText("selector.resume", "pauseViewing", UiText.Resume);
            Click("terminateSession");
            product = loopbackAgent; UiCulture.Apply(CultureInfo.GetCultureInfo("es"));
            await WaitForTextAsync("agentHeading", value => value == UiText.SupportEnded, 20);
            await SelectInterfaceLanguageAsync("English", "en");
            CheckLocalizedText("selector.ended", "agentHeading", UiText.SupportEnded);
            await QuitLocalizedProductAsync("selector.agent_tray"); loopbackAgent = null;
            product = loopbackController; UiCulture.Apply(CultureInfo.GetCultureInfo("fr"));
            await QuitLocalizedProductAsync("selector.controller_tray"); loopbackController = null;

            // An ordinary launch, with no language argument, restores the GUI choice.
            product = loopbackController = LaunchLoopbackProduct(false, controllerRoot, null);
            await WaitUiAsync(); Native.FocusWindow(product.Id);
            CheckLocalizedText("selector.saved", "languageSelector", "Français");
            CheckLocalizedText("selector.saved_title", "headerTitle", "Connexion");
            await SelectInterfaceLanguageAsync(UiText.SystemDefault, UiCulture.Resolve(systemLanguage).Name);
            CheckLocalizedText("selector.system", "languageSelector", UiText.SystemDefault);
            CaptureDesktop("language-selector-system.png");
            await QuitLocalizedProductAsync("selector.system_tray"); loopbackController = null;
            product = loopbackController = LaunchLoopbackProduct(false, controllerRoot, null);
            await WaitUiAsync();
            CheckLocalizedText("selector.system_saved", "languageSelector", UiText.SystemDefault);
            await QuitLocalizedProductAsync("selector.final_tray"); product = loopbackController = null;
            await FinishAsync();
        }
        finally
        {
            foreach (var process in new[] { loopbackAgent, loopbackController })
                if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            CultureInfo.CurrentUICulture = systemLanguage;
        }
    }

    private async Task SelectInterfaceLanguageAsync(string name, string culture)
    {
        Native.FocusWindow(product!.Id);
        await Task.Delay(200, stop.Token);
        var combo = Element("languageSelector");
        ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        await Task.Delay(150, stop.Token);
        var option = AutomationElement.RootElement.FindAll(TreeScope.Descendants,
            new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, product!.Id), new PropertyCondition(AutomationElement.NameProperty, name)))
            .Cast<AutomationElement>().FirstOrDefault(item => item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _));
        if (option == null) throw new InvalidOperationException("Language option missing: " + name);
        ((SelectionItemPattern)option.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse();
        UiCulture.Apply(CultureInfo.GetCultureInfo(culture));
        await Task.Delay(300, stop.Token);
        CheckLocalizedText("selector.label." + serial++, "languageLabel", UiText.Language);
    }

    private void CheckLanguageState(string id, bool passed, string requirement, object? evidence = null)
    {
        if (passed) Pass("language-selector." + id, requirement, evidence);
        else Fail("language-selector." + id, requirement, evidence);
    }

    private static string[] StreamConnections() => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
        .Where(connection => connection.State == TcpState.Established && connection.RemoteEndPoint.Port == 45832)
        .Select(connection => connection.LocalEndPoint + " -> " + connection.RemoteEndPoint).ToArray();
}
