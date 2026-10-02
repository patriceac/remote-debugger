using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text.Json;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private async Task CursorNicknameReviewAsync()
    {
        string root = Path.Combine(output, "cursor-nickname-settings");
        Vault.Save(Path.Combine(root, "internet.dpapi"), JsonSerializer.SerializeToUtf8Bytes(new InternetSettings("https://ui.invalid", new string('a', 64), new string('b', 64)), Json.Options));
        using var form = new MainForm(startAgent: false, dataRoot: root, loopbackOnly: true, languageOverride: "en");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        T Get<T>(string name) => (T)typeof(MainForm).GetField(name, flags)!.GetValue(form)!;
        void Set(string name, object value) => typeof(MainForm).GetField(name, flags)!.SetValue(form, value);
        void Call(string name, params object?[] args) => typeof(MainForm).GetMethod(name, flags)!.Invoke(form, args);
        Set("quitting", true); form.Show(); form.Location = Point.Empty; form.Activate();
        await Task.Delay(150, stop.Token);
        Get<Forms.Timer>("renderTimer").Stop(); Get<Forms.Timer>("resourceRefreshTimer").Stop();
        try
        {
            var type = typeof(MainForm).GetNestedType("FleetDevice", BindingFlags.NonPublic)!;
            var first = new Peer("PC-BEDO", "127.0.0.2", 45832, new string('c', 64));
            var second = new Peer("PC-YOLANDE", "127.0.0.3", 45832, new string('d', 64));
            foreach (var peer in new[] { first, second }) Call("RecordDevice", Activator.CreateInstance(type, peer, "0.6.5.0", "", false, "offline", 0, ""), null);
            // Local presentation fixture: no controller enrollment or network session.
            Set("isUpdateAdmin", true); Get<PageSwitcher>("rolePages").SelectedIndex = 1;
            foreach (string name in new[] { "controllerNavCaption", "navConnection", "navScreen", "navProcesses", "navFiles", "navDiagnostics" }) Get<Forms.Control>(name).Visible = true;
            Call("SelectControllerPage", 0);
            var input = Get<Forms.TextBox>("cursorNickname");
            var save = Get<Forms.Button>("saveCursorNickname");
            void Select(string name)
            {
                var list = Get<RememberedListView>("peers");
                foreach (Forms.ListViewItem item in list.Items) item.Selected = item.Tag is Peer peer && peer.Name == name;
                Call("SelectPeerFromList");
            }
            Select(second.Name); input.Text = "Yolande"; save.PerformClick();
            if (new DeviceCursorNames(root).Get(second.Fingerprint) != "Yolande") throw new IOException("The nickname Save button did not persist this computer's nickname.");
            Select(first.Name);
            if (input.Text != "") throw new IOException("A nickname leaked to another agent's settings.");
            input.Text = "Bedo"; save.PerformClick(); Select(second.Name);
            if (input.Text != "Yolande") throw new IOException("Reopening the computer settings lost its saved nickname.");
            var wan = Get<Forms.TextBox>("wanAddress");
            if (input.Parent!.Width != wan.Parent!.Width || input.Parent.Height != wan.Parent.Height || save.Size != Get<Forms.Button>("saveWanAddress").Size)
                throw new IOException("The nickname row does not match the approved existing WAN row layout.");
            await Task.Delay(150, stop.Token);
            using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
            {
                using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(form.PointToScreen(Point.Empty), Point.Empty, bitmap.Size);
                bitmap.Save(Path.Combine(output, "cursor-nickname-settings.png"), ImageFormat.Png);
            }
            input.Text = ""; save.PerformClick();
            var reopened = new DeviceCursorNames(root);
            var cursor = new CursorPosition(120, 80, "Windows first name", true, 1, 0);
            if (reopened.Apply(second.Fingerprint, cursor) != cursor || reopened.Get(first.Fingerprint) != "Bedo")
                throw new IOException("Clearing a nickname did not restore the Windows name independently of other agents.");
            Pass("cursor.nickname_ui", "The approved nickname field saves per computer, reopens, and clears back to the Windows name");
        }
        finally { form.Close(); }
    }
}
