using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using RemoteDebugger.Core;

namespace RemoteDebugger;

internal static partial class SupportInstaller
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{B12489BE-DF12-4DD2-AFD4-FB82B032BE05}_is1";
    private static string UserStartupLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Remote Debugger.lnk");

    internal static int PrepareOriginalUser()
    {
        try
        {
            StopKnownApplicationProcesses();
            string legacy = Path.GetDirectoryName(SupportPlatformPaths.LegacyUserApplicationExecutable)!;
            string uninstaller = Path.Combine(legacy, "unins000.exe");
            if (File.Exists(uninstaller))
            {
                using var process = Process.Start(new ProcessStartInfo(uninstaller, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS")
                    { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = legacy }) ?? throw new IOException("Legacy uninstaller did not start.");
                if (!process.WaitForExit(120000) || process.ExitCode != 0) throw new IOException("Legacy uninstall failed.");
            }
            DeleteIfExists(UserStartupLink);
            DeleteOwnedDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Remote Debugger"), Environment.GetFolderPath(Environment.SpecialFolder.Programs));
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
            DeleteOwnedDirectory(legacy, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            return 0;
        }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); return 2; }
    }

    internal static int CreateOriginalUserStartup()
    {
        try
        {
            if (!PathsEqual(Environment.ProcessPath!, SupportPlatformPaths.ApplicationExecutable)) return 3;
            Directory.CreateDirectory(Path.GetDirectoryName(UserStartupLink)!);
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            try
            {
                dynamic shortcut = shell.CreateShortcut(UserStartupLink);
                try
                {
                    shortcut.TargetPath = SupportPlatformPaths.ApplicationExecutable;
                    shortcut.WorkingDirectory = SupportPlatformPaths.ProductDirectory;
                    shortcut.Arguments = "--startup";
                    shortcut.Save();
                }
                finally { Marshal.FinalReleaseComObject(shortcut); }
            }
            finally { Marshal.FinalReleaseComObject(shell); }
            return 0;
        }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); return 2; }
    }

    internal static bool CanUninstall(UpdateTransactionState state) => state is
        UpdateTransactionState.Staged or UpdateTransactionState.Completed or UpdateTransactionState.Cancelled or UpdateTransactionState.RolledBack or UpdateTransactionState.Failed;

    internal static int UninstallSupport()
    {
        try
        {
            if (!Native.IsElevated() || !PathsEqual(Environment.ProcessPath!, SupportPlatformPaths.ApplicationExecutable)) return 3;
            using (var installationLock = SupportPlatformPaths.AcquireUpdateLock())
            {
                RequireNoActiveUpdate();
                var configuration = File.Exists(SupportPlatformPaths.ConfigurationPath)
                    ? JsonSerializer.Deserialize<SupportConfiguration>(File.ReadAllText(SupportPlatformPaths.ConfigurationPath), Json.Options) : null;
                StopKnownApplicationProcesses();
                StopExistingService();
                var removed = RunSc(false, "delete", SupportPlatformPaths.ServiceName);
                if (removed.ExitCode != 0 && !removed.Stdout.Contains("1060") && !removed.Stderr.Contains("1060")) throw new IOException("Could not remove the support service.");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                FirewallManager.RemoveAsync(deadline.Token).GetAwaiter().GetResult();
                if (configuration != null) RemoveRegisteredStartup(configuration.RegisteredUserSid);
                DeleteIfExists(UserStartupLink);
                DeleteOwnedDirectory(SupportPlatformPaths.InstallDirectory, SupportPlatformPaths.ProductDirectory);
                DeleteOwnedDirectory(SupportPlatformPaths.StateDirectory, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
            }
            DeleteIfExists(SupportPlatformPaths.UpdateLockPath);
            return 0;
        }
        catch (Exception ex) { TryWriteMaintenanceError("support-uninstall-error.txt", ex); return 2; }
    }

    private static void RequireNoActiveUpdate()
    {
        if (!Directory.Exists(SupportPlatformPaths.TransactionsDirectory)) return;
        foreach (string path in Directory.EnumerateFiles(SupportPlatformPaths.TransactionsDirectory, "*.json"))
        {
            var transaction = JsonSerializer.Deserialize<PrivilegedUpdateTransaction>(File.ReadAllText(path), Json.Options)
                ?? throw new InvalidDataException("Unreadable update transaction.");
            if (!CanUninstall(transaction.State)) throw new InvalidOperationException("Finish or cancel the active agent update before changing the installation.");
        }
    }

    private static void RemoveRegisteredStartup(string sid)
    {
        _ = new SecurityIdentifier(sid);
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid);
        if (key?.GetValue("ProfileImagePath") is not string profile) return;
        profile = Environment.ExpandEnvironmentVariables(profile);
        string link = Path.Combine(profile, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\Remote Debugger.lnk");
        PrivilegedPathSafety.RequireUnderNonReparseRoot(link, profile);
        DeleteIfExists(link);
    }

    private static void DeleteOwnedDirectory(string path, string root)
    {
        if (!Directory.Exists(path)) return;
        PrivilegedPathSafety.RequireUnderNonReparseRoot(path, root);
        foreach (string child in Directory.EnumerateFileSystemEntries(path))
        {
            if (File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Refusing to remove a redirected installation path.");
            if (Directory.Exists(child)) DeleteOwnedDirectory(child, root);
            else File.Delete(child);
        }
        Directory.Delete(path);
    }
}
