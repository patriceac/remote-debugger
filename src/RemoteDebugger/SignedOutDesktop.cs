using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace RemoteDebugger;

internal static class SignedOutDesktop
{
    [ThreadStatic] private static bool bound;
    internal static bool IsHelper { get; set; }
    internal static bool IsPrivileged => UnattendedSupport.IsWorker || IsHelper;
    internal static bool NeedsDispatch => IsPrivileged && !bound && (UnattendedSupport.IsWorker || !DesktopCapture.IsAvailable);
    internal static bool UsesSystemPointer => UnattendedSupport.IsWorker || IsHelper && !DesktopCapture.IsDefaultDesktop;
    internal static Point Pointer()
    {
        if (!GetCursorPos(out var point)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return point;
    }

    internal static T Invoke<T>(Func<T> action)
    {
        if (!NeedsDispatch) return action();
        T result = default!;
        ExceptionDispatchInfo? error = null;
        IntPtr desktop = IntPtr.Zero;
        // A fresh thread owns no windows or hooks. Bind before any USER/GDI work,
        // and release the handle after the thread exits, including desktop changes.
        var thread = new Thread(() =>
        {
            try
            {
                desktop = OpenInputDesktop(0, false, 0x01FF);
                if (desktop == IntPtr.Zero || !SetThreadDesktop(desktop)) throw new Win32Exception(Marshal.GetLastWin32Error());
                bound = true;
                result = action();
            }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        }) { IsBackground = true, Name = "Remote Debugger sign-in desktop" };
        thread.Start();
        thread.Join();
        if (desktop != IntPtr.Zero) CloseDesktop(desktop);
        error?.Throw();
        return result;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out Point point);
}
