using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

internal sealed record SharedPointerReply(bool Sent, bool Applied, CursorPosition Cursor);

// All hook, gesture and overlay state lives on one message thread, including in the SYSTEM input helper.
internal sealed class SharedMouse
{
    private static readonly Lazy<SharedMouse> instance = new(() => new());
    private readonly CursorOverlay overlay;
    private readonly HookProc callback;
    private readonly HashSet<string> localButtons = [], remoteButtons = [], suppressedButtons = [];
    private IntPtr hook;
    private Point local, remote;
    private string name = "";
    private long activity, click, remoteActivity, remoteClick, contact;
    private bool visible;
    private int dragging;
    internal static bool Dragging => instance.IsValueCreated && Volatile.Read(ref instance.Value.dragging) != 0;

    private SharedMouse()
    {
        var ready = new TaskCompletionSource<(CursorOverlay, Forms.Timer, HookProc)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var window = new CursorOverlay(); _ = window.Handle;
                var clock = new Forms.Timer { Interval = 33 };
                HookProc handler = OnMouse;
                clock.Tick += (_, _) => { try { Refresh(); } catch (InputBlockedException) { window.Hide(); } }; clock.Start();
                ready.SetResult((window, clock, handler));
                Forms.Application.Run();
                clock.Dispose(); window.Dispose();
            }
            catch (Exception ex) { ready.TrySetException(ex); }
        }) { IsBackground = true, Name = "Shared mouse" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        var started = ready.Task.GetAwaiter().GetResult();
        overlay = started.Item1; callback = started.Item3;
    }

    internal static SharedPointerReply Apply(JsonElement args) => (SharedPointerReply)instance.Value.overlay.Invoke(() => instance.Value.Handle(args));
    internal static void Release()
    {
        if (instance.IsValueCreated) instance.Value.overlay.Invoke(instance.Value.End);
    }

    private SharedPointerReply Handle(JsonElement args)
    {
        if (hook == IntPtr.Zero)
        {
            GetCursorPos(out local);
            hook = SetWindowsHookEx(14, callback, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            foreach (var (key, button) in new[] { (1, "left"), (2, "right"), (4, "middle") })
                if ((GetAsyncKeyState(key) & 0x8000) != 0) localButtons.Add(button);
            activity++;
        }
        contact = Environment.TickCount64;
        string kind = args.Str("kind");
        bool applied = true;
        if (kind == "pointerLeave") visible = false;
        else if (kind != "pointer")
        {
            var point = new Point(args.Int("x"), args.Int("y"));
            if (!visible || remote != point) remoteActivity++;
            remote = point; visible = true;
            string label = args.Str("pointerName");
            if (label.Length > 0) name = new string(label.Where(c => !char.IsControl(c)).Take(128).ToArray());
            string button = args.Str("button", "left");
            if (kind == "down") { remoteClick++; remoteActivity++; }
            // A drag belongs to its starter until release. Contending presses are dropped, never queued for later.
            if (localButtons.Count == 0)
            {
                if (kind == "down") { Native.InjectPointer(remote, kind, button, 0); remoteButtons.Add(button); }
                else if (kind == "up" && remoteButtons.Contains(button))
                {
                    Native.InjectPointer(remote, kind, button, 0);
                    remoteButtons.Remove(button);
                    if (remoteButtons.Count == 0) Native.InjectPointer(local, "move", "left", 0);
                }
                else if (kind == "move" && remoteButtons.Count > 0) Native.InjectPointer(remote, kind, button, 0);
                else if (kind == "wheel" && remoteButtons.Count == 0)
                {
                    Native.InjectPointer(remote, kind, button, args.Int("delta"));
                    Native.InjectPointer(local, "move", "left", 0);
                }
            }
            else if (kind is "down" or "up" or "wheel") applied = false;
        }
        Volatile.Write(ref dragging, remoteButtons.Count);
        overlay.Pointer.Update(new(remote.X, remote.Y, name, visible, remoteActivity, remoteClick), contact);
        Refresh();
        return new(true, applied, new(local.X, local.Y, Environment.MachineName, true, activity, click));
    }

    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code < 0) return CallNextHookEx(hook, code, message, data);
        var input = Marshal.PtrToStructure<MouseEvent>(data);
        if (input.Extra == Native.InputTag) return CallNextHookEx(hook, code, message, data);
        int id = (int)message;
        string button = id is 0x201 or 0x202 ? "left" : id is 0x204 or 0x205 ? "right" : id is 0x207 or 0x208 ? "middle" : "";
        bool down = id is 0x201 or 0x204 or 0x207, up = id is 0x202 or 0x205 or 0x208;
        if (up && suppressedButtons.Remove(button)) return (IntPtr)1;
        if (remoteButtons.Count > 0)
        {
            if (id == 0x200)
            {
                GetCursorPos(out var current);
                var bounds = Forms.SystemInformation.VirtualScreen;
                local = new(Math.Clamp(local.X + input.Position.X - current.X, bounds.Left, bounds.Right - 1),
                    Math.Clamp(local.Y + input.Position.Y - current.Y, bounds.Top, bounds.Bottom - 1));
                activity++;
            }
            if (down) suppressedButtons.Add(button);
            return (IntPtr)1;
        }
        if (input.Position != local) { local = input.Position; activity++; }
        if (down) { localButtons.Add(button); click++; activity++; }
        if (up) localButtons.Remove(button);
        return CallNextHookEx(hook, code, message, data);
    }

    private void Refresh()
    {
        if (hook == IntPtr.Zero) return;
        if (Environment.TickCount64 - contact > 3000) { End(); return; }
        overlay.Tip = remote;
        overlay.LocalTip = remoteButtons.Count > 0 ? local : null;
        if (visible || overlay.LocalTip != null)
        {
            // Small ordinary overlays; expand only during a shared drag to retain the local person's pointer.
            var bounds = Forms.Screen.FromPoint(remote).Bounds;
            var area = new Rectangle(remote.X - 28, remote.Y - 48, 310, 135);
            area.X = Math.Clamp(area.X, bounds.Left, Math.Max(bounds.Left, bounds.Right - area.Width));
            area.Y = Math.Clamp(area.Y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - area.Height));
            if (overlay.LocalTip is { } own) area = Rectangle.Union(area, new Rectangle(own, new Size(40, 40)));
            overlay.Bounds = area;
            if (!overlay.Visible) overlay.Show();
            overlay.Redraw();
        }
        else overlay.Hide();
    }

    private void End()
    {
        bool restore = remoteButtons.Count > 0;
        try
        {
            foreach (string button in remoteButtons.ToArray()) { Native.InjectPointer(remote, "up", button, 0); remoteButtons.Remove(button); }
            if (restore) Native.InjectPointer(local, "move", "left", 0);
        }
        finally
        {
            // Retain any button Windows refused to release so the input watchdog can retry.
            localButtons.Clear(); suppressedButtons.Clear();
            Volatile.Write(ref dragging, remoteButtons.Count);
            if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            visible = false; overlay.Pointer.Update(null, Environment.TickCount64); overlay.LocalTip = null; overlay.Hide();
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct MouseEvent { public Point Position; public uint Data, Flags, Time; public UIntPtr Extra; }
    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
