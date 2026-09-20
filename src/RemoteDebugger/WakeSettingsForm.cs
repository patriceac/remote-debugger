using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

internal sealed class WakeSettingsForm : Forms.Form
{
    private sealed record Sender(string Fingerprint, string Name) { public override string ToString() => Name; }
    internal WakeSettings? Settings { get; private set; }

    internal WakeSettingsForm(string name, WakeSettings? saved, IEnumerable<Peer> helpers)
    {
        Name = "wakeSettingsDialog"; Text = UiText.Format(UiText.WakeSettingsTitle, name);
        Font = new Font("Segoe UI", 10.5f); AutoScaleMode = Forms.AutoScaleMode.Dpi;
        ClientSize = new Size(550, 550); MinimumSize = Size;
        StartPosition = Forms.FormStartPosition.CenterParent; MaximizeBox = false; MinimizeBox = false;
        var layout = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, Padding = new(20), ColumnCount = 1, RowCount = 12 };
        layout.ColumnStyles.Add(new(Forms.SizeType.Percent, 100));
        for (int i = 0; i < 11; i++) layout.RowStyles.Add(new(Forms.SizeType.AutoSize));
        layout.RowStyles.Add(new(Forms.SizeType.Percent, 100));
        var mac = new Forms.TextBox { Name = "wakeMac", Dock = Forms.DockStyle.Top, MaxLength = 17,
            Text = saved?.MacAddress ?? "", PlaceholderText = "AA:BB:CC:DD:EE:FF", AccessibleName = UiText.WakeMac };
        var sender = new Forms.ComboBox { Name = "wakeSender", Dock = Forms.DockStyle.Top, DropDownStyle = Forms.ComboBoxStyle.DropDownList,
            AccessibleName = UiText.WakeSender };
        sender.Items.Add(new Sender("", UiText.WakeThisPc));
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
        var help = new Forms.Label { Name = "wakeHelp", Text = UiText.WakeHelp, AutoSize = true, Dock = Forms.DockStyle.Top, Margin = new(0, 12, 0, 8) };
        var state = new Forms.Label { Name = "wakeSettingsState", AutoSize = true, ForeColor = Color.Firebrick, Dock = Forms.DockStyle.Top };
        var save = new Forms.Button { Name = "saveWakeSettings", Text = UiText.SaveWanAddress, AutoSize = true, MinimumSize = new(100, 34), Cursor = Forms.Cursors.Hand };
        var cancel = new Forms.Button { Name = "cancelWakeSettings", Text = UiText.Cancel, AutoSize = true, MinimumSize = new(100, 34), DialogResult = Forms.DialogResult.Cancel, Cursor = Forms.Cursors.Hand };
        var actions = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Top, AutoSize = true, Margin = new(0, 8, 0, 0) };
        actions.Controls.AddRange([save, cancel]);
        void Field(string text, Forms.Control control, int row)
        {
            layout.Controls.Add(new Forms.Label { Text = text, AutoSize = true, Margin = new(0, row == 0 ? 0 : 8, 0, 4) }, 0, row);
            layout.Controls.Add(control, 0, row + 1);
        }
        Field(UiText.WakeMac, mac, 0); Field(UiText.WakeSender, sender, 2);
        Field(UiText.WakeDestination, destination, 4); Field(UiText.WakePort, port, 6);
        layout.Controls.Add(help, 0, 8); layout.Controls.Add(actions, 0, 9); layout.Controls.Add(state, 0, 10);
        layout.SizeChanged += (_, _) => { help.MaximumSize = state.MaximumSize = new(Math.Max(200, layout.ClientSize.Width - 40), 0); };
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
        Controls.Add(layout); AcceptButton = save; CancelButton = cancel;
    }
}
