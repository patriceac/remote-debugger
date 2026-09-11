using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RemoteDebugger;

internal sealed class PowerRequestScope : IDisposable
{
    private readonly SafeFileHandle handle;
    private bool set;

    private PowerRequestScope(string reason)
    {
        var context = new ReasonContext
        {
            Version = 0,
            Flags = 0x1,
            SimpleReasonString = Marshal.StringToHGlobalUni(reason)
        };
        try { handle = PowerCreateRequest(ref context); }
        finally { Marshal.FreeHGlobal(context.SimpleReasonString); }
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not create an update power request.");
        if (!PowerSetRequest(handle, 0))
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "Windows could not hold system sleep during update.");
        }
        set = true;
    }

    public static PowerRequestScope HoldSystemAwakeForUpdate() =>
        new("Remote Debugger is replacing and health-checking its signed executable.");

    public void Dispose()
    {
        if (set) { _ = PowerClearRequest(handle, 0); set = false; }
        handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public IntPtr SimpleReasonString;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle PowerCreateRequest(ref ReasonContext context);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerSetRequest(SafeFileHandle powerRequest, int requestType);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerClearRequest(SafeFileHandle powerRequest, int requestType);
}
