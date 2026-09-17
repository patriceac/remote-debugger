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
        if (agent != null) { agent.Dispose(); agent = null; StartAgent(); }
        RefreshUiState();
        _ = DiscoverAsync(false);
    }
}
