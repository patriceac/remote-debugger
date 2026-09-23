using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

public sealed partial class MainForm
{
    private readonly Forms.TextBox wanAddress = TextBox("wanAddress");
    private readonly Forms.Button saveWanAddress = Button(() => UiText.SaveWanAddress, "saveWanAddress", 104);
    private string wanDeviceFingerprint = "";

    private Forms.Control BuildWanAddressFields()
    {
        var panel = ConnectionStack("wanSettings");
        ConnectionRow(panel, ConnectionLabel("wanAddressLabel", 10.5f).WithText(() => UiText.OptionalWanAddress));
        var row = new Forms.Panel { Dock = Forms.DockStyle.Top, Height = 38, Margin = Forms.Padding.Empty };
        wanAddress.BorderStyle = Forms.BorderStyle.None;
        wanAddress.PlaceholderText = "hostname[:port]";
        saveWanAddress.AutoSize = false; saveWanAddress.MinimumSize = new(98, 38); saveWanAddress.Margin = Forms.Padding.Empty;
        var address = ConnectionField.Wrap(wanAddress, 38); address.Dock = Forms.DockStyle.None;
        row.Controls.Add(address); row.Controls.Add(saveWanAddress);
        row.SizeChanged += (_, _) =>
        {
            int saveWidth = HeaderPixels(98); saveWanAddress.SetBounds(Math.Max(0, row.Width - saveWidth), 0, saveWidth, row.Height);
            address.SetBounds(0, 0, Math.Max(0, row.Width - saveWidth - HeaderPixels(8)), row.Height);
        };
        ConnectionRow(panel, row, 4);
        var help = ConnectionLabel("wanHelp", 10.5f).WithText(() => UiText.Get("ConnectionWanHelp"));
        panel.SizeChanged += (_, _) => help.MaximumSize = new Size(Math.Max(120, panel.ClientSize.Width - panel.Padding.Horizontal), 0);
        ConnectionRow(panel, help, 4);
        saveWanAddress.Click += (_, _) => SaveWanAddress();
        RefreshWanAddress();
        return panel;
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
