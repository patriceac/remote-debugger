using System.Drawing;
using System.Globalization;
using System.IO;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private async Task PowerAppearanceAsync()
    {
        WindowState = Forms.FormWindowState.Minimized;
        void Require(bool condition, string id, object? evidence = null)
        {
            if (!condition) throw new IOException(id + ": " + Json.Text(evidence));
            Pass("powerappearance." + id, id, evidence);
        }
        void Fits(Forms.Control parent)
        {
            foreach (Forms.Control child in parent.Controls)
            {
                if (!child.Visible) continue;
                Require(parent.ClientRectangle.Contains(child.Bounds), parent.Name + "." + child.Name + ".fits");
                Fits(child);
            }
        }
        async Task Capture(Forms.Form form, string name)
        {
            form.Activate(); form.Refresh(); await Task.Delay(150, stop.Token);
            using var image = new Bitmap(form.Width, form.Height);
            using (var graphics = Graphics.FromImage(image)) graphics.CopyFromScreen(form.Location, Point.Empty, image.Size);
            image.Save(Path.Combine(output, name + ".png"));
        }
        foreach (string language in new[] { "en", "fr", "es" })
        {
            UiCulture.Apply(CultureInfo.GetCultureInfo(language));
            foreach (string theme in new[] { "dark", "light" })
            {
                AppTheme.SetPreference(theme);
                foreach (bool restart in new[] { true, false })
                {
                    using var dialog = new PowerConfirmationForm(new PowerPreflight("PC-YOLANDE", @"PC-YOLANDE\yolan", true, ""), restart);
                    dialog.Show(); Fits(dialog);
                    Require(dialog.BackColor == (theme == "dark" ? AppTheme.Surface : Color.White), language + theme + restart + ".palette");
                    if (language == "en") await Capture(dialog, theme + (restart ? "-restart" : "-shutdown"));
                    if (restart)
                    {
                        var choice = (Forms.CheckBox)dialog.Controls.Find("oneTimeLogin", true).Single();
                        var password = (Forms.TextBox)dialog.Controls.Find("oneTimePassword", true).Single();
                        Require(!password.Visible && !dialog.OneTimeLogin, "sign_in_off_by_default");
                        choice.Checked = true; Fits(dialog);
                        Require(password.Visible && password.Enabled, "sign_in_expands");
                        if (language == "en") await Capture(dialog, theme + "-restart-expanded");
                    }
                    ((Forms.Button)dialog.CancelButton!).PerformClick();
                    Require(dialog.DialogResult == Forms.DialogResult.Cancel, "cancel");
                }
            }
        }
        UiCulture.Apply(CultureInfo.GetCultureInfo("en")); AppTheme.SetPreference("dark");
        using var table = new Forms.Form { Text = "Table header review", ClientSize = new(1050, 250), Font = new("Segoe UI", 10.5f) };
        var list = new RememberedListView { View = Forms.View.Details, Dock = Forms.DockStyle.Fill, BackColor = Color.White, BorderStyle = Forms.BorderStyle.None };
        list.Columns.Add("PID", 80); list.Columns.Add("Application", 240);
        list.Columns.Add("CPU % ▼", 100, Forms.HorizontalAlignment.Right); list.Columns.Add("RAM MiB", 110, Forms.HorizontalAlignment.Right);
        list.Columns.Add("Responding", 100); list.Columns.Add("Window", 240);
        list.Items.Add(new Forms.ListViewItem(["11236", "msedge", "0.0", "132.3", "—", "—"]));
        list.Items.Add(new Forms.ListViewItem(["11700", "RemoteDebugger", "2.1", "86.8", "Yes", "Remote Debugger"]));
        table.Controls.Add(list); AppTheme.Apply(table); table.Show();
        foreach (string state in new[] { "normal", "reordered", "wide", "theme-restored" })
        {
            if (state == "reordered") list.Columns[1].DisplayIndex = 5;
            if (state == "wide") list.Columns[1].Width = 1100;
            if (state == "theme-restored") { AppTheme.SetPreference("light"); AppTheme.SetPreference("dark"); list.Columns[1].Width = 240; }
            await Capture(table, "dark-table-" + state);
            // Sample the saved evidence inside the header, away from the native window edge.
            using var image = new Bitmap(Path.Combine(output, "dark-table-" + state + ".png"));
            var point = list.PointToScreen(new(list.ClientSize.Width - 32, 8)); point.Offset(-table.Left, -table.Top);
            Color actual = image.GetPixel(point.X, point.Y);
            Require(actual.ToArgb() == AppTheme.Input.ToArgb(), "header_fill_" + state,
                new { point.X, point.Y, actual = actual.ToArgb(), expected = AppTheme.Input.ToArgb() });
        }
        table.Close(); await FinishAsync();
    }
}
