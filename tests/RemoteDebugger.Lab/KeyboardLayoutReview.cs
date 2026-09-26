using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private async Task KeyboardLayoutReviewAsync()
    {
        var original = Forms.InputLanguage.CurrentInputLanguage;
        IntPtr french = LoadKeyboardLayout("0000040C", 0), english = LoadKeyboardLayout("00000409", 0);
        if (french == IntPtr.Zero || english == IntPtr.Zero) throw new IOException("French and US keyboard layouts are required.");
        Text = "AZERTY controller → QWERTY remote"; Width = 680; Height = 320;
        using var editor = new Forms.TextBox { Dock = Forms.DockStyle.Fill, Multiline = true, Font = new System.Drawing.Font("Segoe UI", 18) };
        log.Visible = false; Controls.Add(editor); editor.BringToFront(); Activate(); editor.Focus();
        bool capturing = false;
        var events = new List<JsonElement>();
        var results = new List<string>();
        using var capture = new RemoteKeyboardCapture(() => capturing && editor.Focused,
            value => events.Add(Json.Element(value)), () => events.Add(Json.Element(new { kind = "release" })), () => capturing = false);
        capture.Start();
        try
        {
            await Check("letters_accents", "azqwméèçà&", [[0x10], [0x11], [0x1E], [0x2C], [0x27], [0x03], [0x08], [0x0A], [0x0B], [0x02]]);
            await Check("shift", "A1", [[0x2A, 0x10], [0x2A, 0x02]]);
            await Check("caps_lock", "Aa", [[0x3A], [0x10], [0x3A], [0x10]]);
            await Check("altgr", "@€", [[0x1D, 0xE038, 0x0B], [0x1D, 0xE038, 0x12]]);
            await Check("dead_keys", "êü", [[0x1A], [0x12], [0x2A, 0x1A], [0x16]]);
            await Check("ctrl_a", "select me", [[0x1D, 0x10]], selectAll: true);
            ActivateKeyboardLayout(french, 0);
            events.Clear(); capturing = true; capture.Refresh();
            byte[] special = [0x08, 0x09, 0x0D, 0x1B, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E,
                0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B];
            foreach (byte vk in special) await ViewerChordAsync([vk]);
            capturing = false; capture.Refresh();
            var raw = events.Where(e => e.Str("kind") is "keyDown" or "keyUp").ToArray();
            if (!raw.Select(e => e.Int("virtualKey")).SequenceEqual(special.SelectMany(vk => new[] { (int)vk, vk })) ||
                raw.Where((e, i) => e.Str("kind") != (i % 2 == 0 ? "keyDown" : "keyUp") || e.GetProperty("extended").GetBoolean() != Native.IsExtendedKey(e.Int("virtualKey"))).Any() ||
                events.Any(e => e.Str("kind") == "text")) throw new IOException("Special keys lost their key identity, release or extended flag.");
            Pass("keyboard_layout.special_keys", "Editing, navigation and F1-F12 keys retain paired key events and extended flags", special);
            editor.Text = string.Join(Environment.NewLine, results);
            await Task.Delay(100, stop.Token);
            File.WriteAllBytes(Path.Combine(output, "keyboard-layout.png"), Convert.FromBase64String(Json.Element(Native.Screenshot()).Str("data")));
        }
        finally
        {
            capturing = false; capture.Refresh(); Native.ReleaseAllInput();
            Forms.InputLanguage.CurrentInputLanguage = original;
        }
        await NativeViewerKeyboardAsync();
        await FinishAsync();

        async Task Check(string id, string expected, ushort[][] chords, bool selectAll = false)
        {
            ActivateKeyboardLayout(french, 0);
            Native.FocusWindow(Environment.ProcessId); editor.Focus();
            if (!editor.Focused) throw new IOException("The source editor does not have keyboard focus.");
            editor.Clear(); events.Clear(); capturing = true; capture.Refresh();
            foreach (ushort[] chord in chords)
            {
                foreach (ushort scan in chord) { Key(scan, false); await Task.Delay(40, stop.Token); }
                foreach (ushort scan in chord.Reverse()) { Key(scan, true); await Task.Delay(40, stop.Token); }
            }
            if (editor.TextLength != 0) throw new IOException("Captured text escaped to the controller: " + editor.Text);
            capturing = false; capture.Refresh();
            ActivateKeyboardLayout(english, 0);
            Native.FocusWindow(Environment.ProcessId); editor.Focus();
            if (!editor.Focused) throw new IOException("The destination editor does not have keyboard focus.");
            if (selectAll) { editor.Text = expected; editor.SelectionStart = editor.TextLength; }
            foreach (var input in events.ToArray()) { Native.HandleInput(input); await Task.Delay(25, stop.Token); }
            await Task.Delay(100, stop.Token);
            string actual = selectAll ? editor.SelectedText : editor.Text;
            if (actual != expected) throw new IOException($"{id}: expected '{expected}', got '{actual}'; events: {Json.Text(events)}");
            results.Add(id + ": " + actual);
            Pass("keyboard_layout." + id, "AZERTY input retains its meaning on a QWERTY destination", new { expected, actual });
        }

        void Key(ushort scan, bool up)
        {
            byte vk = (byte)MapVirtualKeyEx(scan, 3, french);
            ViewerTestKey(vk, (byte)scan, (scan > 255 ? 1u : 0u) | (up ? 2u : 0u), UIntPtr.Zero);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadKeyboardLayout(string id, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr ActivateKeyboardLayout(IntPtr layout, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint MapVirtualKeyEx(uint code, uint type, IntPtr layout);
}
