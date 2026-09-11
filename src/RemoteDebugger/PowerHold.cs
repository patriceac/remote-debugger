using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RemoteDebugger;

/// <summary>
/// Keeps Windows awake while the visible Remote Debugger session is active.
/// Power requests are process handles, so cleanup remains correct when the
/// asynchronous session work resumes on a different thread. The user's saved
/// power plan is never changed.
/// </summary>
internal sealed class PowerHold : IDisposable
{
    private const uint SimpleReason = 0x1;
    // POWER_REQUEST_TYPE: DisplayRequired=0, SystemRequired=1.
    private const uint PowerRequestSystemRequired = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public IntPtr Reason;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePowerRequestHandle PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerSetRequest(SafePowerRequestHandle request, uint requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerClearRequest(SafePowerRequestHandle request, uint requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private readonly SafePowerRequestHandle request;
    private int released;

    private PowerHold()
    {
        IntPtr reason = Marshal.StringToHGlobalUni("Remote Debugger support session is active.");
        try
        {
            var context = new ReasonContext { Version = 0, Flags = SimpleReason, Reason = reason };
            request = PowerCreateRequest(ref context);
            if (request.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not create a power request.");
            if (!PowerSetRequest(request, PowerRequestSystemRequired))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not keep this session awake.");
        }
        catch
        {
            // A failed request owns no usable handle. Dispose is idempotent and
            // the SafeHandle closes a handle if PowerSetRequest failed after
            // creation.
            request?.Dispose();
            throw;
        }
        finally { Marshal.FreeHGlobal(reason); }
    }

    public static PowerHold Acquire() => new();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref released, 1) != 0) return;
        if (!request.IsInvalid) _ = PowerClearRequest(request, PowerRequestSystemRequired);
        request.Dispose();
    }

    private sealed class SafePowerRequestHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafePowerRequestHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
}
