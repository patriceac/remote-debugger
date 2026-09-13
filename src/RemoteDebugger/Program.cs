using System.Text.Json;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--platform-service") return SupportService.Run();
        if (args.Length == 2 && args[0] == "--support-provision") return SupportInstaller.ExecuteElevated(args[1]);
        if (args.Length == 2 && args[0] == "--elevated-job") return ElevatedJob.ExecuteAsync(args[1]).GetAwaiter().GetResult();
        if (args.Length == 2 && args[0] == "--ui-job") { Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2); return UiAutomationJob.Execute(args[1]); }
        if (args.Length > 0 && args[0] == "cli") return CliAsync(args.Skip(1).ToArray()).GetAwaiter().GetResult();
        int languageIndex = Array.IndexOf(args, "--ui-language");
        UiCulture.Initialize(languageIndex >= 0 && languageIndex + 1 < args.Length ? args[languageIndex + 1] : null);
        WaitForProvisioningParent(args);
        string? startupPreparationError = null;
        if (!args.Contains("--loopback-only"))
        {
            try
            {
                // Resolve an already-provisioned portable launch before any
                // window displays a code belonging to the departing process.
                if (SupportPlatform.TryRelaunchManagedAgentAsync(args).GetAwaiter().GetResult()) return 0;
            }
            catch (Exception ex) { startupPreparationError = ex.Message; }
        }
        int rootIndex = Array.IndexOf(args, "--data-root");
        string? dataRoot = rootIndex >= 0 && rootIndex + 1 < args.Length ? args[rootIndex + 1] : null;
        bool loopbackOnly = args.Contains("--loopback-only");
        using var instance = SingleInstance.ForCurrentSession(loopbackOnly, dataRoot);
        if (!instance.TryAcquire())
        {
            if (instance.ActivateExistingAsync().GetAwaiter().GetResult()) return 0;
            // Recover if the owner exited or crashed while activation was attempted.
            if (!instance.TryAcquire()) return 1;
        }
        Native.FreeConsole(); ApplicationConfiguration.Initialize();
        bool controllerOnly = args.Contains("--controller");
        var form = new MainForm(!controllerOnly, dataRoot, loopbackOnly, startupPreparationError);
        form.Shown += (_, _) => instance.StartListening(form.ActivateExistingWindow);
        SupportPlatform.ManagedRelaunchRequested += () =>
        {
            if (!form.IsDisposed && form.IsHandleCreated) form.BeginInvoke(Forms.Application.Exit);
        };
        int transactionIndex = Array.IndexOf(args, "--update-transaction"), ticketIndex = Array.IndexOf(args, "--resume-update");
        if (transactionIndex >= 0 && transactionIndex + 1 < args.Length && ticketIndex >= 0 && ticketIndex + 1 < args.Length)
        {
            string transactionId = args[transactionIndex + 1], ticket = args[ticketIndex + 1];
            form.Shown += async (_, _) =>
            {
                Exception? last = null;
                DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(SupportOperationTimeouts.UpdateStartupHealthReportSeconds);
                while (!form.IsDisposed && DateTimeOffset.UtcNow < deadline)
                {
                    try
                    {
                    if (form.AgentNetworkReady && form.Agent is { IsListening: true, Paired: true } agent && agent.Session.HasPaired)
                        {
                            await SupportPlatform.ReportStartupHealthyAsync(transactionId, ticket);
                            return;
                        }
                    }
                    catch (Exception ex) { last = ex; }
                    await Task.Delay(500);
                }
                System.Diagnostics.Trace.WriteLine("Update startup health report failed before the listener became ready: " + last?.Message);
            };
        }
        Forms.Application.Run(form); return 0;
    }

    private static void WaitForProvisioningParent(string[] args)
    {
        int index = Array.IndexOf(args, "--wait-for-process-exit");
        if (index < 0 || index + 2 >= args.Length || !int.TryParse(args[index + 1], out int pid) || !long.TryParse(args[index + 2], out long startTicks)) return;
        try
        {
            using var parent = System.Diagnostics.Process.GetProcessById(pid);
            if (parent.StartTime.ToUniversalTime().Ticks != startTicks) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            parent.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (ArgumentException) { }
        catch (OperationCanceledException) { throw new TimeoutException("The provisioning source application did not exit; managed launch was cancelled to avoid a duplicate agent listener."); }
    }

    private static async Task<int> CliAsync(string[] args)
    {
        using var ct = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; ct.Cancel(); };
        string Option(string key, string fallback = "") { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        try
        {
            string verb = args.FirstOrDefault() ?? "help", config = Option("--connection", RemoteClient.DefaultPath);
            if (verb == "platform-status")
            {
                var status = await SupportPlatform.GetStatusAsync(ct.Token);
                Console.WriteLine(Json.Text(new { ok = true, status, receipt = SupportPlatformPaths.ProvisioningReceiptPath }));
                return 0;
            }
            if (verb == "platform-provision")
            {
                var status = await SupportPlatform.ProvisionAsync(ct.Token);
                Console.WriteLine(Json.Text(new { ok = status.Provisioned, status, receipt = SupportPlatformPaths.ProvisioningReceiptPath }));
                return status.Provisioned ? 0 : 1;
            }
            if (verb == "discover") { Console.WriteLine(Json.Text(new { ok = true, peers = await Discovery.FindAsync(2500, ct.Token) })); return 0; }
            if (verb == "pair")
            {
                string fingerprint = Option("--fingerprint"); if (fingerprint.Length != 0 && fingerprint.Replace(":", "").Length != 64) throw new ArgumentException("When supplied, --fingerprint must be a SHA-256 certificate fingerprint.");
                var client = new RemoteClient(new Connection(Option("--host"), int.Parse(Option("--port", "45832")), fingerprint, ""));
                string code = (await Console.In.ReadLineAsync(ct.Token) ?? "").Trim(); await client.PairAsync(code, ct.Token); client.Save(config);
                Console.WriteLine(Json.Text(new { ok = true, paired = true, connection = config })); return 0;
            }
            if (verb == "help")
            {
                Console.WriteLine("RemoteDebugger cli discover | pair --host IP [--fingerprint SHA256] (code on stdin) | sync | platform-status | platform-provision | call --request FILE | upload --file FILE --path RELATIVE | download --path REMOTE --file LOCAL | screenshot --file IMAGE | stream --seconds 10 --fps 5\nOptional: --connection FILE. Request JSON: {\"operation\":\"status\",\"args\":{},\"timeoutSeconds\":60,\"id\":\"UUID\"}. Exit 0=success, 1=operation failure, 2=transport/input failure. See docs/CLI.md."); return 0;
            }
            var remote = RemoteClient.Load(config);
            if (verb == "sync")
            {
                var result = await SupportPlatform.SynchronizeAgentAsync(remote, ct.Token);
                Console.WriteLine(Json.Text(new { ok = true, synchronization = result }));
                return 0;
            }
            if (verb == "screenshot")
            {
                var elapsed = System.Diagnostics.Stopwatch.StartNew(); var data = RemoteClient.Require(await remote.CallAsync("screenshot", new { monitor = int.Parse(Option("--monitor", "0")) }, ct.Token));
                var frame = data.Deserialize<ScreenFrame>(Json.Options)!; string file = Option("--file"); await File.WriteAllBytesAsync(file, Convert.FromBase64String(frame.Data), ct.Token);
                Console.WriteLine(Json.Text(new { ok = true, file, frame.CapturedUtc, frame.Geometry, frame.EncodedWidth, frame.EncodedHeight, frame.CaptureEncodeMs, frame.CopyMs, frame.JpegMs, roundTripMs = elapsed.Elapsed.TotalMilliseconds })); return 0;
            }
            if (verb == "stream")
            {
                int seconds = Math.Clamp(int.Parse(Option("--seconds", "10")), 1, 290), fps = StreamPolicy.ClampFps(int.Parse(Option("--fps", StreamPolicy.MaximumFps.ToString())));
                int presentationDelayMs = Math.Clamp(int.Parse(Option("--present-delay-ms", "0")), 0, 5000);
                var presentedSequences = new List<long>();
                int frames = 0; long bytes = 0; var capture = new List<double>(); var copies = new List<double>(); var jpegs = new List<double>(); var arrivals = new List<double>(); var hashes = new HashSet<string>(); ScreenFrame? last = null;
                var elapsed = System.Diagnostics.Stopwatch.StartNew(); using var duration = CancellationTokenSource.CreateLinkedTokenSource(ct.Token); duration.CancelAfter(TimeSpan.FromSeconds(seconds));
                using var self = System.Diagnostics.Process.GetCurrentProcess(); var cpu = self.TotalProcessorTime;
                try
                {
                    await remote.StreamAsync(async frame =>
                    {
                        last = frame; frames++; bytes += frame.Data.Length + 400; capture.Add(frame.CaptureEncodeMs); copies.Add(frame.CopyMs); jpegs.Add(frame.JpegMs); arrivals.Add(elapsed.Elapsed.TotalMilliseconds); hashes.Add(Safety.Hash(frame.Data)); presentedSequences.Add(frame.Sequence);
                        if (presentationDelayMs > 0) await Task.Delay(presentationDelayMs, duration.Token);
                    }, fps, int.Parse(Option("--monitor", "0")), seconds + 2, duration.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && frames > 0) { }
                if (last == null) throw new IOException("No stream frame received.");
                string framePath = Option("--last-frame"); if (framePath.Length > 0) await File.WriteAllBytesAsync(framePath, Convert.FromBase64String(last.Data), ct.Token);
                var gaps = arrivals.Zip(arrivals.Skip(1), (a, b) => b - a).OrderBy(x => x).ToArray();
                var report = new { ok = true, transport = "TLS framed JPEG, receipt ACK, newest pending frame only", requestedFps = fps, frames, presentationDelayMs, presentedSequences, framesSkipped = last.Sequence + 1 - frames, distinctFrames = hashes.Count, durationSeconds = elapsed.Elapsed.TotalSeconds, receivedFps = frames / elapsed.Elapsed.TotalSeconds, estimatedApplicationMbitPerSecond = bytes * 8 / elapsed.Elapsed.TotalSeconds / 1e6, meanCaptureEncodeMs = capture.Average(), meanCopyMs = copies.Average(), meanJpegMs = jpegs.Average(), interFrameP95Ms = gaps.Length == 0 ? 0 : gaps[(int)((gaps.Length - 1) * .95)], controllerCpuPercentTotalMachine = (self.TotalProcessorTime - cpu).TotalMilliseconds / elapsed.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100, last.Geometry, last.EncodedWidth, last.EncodedHeight, decodeAndDisplayMeasured = false };
                string reportPath = Option("--report"); if (reportPath.Length > 0) await File.WriteAllTextAsync(reportPath, Json.Text(report), ct.Token); Console.WriteLine(Json.Text(report)); return 0;
            }
            if (verb == "upload") { var result = await remote.UploadAsync(Option("--file"), Option("--path"), ct.Token); Console.WriteLine(Json.Text(new { ok = true, data = result })); return 0; }
            if (verb == "download") { await remote.DownloadAsync(Option("--path"), Option("--file"), ct.Token); Console.WriteLine(Json.Text(new { ok = true, file = Option("--file") })); return 0; }
            if (verb != "call") throw new ArgumentException("Unknown CLI verb.");
            string requestPath = Option("--request"); var request = JsonSerializer.Deserialize<JsonElement>(requestPath == "-" ? await Console.In.ReadToEndAsync(ct.Token) : await File.ReadAllTextAsync(requestPath, ct.Token));
            string op = request.Str("operation"), id = request.Str("id", Guid.NewGuid().ToString());
            if (op is "pair" or "screen.stream") throw new ArgumentException("Use the dedicated pair or stream CLI verb.");
            var pending = remote.CallAsync(op, request.TryGetProperty("args", out var a) ? a : Json.Element(new { }), ct.Token, id, request.Int("timeoutSeconds", 60)); Reply reply;
            try { reply = await pending; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { try { await remote.CallAsync("cancel", new { id }, seconds: 5); } catch (Exception) { } throw; }
            Console.WriteLine(Json.Text(reply)); return reply.Ok ? 0 : 1;
        }
        catch (Exception ex) { Console.WriteLine(Json.Text(new { ok = false, error = ex is OperationCanceledException ? "cancelled" : "transport_or_input", message = ex.Message })); return 2; }
    }
}
