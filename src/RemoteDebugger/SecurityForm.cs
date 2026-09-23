using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

internal sealed class SecurityForm : Forms.Form
{
    private readonly string root;
    private readonly SecurityMigrationStore store;
    private readonly Forms.TextBox passphrase = new() { Name = "securityPassphrase", UseSystemPasswordChar = true, MaxLength = 1024, Dock = Forms.DockStyle.Top };
    private readonly Forms.TextBox confirmation = new() { Name = "securityConfirmation", UseSystemPasswordChar = true, MaxLength = 1024, Dock = Forms.DockStyle.Top };
    private readonly Forms.Button create = new() { Name = "securityCreate", AutoSize = true, MinimumSize = new(180, 38), Cursor = Forms.Cursors.Hand };
    private readonly Forms.Button migrate = new() { Name = "securityMigrate", AutoSize = true, MinimumSize = new(180, 38), Cursor = Forms.Cursors.Hand };
    private readonly Forms.Label status = new() { Name = "securityStatus", AutoSize = true, MaximumSize = new(650, 0) };
    private readonly Forms.ListView devices = new() { Name = "securityDevices", View = Forms.View.Details, FullRowSelect = true, Dock = Forms.DockStyle.Fill, Height = 170 };
    private readonly Forms.TableLayoutPanel inputs = new() { AutoSize = true, Dock = Forms.DockStyle.Top, ColumnCount = 1 };
    private readonly CancellationTokenSource lifetime = new();
    private bool busy;
    public bool SettingsChanged { get; private set; }

    public SecurityForm(string root)
    {
        this.root = root; store = new(root);
        Name = "securityWindow"; Text = UiText.SecurityTitle; Font = new Font("Segoe UI", 10.5F);
        BackColor = Color.FromArgb(247, 249, 250); ForeColor = Color.FromArgb(24, 48, 57);
        StartPosition = Forms.FormStartPosition.CenterParent; Width = 780; Height = 650; MinimumSize = new(660, 560);
        AutoScaleMode = Forms.AutoScaleMode.Dpi; MinimizeBox = false; MaximizeBox = false;
        var layout = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, Padding = new(28), ColumnCount = 1, RowCount = 6 };
        for (int i = 0; i < 6; i++) layout.RowStyles.Add(new(i == 4 ? Forms.SizeType.Percent : Forms.SizeType.AutoSize, i == 4 ? 100 : 0));
        layout.ColumnStyles.Add(new(Forms.SizeType.Percent, 100));
        layout.Controls.Add(new Forms.Label { Text = UiText.SecurityTitle, AutoSize = true, Font = new Font(Font.FontFamily, 21, FontStyle.Bold), Margin = new(0, 0, 0, 12) }, 0, 0);
        bool unlock = File.Exists(store.PendingSetupPath);
        var intro = new Forms.Label { Text = unlock ? UiText.SecurityUnlockHelp : UiText.SecuritySetupHelp, AutoSize = true, MaximumSize = new(660, 0), Margin = new(0, 0, 0, 20) };
        layout.Controls.Add(intro, 0, 1);
        inputs.Controls.Add(new Forms.Label { Text = UiText.SecurityPassphrase, AutoSize = true }); inputs.Controls.Add(passphrase);
        if (!unlock)
        {
            inputs.Controls.Add(new Forms.Label { Text = UiText.SecurityConfirmPassphrase, AutoSize = true, Margin = new(0, 12, 0, 3) }); inputs.Controls.Add(confirmation);
        }
        inputs.Margin = new(0, 0, 0, 16); layout.Controls.Add(inputs, 0, 2);
        create.Text = unlock ? UiText.SecurityUnlock : UiText.SecurityCreate;
        create.BackColor = Color.FromArgb(8, 127, 131); create.ForeColor = Color.White; create.FlatStyle = Forms.FlatStyle.Flat;
        create.FlatAppearance.BorderSize = 0;
        migrate.Text = UiText.SecurityMigrate;
        var actions = new Forms.FlowLayoutPanel { AutoSize = true, Dock = Forms.DockStyle.Top, Margin = new(0, 0, 0, 14) };
        actions.Controls.Add(create); actions.Controls.Add(migrate); layout.Controls.Add(actions, 0, 3);
        devices.Columns.Add(UiText.SecurityComputer, 280); devices.Columns.Add(UiText.SecurityState, 320);
        layout.Controls.Add(devices, 0, 4); status.Margin = new(0, 16, 0, 0); layout.Controls.Add(status, 0, 5);
        Controls.Add(layout);
        create.Click += async (_, _) => await CreateOrUnlockAsync(unlock);
        migrate.Click += async (_, _) => await MigrateAsync();
        FormClosing += (_, e) => { if (busy) { lifetime.Cancel(); e.Cancel = true; status.Text = UiText.SecurityStopping; } };
        FormClosed += (_, _) => lifetime.Cancel();
        RefreshState(unlock);
        AppTheme.Apply(this);
    }

    private void RefreshState(bool unlock = false)
    {
        var state = store.Load();
        bool showInputs = unlock || state == null;
        inputs.Visible = showInputs; create.Visible = showInputs;
        create.Enabled = !busy && (unlock || store.Preparation() != null);
        migrate.Visible = !unlock && state != null; migrate.Enabled = !busy;
        devices.Visible = !unlock && state != null;
        if (state != null && !unlock)
        {
            devices.Items.Clear();
            foreach (var device in state.Devices)
                devices.Items.Add(new Forms.ListViewItem([device.Name, Describe(device)]));
        }
        if (!busy) status.Text = unlock ? UiText.SecurityUnlockHelp : state != null ? UiText.SecurityMigrationHelp
            : store.Preparation() == null ? UiText.SecurityPreparing : UiText.SecurityPassphraseHelp;
    }

    private static string Describe(SecurityDevice device) => device.State switch
    {
        "protected" => UiText.SecurityProtected, "updating" => UiText.SecurityUpdating,
        "verifying" => UiText.SecurityVerifying, _ => device.Error.Length > 0 ? UiText.SecurityRetry : UiText.SecurityPending
    };

    private async Task CreateOrUnlockAsync(bool unlock)
    {
        if (!unlock && (passphrase.Text.Length < 16 || passphrase.Text != confirmation.Text)) { status.Text = UiText.SecurityPassphraseMismatch; return; }
        busy = true; create.Enabled = false; migrate.Enabled = false; status.Text = UiText.SecurityWorking;
        string password = passphrase.Text; passphrase.Clear(); confirmation.Clear();
        try
        {
            if (unlock) await Task.Run(() => store.Unlock(password), lifetime.Token);
            else await store.CreateAsync(password, lifetime.Token);
            SettingsChanged = true;
            if (unlock) { busy = false; DialogResult = Forms.DialogResult.OK; Close(); return; }
            busy = false; RefreshState();
        }
        catch (Exception ex) { busy = false; RefreshState(unlock); status.Text = ex is System.Security.Cryptography.CryptographicException ? UiText.SecurityWrongPassphrase : UiText.SecuritySetupFailed; }
        finally { password = ""; busy = false; }
    }

    private async Task MigrateAsync()
    {
        busy = true; migrate.Enabled = false; status.Text = UiText.SecurityWorking;
        try
        {
            await new SecurityMigrationClient(root).RunAsync(new Progress<SecurityDevice>(_ => RefreshState()), lifetime.Token);
            busy = false; RefreshState();
        }
        catch (OperationCanceledException) { busy = false; RefreshState(); status.Text = UiText.SecurityStopped; }
        catch { busy = false; RefreshState(); status.Text = UiText.SecuritySetupFailed; }
    }
}
