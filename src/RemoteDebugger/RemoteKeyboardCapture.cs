using System.ComponentModel;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

// Only the focused viewer owns keyboard input. Keep the hook callback synchronous
// and short: enqueue network work, never wait for it here.
internal sealed class RemoteKeyboardCapture(Func<bool> canCapture, Action<object> send, Action release, Action escape, Func<bool>? canEscape = null) : IDisposable
{
    private readonly HashSet<int> swallowed = [];
    private readonly HashSet<int> held = [];
    private readonly HashSet<int> pressed = [];
    private readonly Forms.Timer keepAlive = new() { Interval = 750 };
    private HookProc? callback;
    private IntPtr hook;
    private bool active;

    public void Start()
    {
        if (hook != IntPtr.Zero) return;
        callback = OnKey;
        hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        keepAlive.Tick += (_, _) => { Refresh(); if (active && held.Count > 0) send(new { kind = "keepAlive" }); };
        keepAlive.Start();
    }

    public void Refresh()
    {
        bool capture = hook != IntPtr.Zero && canCapture();
        if (capture == active) return;
        active = capture;
        if (!active) { held.Clear(); release(); return; }
        // A modifier may already be held when the user clicks into the viewer.
        foreach (int vk in new[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C })
            if ((GetAsyncKeyState(vk) & 0x8000) != 0 && !swallowed.Contains(vk))
            {
                held.Add(vk);
                pressed.Add(vk);
                send(new { kind = "keyDown", virtualKey = vk });
            }
    }

    public void Reset() { active = false; held.Clear(); }

    internal static bool IsReleaseShortcut(int vk, IEnumerable<int> keys) => vk == 0x7B &&
        keys.Any(k => k is 0x11 or 0xA2 or 0xA3) &&
        (keys.Any(k => k is 0x12 or 0xA4 or 0xA5) || keys.Any(k => k is 0x10 or 0xA0 or 0xA1));

    private IntPtr OnKey(int code, IntPtr message, IntPtr data)
    {
        if (code < 0) return CallNextHookEx(hook, code, message, data);
        var key = Marshal.PtrToStructure<KeyboardEvent>(data);
        if (key.Extra == Native.InputTag) return CallNextHookEx(hook, code, message, data);
        bool down = message.ToInt64() is 0x100 or 0x104;
        bool up = message.ToInt64() is 0x101 or 0x105;
        if (!down && !up) return CallNextHookEx(hook, code, message, data);
        int vk = (int)key.VirtualKey;
        if (down) pressed.Add(vk); else pressed.Remove(vk);
        bool suppress = swallowed.Contains(vk);
        try
        {
            Refresh();
            if (down && (active || canEscape?.Invoke() == true) && IsReleaseShortcut(vk, pressed))
            {
                swallowed.Add(vk); Reset(); release(); escape(); return (IntPtr)1;
            }
            if (active && !(suppress && !held.Contains(vk)))
            {
                if (down)
                {
                    swallowed.Add(vk); suppress = true;
                    held.Add(vk);
                }
                else held.Remove(vk);
                send(new { kind = down ? "keyDown" : "keyUp", virtualKey = vk, scanCode = key.ScanCode, extended = (key.Flags & 1) != 0 });
            }
        }
        catch { Reset(); release(); }
        if (up) swallowed.Remove(vk);
        return suppress ? (IntPtr)1 : CallNextHookEx(hook, code, message, data);
    }

    public void Dispose()
    {
        keepAlive.Dispose();
        if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        Reset(); release();
    }

    [StructLayout(LayoutKind.Sequential)] private struct KeyboardEvent { public uint VirtualKey, ScanCode, Flags, Time; public UIntPtr Extra; }
    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
