using System.Runtime.InteropServices;
using System.Security.Principal;

namespace RemoteDebugger;

internal static class CursorUserName
{
    private static readonly Lazy<string> current = new(Read);
    internal static string Current => current.Value;
    internal static string FirstName(string? fullName, string login) =>
        string.IsNullOrWhiteSpace(fullName) ? login : fullName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];

    private static string Read()
    {
        // Input helpers run as SYSTEM: use the person in their Windows session.
        string login = SessionName(5), domain = SessionName(7);
        if (login.Length == 0)
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.IsSystem) return "";
            login = Environment.UserName; domain = Environment.UserDomainName;
        }
        if (domain.Length > 0 && !domain.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return login;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            string? fullName = NetUserGetInfo(null, login, 10, out buffer) == 0
                ? Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, 3 * IntPtr.Size)) : null;
            return FirstName(fullName, login);
        }
        finally { if (buffer != IntPtr.Zero) NetApiBufferFree(buffer); }
    }

    private static string SessionName(int kind)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, -1, kind, out var buffer, out _)) return "";
        try { return Marshal.PtrToStringUni(buffer) ?? ""; }
        finally { WTSFreeMemory(buffer); }
    }

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", CharSet = CharSet.Unicode)] private static extern bool WTSQuerySessionInformation(IntPtr server, int session, int kind, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr buffer);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] private static extern uint NetUserGetInfo(string? server, string user, int level, out IntPtr buffer);
    [DllImport("netapi32.dll")] private static extern uint NetApiBufferFree(IntPtr buffer);
}
