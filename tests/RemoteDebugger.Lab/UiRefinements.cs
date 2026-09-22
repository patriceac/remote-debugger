using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private async Task UiRefinementsAsync()
    {
        string root = Path.Combine(output, "ui-fixture");
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
        Get<Forms.Timer>("renderTimer").Stop(); Get<Forms.Timer>("resourceRefreshTimer").Stop();
        try
        {
            var peer = new Peer("PC-YOLANDE", "127.0.0.2", 45832, new string('c', 64));
            Set("selectedPeer", peer);
            var deviceType = typeof(MainForm).GetNestedType("FleetDevice", BindingFlags.NonPublic)!;
            var device = Activator.CreateInstance(deviceType, peer, "0.5.3.0", "", true, "finalizing", 0, "")!;
            Call("RecordDevice", device, new AgentUpdateProgress("finalizing", 0, 0));
            var peers = Get<RememberedListView>("peers");
            foreach (int width in new[] { 1280, 1060 })
            {
                form.Size = new(width, 860); peers.Items[0].Selected = true; peers.Focus();
                await Task.Delay(150, stop.Token);
                int columns = peers.Columns.Cast<Forms.ColumnHeader>().Sum(column => column.Width);
                Require(columns <= peers.ClientSize.Width && (UiWindowStyle(peers.Handle, -16) & 0x100000) == 0, "ui.columns_fit_" + width, new { columns, viewport = peers.ClientSize.Width });
                Capture("fleet-" + width);
                using var row = new Bitmap(peers.Width, peers.Height);
                peers.DrawToBitmap(row, peers.ClientRectangle);
                Require(row.GetPixel(peers.ClientSize.Width - 2, peers.Items[0].Bounds.Top + 12).ToArgb() == peers.BackColor.ToArgb(), "ui.no_blue_selection_" + width);
            }
            Set("supportSession", true); Set("heartbeatHealthy", true); Set("liveFrameFresh", true);
            Call("SelectControllerPage", 1);
            var charts = Get<ResourceMiniCharts>("headerCharts");
            for (int i = 0; i < 36; i++) charts.Add(43 + Math.Sin(i) * 8, 83, 8 + Math.Sin(i * 2) * 5, 10 + Math.Sin(i) * 3);
            using var frame = new Bitmap(900, 700);
            using (var graphics = Graphics.FromImage(frame))
            using (var font = new Font("Segoe UI", 22))
            {
                graphics.Clear(Color.FromArgb(244, 247, 250));
                graphics.DrawString("Isolated UI test\nRemote screen area", font, Brushes.SlateGray, 48, 48);
            }
            Get<RemoteScreenView>("screen").Image = frame;
            form.Size = new(1800, 1000); await Task.Delay(150, stop.Token);
            var chartBounds = Bounds("headerCharts");
            Require(chartBounds.Left >= Bounds("headerTitle").Right && chartBounds.Right <= Bounds("statusPill").Left && charts.Width > 440 && charts.Height > 40,
                "ui.charts_use_header_gap", new { chartBounds, title = Bounds("headerTitle"), status = Bounds("statusPill") });
            Capture("viewer-wide");
            form.Size = new(1060, 720); await Task.Delay(150, stop.Token);
            Require(Bounds("headerCharts").Top >= Bounds("headerSubtitle").Bottom && Bounds("headerCharts").Right <= Bounds("header").Right,
                "ui.charts_fit_minimum", new { charts = Bounds("headerCharts"), subtitle = Bounds("headerSubtitle") });
            Capture("viewer-minimum");
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
            Get<RemoteScreenView>("screen").Image = null;
        }
        finally { form.Close(); }
        await FinishAsync();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int UiWindowStyle(IntPtr window, int index);
}
