using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

public sealed partial class MainForm
{
    private readonly Forms.TextBox wanAddress = TextBox("wanAddress");
    private readonly Forms.Button saveWanAddress = Button(() => UiText.SaveWanAddress, "saveWanAddress", 104);
    private string wanDeviceFingerprint = "";

    private void BuildWanAddressFields(Forms.TableLayoutPanel panel)
    {
        panel.Controls.Add(new WorkspaceLabel { AutoSize = true, ForeColor = SecondaryText, Margin = Forms.Padding.Empty }
            .WithText(() => UiText.OptionalWanAddress), 0, 2);
        wanAddress.Dock = Forms.DockStyle.Top; wanAddress.Margin = new Forms.Padding(0, 4, 0, 0);
        wanAddress.PlaceholderText = "hostname[:port]";
        panel.Controls.Add(wanAddress, 0, 3);
        var help = new WorkspaceLabel { AutoSize = true, ForeColor = SecondaryText, Margin = new Forms.Padding(0, 4, 0, 12) }
            .WithText(() => UiText.WanAddressHelp);
        panel.SizeChanged += (_, _) => help.MaximumSize = new Size(Math.Max(120, panel.ClientSize.Width - panel.Padding.Horizontal), 0);
        panel.Controls.Add(help, 0, 4);
        saveWanAddress.Click += (_, _) => SaveWanAddress();
        RefreshWanAddress();
    }

    private void RefreshWanAddress()
    {
        string fingerprint = selectedPeer?.Fingerprint ?? "";
        if (fingerprint != wanDeviceFingerprint)
        {
            wanAddress.SetText(DeviceWanAddress.Format(DeviceWanAddress.Load(root, fingerprint)));
            wanDeviceFingerprint = fingerprint;
        }
        wanAddress.Enabled = saveWanAddress.Enabled = PrivateInternet && PairingExchange.ValidHash(fingerprint)
            && !supportSession && !pairingBusy && !FleetBusy && !terminating;
    }

    private bool SaveWanAddress()
    {
        if (!PrivateInternet || !PairingExchange.ValidHash(wanDeviceFingerprint)) return true;
        try
        {
            var address = DeviceWanAddress.Parse(wanAddress.Text);
            if (address != DeviceWanAddress.Load(root, wanDeviceFingerprint)) DeviceWanAddress.Save(root, wanDeviceFingerprint, address);
            wanAddress.SetText(DeviceWanAddress.Format(address));
            connectionState.SetText(() => UiText.WanAddressSaved);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException)
        {
            connectionState.SetText(ex.Message); wanAddress.Focus(); return false;
        }
    }
}
