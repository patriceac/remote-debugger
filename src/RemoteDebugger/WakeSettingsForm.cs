using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

internal sealed partial class WakeSettingsForm : Forms.Form
{
    internal WakeSettings? Settings { get; private set; }

    internal WakeSettingsForm(string name, WakeSettings? saved)
    {
        Name = "wakeSettingsDialog"; Text = UiText.Format(UiText.WakeSettingsTitle, name);
        Font = new Font("Segoe UI", 13.5f); AutoScaleMode = Forms.AutoScaleMode.Dpi;
        ClientSize = new Size(668, 726); BackColor = Color.White; FormBorderStyle = Forms.FormBorderStyle.None; ShowInTaskbar = false;
        StartPosition = Forms.FormStartPosition.CenterParent; MaximizeBox = false; MinimizeBox = false;
        var mac = new Forms.TextBox { Name = "wakeMac", Dock = Forms.DockStyle.Top, MaxLength = 17,
            Text = saved?.MacAddress ?? "", PlaceholderText = "00:11:22:33:44:55", AccessibleName = UiText.WakeMac };
        var destination = new Forms.TextBox { Name = "wakeDestination", Dock = Forms.DockStyle.Top, MaxLength = 253,
            Text = saved?.Destination ?? "", PlaceholderText = "192.168.1.255", AccessibleName = UiText.WakeDestination };
        var port = new Forms.NumericUpDown { Name = "wakePort", Minimum = 1, Maximum = 65535, Value = saved?.Port ?? 9,
            Width = 120, AccessibleName = UiText.WakePort };
        var help = ModalHelp("WakePrerequisite", "wakeHelp");
        var state = new Forms.Label { Name = "wakeSettingsState", AutoSize = true, ForeColor = Color.Firebrick, Dock = Forms.DockStyle.Top, Visible = false };
        var save = ModalAction(UiText.Get("ConnectionSaveSettings"), "saveWakeSettings", true);
        var cancel = ModalAction(UiText.Cancel, "cancelWakeSettings", false); cancel.DialogResult = Forms.DialogResult.Cancel;
        BuildModal(name, mac, destination, port, help, state, save, cancel);
        save.Click += (_, _) =>
        {
            try
            {
                Settings = DeviceWakeSettings.Validate(new(mac.Text, destination.Text, (int)port.Value));
                DialogResult = Forms.DialogResult.OK; Close();
            }
            catch (ArgumentException ex) { state.Text = ex.Message; }
        };
        AcceptButton = save; CancelButton = cancel;
    }
}
