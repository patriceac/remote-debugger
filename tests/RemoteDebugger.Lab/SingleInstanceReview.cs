using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private async Task SingleInstanceReviewAsync()
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        var launched = new List<Process>();
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            product = loopbackController = Launch("first", "en");
            await WaitUiAsync(); Native.FocusWindow(product.Id);
            Set("host", "192.0.2.1");
            int primaryPid = product.Id;

            using var duplicate = Launch("different-data-root", "es");
            await WaitForProcessExitAsync(duplicate, TimeSpan.FromSeconds(10));
            CheckSingleInstance("visible", primaryPid, duplicate.ExitCode == 0);
            CaptureDesktop("single-instance-visible.png");

            var cli = await CliAsync(["platform-status"]);
            if (cli.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                Pass("singleinstance.cli", "The command-line helper still runs while the desktop workspace is open");
            else Fail("singleinstance.cli", "The command-line helper still runs while the desktop workspace is open", cli);

            product.CloseMainWindow(); await Task.Delay(600, stop.Token);
            WindowState = Forms.FormWindowState.Normal; Activate();
            var simultaneous = Enumerable.Range(0, 3).Select(index => Launch("concurrent-" + index, "fr")).ToArray();
            foreach (var process in simultaneous) await WaitForProcessExitAsync(process, TimeSpan.FromSeconds(10));
            await WaitUiAsync();
            CheckSingleInstance("restored_from_tray", primaryPid, simultaneous.All(process => process.ExitCode == 0));
            bool foreground = Native.NativeWindows().Any(window => window.Pid == primaryPid && window.Foreground);
            if (foreground) Pass("singleinstance.foreground", "A second launch restores the primary workspace to the foreground");
            else Fail("singleinstance.foreground", "A second launch restores the primary workspace to the foreground");
            CaptureDesktop("single-instance-restored.png");

            await QuitLocalizedProductAsync("singleinstance.quit");
            product = loopbackController = Launch("after-quit", "en");
            await WaitUiAsync();
            Pass("singleinstance.after_quit", "A new desktop instance starts after a normal Quit", new { oldPid = primaryPid, newPid = product.Id });
            product.Kill(entireProcessTree: true); await product.WaitForExitAsync(stop.Token);
            product = loopbackController = Launch("after-crash", "en");
            await WaitUiAsync();
            Pass("singleinstance.after_crash", "The next launch recovers ownership after an unexpected process exit", new { pid = product.Id });
            await QuitLocalizedProductAsync("singleinstance.final_quit");
            product = loopbackController = null;
            await FinishAsync();
        }
        finally
        {
            foreach (var process in launched)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                process.Dispose();
            }
            CultureInfo.CurrentUICulture = previousCulture;
        }

        Process Launch(string data, string language)
        {
            var start = new ProcessStartInfo(application) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(application)! };
            // Normal desktop scope: no loopback-only exemption, even though the
            // controller is offline and the guest network remains disconnected.
            foreach (string argument in new[] { "--controller", "--data-root", Path.Combine(output, data), "--ui-language", language })
                start.ArgumentList.Add(argument);
            var process = Process.Start(start) ?? throw new IOException("Desktop launch failed.");
            launched.Add(process);
            return process;
        }
    }

    private void CheckSingleInstance(string phase, int primaryPid, bool duplicatesExited)
    {
        var processes = Process.GetProcessesByName("RemoteDebugger");
        try
        {
            bool one = processes.Length == 1 && processes[0].Id == primaryPid;
            bool stateRetained = TryValue("host") == "192.0.2.1" && TryValue("headerTitle") == "Connection";
            var evidence = new { duplicatesExited, count = processes.Length, primaryPid, stateRetained };
            if (one && stateRetained && duplicatesExited) Pass("singleinstance." + phase, "Concurrent launches keep one desktop process and preserve its workspace and language", evidence);
            else Fail("singleinstance." + phase, "Concurrent launches keep one desktop process and preserve its workspace and language", evidence);
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
