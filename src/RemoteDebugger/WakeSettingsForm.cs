using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

internal sealed partial class WakeSettingsForm : Forms.Form
{
    private sealed record Sender(string Fingerprint, string Name) { public override string ToString() => Name; }
    internal WakeSettings? Settings { get; private set; }

    internal WakeSettingsForm(string name, WakeSettings? saved, IEnumerable<Peer> helpers)
    {
        Name = "wakeSettingsDialog"; Text = UiText.Format(UiText.WakeSettingsTitle, name);
        Font = new Font("Segoe UI", 13.5f); AutoScaleMode = Forms.AutoScaleMode.Dpi;
        ClientSize = new Size(668, 726); BackColor = Color.White; FormBorderStyle = Forms.FormBorderStyle.None; ShowInTaskbar = false;
        StartPosition = Forms.FormStartPosition.CenterParent; MaximizeBox = false; MinimizeBox = false;
        var mac = new Forms.TextBox { Name = "wakeMac", Dock = Forms.DockStyle.Top, MaxLength = 17,
            Text = saved?.MacAddress ?? "", PlaceholderText = "00:11:22:33:44:55", AccessibleName = UiText.WakeMac };
        var sender = new ConnectionComboBox { Name = "wakeSender", Dock = Forms.DockStyle.Top, DropDownStyle = Forms.ComboBoxStyle.DropDownList,
            AccessibleName = UiText.WakeSender };
        sender.Items.Add(new Sender("", UiText.WakeThisPc + " · " + Environment.MachineName));
        foreach (var peer in helpers) sender.Items.Add(new Sender(peer.Fingerprint, peer.Name));
        sender.SelectedIndex = 0;
        if (saved?.HelperFingerprint is { Length: > 0 } fingerprint)
        {
            var match = sender.Items.Cast<Sender>().FirstOrDefault(s => s.Fingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase));
            if (match == null) { match = new Sender(fingerprint, UiText.WakeHelperUnavailable); sender.Items.Add(match); }
            sender.SelectedItem = match;
        }
        var destination = new Forms.TextBox { Name = "wakeDestination", Dock = Forms.DockStyle.Top, MaxLength = 253,
            Text = saved?.Destination ?? "", PlaceholderText = "192.168.1.255", AccessibleName = UiText.WakeDestination };
        var port = new Forms.NumericUpDown { Name = "wakePort", Minimum = 1, Maximum = 65535, Value = saved?.Port ?? 9,
            Width = 120, AccessibleName = UiText.WakePort };
        var help = ModalHelp("WakePrerequisite", "wakeHelp");
        var state = new Forms.Label { Name = "wakeSettingsState", AutoSize = true, ForeColor = Color.Firebrick, Dock = Forms.DockStyle.Top, Visible = false };
        var save = ModalAction(UiText.Get("ConnectionSaveSettings"), "saveWakeSettings", true);
        var cancel = ModalAction(UiText.Cancel, "cancelWakeSettings", false); cancel.DialogResult = Forms.DialogResult.Cancel;
        BuildModal(name, mac, sender, destination, port, help, state, save, cancel);
        void RefreshSender() => destination.Enabled = sender.SelectedItem is Sender { Fingerprint.Length: 0 };
        sender.SelectedIndexChanged += (_, _) => RefreshSender(); RefreshSender();
        save.Click += (_, _) =>
        {
            try
            {
                Settings = DeviceWakeSettings.Validate(new(mac.Text, destination.Text, (int)port.Value, ((Sender)sender.SelectedItem!).Fingerprint));
                DialogResult = Forms.DialogResult.OK; Close();
            }
            catch (ArgumentException ex) { state.Text = ex.Message; }
        };
        AcceptButton = save; CancelButton = cancel;
    }
}
