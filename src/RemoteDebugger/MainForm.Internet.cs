using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

public sealed partial class MainForm
{
    private readonly Forms.Label internetState = new WorkspaceLabel { Name = "internetState", AutoSize = true, ForeColor = SecondaryText };
    private string? internetSetupError;
    private bool internetConfigured;
    private bool PrivateInternet => internetConfigured;
    private bool privateSupportEnabled;
    private readonly Forms.Button enableSupport = Button(() => UiText.EnableOnThisPc, "enableSupport", 200, primary: true);

    private bool HasConfiguredPrivateSupport()
    {
        if (!PrivateInternet || File.Exists(new SecurityMigrationStore(root).PendingSetupPath)) return false;
        try { return InternetSettings.Load(root) != null; }
        catch { return false; }
    }

    private async Task EnablePrivateSupportAsync()
    {
        if (File.Exists(new SecurityMigrationStore(root).PendingSetupPath)) { ShowSecuritySetup(); return; }
        enableSupport.Enabled = false;
        try
        {
            var status = await SupportPlatform.GetStatusAsync();
            if (!status.Available) status = await SupportPlatform.ProvisionAsync(enableSupport: true);
            if (quitting) return;
            if (!status.Available) throw new IOException(status.Message);
            privateSupportEnabled = true; internetSetupError = null;
            agent?.Dispose(); agent = null; agentIdle = false;
            StartAgent(); await PrepareAgentAsync();
        }
        catch (Exception ex) { internetSetupError = ex.Message; }
        finally { enableSupport.Enabled = true; RefreshUiState(); }
    }

    private void UpdatePrivateAgentState()
    {
        CurrentPairingCode = null;
        bool active = agent != null && !agentIdle;
        bool relayUnavailable = agent?.Internet?.Error.Length > 0;
        bool lanFallbackNeedsSetup = active && !agentNetworkPrepared && relayUnavailable;
        enableSupport.Visible = ((agent == null || agentIdle) && !privateSupportEnabled) || lanFallbackNeedsSetup;
        restartAgent.Visible = false; copyAgentCode.Visible = false;
        bool updateOngoing = agent != null && !agentIdle && MainForm.IsOngoingUpdate(agent.UpdateProgress);
        pairingCountdownText.Visible = agentIdle || updateOngoing || agent?.Session.Connected == true && agent.Operations.FileTransfer != null;
        if (agent?.Session.HasPaired == true || updateOngoing) return;
        bool ready = IsAgentReadyForSupport(active, agent?.Internet?.Connected == true, agentNetworkPrepared);
        agentHeading.SetText(() => agentIdle ? UiText.SupportEnded : ready ? UiText.Get("GiveReady") : active ? UiText.Preparing : UiText.EnableOnThisPc);
        agentEyebrow.SetText(() => UiText.GiveControl);
        agentSubtitle.SetText(() => ready ? UiText.Get("GiveWaiting") : active ? UiText.Get("GivePreparing") : UiText.PrivateEnableInstructions);
        agentState.SetText(() => active ? UiText.WaitingForConnection : UiText.NoActiveConnection);
        agentSessionNote.SetText(() => active ? UiText.Get("GiveAuthorizedOnly") : UiText.PcNoLongerAccessible);
        bool relayConnected = agent?.Internet?.Connected == true;
        agentNetworkState.SetText(() => relayConnected ? UiText.Ready : agentNetworkPrepared ? UiText.LanFallbackReady : active ? UiText.Preparing : UiText.Inactive);
        agentNetworkState.ForeColor = relayConnected || agentNetworkPrepared ? ConnectedText : SecondaryText;
        SetFooterMessage(() => agentIdle ? UiText.SupportEnded : active ? UiText.WaitingForConnection : UiText.NoActiveConnection);
        setupNotice.Visible = false;
    }

    private void LoadInternetMode()
    {
        try { internetConfigured = InternetSettings.Load(root) != null || File.Exists(new SecurityMigrationStore(root).PendingSetupPath); }
        catch (Exception ex) { internetSetupError = ex.Message; }
    }

    private void UpdateInternetState()
    {
        var relay = agent?.Internet;
        internetState.SetText(() => internetSetupError != null ? UiText.InternetUnavailable + ": " + internetSetupError
            : loopbackOnly ? UiText.LocalOnly
            : !internetConfigured ? UiText.InternetSetupRequired
            : agentIdle ? UiText.SupportEnded
            : agent == null ? UiText.Inactive
            : relay?.Connected == true ? UiText.InternetReady
            : relay?.Error.Length > 0 ? UiText.InternetRetrying : UiText.InternetConnecting);
        internetState.ForeColor = relay?.Connected == true ? ConnectedText : SecondaryText;
    }
}
