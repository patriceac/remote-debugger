using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text.Json;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private async Task GiveControlRedlineAsync()
    {
        string root = Path.Combine(output, "give-control-presentation-fixture");
        UiCulture.Apply(System.Globalization.CultureInfo.GetCultureInfo("en"));
        Vault.Save(Path.Combine(root, "internet.dpapi"), JsonSerializer.SerializeToUtf8Bytes(new InternetSettings("https://ui.invalid", new string('a', 64), new string('b', 64)), Json.Options));
        AdminMaintenancePreference.Save(root, false);
        using var form = new MainForm(startAgent: false, dataRoot: root, loopbackOnly: true);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        T Get<T>(string name) => (T)typeof(MainForm).GetField(name, flags)!.GetValue(form)!;
        void Set(string name, object value) => typeof(MainForm).GetField(name, flags)!.SetValue(form, value);
        void Call(string name, params object?[] args) => typeof(MainForm).GetMethod(name, flags)!.Invoke(form, args);
        Forms.Control Find(string name) => form.Controls.Find(name, true).Single();
        void Require(bool ok, string id) { if (!ok) throw new IOException(id); Pass(id, id); }
        void Capture(string name)
        {
            using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(form.PointToScreen(Point.Empty), Point.Empty, bitmap.Size);
            bitmap.Save(Path.Combine(output, name + ".png"));
        }
        IEnumerable<Forms.Control> Children(Forms.Control parent) => parent.Controls.Cast<Forms.Control>().SelectMany(child => new[] { child }.Concat(Children(child)));
        void WaitingFixture()
        {
            // Synthetic presentation only; no remote peer or production listener is started.
            Get<Forms.Label>("agentHeading").SetText(() => UiText.Get("GiveReady"));
            Get<Forms.Label>("agentSubtitle").SetText(() => UiText.Get("GiveWaiting"));
            Get<Forms.Label>("agentSessionNote").SetText(() => UiText.Get("GiveAuthorizedOnly"));
            Get<Forms.Label>("agentNetworkState").SetText(() => UiText.Ready);
            Get<Forms.Label>("agentSleepState").SetText(() => UiText.Get("GiveSleepAllowed"));
            foreach (string field in new[] { "copyAgentCode", "enableSupport", "restartAgent", "pairingCountdown", "pairingCountdownText", "setupNotice" }) Get<Forms.Control>(field).Visible = false;
            Call("UpdateAgentPresentation"); Call("UpdateHeader"); Call("RefreshFooter");
        }
        Set("quitting", true); form.Show(); form.Location = Point.Empty; form.Activate(); await Task.Delay(150, stop.Token);
        Get<Forms.Timer>("renderTimer").Stop(); Get<Forms.Timer>("resourceRefreshTimer").Stop(); Call("SelectRole", 0);
        try
        {
            var viewport = (Forms.Panel)Find("agentWorkspace");
            var toggle = Get<Forms.CheckBox>("adminMaintenanceToggle");
            var language = Get<Forms.ComboBox>("languageSelector");
            foreach (var size in new[] { new Size(1584, 992), new Size(1280, 860), new Size(1060, 720) })
            {
                if (size.Width == 1584) form.ClientSize = new(1584, 960); else form.Size = size;
                WaitingFixture(); viewport.AutoScrollPosition = Point.Empty;
                await Task.Delay(150, stop.Token); Capture("give-waiting-" + size.Width);
                File.WriteAllText(Path.Combine(output, "give-layout-" + size.Width + ".json"), JsonSerializer.Serialize(Children(form).Select(control => new { control.Name, Parent = control.Parent?.Name, control.Bounds, control.Visible, control.Text }), new JsonSerializerOptions { WriteIndented = true }));
                Require(!viewport.HorizontalScroll.Visible, "give.no_horizontal_scroll_" + size.Width);
                Require(!Get<Forms.Control>("statusPill").Visible && !Get<Forms.Control>("terminateSession").Visible, "give.no_duplicate_waiting_or_end_action_" + size.Width);
                Require(Find("agentInstructions").Visible && !Get<Forms.Label>("agentPairCode").Visible, "give.private_instructions_" + size.Width);
                var right = Find("agentPermissions");
                foreach (string id in new[] { "agentNetworkState", "agentSleepState", "adminMaintenanceToggle", "agentMaintenanceHelp" })
                    Require(right.ClientRectangle.Contains(Find(id).Bounds), "give.permissions_contained_" + id + "_" + size.Width);
                viewport.ScrollControlIntoView(toggle);
                await Task.Delay(80, stop.Token);
                File.WriteAllText(Path.Combine(output, "give-scroll-" + size.Width + ".json"), JsonSerializer.Serialize(new
                {
                    viewport.AutoScrollPosition, viewport.AutoScrollMinSize, viewport.DisplayRectangle,
                    Viewport = viewport.RectangleToScreen(viewport.ClientRectangle), Toggle = toggle.RectangleToScreen(toggle.ClientRectangle)
                }, new JsonSerializerOptions { WriteIndented = true }));
                if (size.Width == 1060) Capture("give-settings-1060");
                Require(viewport.RectangleToScreen(viewport.ClientRectangle).Contains(toggle.RectangleToScreen(toggle.ClientRectangle)), "give.switch_reachable_" + size.Width);
                if (size.Width == 1584)
                {
                    Require(!viewport.VerticalScroll.Visible, "give.reference_fits_without_scrolling");
                    File.WriteAllText(Path.Combine(output, "give-layout.json"), JsonSerializer.Serialize(Children(form).Select(control => new { control.Name, Parent = control.Parent?.Name, control.Bounds, control.Visible, control.Text }), new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            foreach (string culture in new[] { "fr", "es" })
            {
                language.SelectedIndex = culture == "fr" ? 2 : 3; WaitingFixture();
                viewport.ScrollControlIntoView(Find("agentMaintenanceHelp")); await Task.Delay(100, stop.Token); Capture("give-compact-" + culture);
                Require(!viewport.HorizontalScroll.Visible && Find("agentMaintenanceHelp").Height >= Find("agentMaintenanceHelp").GetPreferredSize(new(Find("agentMaintenanceHelp").Width, 0)).Height, "give.localized_help_readable_" + culture);
            }
            language.SelectedIndex = 0;
            toggle.Checked = true; await Task.Delay(80, stop.Token);
            Require(AdminMaintenancePreference.Load(root) && Get<bool>("adminMaintenanceEnabled"), "give.maintenance_switch_saves_enabled");
            toggle.Checked = false; await Task.Delay(80, stop.Token);
            Require(!AdminMaintenancePreference.Load(root) && !Get<bool>("adminMaintenanceEnabled"), "give.maintenance_switch_saves_disabled");
            Set("agentIdle", true); Call("UpdateAgentState"); Call("UpdatePrivateAgentState"); Call("UpdateAgentPresentation");
            Require(!Find("agentInstructions").Visible && Get<Forms.Label>("agentHeading").Text == UiText.SupportEnded, "give.ended_state_preserved");
            Set("internetConfigured", false); Set("agentIdle", false); Call("UpdateAgentState"); Call("UpdateAgentPresentation");
            Require(Get<Forms.Label>("agentPairCode").Visible && Get<Forms.Button>("copyAgentCode").Visible, "give.lan_pairing_controls_preserved");
            Set("internetConfigured", true); form.ClientSize = new(1584, 960); WaitingFixture(); viewport.AutoScrollPosition = Point.Empty;
            // Match the reference's example identity; this does not grant an admin role.
            foreach (var label in Children(form).OfType<Forms.Label>().Where(label => label.Text == Environment.MachineName)) label.SetText("PC-PATRICE");
            var second = (AgentInstructionLabel)Find("agentInstructionTwo"); second.Emphasis = "PC-PATRICE"; second.SetText(UiText.Format(UiText.Get("GiveInstructionTwo"), "PC-PATRICE"));
            await Task.Delay(150, stop.Token); Capture("give-reference");
        }
        finally { form.Close(); }
        await FinishAsync();
    }
}
