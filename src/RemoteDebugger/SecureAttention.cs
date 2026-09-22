using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace RemoteDebugger;

internal static class SecureAttention
{
    private static readonly object Sync = new();
    internal static bool PolicyAllowsService(object? value) => value is null or 1 or 3;

    internal static void Send(NamedPipeServerStream controller)
    {
        if (!WindowsIdentity.GetCurrent().IsSystem) throw new InputBlockedException("Ctrl+Alt+Del requires the installed support service.");
        lock (Sync)
        {
            using var policy = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", true);
            const string name = "SoftwareSASGeneration";
            object? saved = policy.GetValue(name);
            if (!PolicyAllowsService(saved)) throw new InputBlockedException("Windows policy blocks remote Ctrl+Alt+Del. Allow Services in 'Disable or enable software Secure Attention Sequence' on the remote PC.");
            // Enable only for this explicit request when no policy was configured.
            // Never override an administrator's existing policy.
            if (saved == null) policy.SetValue(name, 1, RegistryValueKind.DWord);
            try { controller.RunAsClient(() => SendSAS(false)); }
            finally { if (saved == null && policy.GetValue(name) is 1) policy.DeleteValue(name, false); }
        }
    }

    [DllImport("sas.dll")] private static extern void SendSAS([MarshalAs(UnmanagedType.Bool)] bool asUser);
}
