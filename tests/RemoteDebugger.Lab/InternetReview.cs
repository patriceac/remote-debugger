using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Automation;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private Process LaunchInternetProduct(bool isAgent, string dataRoot, bool enableSupport = false, bool openSecurity = false)
    {
        var start = new ProcessStartInfo(application) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(application)! };
        if (isAgent && enableSupport) start.ArgumentList.Add("--enable-support");
        if (openSecurity) start.ArgumentList.Add("--security");
        if (!isAgent)
        {
            start.ArgumentList.Add("--controller");
            // Give the second GUI its own instance scope without starting an
            // agent listener. Outbound controller connections still use WSS.
            start.ArgumentList.Add("--loopback-only");
        }
        start.ArgumentList.Add("--data-root"); start.ArgumentList.Add(dataRoot);
        start.ArgumentList.Add("--ui-language"); start.ArgumentList.Add("fr");
        return Process.Start(start) ?? throw new IOException("Release did not launch.");
    }

    private async Task InternetReviewAsync()
    {
        string profile = Environment.GetCommandLineArgs().Last();
        string controllerRoot = Path.Combine(output, "internet-controller");
        try
        {
            await CliAsync(["internet-import", "--file", profile, "--data-root", productData]);
            await CliAsync(["internet-import", "--file", profile, "--data-root", controllerRoot]);
            Pass("internet.configuration", "Both Release instances import the private relay profile");
            loopbackAgent = LaunchInternetProduct(true, productData, enableSupport: true); product = loopbackAgent;
            await WaitUiAsync(); WindowState = Forms.FormWindowState.Minimized;
            ResizeProductWindow(1060, 720); Native.FocusWindow(product.Id);
            string supportId = "";
            var settings = InternetSettings.Load(controllerRoot)!;
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(75))
            {
                supportId = (await settings.FindAsync(stop.Token)).FirstOrDefault(peer => peer.Name == Environment.MachineName)?.Host ?? "";
                if (InternetSettings.IsSupportId(supportId)) break;
                await Task.Delay(500, stop.Token);
            }
            if (!InternetSettings.IsSupportId(supportId)) throw new IOException("Relay registration failed: " + Value(Find("internetState")!));
            if (Find("supportId", 100) != null || Find("copySupportId", 100) != null)
                throw new IOException("An internal support ID is visible in the normal agent UI.");
            Pass("internet.discovery", "The actual Release is privately discoverable by computer name", new { computer = Environment.MachineName, relay = "Cloudflare" });
            if (Find("agentPairCode", 100) != null) throw new IOException("The private client still displays an authorization code.");
            CaptureDesktop("internet-agent.png");
            var wrongSettings = settings with { PairingKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) };
            string controllerSettings = Path.Combine(controllerRoot, "internet.dpapi");
            Vault.Save(controllerSettings, JsonSerializer.SerializeToUtf8Bytes(wrongSettings, Json.Options));
            JsonElement rejected;
            try { rejected = await CliAsync(["pair", "--host", supportId, "--data-root", controllerRoot], requireSuccess: false); }
            finally { Vault.Save(controllerSettings, JsonSerializer.SerializeToUtf8Bytes(settings, Json.Options)); }
            if (rejected.GetProperty("ok").GetBoolean()) throw new IOException("A different installer secret was accepted.");
            Pass("internet.wrong_installer", "Relay access alone cannot authenticate: a different private installer secret is rejected");

            loopbackController = LaunchInternetProduct(false, controllerRoot); product = loopbackController;
            await WaitUiAsync(); ResizeProductWindow(1060, 720); Native.FocusWindow(product.Id);
            var discoveryDeadline = Stopwatch.StartNew();
            AutomationElement? computer = null;
            while (discoveryDeadline.Elapsed < TimeSpan.FromSeconds(30) && computer == null)
            {
                computer = Find("peers", 100)?.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                    .Cast<AutomationElement>().FirstOrDefault(item => item.Current.Name == Environment.MachineName);
                if (computer == null) await Task.Delay(500, stop.Token);
            }
            if (computer == null || Find("host", 100) != null) throw new IOException("The private controller did not replace the address field with computer discovery.");
            ((SelectionItemPattern)computer.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            if (Find("pairCode", 100) != null) throw new IOException("The private controller still asks for an authorization code.");
            Click("pair");
            if (!await WaitForTextAsync("connectionStatus", IsConnected, 90))
                throw new IOException("Internet GUI pairing failed: " + TryValue("connectionFormState"));
            Pass("internet.gui_pairing", "The shipped GUI connects by computer name with no ID or authorization code");
            var saved = RemoteClient.Load().Connection;
            if (saved.RelayUrl.Length == 0 || saved.Host != supportId || saved.Fingerprint.Length != 64)
                throw new IOException("The saved connection did not bind relay routing and endpoint identity.");
            Pass("internet.pinned_connection", "The saved connection uses the relay and authenticated endpoint certificate", new { saved.Host, saved.RelayUrl });
            var status = Data(await CallAsync("status")); workspace = status.Str("workspace");
            await WaitForBinaryMatchAsync(await HashFileAsync(application), 30);
            Pass("internet.exact_release", "The internet endpoints verify the exact same Release executable");
            var live = await WaitForLiveEvidenceAsync(60);
            if (!live.BadgeVisible || !live.TelemetryVisible) throw new IOException("No live desktop over the relay.");
            CaptureDesktop("internet-controller-live.png");
            var metrics = await CliAsync(["stream", "--seconds", "12", "--fps", "5", "--report", Path.Combine(output, "internet-stream.json"), "--last-frame", Path.Combine(output, "internet-last-frame.jpg")]);
            Pass("internet.streaming", "The Release CLI receives a sustained desktop stream through Cloudflare", metrics);

            byte[] source = RandomNumberGenerator.GetBytes(512 * 1024);
            string upload = Path.Combine(output, "internet-upload.bin"), download = Path.Combine(output, "internet-download.bin");
            await File.WriteAllBytesAsync(upload, source, stop.Token);
            await CliAsync(["upload", "--file", upload, "--path", "internet-proof.bin"]);
            await CliAsync(["download", "--path", Path.Combine(workspace, "internet-proof.bin"), "--file", download]);
            if (!source.SequenceEqual(await File.ReadAllBytesAsync(download, stop.Token))) throw new IOException("Internet file transfer changed bytes.");
            Pass("internet.file_transfer", "Chunked upload and download preserve 512 KiB byte-for-byte through the relay");
            string badPin = Path.Combine(output, "bad-pin.connection"), request = Path.Combine(output, "status-request.json");
            new RemoteClient(saved with { Fingerprint = new string('0', 64) }).Save(badPin);
            await File.WriteAllTextAsync(request, Json.Text(new { operation = "status", args = new { } }), stop.Token);
            var pinFailure = await CliAsync(["call", "--request", request, "--connection", badPin], requireSuccess: false);
            if (pinFailure.GetProperty("ok").GetBoolean()) throw new IOException("An invalid endpoint pin was accepted.");
            Pass("internet.bad_pin", "A changed endpoint certificate is rejected through the relay");
            await CallAsync("session.end");
            var denied = await CallAsync("status", requireSuccess: false);
            if (denied.GetProperty("ok").GetBoolean()) throw new IOException("A terminated session kept access.");
            Pass("internet.revocation", "Ending support revokes the old grant through the internet relay");
            product = loopbackAgent; Native.FocusWindow(product.Id); CaptureDesktop("internet-ended.png");
            await FinishAsync();
        }
        finally { await CleanupLoopbackProcessesAsync(); }
    }
}
