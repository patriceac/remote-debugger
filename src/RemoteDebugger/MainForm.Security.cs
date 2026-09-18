using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

public sealed partial class MainForm
{
    internal void ShowSecuritySetup()
    {
        if (supportSession || agent is { IsListening: true })
        {
            SetFooterMessage(() => UiText.SecuritySessionActive); RefreshFooter(); return;
        }
        using var dialog = new SecurityForm(root);
        dialog.ShowDialog(this);
        if (!dialog.SettingsChanged) return;
        internetConfigured = InternetSettings.Load(root) != null;
        privateSupportEnabled = HasConfiguredPrivateSupport();
        if (agent != null) { agent.Dispose(); agent = null; StartAgent(); _ = PrepareAgentAsync(); }
        else if (rolePages.SelectedIndex == 0 && privateSupportEnabled) { StartAgent(); _ = PrepareAgentAsync(); }
        RefreshUiState();
    }
}
