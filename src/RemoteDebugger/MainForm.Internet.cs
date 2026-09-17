using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

public sealed partial class MainForm
{
    private readonly Forms.Label internetState = new WorkspaceLabel { Name = "internetState", AutoSize = true, ForeColor = SecondaryText };
    private readonly Forms.TextBox supportId = new() { Name = "supportId", ReadOnly = true, Width = 295, Font = new Font("Consolas", 14), BorderStyle = Forms.BorderStyle.FixedSingle };
    private readonly Forms.Button internetSetup = Button(() => UiText.InternetSetup, "internetSetup", 170);
    private readonly Forms.Button copySupportId = Button(() => UiText.Copy, "copySupportId", 86);
    private string? internetSetupError;
    private bool internetConfigured;

    private Forms.Control BuildInternetSection()
    {
        try { internetConfigured = InternetSettings.Load(root) != null; }
        catch (Exception ex) { internetSetupError = ex.Message; }
        var panel = new Forms.TableLayoutPanel { AutoSize = true, Dock = Forms.DockStyle.Top, ColumnCount = 1, RowCount = 3, Margin = Forms.Padding.Empty };
        panel.Controls.Add(agentSubtitle, 0, 0);
        var row = new Forms.FlowLayoutPanel { AutoSize = true, Dock = Forms.DockStyle.Top, WrapContents = true, Margin = new Forms.Padding(0, 10, 0, 2) };
        row.Controls.Add(supportId); row.Controls.Add(copySupportId); row.Controls.Add(internetSetup);
        panel.Controls.Add(row, 0, 1); panel.Controls.Add(internetState, 0, 2);
        copySupportId.Click += (_, _) => { if (agent?.Internet is { Connected: true } relay) Forms.Clipboard.SetText(relay.SupportId); };
        internetSetup.Click += async (_, _) => await ImportInternetSettingsAsync();
        return panel;
    }

    private async Task ImportInternetSettingsAsync()
    {
        using var picker = new Forms.OpenFileDialog { Filter = "Remote Debugger (*.rdrelay)|*.rdrelay", Title = UiText.InternetSetup };
        if (picker.ShowDialog(this) != Forms.DialogResult.OK) return;
        try
        {
            InternetSettings.Import(picker.FileName, root); internetSetupError = null; internetConfigured = true;
            if (agent != null)
            {
                if (!await StopAgentAsync()) return;
                agentIdle = false; StartAgent(); _ = PrepareAgentAsync();
            }
            UpdateInternetState();
        }
        catch (Exception ex) { internetSetupError = ex.Message; UpdateInternetState(); }
    }

    private void UpdateInternetState()
    {
        var relay = agent?.Internet;
        supportId.Visible = copySupportId.Visible = internetConfigured && !loopbackOnly;
        supportId.Text = relay?.Connected == true && !agentIdle ? relay.SupportId : "";
        copySupportId.Enabled = supportId.Text.Length != 0;
        internetSetup.Enabled = !supportSession && !pairingBusy && agent?.Paired != true;
        internetState.SetText(() => internetSetupError != null ? UiText.InternetUnavailable + ": " + internetSetupError
            : loopbackOnly ? UiText.LocalOnly
            : !internetConfigured ? UiText.InternetSetupRequired
            : agentIdle ? UiText.SupportEnded
            : relay?.Connected == true ? UiText.InternetReady + (relay.PublicIp.Length == 0 ? "" : " · " + UiText.PublicIp + " " + relay.PublicIp)
            : relay?.Error.Length > 0 ? UiText.InternetRetrying : UiText.InternetConnecting);
        internetState.ForeColor = relay?.Connected == true ? ConnectedText : SecondaryText;
        supportId.AccessibleName = UiText.SupportId;
    }
}
