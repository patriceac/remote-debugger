using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

internal sealed class AdminSetupForm : Forms.Form
{
    internal AdminSetupForm(string root)
    {
        Name = "adminSetup"; Text = UiText.AdminSetupTitle;
        Font = new Font("Segoe UI", 11); Size = new(540, 300); MinimumSize = Size;
        StartPosition = Forms.FormStartPosition.CenterScreen; MaximizeBox = false; MinimizeBox = false;
        var layout = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Fill, FlowDirection = Forms.FlowDirection.TopDown, Padding = new(24), WrapContents = false };
        var explanation = new Forms.Label { Text = UiText.AdminSetupHelp, AutoSize = true, MaximumSize = new(470, 0), Margin = new(0, 0, 0, 16) };
        var password = new Forms.TextBox { Name = "adminPassword", UseSystemPasswordChar = true, Width = 470, MaxLength = 1024, AccessibleName = UiText.SecurityPassphrase };
        var activate = new Forms.Button { Name = "activateAdmin", Text = UiText.ActivateAdmin, AutoSize = true, MinimumSize = new(150, 38), Margin = new(0, 16, 0, 10), Cursor = Forms.Cursors.Hand };
        var state = new Forms.Label { Name = "adminSetupState", AutoSize = true, MaximumSize = new(470, 0) };
        layout.Controls.AddRange([explanation, password, activate, state]); Controls.Add(layout); AcceptButton = activate;
        activate.Click += async (_, _) =>
        {
            string secret = password.Text; password.Clear(); activate.Enabled = false;
            try
            {
                var store = new UpdateAdminStore(root);
                await Task.Run(() => store.Restore(store.PendingPath, secret));
                File.Delete(store.PendingPath); DialogResult = Forms.DialogResult.OK; Close();
            }
            catch (Exception) { state.Text = UiText.AdminCredentialRejected; activate.Enabled = true; }
            finally { secret = ""; }
        };
    }
}
