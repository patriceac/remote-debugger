using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text.Json;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private async Task ConnectionRedlineAsync()
    {
        string root = Path.Combine(output, "connection-presentation-fixture");
        UiCulture.Apply(System.Globalization.CultureInfo.GetCultureInfo("en"));
        Vault.Save(Path.Combine(root, "internet.dpapi"), JsonSerializer.SerializeToUtf8Bytes(new InternetSettings("https://ui.invalid", new string('a', 64), new string('b', 64)), Json.Options));
        using var form = new MainForm(startAgent: false, dataRoot: root, loopbackOnly: true, languageOverride: "en");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        T Get<T>(string name) => (T)typeof(MainForm).GetField(name, flags)!.GetValue(form)!;
        void Set(string name, object value) => typeof(MainForm).GetField(name, flags)!.SetValue(form, value);
        void Call(string name, params object?[] args) => typeof(MainForm).GetMethod(name, flags)!.Invoke(form, args);
        void Capture(string name)
        {
            using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(form.PointToScreen(Point.Empty), Point.Empty, bitmap.Size);
            bitmap.Save(Path.Combine(output, name + ".png"));
        }
        void Require(bool ok, string id) { if (!ok) throw new IOException(id); Pass(id, id); }
        IEnumerable<Forms.Control> Children(Forms.Control parent) => parent.Controls.Cast<Forms.Control>().SelectMany(child => new[] { child }.Concat(Children(child)));
        Set("quitting", true); form.Show(); form.Location = Point.Empty; form.Activate(); await Task.Delay(150, stop.Token);
        Get<Forms.Timer>("renderTimer").Stop(); Get<Forms.Timer>("resourceRefreshTimer").Stop();
        try
        {
            var type = typeof(MainForm).GetNestedType("FleetDevice", BindingFlags.NonPublic)!;
            var first = new Peer("PC-BEDO", "127.0.0.2", 45832, new string('c', 64));
            var selected = new Peer("PC-YOLANDE", "127.0.0.3", 45832, new string('d', 64));
            foreach (var peer in new[] { first, selected }) Call("RecordDevice", Activator.CreateInstance(type, peer, peer == first ? "0.5.0.0" : "0.5.4.0", "", false, "offline", 0, ""), null);
            Call("SelectRole", 1); Call("SelectControllerPage", 0);
            Get<RememberedListView>("peers").Items[1].Selected = true; Call("SelectPeerFromList");
            var toggle = Get<WorkspaceButton>("connectionSettingsToggle");
            foreach (var size in new[] { new Size(1586, 992), new Size(1280, 860), new Size(1060, 720) })
            {
                if (scope == "update-rendering" && size.Width == 1280) continue;
                if (size.Width == 1586) form.ClientSize = new(1586, 960); else form.Size = size;
                Set("pairingBusy", false); Set("synchronizingAgent", false); Get<Forms.Control>("updateProgressArea").Visible = false;
                Call("RefreshControllerControls"); Call("UpdateHeader");
                if (!Get<Forms.Control>("connectionSettings").Visible) toggle.PerformClick();
                var viewport = (Forms.Panel)form.Controls.Find("connectionInspector", true).Single();
                viewport.AutoScrollPosition = Point.Empty;
                if (scope == "update-rendering")
                {
                    // Native rendering with replayed agent reports. Exercise each production
                    // progress handler separately; this does not claim a network update.
                    var timeline = Get<UpdateTimeline>("updateTimeline");
                    foreach (string route in new[] { "connect", "update-all" })
                    {
                        using var batch = new CancellationTokenSource();
                        Set("fleetLifetime", route == "update-all" ? batch : null!);
                        Set("pairingBusy", route == "connect"); Set("synchronizingAgent", route == "connect");
                        Get<Forms.Control>("updateProgressArea").Visible = false;
                        if (route == "connect") Call("ShowUpdateProgress", new AgentUpdateProgress("idle", 0, 0));
                        else Call("RecordDevice", Activator.CreateInstance(type, selected, "0.5.4.0", "", true, "hashing", 0, ""), null);
                        string[] stages = ["preparing", "transferring", "verifying", "restarting"];
                        foreach (string stage in stages)
                        {
                            var report = new AgentUpdateProgress(stage, 5 * 1024 * 1024, 10 * 1024 * 1024);
                            if (route == "connect") Call("ShowUpdateProgress", report);
                            else Call("RecordDevice", Activator.CreateInstance(type, selected, "0.5.4.0", "", true, stage, report.TransferPercent, "5.0/10.0 MiB"), report);
                            Call("RefreshControllerControls"); Call("UpdateHeader");
                            viewport.AutoScrollPosition = Point.Empty;
                            if (size.Width < 1586) viewport.ScrollControlIntoView(timeline);
                            await Task.Delay(150, stop.Token);
                            string sample = route + "-" + stage + "-" + size.Width;
                            Capture("update-map-" + sample);
                            Require(timeline.Visible && viewport.RectangleToScreen(viewport.ClientRectangle).Contains(timeline.RectangleToScreen(timeline.ClientRectangle)), "connection.map_visible_" + sample);
                            int activeStep = Array.IndexOf(stages, stage);
                            var expectedStates = Enumerable.Range(0, 4).Select(i => UiText.Get(i < activeStep ? "ConnectionComplete" : i == activeStep ? "ConnectionInProgress" : "ConnectionPending"));
                            Require(timeline.AccessibleName!.Split("; ").Select(entry => entry.Split(": ")[1]).SequenceEqual(expectedStates), "connection.map_step_states_" + sample);
                            using var pixels = new Bitmap(timeline.Width, timeline.Height);
                            using (var graphics = Graphics.FromImage(pixels)) graphics.CopyFromScreen(timeline.PointToScreen(Point.Empty), Point.Empty, pixels.Size);
                            int scale = Math.Max(1, timeline.DeviceDpi / 96), painted = 0;
                            for (int y = 36 * scale; y < Math.Min(170 * scale, pixels.Height); y++)
                            {
                                var pixel = pixels.GetPixel(22 * scale, y);
                                if (pixel.R < 70 && pixel.G > 120 && pixel.B > 120) painted++;
                            }
                            Require(painted > 60 * scale, "connection.map_painted_" + sample);
                        }
                        if (route == "update-all")
                        {
                            var list = Get<RememberedListView>("peers");
                            list.SelectedIndices.Clear(); list.Items[0].Selected = true; Call("SelectPeerFromList");
                            Require(Get<Peer>("selectedPeer") == first && !timeline.Visible, "connection.map_follows_selection_" + size.Width);
                            list.SelectedIndices.Clear(); list.Items[1].Selected = true; Call("SelectPeerFromList");
                            Require(Get<Peer>("selectedPeer") == selected && timeline.Visible, "connection.map_restored_on_reselection_" + size.Width);
                            Call("RecordDevice", Activator.CreateInstance(type, selected, "0.5.9.0", "", true, "current", 100, ""), null);
                            Require(!timeline.Visible, "connection.map_cleared_on_completion_" + size.Width);
                        }
                        Set("fleetLifetime", null!);
                    }
                    continue;
                }
                Get<Forms.Label>("footerRight").Text = "Design preview · simulated devices";
                await Task.Delay(100, stop.Token); Capture("connection-idle-" + size.Width);
                File.WriteAllText(Path.Combine(output, "connection-layout-" + size.Width + ".json"), JsonSerializer.Serialize(Children(form).Select(control => new { control.Name, Parent = control.Parent?.Name, Type = control.GetType().Name, control.Bounds, control.Visible }), new JsonSerializerOptions { WriteIndented = true }));
                Require(viewport.HorizontalScroll.Visible == false, "connection.no_horizontal_scroll_" + size.Width);
                var navigation = Get<Forms.Button>("navDiagnostics");
                Require(navigation.Parent!.RectangleToScreen(navigation.Parent.ClientRectangle).Contains(navigation.RectangleToScreen(navigation.ClientRectangle)),
                    "connection.sidebar_navigation_visible_" + size.Width);
                var toolbar = form.Controls.Find("connectionToolbar", true).Single();
                var tableHeader = form.Controls.Find("computerTableHeader", true).Single();
                Require(tableHeader.RectangleToScreen(tableHeader.ClientRectangle).Top >= toolbar.RectangleToScreen(toolbar.ClientRectangle).Bottom,
                    "connection.table_header_below_toolbar_" + size.Width);
                Require(new[] { Get<Forms.Button>("discoverButton"), Get<Forms.Button>("updateAllDevices") }.All(button =>
                    toolbar.RectangleToScreen(toolbar.ClientRectangle).Contains(button.RectangleToScreen(button.ClientRectangle))),
                    "connection.toolbar_buttons_visible_" + size.Width);
                toggle.PerformClick(); Require(!Get<Forms.Control>("connectionSettings").Visible, "connection.settings_collapse_" + size.Width);
                toggle.PerformClick(); Require(Get<Forms.Control>("connectionSettings").Visible, "connection.settings_expand_" + size.Width);
                Set("pairingBusy", true); Set("synchronizingAgent", true);
                // Presentation fixture only: completed steps are explicit synthetic reports; no claim of a live update.
                foreach (string stage in new[] { "idle", "transferring", "verifying", "restarting" }) Call("ShowUpdateProgress", new AgentUpdateProgress(stage, 0, 0));
                Call("RefreshControllerControls"); Call("UpdateHeader"); viewport.AutoScrollPosition = Point.Empty;
                await Task.Delay(100, stop.Token); Capture("connection-sync-" + size.Width);
                Require(Get<Forms.Label>("updateRemaining").Text == "—", "connection.no_fabricated_eta_" + size.Width);
                Require(!Get<Forms.Button>("configureWake").Enabled, "connection.wake_disabled_during_sync_" + size.Width);
                if (size.Width == 1586)
                {
                    var clock = new ConnectionPreviewClock();
                    var tracker = new UpdateProgressTracker(new Dictionary<string, double> { ["restarting"] = 75 }, clock);
                    foreach (string stage in new[] { "preparing", "transferring", "verifying", "restarting" }) tracker.Report(stage);
                    clock.Seconds = 51; Set("connectedUpdateProgress", tracker); Call("RefreshUpdateProgress");
                    Require(Get<Forms.Label>("updateRemaining").Text == "0:24", "connection.known_timing_estimate");
                    Get<Forms.Label>("footerRight").Text = "Design preview · simulated update";
                    await Task.Delay(100, stop.Token); Capture("connection-reference");
                    File.WriteAllText(Path.Combine(output, "connection-layout.json"), JsonSerializer.Serialize(Children(form).Select(control => new { control.Name, Type = control.GetType().Name, control.Bounds, control.Visible, Preferred = control.PreferredSize }), new JsonSerializerOptions { WriteIndented = true }));
                    Require(!viewport.VerticalScroll.Visible, "connection.reference_fits_without_scrolling");
                }
                viewport.ScrollControlIntoView(Get<Forms.Button>("configureWake")); await Task.Delay(50, stop.Token); Capture("connection-settings-" + size.Width);
                Require(viewport.RectangleToScreen(viewport.ClientRectangle).Contains(Get<Forms.Button>("configureWake").RectangleToScreen(Get<Forms.Button>("configureWake").ClientRectangle)), "connection.settings_reachable_" + size.Width);
                Set("pairingBusy", false); Set("synchronizingAgent", false); Call("RefreshControllerControls");
                Require(Get<Forms.Button>("pairButton").Visible && Get<Forms.Label>("connectionState").Visible,
                    "connection.interrupted_update_keeps_retry_and_error_" + size.Width);
                if (size.Width == 1586)
                {
                    Get<Forms.Control>("updateProgressArea").Visible = false; Call("RefreshControllerControls"); Call("UpdateHeader"); viewport.AutoScrollPosition = Point.Empty;
                    Get<Forms.Label>("footerRight").Text = "Design preview · simulated devices";
                    using var dialog = new WakeSettingsForm(selected.Name, null);
                    using var captureTimer = new Forms.Timer { Interval = 250 };
                    bool modalFits = false;
                    captureTimer.Tick += (_, _) =>
                    {
                        captureTimer.Stop(); Capture("wake-modal-reference");
                        var body = (Forms.Panel)dialog.Controls.Find("wakeSettingsBody", true).Single();
                        var help = dialog.Controls.Find("wakeHelp", true).Single();
                        modalFits = dialog.ClientSize.Height == 726 && !body.VerticalScroll.Visible
                            && dialog.Controls.Find("wakeSender", true).Length == 0
                            && body.RectangleToScreen(body.ClientRectangle).Contains(help.RectangleToScreen(help.ClientRectangle))
                            && help.Height >= help.GetPreferredSize(new(help.Width, 0)).Height;
                        dialog.DialogResult = Forms.DialogResult.Cancel; dialog.Close();
                    };
                    captureTimer.Start(); dialog.ShowModal(form);
                    Require(modalFits, "connection.wake_reference_help_fully_visible");
                }
            }
        }
        finally { form.Close(); }
        await FinishAsync();
    }

    private sealed class ConnectionPreviewClock : TimeProvider
    {
        internal long Seconds { get; set; }
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }
}
