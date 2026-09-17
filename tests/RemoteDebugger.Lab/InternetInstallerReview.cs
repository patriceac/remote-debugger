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
        if (InternetSettings.Load(Vault.DefaultRoot) != null)
            throw new IOException("The installation test requires a fresh internet configuration.");
        Pass("installer.fresh_profile", "Internet settings are absent before installation");
        try
        {
            var start = new ProcessStartInfo(installer) { UseShellExecute = false };
            foreach (string argument in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/LOG=" + Path.Combine(output, "install.log") })
                start.ArgumentList.Add(argument);
            using var setup = Process.Start(start) ?? throw new IOException("The private installer did not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(180));
            await setup.WaitForExitAsync(timeout.Token);
            if (setup.ExitCode != 0) throw new IOException("The private installer failed with exit code " + setup.ExitCode);
            Pass("installer.completed", "The signed private installer completes without interactive setup", new { sha256 = await HashFileAsync(installer) });

            var settings = InternetSettings.Load(Vault.DefaultRoot) ?? throw new IOException("The installer did not import internet settings.");
            byte[] encrypted = await File.ReadAllBytesAsync(Path.Combine(Vault.DefaultRoot, "internet.dpapi"), stop.Token);
            if (Encoding.UTF8.GetString(encrypted).Contains(settings.AccessKey, StringComparison.Ordinal))
                throw new IOException("Installed internet settings contain a plaintext access key.");
            Pass("installer.protected_settings", "The current user can load the automatically imported DPAPI-protected settings", new { settings.RelayUrl });

            string installedDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Remote Debugger");
            application = Path.Combine(installedDirectory, "RemoteDebugger.exe");
            if (!File.Exists(application)) throw new IOException("The per-user Release executable is missing.");
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            if (Directory.EnumerateFiles(installedDirectory, "*.rdrelay", options).Any() ||
                Directory.EnumerateFiles(Path.GetTempPath(), "RemoteDebugger-Internet.rdrelay", options).Any())
                throw new IOException("The installer left its plaintext relay profile on disk.");
            Pass("installer.profile_cleanup", "The embedded plaintext profile is removed after import and is absent from installed files");

            string shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Remote Debugger", "Remote Debugger.lnk");
            using var uninstall = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{B12489BE-DF12-4DD2-AFD4-FB82B032BE05}_is1");
            if (!File.Exists(shortcut) || uninstall == null) throw new IOException("Per-user Start menu or uninstall registration is missing.");
            Pass("installer.desktop_integration", "Installation retains the per-user Start menu shortcut and uninstaller");

            loopbackAgent = LaunchInternetProduct(true, Vault.DefaultRoot); product = loopbackAgent;
            await WaitUiAsync(); WindowState = Forms.FormWindowState.Minimized;
            ResizeProductWindow(1060, 720); Native.FocusWindow(product.Id);
            var deadline = Stopwatch.StartNew();
            string supportId = "";
            while (deadline.Elapsed < TimeSpan.FromSeconds(75))
            {
                var field = Find("supportId", 100);
                supportId = field == null ? "" : Value(field);
                if (InternetSettings.IsSupportId(supportId)) break;
                await Task.Delay(500, stop.Token);
            }
            if (!InternetSettings.IsSupportId(supportId)) throw new IOException("The installed app did not register with its bundled relay settings.");
            await WaitPairingCodeAsync();
            CaptureDesktop("installed-internet-ready.png");
            Pass("installer.internet_ready", "First launch registers with the live relay and displays a code without manual configuration", new { supportId });
            await FinishAsync();
        }
        finally { await CleanupLoopbackProcessesAsync(); }
    }
}
