using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using RemoteDebugger;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private async Task PerformanceReviewAsync()
    {
        if (scope != "runtime" || IsUpdateVariant)
            throw new ArgumentException("Loopback performance review only accepts the Runtime/None configuration.");

        var connection = JsonSerializer.Deserialize<Connection>(Vault.Read(RemoteClient.DefaultPath), Json.Options)
            ?? throw new InvalidDataException("The loopback controller did not save a connection.");
        var remote = new RemoteClient(connection, await HashFileAsync(application));
        await ProbePerformanceStreamAsync(remote);
        await ProbePerformanceInputAsync(remote);
        await ProbePerformanceTelemetryAsync(remote);
        await ProbePersistentWorkerAsync();
        await ProbeLoopbackMinimizeRestoreAsync();
    }

    private async Task ProbePerformanceStreamAsync(RemoteClient remote)
    {
        using var cover = new Forms.Form
        {
            FormBorderStyle = Forms.FormBorderStyle.None,
            ShowInTaskbar = false,
            TopMost = true,
            Bounds = Forms.Screen.PrimaryScreen!.Bounds,
            BackColor = System.Drawing.Color.DarkSlateBlue
        };
        cover.Show();
        cover.Refresh();
        await Task.Delay(250, stop.Token);

        using var streamStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        streamStop.CancelAfter(TimeSpan.FromSeconds(20));
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sequences = new List<long>();
        string codec = "negotiating";
        string? fallbackReason = null;
        string capture = "";
        string encoder = "";
        var stream = remote.StreamAdaptiveAsync(frame =>
        {
            lock (sequences)
            {
                sequences.Add(frame.Sequence);
                if (sequences.Count == 1) first.TrySetResult();
                else refreshed.TrySetResult();
            }
            frame.Dispose();
            return Task.CompletedTask;
        }, (value, reason) =>
        {
            codec = value;
            fallbackReason = reason;
        }, fps: 5, seconds: 20, ct: streamStop.Token);

        try
        {
            await first.Task.WaitAsync(streamStop.Token);
            capture = remote.StreamNegotiation.Str("capture");
            encoder = remote.StreamNegotiation.Str("encoder");
            await Task.Delay(1500, streamStop.Token);
            int beforeRefresh;
            lock (sequences) beforeRefresh = sequences.Count;
            RemoteClient.Require(await remote.CallAsync("screen.refresh", ct: streamStop.Token, seconds: 10));
            await refreshed.Task.WaitAsync(streamStop.Token);

            long[] observed;
            lock (sequences) observed = sequences.ToArray();
            bool duplicateSuppressed = beforeRefresh == 1;
            bool refreshProducedFrame = observed.Length >= 2;
            bool codecEvidence = codec.Equals("h264", StringComparison.OrdinalIgnoreCase)
                || (codec.Equals("jpeg", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(fallbackReason));
            var evidence = new
            {
                capture,
                encoder,
                codec,
                fallbackReason,
                framesBeforeRefresh = beforeRefresh,
                framesAfterRefresh = observed.Length,
                sequences = observed,
                hardwareAccelerationClaim = "not asserted in Hyper-V guest"
            };
            WritePerformanceEvidence("performance-stream.json", evidence);
            CaptureDesktop("performance-capture.png");
            if (duplicateSuppressed && refreshProducedFrame && codecEvidence)
                Pass("performance.capture_refresh", "The adaptive stream suppresses unchanged frames, responds to screen.refresh, and reports a valid software codec/fallback", evidence);
            else
                Fail("performance.capture_refresh", "The adaptive stream suppresses unchanged frames, responds to screen.refresh, and reports a valid software codec/fallback", evidence);
        }
        catch (Exception ex)
        {
            WritePerformanceEvidence("performance-stream.json", new { capture, encoder, codec, fallbackReason, error = ex.ToString() });
            Fail("performance.capture_refresh", "The adaptive stream suppresses unchanged frames, responds to screen.refresh, and reports a valid software codec/fallback", new { capture, encoder, codec, fallbackReason, error = ex.ToString() });
        }
        finally
        {
            streamStop.Cancel();
            try { await stream; }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }

    private async Task ProbePerformanceInputAsync(RemoteClient remote)
    {
        using var canary = new Forms.Form { Text = "Remote input canary", Width = 520, Height = 220, TopMost = true };
        using var editor = new Forms.TextBox { Multiline = true, Dock = Forms.DockStyle.Fill };
        canary.Controls.Add(editor);
        canary.Show();
        canary.Activate();
        editor.Focus();

        try
        {
            editor.Text = "alpha beta";
            editor.SelectionStart = editor.TextLength;
            editor.SelectionLength = 0;
            RemoteClient.Require(await remote.SendInputAsync(new
            {
                kind = "batch",
                events = new object[]
                {
                    new { kind = "keyDown", virtualKey = 0xA0, scanCode = 0x2A },
                    new { kind = "keyDown", virtualKey = 0x24, scanCode = 0x47, extended = true },
                    new { kind = "keyUp", virtualKey = 0x24, scanCode = 0x47, extended = true },
                    new { kind = "keyUp", virtualKey = 0xA0, scanCode = 0x2A }
                }
            }, stop.Token));
            await Task.Delay(200, stop.Token);
            string selected = editor.SelectedText;

            editor.Text = "";
            editor.SelectionStart = 0;
            await remote.SendInputAsync(new { kind = "keyDown", virtualKey = 0xA0, scanCode = 0x2A }, stop.Token);
            RemoteClient.Require(await remote.SendInputAsync(new { kind = "release" }, stop.Token));
            await remote.SendInputAsync(new { kind = "keyDown", virtualKey = 0x58 }, stop.Token);
            await remote.SendInputAsync(new { kind = "keyUp", virtualKey = 0x58 }, stop.Token);
            await Task.Delay(200, stop.Token);
            string afterRelease = editor.Text;
            var evidence = new
            {
                orderedEvents = new[] { "keyDown:Shift", "keyDown:Home(extended)", "keyUp:Home(extended)", "keyUp:Shift" },
                selected,
                afterRelease,
                releaseResult = "release followed by X must produce lowercase x"
            };
            WritePerformanceEvidence("performance-input.json", evidence);
            if (selected == "alpha beta" && afterRelease == "x")
                Pass("performance.input_order_release", "Ordered modifier/extended-key input reaches the remote desktop and release clears held modifiers", evidence);
            else
                Fail("performance.input_order_release", "Ordered modifier/extended-key input reaches the remote desktop and release clears held modifiers", evidence);
        }
        catch (Exception ex)
        {
            WritePerformanceEvidence("performance-input.json", new { error = ex.ToString() });
            Fail("performance.input_order_release", "Ordered modifier/extended-key input reaches the remote desktop and release clears held modifiers", new { error = ex.ToString() });
        }
        finally
        {
            try { RemoteClient.Require(await remote.SendInputAsync(new { kind = "release" }, stop.Token)); }
            catch (Exception) { }
            canary.Close();
        }
    }

    private async Task ProbePerformanceTelemetryAsync(RemoteClient remote)
    {
        try
        {
            var processes1 = RemoteClient.Require(await remote.CallAsync("processes", ct: stop.Token, seconds: 20));
            await Task.Delay(1250, stop.Token);
            var processes2 = RemoteClient.Require(await remote.CallAsync("processes", ct: stop.Token, seconds: 20));
            var system1 = RemoteClient.Require(await remote.CallAsync("system", ct: stop.Token, seconds: 20));
            await Task.Delay(1250, stop.Token);
            var system2 = RemoteClient.Require(await remote.CallAsync("system", ct: stop.Token, seconds: 20));
            bool processSample = ValidResourceSample(processes2);
            bool systemSample = ValidResourceSample(system2);
            int expectedPid = loopbackAgent?.Id ?? -1;
            bool stableProcess = ContainsProcess(processes1, expectedPid) && ContainsProcess(processes2, expectedPid);
            var evidence = new
            {
                processSample,
                systemSample,
                stableProcess,
                expectedPid,
                processes1,
                processes2,
                system1,
                system2
            };
            WritePerformanceEvidence("performance-telemetry.json", evidence);
            if (processSample && systemSample && stableProcess)
                Pass("performance.telemetry_repeat", "Repeated process/system samples expose positive intervals and preserve process identity", evidence);
            else
                Fail("performance.telemetry_repeat", "Repeated process/system samples expose positive intervals and preserve process identity", evidence);
        }
        catch (Exception ex)
        {
            WritePerformanceEvidence("performance-telemetry.json", new { error = ex.ToString() });
            Fail("performance.telemetry_repeat", "Repeated process/system samples expose positive intervals and preserve process identity", new { error = ex.ToString() });
        }
    }

    private async Task ProbePersistentWorkerAsync()
    {
        Process? worker = null;
        try
        {
            var start = new ProcessStartInfo(application)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(application)!
            };
            foreach (string argument in new[] { "cli", "connected", "--worker", "--connection", RemoteClient.DefaultPath })
                start.ArgumentList.Add(argument);
            worker = Process.Start(start) ?? throw new IOException("The signed connected CLI worker did not start.");
            int workerPid = worker.Id;
            Task<string> errors = worker.StandardError.ReadToEndAsync();
            string firstId = Guid.NewGuid().ToString();
            string secondId = Guid.NewGuid().ToString();
            await worker.StandardInput.WriteLineAsync(Json.Text(new { id = firstId, operation = "status", args = new { }, timeoutSeconds = 30 }));
            await worker.StandardInput.WriteLineAsync(Json.Text(new { id = secondId, operation = "status", args = new { }, timeoutSeconds = 30 }));
            await worker.StandardInput.FlushAsync();
            string? firstLine = await worker.StandardOutput.ReadLineAsync(stop.Token);
            string? secondLine = await worker.StandardOutput.ReadLineAsync(stop.Token);
            if (firstLine == null || secondLine == null) throw new IOException("The connected CLI worker did not return two replies.");
            var first = JsonSerializer.Deserialize<JsonElement>(firstLine);
            var second = JsonSerializer.Deserialize<JsonElement>(secondLine);
            bool correlated = first.Str("id") == firstId && second.Str("id") == secondId
                && first.TryGetProperty("ok", out var firstOk) && firstOk.ValueKind == JsonValueKind.True
                && second.TryGetProperty("ok", out var secondOk) && secondOk.ValueKind == JsonValueKind.True;
            bool stillLive = !worker.HasExited && worker.Id == workerPid;
            worker.StandardInput.Close();
            await worker.WaitForExitAsync(stop.Token);
            string stderr = await errors;
            var evidence = new { workerPid, correlated, stillLiveAfterReplies = stillLive, exitCode = worker.ExitCode, stderr };
            WritePerformanceEvidence("performance-mcp-worker.json", evidence);
            if (correlated && stillLive && worker.ExitCode == 0)
                Pass("performance.mcp_worker", "Two authenticated status requests share one live signed CLI worker and replies remain correlated", evidence);
            else
                Fail("performance.mcp_worker", "Two authenticated status requests share one live signed CLI worker and replies remain correlated", evidence);
        }
        catch (Exception ex)
        {
            WritePerformanceEvidence("performance-mcp-worker.json", new { error = ex.ToString() });
            Fail("performance.mcp_worker", "Two authenticated status requests share one live signed CLI worker and replies remain correlated", new { error = ex.ToString() });
        }
        finally
        {
            if (worker != null)
            {
                try { if (!worker.HasExited) worker.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                worker.Dispose();
            }
        }
    }

    private static bool ValidResourceSample(JsonElement value)
    {
        if (!value.TryGetProperty("sampleStartUtc", out var start) || !value.TryGetProperty("sampleEndUtc", out var end)
            || !DateTimeOffset.TryParse(start.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var startUtc)
            || !DateTimeOffset.TryParse(end.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var endUtc)) return false;
        return endUtc > startUtc && value.TryGetProperty("intervalMs", out var interval) && interval.ValueKind == JsonValueKind.Number && interval.GetDouble() > 0;
    }

    private static bool ContainsProcess(JsonElement value, int pid)
        => pid > 0 && value.TryGetProperty("processes", out var processes) && processes.ValueKind == JsonValueKind.Array
            && processes.EnumerateArray().Any(item => item.TryGetProperty("pid", out var found) && found.ValueKind == JsonValueKind.Number && found.GetInt32() == pid);

    private void WritePerformanceEvidence(string name, object evidence)
        => File.WriteAllText(Path.Combine(output, name), Json.Text(evidence));
}
