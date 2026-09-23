using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

internal sealed partial class WakeSettingsForm
{
    private static readonly Color Ink = Color.FromArgb(24, 38, 62), Muted = Color.FromArgb(105, 124, 158), Line = Color.FromArgb(223, 230, 234);
    private Size roundedModalSize;
    private void BuildModal(string name, Forms.TextBox mac, Forms.TextBox destination,
        Forms.NumericUpDown port, Forms.Label help, Forms.Label state, Forms.Button save, Forms.Button cancel)
    {
        var header = new Forms.Panel { Dock = Forms.DockStyle.Top, Height = 118 };
        var title = ModalLabel(UiText.WakeSettings, "wakeTitle", 22.5f, true); title.Dock = Forms.DockStyle.None; title.Location = new(102, 30);
        var target = ModalLabel(name, "wakeTarget", 14.5f); target.Dock = Forms.DockStyle.None; target.Location = new(102, 72); target.ForeColor = Muted;
        var icon = UiGlyph.Icon(UiGlyph.Wake, 44, Ink); icon.Location = new(34, 36);
        var close = ModalAction("", "closeWakeSettings", false); close.Glyph = UiGlyph.Close; close.AutoSize = false; close.MinimumSize = Size.Empty; close.Size = new(40, 40);
        close.Padding = Forms.Padding.Empty;
        close.Font = new Font("Segoe UI", 20); close.FlatAppearance.BorderSize = 0; close.Anchor = Forms.AnchorStyles.Top | Forms.AnchorStyles.Right;
        header.Size = new(ClientSize.Width, 118); close.Location = new(header.Width - 60, 18); close.AccessibleName = UiText.Cancel;
        close.Click += (_, _) => { DialogResult = Forms.DialogResult.Cancel; Close(); };
        header.Controls.AddRange([title, target, icon, close]);
        header.Paint += (_, e) => { using var pen = new Pen(Line); e.Graphics.DrawLine(pen, 34, header.Height - 1, header.Width - 34, header.Height - 1); };
        var body = new Forms.Panel { Name = "wakeSettingsBody", Dock = Forms.DockStyle.Fill, AutoScroll = true, Padding = new(34, 32, 34, 20) };
        var layout = new ConnectionLayoutPanel { Dock = Forms.DockStyle.Top, AutoSize = true, AutoSizeMode = Forms.AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 0, Margin = Forms.Padding.Empty };
        layout.ColumnStyles.Add(new(Forms.SizeType.Percent, 100));
        foreach (var edit in new[] { mac, destination }) { edit.BorderStyle = Forms.BorderStyle.None; edit.BackColor = Color.White; edit.ForeColor = Ink; }
        port.BorderStyle = Forms.BorderStyle.None;
        Add(ModalLabel(UiText.WakeMac, "wakeMacLabel")); Add(ModalField(mac), 8);
        var addressRow = new ConnectionLayoutPanel { Dock = Forms.DockStyle.Top, AutoSize = true, AutoSizeMode = Forms.AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 2, Margin = Forms.Padding.Empty };
        addressRow.RowStyles.Add(new(Forms.SizeType.AutoSize)); addressRow.RowStyles.Add(new(Forms.SizeType.AutoSize));
        addressRow.ColumnStyles.Add(new(Forms.SizeType.Percent, 72)); addressRow.ColumnStyles.Add(new(Forms.SizeType.Percent, 28));
        var addressLabel = ModalLabel(UiText.WakeDestination, "wakeDestinationLabel"); addressLabel.Margin = new(0, 0, 20, 8);
        var addressField = ModalField(destination); addressField.Margin = new(0, 0, 20, 0);
        addressRow.Controls.Add(addressLabel, 0, 0); addressRow.Controls.Add(ModalLabel(UiText.WakePort, "wakePortLabel"), 1, 0);
        addressRow.Controls.Add(addressField, 0, 1); addressRow.Controls.Add(ModalField(port), 1, 1); Add(addressRow, 38);
        var broadcastHelp = ModalHelp("WakeDestinationHelp", "wakeDestinationHelp"); Add(broadcastHelp, 6);
        var helpRow = new ConnectionLayoutPanel { Name = "wakePrerequisiteRow", Dock = Forms.DockStyle.Top, AutoSize = true, AutoSizeMode = Forms.AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Size = Size.Empty };
        helpRow.ColumnStyles.Add(new(Forms.SizeType.Absolute, 52)); helpRow.ColumnStyles.Add(new(Forms.SizeType.Percent, 100)); helpRow.RowStyles.Add(new(Forms.SizeType.AutoSize));
        help.ForeColor = Ink; help.Font = new Font("Segoe UI", 12.5f);
        helpRow.Controls.Add(UiGlyph.Icon(UiGlyph.Info, 28, Muted), 0, 0); helpRow.Controls.Add(help, 1, 0);
        Add(helpRow, 38); Add(state, 10); body.Controls.Add(layout);
        void Add(Forms.Control control, int gap = 0)
        {
            int row = layout.RowCount++; layout.RowStyles.Add(new(Forms.SizeType.AutoSize));
            control.Margin = new(0, gap, 0, 0); layout.Controls.Add(control, 0, row);
        }
        void Fit()
        {
            int width = Math.Max(200, body.ClientSize.Width - body.Padding.Horizontal);
            foreach (var label in layout.Controls.OfType<Forms.Label>()) label.MaximumSize = new(width, 0);
            addressLabel.MaximumSize = new(Math.Max(150, width * 72 / 100 - 20), 0); help.MaximumSize = new(width - 52, 0);
            body.AutoScrollMinSize = new(0, layout.Height + body.Padding.Vertical);
        }
        body.SizeChanged += (_, _) => Fit(); layout.SizeChanged += (_, _) => Fit();
        state.TextChanged += (_, _) => { state.Visible = state.Text.Length > 0; if (state.Visible) body.ScrollControlIntoView(state); };
        var footer = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Bottom, Height = 92, Padding = new(34, 16, 34, 26), ColumnCount = 3 };
        footer.ColumnStyles.Add(new(Forms.SizeType.Percent, 100)); footer.ColumnStyles.Add(new(Forms.SizeType.AutoSize)); footer.ColumnStyles.Add(new(Forms.SizeType.AutoSize));
        var note = ModalHelp("WakeAfterSaving", "wakeAfterSaving"); note.Anchor = Forms.AnchorStyles.Left; note.Dock = Forms.DockStyle.None; note.MaximumSize = new(300, 0);
        save.MinimumSize = new(148, 48); cancel.MinimumSize = new(110, 48); save.Font = cancel.Font = new Font("Segoe UI", 12);
        save.Margin = new(14, 0, 0, 0);
        footer.Controls.Add(note, 0, 0); footer.Controls.Add(cancel, 1, 0); footer.Controls.Add(save, 2, 0);
        footer.Paint += (_, e) => { using var pen = new Pen(Line); e.Graphics.DrawLine(pen, 34, 0, footer.Width - 34, 0); };
        Controls.Add(body); Controls.Add(footer); Controls.Add(header);
        SizeChanged += (_, _) => ControlRegions.ApplyRounded(this, ref roundedModalSize, (int)(8 * DeviceDpi / 96f));
        ControlRegions.ApplyRounded(this, ref roundedModalSize, 8);
        Shown += (_, _) => { var area = Forms.Screen.FromControl(this).WorkingArea; Height = Math.Min(Height, area.Height - 32); Fit(); mac.Focus(); };
    }
    internal Forms.DialogResult ShowModal(Forms.Form owner)
    {
        using var shade = new Forms.Form { FormBorderStyle = Forms.FormBorderStyle.None, ShowInTaskbar = false,
            StartPosition = Forms.FormStartPosition.Manual, Bounds = owner.RectangleToScreen(owner.ClientRectangle), BackColor = Color.Black, Opacity = .32 };
        var area = Forms.Screen.FromControl(owner).WorkingArea;
        Height = Math.Min(Height, area.Height - 32); StartPosition = Forms.FormStartPosition.Manual;
        Location = new(Math.Clamp(owner.Left + (owner.Width - Width) / 2, area.Left, area.Right - Width), Math.Clamp(owner.Top + (owner.Height - Height) / 2, area.Top, area.Bottom - Height));
        shade.Show(owner); return ShowDialog(shade);
    }
    private static Forms.Panel ModalField(Forms.Control editor) => ConnectionField.Wrap(editor);
    private static WorkspaceLabel ModalLabel(string text, string name, float size = 12.5f, bool bold = false) => new()
    {
        Name = name, Text = text, AutoSize = true, Dock = Forms.DockStyle.Top, Margin = Forms.Padding.Empty,
        ForeColor = Ink, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular)
    };
    private static WorkspaceLabel ModalHelp(string key, string name)
    {
        var label = ModalLabel(UiText.Get(key), name, 11.5f); label.ForeColor = Muted; return label;
    }
    private static WorkspaceButton ModalAction(string text, string name, bool primary) => new()
    {
        Name = name, Text = text, AutoSize = true, AutoSizeMode = Forms.AutoSizeMode.GrowAndShrink, MinimumSize = new(92, 40), Padding = new(12, 0, 12, 0),
        Margin = new(8, 0, 0, 0), FlatStyle = Forms.FlatStyle.Flat, FlatAppearance = { BorderSize = primary ? 0 : 1, BorderColor = Line },
        BackColor = primary ? Color.FromArgb(0, 143, 153) : Color.White, ForeColor = primary ? Color.White : Ink, UseMnemonic = false
    };
}
