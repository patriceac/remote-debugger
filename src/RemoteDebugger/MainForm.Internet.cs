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
        enableSupport.Visible = (agent == null || agentIdle) && !privateSupportEnabled;
        restartAgent.Visible = false; copyAgentCode.Visible = false;
        pairingCountdownText.Visible = agentIdle;
        if (agent?.Session.HasPaired == true) return;
        bool active = agent != null && !agentIdle;
        agentHeading.SetText(() => agentIdle ? UiText.SupportEnded : active ? UiText.PrivateSupportReady : UiText.EnableOnThisPc);
        agentEyebrow.SetText(() => UiText.GiveControl);
        agentSubtitle.SetText(() => active ? UiText.InternetInstructions : UiText.PrivateEnableInstructions);
        agentState.SetText(() => active ? UiText.WaitingForConnection : UiText.NoActiveConnection);
        agentSessionNote.SetText(() => active ? UiText.PrivateAccessNote : UiText.PcNoLongerAccessible);
        agentNetworkState.SetText(() => agent?.Internet?.Connected == true ? UiText.Ready : active ? UiText.Preparing : UiText.Inactive);
        agentNetworkState.ForeColor = agent?.Internet?.Connected == true ? ConnectedText : SecondaryText;
        SetFooterMessage(() => agentIdle ? UiText.SupportEnded : active ? UiText.WaitingForConnection : UiText.NoActiveConnection);
        setupNotice.Visible = false;
    }

    private Forms.Control BuildInternetSection()
    {
        try { internetConfigured = InternetSettings.Load(root) != null || File.Exists(new SecurityMigrationStore(root).PendingSetupPath); }
        catch (Exception ex) { internetSetupError = ex.Message; }
        var panel = new Forms.TableLayoutPanel { AutoSize = true, Dock = Forms.DockStyle.Top, ColumnCount = 1, RowCount = 2, Margin = Forms.Padding.Empty };
        panel.Controls.Add(agentSubtitle, 0, 0);
        internetState.Margin = new Forms.Padding(0, 10, 0, 2);
        panel.Controls.Add(internetState, 0, 1);
        return panel;
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
