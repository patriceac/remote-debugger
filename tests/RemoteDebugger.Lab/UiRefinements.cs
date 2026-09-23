using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private async Task UiRefinementsAsync(bool redlineOnly = false)
    {
        string root = Path.Combine(output, "ui-fixture");
        UiCulture.Apply(System.Globalization.CultureInfo.GetCultureInfo("en"));
        Vault.Save(Path.Combine(root, "internet.dpapi"), JsonSerializer.SerializeToUtf8Bytes(new InternetSettings("https://ui.invalid", new string('a', 64), new string('b', 64)), Json.Options));
        TableLayoutStore.Save(root, "peers", [new("name", 210, 0), new("version", 94, 2), new("state", 354, 1), new("progress", 380, 3)]);
        using var form = new MainForm(startAgent: false, dataRoot: root, loopbackOnly: true, languageOverride: "en");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo Field(string name) => typeof(MainForm).GetField(name, flags)!;
        T Get<T>(string name) => (T)Field(name).GetValue(form)!;
        void Set(string name, object value) => Field(name).SetValue(form, value);
        void Call(string name, params object?[] arguments) => typeof(MainForm).GetMethod(name, flags)!.Invoke(form, arguments);
        Rectangle Bounds(string name) => form.RectangleToClient(Get<Forms.Control>(name).RectangleToScreen(Get<Forms.Control>(name).ClientRectangle));
        void Require(bool condition, string id, object? evidence = null)
        {
            if (!condition) throw new IOException(id + ": " + JsonSerializer.Serialize(evidence));
            Pass(id, id, evidence);
        }
        void Capture(string name)
        {
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(output, name + ".png"));
        }
        // Keep this a presentation fixture: no listener, discovery, pairing or live session.
        Set("quitting", true);
        form.Show(); form.Activate();
        await Task.Delay(150, stop.Token); // Let the deferred Shown event select the initial role.
        Get<Forms.Timer>("renderTimer").Stop(); Get<Forms.Timer>("resourceRefreshTimer").Stop();
        try
        {
            var peer = new Peer("PC-YOLANDE", "127.0.0.2", 45832, new string('c', 64));
            Set("selectedPeer", peer);
            var deviceType = typeof(MainForm).GetNestedType("FleetDevice", BindingFlags.NonPublic)!;
            var device = Activator.CreateInstance(deviceType, peer, "0.5.3.0", "", true, "finalizing", 0, "")!;
            Call("RecordDevice", device, new AgentUpdateProgress("finalizing", 0, 0));
            var peers = Get<RememberedListView>("peers");
            foreach (int width in redlineOnly ? Array.Empty<int>() : new[] { 1280, 1060 })
            {
                form.Size = new(width, 860); peers.Items[0].Selected = true; peers.Focus();
                await Task.Delay(150, stop.Token);
                int columns = peers.Columns.Cast<Forms.ColumnHeader>().Sum(column => column.Width);
                Require(columns <= peers.ClientSize.Width && (UiWindowStyle(peers.Handle, -16) & 0x100000) == 0, "ui.columns_fit_" + width, new { columns, viewport = peers.ClientSize.Width });
                Capture("fleet-" + width);
                using var row = new Bitmap(peers.Width, peers.Height);
                peers.DrawToBitmap(row, peers.ClientRectangle);
                Require(row.GetPixel(peers.Columns[0].Width - 5, peers.Items[0].Bounds.Top + 8).ToArgb() == Color.FromArgb(227, 246, 248).ToArgb(), "ui.teal_selection_" + width);
            }
            Set("supportSession", true); Set("heartbeatHealthy", true); Set("liveFrameFresh", true);
            Call("SelectRole", 1);
            Call("SelectControllerPage", 1);
            var charts = Get<ResourceMiniCharts>("headerCharts");
            using var frame = new Bitmap(900, 700);
            using (var graphics = Graphics.FromImage(frame))
            using (var font = new Font("Segoe UI", 22))
            {
                graphics.Clear(Color.FromArgb(244, 247, 250));
                graphics.DrawString("Isolated UI test\nRemote screen area", font, Brushes.SlateGray, 48, 48);
            }
            Get<RemoteScreenView>("screen").Image = frame;
            int Pixels(int value) => (int)Math.Round(value * form.DeviceDpi / 96f);
            form.ClientSize = new(Pixels(216 + 1447), Pixels(900)); await Task.Delay(150, stop.Token);
            double[] cpu = [32, 42, 47, 38, 38, 37, 10, 10, 8, 4, 8, 15, 11, 7, 7, 7, 4, 6, 6, 5, 6, 4, 5, 4, 6];
            for (int i = 0; i < 36; i++)
                charts.Add(cpu[(int)Math.Round(i * (cpu.Length - 1) / 35d)], i < 3 ? 68 + i * 7 : i < 11 ? 88 : i < 22 ? 78 : 82,
                    i is >= 10 and <= 14 ? 20 - Math.Abs(i - 12) * 8 : 0, i is >= 10 and <= 14 ? 20 - Math.Abs(i - 12) * 8 : 1);
            var chartBounds = Bounds("headerCharts");
            Require(chartBounds.Left >= Bounds("headerTitle").Right && chartBounds.Right <= Bounds("statusPill").Left && charts.Width > 440 && charts.Height > 40,
                "ui.charts_use_header_gap", new { chartBounds, title = Bounds("headerTitle"), status = Bounds("statusPill") });
            var toolbarControls = new Forms.Control[]
            {
                form.Controls.Find("monitorLabel", true).Single(), Get<Forms.Control>("monitor"), Get<Forms.Control>("mouseEnabled"),
                Get<Forms.Control>("shareClipboard"), Get<Forms.Control>("relayEconomy")
            };
            double[] centers = toolbarControls.Select(control => control.RectangleToScreen(control.ClientRectangle))
                .Select(bounds => bounds.Top + bounds.Height / 2d).ToArray();
            Require(centers.Max() - centers.Min() <= 2, "ui.viewer_toolbar_centered", new { centers });
            Capture("viewer-wide");
            var shell = Get<Forms.TableLayoutPanel>("shell");
            var headerPanel = Get<Forms.Panel>("header");
            var toolbar = form.Controls.Find("viewerToolbar", true).Single();
            using (var image = new Bitmap(headerPanel.Width, headerPanel.Height + toolbar.Height))
            {
                headerPanel.DrawToBitmap(image, new Rectangle(0, 0, headerPanel.Width, headerPanel.Height));
                toolbar.DrawToBitmap(image, new Rectangle(0, headerPanel.Height, toolbar.Width, toolbar.Height));
                image.Save(Path.Combine(output, "viewer-header-reference.png"));
            }
            Require(headerPanel.Height == Pixels(116) && toolbar.Height == Pixels(52), "ui.viewer_header_reference_size",
                new { height = Bounds("header").Height, dpi = form.DeviceDpi, shellRow = shell.RowStyles[0].Height, shellRowType = shell.RowStyles[0].SizeType,
                    shellRows = shell.GetRowHeights(), toolbar = toolbar.Bounds,
                    titlePreferred = Get<Forms.Control>("headerTitle").PreferredSize,
                    chartsPreferred = charts.PreferredSize, headerPreferred = headerPanel.PreferredSize });
            form.Size = new(1060, 720); await Task.Delay(150, stop.Token);
            charts.Add(6, 82, 0, 1);
            Require(Bounds("headerCharts").Top >= Bounds("headerSubtitle").Bottom && Bounds("headerCharts").Right <= Bounds("header").Right && charts.Height >= 64,
                "ui.charts_fit_minimum", new { charts = Bounds("headerCharts"), subtitle = Bounds("headerSubtitle") });
            Capture("viewer-minimum");
            form.ClientSize = new(Pixels(216 + 1447), Pixels(900)); await Task.Delay(150, stop.Token);
            Require(headerPanel.Height == Pixels(116), "ui.viewer_header_height_restored_after_resize", new { headerPanel.Height });
            var controlToggle = Get<Forms.CheckBox>("mouseEnabled");
            controlToggle.AccessibilityObject.DoDefaultAction(); await Task.Delay(50, stop.Token);
            Require(!controlToggle.Checked, "ui.viewer_checkbox_accessible_toggle");
            controlToggle.AccessibilityObject.DoDefaultAction();
            for (int i = 0; i < 12; i++)
                foreach (var control in toolbarControls) control.Refresh();
            Capture("viewer-after-repaints");
            var selector = Get<Forms.ComboBox>("monitor");
            selector.AccessibilityObject.DoDefaultAction(); await Task.Delay(50, stop.Token);
            Require(selector.DroppedDown, "ui.viewer_selector_accessible_open");
            selector.DroppedDown = false;
            if (!redlineOnly)
            {
                var tips = Get<Forms.ToolTip>("viewerTips");
                Require(tips.GetToolTip(Get<Forms.Control>("screen")) == "" && tips.GetToolTip(Get<Forms.Control>("fullScreenButton")) == UiText.ReleaseKeyboard && tips.InitialDelay >= 1000 && tips.ReshowDelay >= 1000,
                    "ui.tooltip_only_buttons_after_one_second");
                foreach (byte[] chord in new byte[][] { [0xA2, 0xA4, 0x7B], [0xA3, 0xA1, 0x7B, 0x7B] })
                {
                    form.Activate(); Get<Forms.Control>("remoteText").Focus();
                    await ViewerChordAsync(chord);
                    Require(Field("fullScreenHost").GetValue(form) != null, "ui.shortcut_enters_fullscreen_" + chord[1]);
                    await ViewerChordAsync(chord);
                    Require(Field("fullScreenHost").GetValue(form) == null, "ui.shortcut_exits_fullscreen_" + chord[1]);
                }
            }
            Get<RemoteScreenView>("screen").Image = null;
        }
        finally { form.Close(); }
        using var notice = (Forms.Form)Activator.CreateInstance(typeof(MainForm).Assembly.GetType("RemoteDebugger.SupportConnectionNotice")!)!;
        var progress = (Forms.Panel)notice.GetType().GetField("progress", flags)!.GetValue(notice)!;
        var elapsed = new Stopwatch();
        double? closedAt = null;
        notice.Shown += (_, _) => elapsed.Start();
        notice.FormClosed += (_, _) => closedAt = elapsed.Elapsed.TotalSeconds;
        var cursor = Forms.Cursor.Position;
        try
        {
            notice.Show();
            await Task.Delay(150, stop.Token);
            Forms.Cursor.Position = notice.PointToScreen(new Point(notice.Width / 2, notice.Height / 2));
            int fullWidth = notice.ClientSize.Width - progress.Left, previousWidth = fullWidth;
            Thread.Sleep(1200); // A delayed UI tick must not extend the notice's lifetime.
            while (closedAt == null && elapsed.Elapsed.TotalSeconds < 12)
            {
                await Task.Delay(100, stop.Token);
                if (closedAt != null) break;
                double expected = Math.Max(0, 1 - elapsed.Elapsed.TotalSeconds / 10);
                if (progress.Width > previousWidth || Math.Abs(progress.Width / (double)fullWidth - expected) > .035)
                    throw new IOException("Notice countdown diverged from elapsed time.");
                previousWidth = progress.Width;
                if (elapsed.Elapsed.TotalSeconds is > 4.8 and < 5.05)
                {
                    using var bitmap = new Bitmap(notice.Width, notice.Height);
                    notice.DrawToBitmap(bitmap, notice.ClientRectangle);
                    bitmap.Save(Path.Combine(output, "notice-halfway.png"));
                }
            }
            Require(closedAt is >= 9.9 and < 10.5, "ui.notice_closes_after_ten_seconds_while_hovered",
                new { closedAt, fullWidth, notice.DeviceDpi });
        }
        finally { Forms.Cursor.Position = cursor; notice.Close(); }
        await FinishAsync();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int UiWindowStyle(IntPtr window, int index);
}
