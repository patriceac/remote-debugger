using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Automation;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

internal sealed class InputBlockedException(string message) : UnauthorizedAccessException(message);

public static class Native
{
    [DllImport("kernel32.dll")] public static extern bool AttachConsole(uint processId);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder text, int length);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    public sealed record NativeWindow(long Handle, int Pid, string Name, string Class, int X, int Y, int Width, int Height, bool Foreground);
    public static NativeWindow[] NativeWindows()
    {
        var list = new List<NativeWindow>(); IntPtr foreground = GetForegroundWindow();
        EnumWindows((window, _) => { if (!IsWindowVisible(window)) return true; var name = new System.Text.StringBuilder(512); var type = new System.Text.StringBuilder(256); GetWindowText(window, name, 512); GetClassName(window, type, 256); GetWindowRect(window, out var r); GetWindowThreadProcessId(window, out uint pid); list.Add(new NativeWindow(window.ToInt64(), (int)pid, name.ToString(), type.ToString(), r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, window == foreground)); return true; }, IntPtr.Zero);
        return list.ToArray();
    }
    public static void FocusWindow(int pid) => Focus(pid);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AttachThreadInput(uint current, uint other, bool attach);
    [DllImport("user32.dll")] private static extern bool PeekMessage(IntPtr message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public UNION data; }
    [StructLayout(LayoutKind.Explicit)] private struct UNION { [FieldOffset(0)] public MOUSE mouse; [FieldOffset(0)] public KEY key; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSE { public int x, y; public uint data, flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KEY { public ushort vk, scan; public uint flags, time; public UIntPtr extra; }
    public static bool IsElevated() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    private static IntPtr Focus(int pid)
    {
        DesktopCapture.RequireDesktop();
        if (pid == 0) return GetForegroundWindow();
        using var p = Process.GetProcessById(pid); IntPtr h = p.MainWindowHandle; if (h == IntPtr.Zero) throw new InvalidOperationException("The process has no main window.");
        ShowWindow(h, 9); SetForegroundWindow(h);
        GetWindowThreadProcessId(GetForegroundWindow(), out uint initiallyFocused);
        if (initiallyFocused != pid)
        {
            // Join the foreground input queue briefly; never inject a key to steal focus.
            uint current = GetCurrentThreadId(), foreground = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            IntPtr message = Marshal.AllocHGlobal(64); try { PeekMessage(message, IntPtr.Zero, 0, 0, 0); } finally { Marshal.FreeHGlobal(message); }
            bool attached = foreground != 0 && current != foreground && AttachThreadInput(current, foreground, true);
            try { BringWindowToTop(h); SetForegroundWindow(h); }
            finally { if (attached) AttachThreadInput(current, foreground, false); }
        }
        Thread.Sleep(120);
        GetWindowThreadProcessId(GetForegroundWindow(), out uint focused);
        if (focused != pid) throw new InvalidOperationException("Windows refused foreground focus; no input was sent."); return h;
    }
    internal static readonly UIntPtr InputTag = (UIntPtr)0x52444247;
    private static void Input(params INPUT[] input) { for (int i = 0; i < input.Length; i++) if (input[i].type == 1) input[i].data.key.extra = InputTag; if (SendInput((uint)input.Length, input, Marshal.SizeOf<INPUT>()) != input.Length) throw new InputBlockedException("Windows blocked input. Check that the support helper is ready for elevated windows; unlock or handle secure desktop prompts locally."); }
    public static object Windows(int pid) => AutomationElement.RootElement.FindAll(TreeScope.Children, pid > 0 ? new PropertyCondition(AutomationElement.ProcessIdProperty, pid) : Condition.TrueCondition).Cast<AutomationElement>().Take(100).Select(x => new { pid = x.Current.ProcessId, name = x.Current.Name, handle = x.Current.NativeWindowHandle }).ToArray();
    public static object Inspect(int pid)
    {
        using var process = Process.GetProcessById(pid); var root = AutomationElement.FromHandle(process.MainWindowHandle);
        return root.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().Take(250).Select(x => new { automationId = x.Current.AutomationId, name = x.Current.IsPassword ? "<password>" : x.Current.Name, type = x.Current.ControlType.ProgrammaticName, enabled = x.Current.IsEnabled, bounds = new { x = x.Current.BoundingRectangle.X, y = x.Current.BoundingRectangle.Y, width = x.Current.BoundingRectangle.Width, height = x.Current.BoundingRectangle.Height } }).ToArray();
    }
    public static void Click(int pid, string id, string name)
    {
        var root = AutomationElement.FromHandle(Focus(pid));
        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name)) throw new ArgumentException("automationId or name is required.");
        var matches = root.FindAll(TreeScope.Descendants, new PropertyCondition(!string.IsNullOrEmpty(id) ? AutomationElement.AutomationIdProperty : AutomationElement.NameProperty, !string.IsNullOrEmpty(id) ? id : name));
        if (matches.Count != 1) throw new InvalidOperationException($"Expected one control; found {matches.Count}. Inspect the window.");
        var element = matches[0];
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) ((InvokePattern)invoke).Invoke();
        else { element.SetFocus(); var point = element.GetClickablePoint(); Mouse(pid, (int)point.X, (int)point.Y); }
    }
    public static void TypeText(int pid, string text)
    {
        if (text.Length > 8192) throw new ArgumentException("Text exceeds 8192 characters."); Focus(pid);
        foreach (char c in text) Input(new INPUT { type = 1, data = new UNION { key = new KEY { scan = c, flags = 4 } } }, new INPUT { type = 1, data = new UNION { key = new KEY { scan = c, flags = 6 } } });
    }
    private static void TypeTextIntoFocusedControl(string text)
    {
        if (text.Length > 8192) throw new ArgumentException("Text exceeds 8192 characters.");
        if (text.Length == 0) return;
        var inputs = new INPUT[checked(text.Length * 2)];
        int index = 0;
        foreach (char c in text)
        {
            inputs[index++] = new INPUT { type = 1, data = new UNION { key = new KEY { scan = c, flags = 4 } } };
            inputs[index++] = new INPUT { type = 1, data = new UNION { key = new KEY { scan = c, flags = 6 } } };
        }
        Input(inputs);
    }
    public static void Key(int pid, string chord)
    {
        var map = new Dictionary<string, ushort> { ["CTRL"] = 17, ["ALT"] = 18, ["SHIFT"] = 16, ["ENTER"] = 13, ["TAB"] = 9, ["ESCAPE"] = 27, ["BACKSPACE"] = 8, ["DELETE"] = 46, ["LEFT"] = 37, ["RIGHT"] = 39, ["UP"] = 38, ["DOWN"] = 40, ["SPACE"] = 32 };
        for (char c = 'A'; c <= 'Z'; c++) map[c.ToString()] = c;
        for (int i = 1; i <= 12; i++) map["F" + i] = (ushort)(111 + i);
        var parts = chord.ToUpperInvariant().Split('+');
        if (parts.Length is < 1 or > 4 || parts.Distinct().Count() != parts.Length || parts.Any(x => !map.ContainsKey(x)) || parts.SkipLast(1).Any(x => x is not ("CTRL" or "ALT" or "SHIFT")) || parts[^1] is "CTRL" or "ALT" or "SHIFT") throw new ArgumentException("Use a key or modifier chord such as CTRL+A, ENTER or F5.");
        Focus(pid); foreach (string p in parts) Input(new INPUT { type = 1, data = new UNION { key = new KEY { vk = map[p] } } });
        foreach (string p in parts.Reverse()) Input(new INPUT { type = 1, data = new UNION { key = new KEY { vk = map[p], flags = 2 } } });
    }
    public static void Mouse(int pid, int x, int y)
    {
        Focus(pid); var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        if (!bounds.Contains(x, y)) throw new ArgumentException("Mouse coordinates are outside the virtual screen.");
        SetCursorPos(x, y); Input(new INPUT { data = new UNION { mouse = new MOUSE { flags = 2 } } }, new INPUT { data = new UNION { mouse = new MOUSE { flags = 4 } } });
    }
    private static readonly object InputLock = new();
    private static readonly Dictionary<ushort, KEY> HeldKeys = new();
    private static readonly HashSet<string> HeldButtons = new();
    private static DateTime lastInput = DateTime.UtcNow;
    private static readonly System.Threading.Timer InputWatchdog = new(_ => { lock (InputLock) if (DateTime.UtcNow - lastInput > TimeSpan.FromSeconds(3)) ReleaseAllInput(); }, null, 1000, 1000);
    public static void HandleInput(JsonElement a)
    {
        lock (InputLock)
        {
            try { DesktopCapture.RequireDesktop(); }
            catch (InvalidOperationException ex) { throw new InputBlockedException(ex.Message); }
            lastInput = DateTime.UtcNow;
            string kind = a.Str("kind");
            if (kind == "release") { ReleaseAllInput(requireSuccess: true); return; }
            if (kind == "keepAlive") return;
            if (kind == "secureAttention") throw new InputBlockedException("Ctrl+Alt+Del requires the current Remote Debugger support service on the remote PC.");
            if (kind == "text") { TypeTextIntoFocusedControl(a.Str("text")); return; }
            if (kind is "move" or "down" or "up" or "wheel")
            {
                if (a.Str("layoutId") != DesktopCapture.LayoutId()) { ReleaseAllInput(); throw new InvalidOperationException("Display geometry changed. Refresh the screen before sending input."); }
                int x = a.Int("x"), y = a.Int("y");
                if (!System.Windows.Forms.Screen.AllScreens.Any(s => s.Bounds.Contains(x, y))) throw new ArgumentException("Pointer is outside all displays.");
                SetCursorPos(x, y);
                if (kind == "move") return;
                string button = a.Str("button", "left"); uint flags = (button, kind) switch { ("left", "down") => 2, ("left", "up") => 4, ("right", "down") => 8, ("right", "up") => 16, ("middle", "down") => 32, ("middle", "up") => 64, (_, "wheel") => 0x0800, _ => throw new ArgumentException("Invalid mouse button.") };
                Input(new INPUT { data = new UNION { mouse = new MOUSE { flags = flags, data = kind == "wheel" ? unchecked((uint)Math.Clamp(a.Int("delta"), -1200, 1200)) : 0 } } });
                if (kind == "down") HeldButtons.Add(button); if (kind == "up") HeldButtons.Remove(button); return;
            }
            if (kind is "keyDown" or "keyUp")
            {
                int vk = a.Int("virtualKey"); if (vk is < 8 or > 254) throw new ArgumentException("Invalid virtual key.");
                int scan = a.Int("scanCode"); if (scan is < 0 or > 255) throw new ArgumentException("Invalid scan code.");
                bool extended = a.TryGetProperty("extended", out var ext) ? ext.GetBoolean() : IsExtendedKey(vk);
                var key = new KEY { vk = (ushort)vk, scan = (ushort)scan, flags = KeyboardFlags(kind == "keyUp", scan, extended), extra = InputTag };
                Input(new INPUT { type = 1, data = new UNION { key = key } });
                if (kind == "keyDown") HeldKeys[(ushort)vk] = key; else HeldKeys.Remove((ushort)vk); return;
            }
            throw new ArgumentException("Unknown input kind.");
        }
    }
    public static void ReleaseAllInput(bool requireSuccess = false)
    {
        lock (InputLock)
        {
            foreach (var entry in HeldKeys.ToArray())
            {
                var key = entry.Value; key.flags |= 2;
                if (SendInput(1, [new INPUT { type = 1, data = new UNION { key = key } }], Marshal.SizeOf<INPUT>()) == 1) HeldKeys.Remove(entry.Key);
            }
            foreach (string button in HeldButtons.ToArray())
                if (SendInput(1, [new INPUT { data = new UNION { mouse = new MOUSE { flags = button == "left" ? 4u : button == "right" ? 16u : 64u } } }], Marshal.SizeOf<INPUT>()) == 1) HeldButtons.Remove(button);
            if (requireSuccess && (HeldKeys.Count > 0 || HeldButtons.Count > 0)) throw new InputBlockedException("Windows has not released held input; return to the interactive desktop.");
        }
    }
    internal static bool IsExtendedKey(int vk) => vk is >= 0x21 and <= 0x28 or 0x2C or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0x6F or 0x90 or 0xA3 or 0xA5;
    internal static uint KeyboardFlags(bool up, int scanCode, bool extended) => (up ? 2u : 0u) | (scanCode != 0 ? 8u : 0u) | (extended ? 1u : 0u);
    public static object Screenshot()
    {
        var r = System.Windows.Forms.SystemInformation.VirtualScreen;
        using var bmp = new Bitmap(r.Width, r.Height); using var g = Graphics.FromImage(bmp); g.CopyFromScreen(r.Location, Point.Empty, r.Size);
        using var ms = new MemoryStream(); bmp.Save(ms, ImageFormat.Jpeg);
        if (ms.Length > 2 * 1024 * 1024) throw new IOException("Screenshot exceeds 2 MiB; reduce the remote display resolution.");
        return new { x = r.X, y = r.Y, width = r.Width, height = r.Height, mime = "image/jpeg", data = Convert.ToBase64String(ms.ToArray()) };
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DebugActiveProcess(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DebugActiveProcessStop(uint pid);
    [DllImport("kernel32.dll")] private static extern bool DebugSetProcessKillOnExit(bool value);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WaitForDebugEvent(IntPtr debugEvent, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool ContinueDebugEvent(uint pid, uint tid, uint status);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DebugBreakProcess(IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("dbghelp.dll", SetLastError = true)] private static extern bool MiniDumpWriteDump(IntPtr process, uint pid, Microsoft.Win32.SafeHandles.SafeFileHandle file, uint flags, IntPtr exception, IntPtr user, IntPtr callback);
    public static object Debug(int pid, int seconds, CancellationToken ct)
    {
        if (pid == Environment.ProcessId) throw new ArgumentException("Cannot debug the agent itself.");
        if (!DebugActiveProcess((uint)pid)) throw new Win32Exception(Marshal.GetLastWin32Error());
        DebugSetProcessKillOnExit(false); var events = new List<object>(); bool breakpoint = false; bool detached;
        IntPtr buffer = Marshal.AllocHGlobal(176);
        try
        {
            using var p = Process.GetProcessById(pid); if (!DebugBreakProcess(p.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed.TotalSeconds < seconds)
            {
                ct.ThrowIfCancellationRequested(); if (!WaitForDebugEvent(buffer, 100)) continue;
                uint kind = (uint)Marshal.ReadInt32(buffer), process = (uint)Marshal.ReadInt32(buffer, 4), thread = (uint)Marshal.ReadInt32(buffer, 8);
                uint exceptionCode = kind == 1 ? (uint)Marshal.ReadInt32(buffer, 16) : 0;
                if (kind == 1 && exceptionCode == 0x80000003) breakpoint = true;
                events.Add(new { kind, process, thread, exceptionCode = exceptionCode.ToString("X8") });
                if (kind is 3 or 6) { IntPtr file = Marshal.ReadIntPtr(buffer, 16); if (file != IntPtr.Zero) CloseHandle(file); }
                // Windows owns the process/thread event handles until their exit event is continued.
                if (!ContinueDebugEvent(process, thread, kind == 1 && exceptionCode != 0x80000003 ? 0x80010001u : 0x00010002u)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (kind == 5) break;
            }
        }
        finally { detached = DebugActiveProcessStop((uint)pid); Marshal.FreeHGlobal(buffer); }
        return new { attached = true, breakpointObserved = breakpoint, detached, events };
    }
    public static void Dump(int pid, string path)
    {
        using var p = Process.GetProcessById(pid); using var file = File.Create(path);
        if (!MiniDumpWriteDump(p.Handle, (uint)pid, file.SafeFileHandle, 0x00001000, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
