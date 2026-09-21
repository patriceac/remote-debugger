using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private async Task InternetInstallerReviewAsync()
    {
        string installer = application;
        string localInstaller = Path.Combine(output, Path.GetFileName(installer));
        File.Copy(installer, localInstaller, overwrite: true);
        installer = localInstaller;
        if (InternetSettings.Load(Vault.DefaultRoot) != null)
            throw new IOException("The installation test requires a fresh internet configuration.");
        Pass("installer.fresh_profile", scope == "installed" ? "The fresh installation has no unlocked Internet credentials" : "Internet settings are absent before installation");
        try
        {
            string installedDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RemoteDebugger");
            if (scope == "runtime")
            {
                string customDirectory = Path.Combine(output, "unsupported-installation");
                int rejected = await RunSetupAsync(installer, "/DIR=" + customDirectory, "install-rejected.log");
                if (rejected == 0 || File.Exists(Path.Combine(customDirectory, "RemoteDebugger.exe")) || File.Exists(Path.Combine(installedDirectory, "RemoteDebugger.exe")))
                    throw new IOException("The custom installation directory was not rejected before copying application files.");
                Pass("installer.fixed_directory", "A custom /DIR is rejected before installing any application files", new { exitCode = rejected });
            }
            if (scope != "installed")
            {
                var start = new ProcessStartInfo(installer) { UseShellExecute = true, Verb = "runas" };
                foreach (string argument in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/LOG=" + Path.Combine(output, "install.log") })
                    start.ArgumentList.Add(argument);
                using var setup = Process.Start(start) ?? throw new IOException("The private installer did not start.");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(180));
                await setup.WaitForExitAsync(timeout.Token);
                if (setup.ExitCode != 0) throw new IOException("The private installer failed with exit code " + setup.ExitCode);
            }
            Pass("installer.completed", "The signed private installer completes without interactive setup", new { sha256 = await HashFileAsync(installer) });

            string installedApp = Path.Combine(installedDirectory, "RemoteDebugger.exe");
            string startup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Remote Debugger.lnk");
            string menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Remote Debugger.lnk");
            if (!File.Exists(installedApp) || !File.Exists(startup) || !File.Exists(menu) || !File.Exists(Path.Combine(installedDirectory, "build-info.json")))
                throw new IOException("The protected application, build identity, or desktop shortcuts are missing.");
            if (scope == "installed" && await HashFileAsync(installedApp) != await HashFileAsync(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../release/RemoteDebugger.exe"))))
                throw new IOException("The installed executable differs from the final signed Release.");
            if (Directory.EnumerateFiles(installedDirectory, "*.rdrelay").Any()) throw new IOException("The imported encrypted profile was not removed from the installation directory.");
            Pass("installer.original_user_integration", "The protected Release includes build identity, machine Start menu and initiating user startup shortcuts");

            string pendingSetup = new SecurityMigrationStore(Vault.DefaultRoot).PendingSetupPath;
            if (File.Exists(pendingSetup))
            {
                _ = ProtectedSetup.Read(await File.ReadAllBytesAsync(pendingSetup, stop.Token));
                if (InternetSettings.Load(Vault.DefaultRoot) != null) throw new IOException("The encrypted installer unlocked without a passphrase.");
                Pass("installer.locked_setup", "The private installer stages a valid encrypted envelope without authorizing the Windows account");
                application = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RemoteDebugger", "RemoteDebugger.exe");
                loopbackAgent = LaunchInternetProduct(true, Vault.DefaultRoot); product = loopbackAgent;
                await WaitUiAsync(); WindowState = Forms.FormWindowState.Minimized;
                if (!SecurityControl("securityPassphrase").Current.IsPassword || !SecurityControl("securityCreate").Current.IsEnabled)
                    throw new IOException("First launch did not offer an enabled, masked passphrase unlock.");
                CaptureDesktop("installed-passphrase-required.png", focusProduct: false);
                if (InternetSettings.Load(Vault.DefaultRoot) != null) throw new IOException("First launch silently authorized access.");
                Pass("installer.passphrase_required", "The installed Release automatically requests the passphrase on first normal launch");
                if (scope == "runtime")
                {
                    await CleanupLoopbackProcessesAsync();
                    int removed = await RunSetupAsync(Path.Combine(installedDirectory, "unins000.exe"), "", "uninstall.log");
                    if (removed != 0 || File.Exists(installedApp) || File.Exists(startup) || File.Exists(menu) || Directory.Exists(SupportPlatformPaths.InstallDirectory))
                        throw new IOException("Uninstall did not remove application, support and shortcut files.");
                    if (!File.Exists(pendingSetup)) throw new IOException("Uninstall removed personal setup data.");
                    Pass("installer.uninstall", "Uninstall removes application and shortcuts while preserving personal setup data", new { exitCode = removed });
                }
                await FinishAsync(); return;
            }

            var settings = InternetSettings.Load(Vault.DefaultRoot) ?? throw new IOException("The installer did not import internet settings.");
            byte[] encrypted = await File.ReadAllBytesAsync(Path.Combine(Vault.DefaultRoot, "internet.dpapi"), stop.Token);
            if (Encoding.UTF8.GetString(encrypted).Contains(settings.AccessKey, StringComparison.Ordinal))
                throw new IOException("Installed internet settings contain a plaintext access key.");
            Pass("installer.protected_settings", "The current user can load the automatically imported DPAPI-protected settings", new { settings.RelayUrl });

            application = Path.Combine(installedDirectory, "RemoteDebugger.exe");
            if (!File.Exists(application)) throw new IOException("The Program Files Release executable is missing.");
            string legacyDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Remote Debugger");
            if (Directory.Exists(legacyDirectory)) throw new IOException("The legacy per-user installation was not removed.");
            Pass("installer.program_files", "The installer uses the single protected Program Files installation", new { application });
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            if (Directory.EnumerateFiles(installedDirectory, "*.rdrelay", options).Any() ||
                Directory.EnumerateFiles(Path.GetTempPath(), "RemoteDebugger-Internet.rdrelay", options).Any())
                throw new IOException("The installer left its plaintext relay profile on disk.");
            Pass("installer.profile_cleanup", "The embedded plaintext profile is removed after import and is absent from installed files");

            string shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Remote Debugger.lnk");
            using var uninstall = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{B12489BE-DF12-4DD2-AFD4-FB82B032BE05}_is1");
            if (!File.Exists(shortcut) || uninstall == null) throw new IOException("Machine Start menu or uninstall registration is missing.");
            Pass("installer.desktop_integration", "Installation retains the machine Start menu shortcut and uninstaller");

            string startupShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Remote Debugger.lnk");
            if (!File.Exists(startupShortcut)) throw new IOException("The current user's Windows startup shortcut is missing.");
            if (scope == "handoff")
            {
                loopbackAgent = Process.Start(new ProcessStartInfo(application) { UseShellExecute = false, WorkingDirectory = installedDirectory })
                    ?? throw new IOException("The installed app did not start for handoff.");
                product = loopbackAgent;
                await WaitUiAsync();
                Pass("installer.handoff", "The Internet-enabled VM has Remote Debugger installed and open for the user");
                CaptureDesktop("installed-handoff.png");
                await FinishAsync();
                while (!product.HasExited) await Task.Delay(1000, stop.Token);
                Close();
                return;
            }
            using var launched = Process.Start(new ProcessStartInfo(startupShortcut) { UseShellExecute = true });
            var startupDeadline = Stopwatch.StartNew();
            while (startupDeadline.Elapsed < TimeSpan.FromSeconds(30) && loopbackAgent == null)
            {
                foreach (var candidate in Process.GetProcessesByName("RemoteDebugger"))
                {
                    bool matches = false;
                    try { matches = !candidate.HasExited && string.Equals(candidate.MainModule?.FileName, application, StringComparison.OrdinalIgnoreCase); }
                    catch (System.ComponentModel.Win32Exception) { }
                    if (matches) { loopbackAgent = candidate; break; }
                    candidate.Dispose();
                }
                if (loopbackAgent == null) await Task.Delay(250, stop.Token);
            }
            product = loopbackAgent ?? throw new IOException("The Windows startup shortcut did not launch the installed app.");
            WindowState = Forms.FormWindowState.Minimized;
            var trayContext = await OpenTrayContextAsync();
            if (trayContext.OpenItem == null || Native.NativeWindows().Any(window => window.Pid == product.Id && window.Name == "Remote Debugger"))
                throw new IOException("Automatic startup did not stay in the system tray.");
            Pass("installer.startup", "The installed Windows startup shortcut starts the Release in the system tray");
            CaptureDesktop("installed-startup-tray.png", focusProduct: false);
            using var duplicateStartup = Process.Start(new ProcessStartInfo(startupShortcut) { UseShellExecute = true });
            if (duplicateStartup != null) await duplicateStartup.WaitForExitAsync(stop.Token);
            if (Native.NativeWindows().Any(window => window.Pid == product.Id && window.Name == "Remote Debugger"))
                throw new IOException("A repeated automatic startup restored the hidden window.");
            Pass("installer.startup_quiet", "Repeated automatic startup leaves the existing window in the tray");
            InvokeElement(trayContext.OpenItem);
            await WaitUiAsync(); WindowState = Forms.FormWindowState.Minimized;
            ResizeProductWindow(1060, 720); Native.FocusWindow(product.Id);
            Pass("installer.startup_restore", "The tray Open action restores the installed app");
            if (Find("internetSetup", 100) != null)
                throw new IOException("The installed app still exposes a manual internet setup step.");
            if (Find("enableSupport", 100) is not { } enable || Value(enable) != "Activer l’assistance")
                throw new IOException("The installed app does not expose its single Enable support action.");
            if (Find("supportId", 100) != null || Find("agentPairCode", 100) != null ||
                (await settings.FindAsync(stop.Token)).Any(peer => peer.Name == Environment.MachineName))
                throw new IOException("The private client exposes a code or grants access before Enable support.");
            Pass("installer.seamless_ui", "First launch waits for Enable support, with no ID, code, or manual Internet setup");
            CaptureDesktop("installed-awaiting-enable.png");
            if (scope is "demo" or "demo-hold")
            {
                await WaitForHostDemonstrationAsync(); await FinishAsync();
                if (scope == "demo-hold")
                {
                    // The user owns the live demo until they exit the client;
                    // the broker still enforces its approved two-hour limit.
                    while (product is { HasExited: false }) await Task.Delay(1000, stop.Token);
                    Close();
                }
                return;
            }
            // Automated transport qualification opts in explicitly at launch.
            // The interactive demo separately exercises the Windows activation button.
            await CleanupLoopbackProcessesAsync();
            loopbackAgent = LaunchInternetProduct(true, Vault.DefaultRoot, enableSupport: true); product = loopbackAgent;
            await WaitUiAsync(); ResizeProductWindow(1060, 720); Native.FocusWindow(product.Id);
            var deadline = Stopwatch.StartNew();
            bool online = false;
            while (deadline.Elapsed < TimeSpan.FromSeconds(75))
            {
                online = (await settings.FindAsync(stop.Token)).Any(peer => peer.Name == Environment.MachineName);
                if (online) break;
                await Task.Delay(500, stop.Token);
            }
            if (!online) throw new IOException("The installed app did not appear in private discovery.");
            if (Find("supportId", 100) != null || Find("copySupportId", 100) != null)
                throw new IOException("The client still displays a support ID.");
            CaptureDesktop("installed-internet-ready.png");
            Pass("installer.internet_ready", "Explicit support activation is discoverable by computer name without codes", new { computer = Environment.MachineName });
            await FinishAsync();
        }
        finally { await CleanupLoopbackProcessesAsync(); }
    }

    private async Task<int> RunSetupAsync(string executable, string extraArgument, string logName)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" };
        foreach (string argument in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/LOG=" + Path.Combine(output, logName) }) start.ArgumentList.Add(argument);
        if (extraArgument.Length > 0) start.ArgumentList.Add(extraArgument);
        using var process = Process.Start(start) ?? throw new IOException("Installer process did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(180));
        await process.WaitForExitAsync(timeout.Token);
        return process.ExitCode;
    }

    private async Task WaitForHostDemonstrationAsync()
    {
        var deadline = Stopwatch.StartNew();
        bool connected = false;
        string managedApplication = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RemoteDebugger", "RemoteDebugger.exe");
        while (deadline.Elapsed < TimeSpan.FromMinutes(15))
        {
            if (product is { HasExited: true })
            {
                // Enable support relaunches the same signed app from Program Files.
                var replacement = Process.GetProcessesByName("RemoteDebugger").FirstOrDefault(candidate =>
                {
                    try { return candidate.MainWindowHandle != IntPtr.Zero && string.Equals(candidate.MainModule?.FileName,
                        managedApplication, StringComparison.OrdinalIgnoreCase); }
                    catch { return false; }
                });
                if (replacement != null) { product = loopbackAgent = replacement; application = managedApplication; }
            }
            connected |= IsConnected(TryValue("connectionStatus"));
            if (scope == "demo-hold" && connected)
            {
                Pass("installer.host_demo", "The host connected without an ID or code; the user retains the live session");
                CaptureDesktop("host-demo-live.png"); return;
            }
            if (TryValue("agentHeading") == "Assistance terminée" && connected)
            {
                Pass("installer.host_demo", "The host connected to the installed VM client and ended support without an ID or code");
                CaptureDesktop("host-demo-final.png"); return;
            }
            await Task.Delay(1000, stop.Token);
        }
        throw new IOException("The host demonstration did not complete a connected session followed by End support.");
    }
}
