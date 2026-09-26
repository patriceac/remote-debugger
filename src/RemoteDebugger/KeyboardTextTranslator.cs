using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

internal sealed class KeyboardTextTranslator
{
    private IntPtr layout;
    private bool deadKey, capsLock;

    public void Begin() => capsLock = Forms.Control.IsKeyLocked(Forms.Keys.CapsLock);

    public void Reset()
    {
        // A swallowed dead key must not leak into local typing after capture ends.
        if (deadKey)
            for (int i = 0; i < 4 && ToUnicodeEx(0x20, 0x39, new byte[256], new char[8], 8, 0, layout) < 0; i++) { }
        deadKey = false;
        layout = IntPtr.Zero;
    }

    public string? Translate(int vk, uint scan, IReadOnlySet<int> pressed, bool repeat)
    {
        if (vk == 0x14 && !repeat) capsLock = !capsLock;
        bool control = pressed.Overlaps([0x11, 0xA2, 0xA3]);
        bool alt = pressed.Overlaps([0x12, 0xA4, 0xA5]);
        if (pressed.Overlaps([0x5B, 0x5C]) || (control || alt) && !pressed.Contains(0xA5)) return null;

        IntPtr current = Forms.InputLanguage.CurrentInputLanguage.Handle;
        if (current != layout) { Reset(); layout = current; }
        var state = new byte[256];
        foreach (int key in pressed) if (key is >= 0 and < 256) state[key] = 0x80;
        state[0x10] = pressed.Overlaps([0x10, 0xA0, 0xA1]) ? (byte)0x80 : (byte)0;
        state[0x11] = control ? (byte)0x80 : (byte)0;
        state[0x12] = alt ? (byte)0x80 : (byte)0;
        state[0x14] |= capsLock ? (byte)1 : (byte)0;
        var buffer = new char[8];
        int count = ToUnicodeEx((uint)vk, scan, state, buffer, buffer.Length, 0, layout);
        if (count < 0) { deadKey = true; return ""; }
        if (count == 0) return null;
        deadKey = false;
        var text = new string(buffer, 0, count);
        return text.Any(char.IsControl) ? null : text;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int ToUnicodeEx(uint key, uint scan, byte[] state, [Out] char[] text, int length, uint flags, IntPtr layout);
}
