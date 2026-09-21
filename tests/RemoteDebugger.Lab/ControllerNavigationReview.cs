using System.IO;
using RemoteDebugger;
using RemoteDebugger.Core;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private static readonly string[] OperationalNavigation = ["navScreen", "navProcesses", "navFiles", "navDiagnostics"];

    private async Task LoopbackNavigationAsync()
    {
        if (scope != "runtime" || IsUpdateVariant) throw new ArgumentException("Loopback navigation only accepts the Runtime/None configuration.");

        try
        {
            string root = Path.Combine(output, "loopback-navigation");
            string agentRoot = Path.Combine(root, "agent-data");
            string controllerRoot = Path.Combine(root, "controller-data");
            Directory.CreateDirectory(agentRoot);
            Directory.CreateDirectory(controllerRoot);
            try { if (File.Exists(RemoteClient.DefaultPath)) File.Delete(RemoteClient.DefaultPath); } catch (IOException) { }

            loopbackAgent = LaunchLoopbackProduct(true, agentRoot);
            product = loopbackAgent;
            await WaitUiAsync();
            string pairingCode = await WaitPairingCodeAsync();

            loopbackController = LaunchLoopbackProduct(false, controllerRoot);
            product = loopbackController;
            await WaitUiAsync();
            WindowState = System.Windows.Forms.FormWindowState.Minimized;
            await CheckControllerNavigationAsync("navigation.disconnected", connected: false, connectionPage: true,
                "The operational tabs are unavailable before a support session");

            await ConnectNavigationSessionAsync(pairingCode, "navigation.connected");
            string savedToken = RemoteClient.Load().Connection.Token;
            loopbackController!.Kill(entireProcessTree: true);
            await loopbackController.WaitForExitAsync(stop.Token);
            product = loopbackController = LaunchLoopbackProduct(true, controllerRoot);
            await WaitUiAsync();
            Click("roleController");
            bool resumed = await WaitForTextAsync("connectionStatus", IsConnected, 60);
            if (!resumed || RemoteClient.Load().Connection.Token != savedToken)
                throw new IOException("Take control did not resume the saved session after an ordinary launch.");
            Pass("navigation.saved_session_resume", "Take control resumes the existing authenticated session without pairing again");
            CaptureDesktop("navigation-saved-session-resumed.png");
            await OpenControllerPageAsync("navDiagnostics");
            Click("terminateSession");
            bool controllerDisconnected = await WaitForTextAsync("connectionStatus", text => !IsConnected(text), 30);
            product = loopbackAgent;
            string restartedCode = await WaitPairingCodeAsync();
            product = loopbackController;
            bool controllerEnded = await CheckControllerNavigationAsync("navigation.controller_ended", connected: false, connectionPage: true,
                "Ending support on the controller returns it to Connection and disables the operational tabs");
            CaptureDesktop("navigation-controller-ended.png");
            if (!controllerDisconnected || !SixDigits.IsMatch(restartedCode) || !controllerEnded)
                Fail("navigation.controller_end_state", "The controller termination completed and the assisted PC issued a fresh invitation",
                    new { controllerDisconnected, restartedCode = CodeEvidence(restartedCode), controllerEnded });
            else Pass("navigation.controller_end_state", "The controller termination completed and the assisted PC issued a fresh invitation",
                new { controllerDisconnected, restartedCode = CodeEvidence(restartedCode), controllerEnded });

            await ConnectNavigationSessionAsync(restartedCode, "navigation.reconnected");
            await OpenControllerPageAsync("navFiles");
            product = loopbackAgent;
            Native.FocusWindow(product.Id);
            Click("terminateSession");
            string restartedAfterAgentEnd = await WaitPairingCodeAsync();
            product = loopbackController;
            bool agentEndedDisconnect = await WaitForTextAsync("connectionStatus", text => !IsConnected(text), 35);
            bool agentEnded = await CheckControllerNavigationAsync("navigation.agent_ended", connected: false, connectionPage: true,
                "Ending support on the assisted PC returns the controller to Connection and disables the operational tabs");
            CaptureDesktop("navigation-agent-ended.png");
            if (!agentEndedDisconnect || !SixDigits.IsMatch(restartedAfterAgentEnd) || !agentEnded)
                Fail("navigation.agent_end_state", "The assisted-PC termination reached the controller and issued a fresh invitation",
                    new { agentEndedDisconnect, restartedCode = CodeEvidence(restartedAfterAgentEnd), agentEnded });
            else Pass("navigation.agent_end_state", "The assisted-PC termination reached the controller and issued a fresh invitation",
                new { agentEndedDisconnect, restartedCode = CodeEvidence(restartedAfterAgentEnd), agentEnded });

            await FinishAsync();
        }
        finally
        {
            await CleanupLoopbackProcessesAsync();
        }
    }

    private async Task ConnectNavigationSessionAsync(string pairingCode, string checkId)
    {
        product = loopbackController ?? throw new InvalidOperationException("Controller missing.");
        Native.FocusWindow(product.Id);
        Set("host", "127.0.0.1");
        Set("pairCode", pairingCode);
        FocusAndEnter("pairCode");
        bool connected = await WaitForTextAsync("connectionStatus", IsConnected, 60);
        bool navigation = connected && await CheckControllerNavigationAsync(checkId, connected: true, connectionPage: false,
            "The operational tabs are available during an established support session");
        if (!connected || !navigation) throw new InvalidOperationException("The navigation test session did not connect.");
    }

    private async Task OpenControllerPageAsync(string navigationId)
    {
        Click(navigationId);
        await WaitForUiAsync(() => TryValue("headerTitle") == TryValue(navigationId), 10);
    }

    private async Task<bool> CheckControllerNavigationAsync(string id, bool connected, bool connectionPage, string requirement)
    {
        product = loopbackController ?? throw new InvalidOperationException("Controller missing.");
        Native.FocusWindow(product.Id);
        bool matched;
        try
        {
            await WaitForUiAsync(() => ControllerNavigationMatches(connected, connectionPage), 10);
            matched = true;
        }
        catch (TimeoutException)
        {
            matched = false;
        }

        var enabled = new[] { "navConnection" }.Concat(OperationalNavigation)
            .ToDictionary(key => key, NavigationEnabled);
        var evidence = new { connected, connectionPage, header = TryValue("headerTitle"), connectionLabel = TryValue("navConnection"), enabled };
        if (matched) Pass(id, requirement, evidence);
        else Fail(id, requirement, evidence);
        return matched;
    }

    private bool ControllerNavigationMatches(bool connected, bool connectionPage)
    {
        bool? connectionEnabled = NavigationEnabled("navConnection");
        bool operationalState = OperationalNavigation.All(key => NavigationEnabled(key) == connected);
        bool pageState = !connectionPage || TryValue("headerTitle") == TryValue("navConnection");
        return connectionEnabled == true && operationalState && pageState;
    }

    private bool? NavigationEnabled(string key)
    {
        try { return Element(key).Current.IsEnabled; }
        catch (Exception) { return null; }
    }
}
