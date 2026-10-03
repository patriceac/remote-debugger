using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using RemoteDebugger;

namespace RemoteDebugger.Lab;

internal static class InstallerStartupReview
{
    public static async Task RunAsync(string[] args)
    {
        string phase = args[1], output = args[3];
        Directory.CreateDirectory(output);
        var checks = new List<object>();
        bool passed = true;
        void Check(string id, bool success, object evidence)
        {
            passed &= success;
            checks.Add(new { id, passed = success, evidence });
        }
        try
        {
            string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RemoteDebugger", "RemoteDebugger.exe");
            if (phase == "Before")
            {
                Check("fresh_install", !File.Exists(installed) && Process.GetProcessesByName("RemoteDebugger").Length == 0, new { installed });
            }
            else
            {
                var identity = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(args[2]));
                string sid = identity.GetProperty("Identity").GetProperty("Initiator").GetProperty("Sid").GetString()!;
                string expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[4])));
                string installedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(installed)));
                Check("installed_release", installedHash == expectedHash, new { installed, installedHash, expectedHash });
                using var process = await WaitForStartupAsync(installed);
                var token = ProvisioningEvidence.InspectProduct(process, installed, sid);
                Check("original_user", token.Accepted && token.SessionId == Process.GetCurrentProcess().SessionId, token);
                bool trayRegistered = false;
                for (int attempt = 0; attempt < 40 && !trayRegistered; attempt++)
                {
                    trayRegistered = HasTrayIcon(process.Id);
                    if (!trayRegistered) await Task.Delay(250);
                }
                Check("tray_icon", trayRegistered, new { process.Id });
                await Task.Delay(1000);
                var windows = Native.NativeWindows().Where(window => window.Pid == process.Id).ToArray();
                Check("hidden_main_window", !process.HasExited && windows.Length == 0, windows);
            }
        }
        catch (Exception ex) { Check("observation", false, new { error = ex.ToString() }); }
        await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { phase, passed, checks }));
    }

    private static async Task<Process> WaitForStartupAsync(string installed)
    {
        for (int attempt = 0; attempt < 120; attempt++)
        {
            foreach (var process in Process.GetProcessesByName("RemoteDebugger"))
            {
                if (string.Equals(process.MainModule?.FileName, installed, StringComparison.OrdinalIgnoreCase)) return process;
                process.Dispose();
            }
            await Task.Delay(250);
        }
        throw new IOException("The installer did not start the installed program.");
    }

    private static bool HasTrayIcon(int processId)
    {
        bool found = false;
        EnumWindows((window, parameter) =>
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner != processId) return true;
            for (uint id = 1; id <= 4; id++)
            {
                var icon = new NotifyIconIdentifier { Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), Window = window, Id = id };
                if (Shell_NotifyIconGetRect(ref icon, out _) == 0) { found = true; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rect location);
}
