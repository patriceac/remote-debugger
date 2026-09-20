using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Automation;
using RemoteDebugger;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private sealed class TransferProbe(Action<FileTransferProgress> report) : IProgress<FileTransferProgress>
    {
        public void Report(FileTransferProgress value) => report(value);
    }

    private async Task ProbeAuditRegressionsAsync()
    {
        var connection = JsonSerializer.Deserialize<Connection>(Vault.Read(RemoteClient.DefaultPath), Json.Options)!;
        var remote = new RemoteClient(connection, await HashFileAsync(application));
        string source = Path.Combine(output, "transfer-32MiB.bin"), destination = Path.Combine(output, "transfer-downloaded.bin");
        string path = "diagnostics/audit-" + Guid.NewGuid().ToString("N") + ".bin";
        byte[] bytes = new byte[32 * 1024 * 1024]; RandomNumberGenerator.Fill(bytes);
        await File.WriteAllBytesAsync(source, bytes, stop.Token);
        string expected = Convert.ToHexString(SHA256.HashData(bytes));
        try
        {
            using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            {
                try
                {
                    await remote.UploadAsync(source, path, cancel.Token, new TransferProbe(value =>
                    { if (value.TransferredBytes >= BulkTransfer.WindowSize && value.TransferredBytes < value.TotalBytes) cancel.Cancel(); }));
                    throw new IOException("Upload cancellation was not observed.");
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
            }
            long uploadOffset = -1; var elapsed = Stopwatch.StartNew();
            var uploaded = await remote.UploadAsync(source, path, stop.Token, new TransferProbe(value => { if (uploadOffset < 0) uploadOffset = value.TransferredBytes; }));
            double uploadSeconds = elapsed.Elapsed.TotalSeconds;
            if (uploadOffset <= 0 || uploaded.Long("size") != bytes.Length || uploaded.Str("sha256") != expected) throw new IOException("Resumed upload did not preserve its offset and verified bytes.");
            using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            {
                try
                {
                    await remote.DownloadAsync(path, destination, cancel.Token, new TransferProbe(value =>
                    { if (value.TransferredBytes >= BulkTransfer.WindowSize && value.TransferredBytes < value.TotalBytes) cancel.Cancel(); }));
                    throw new IOException("Download cancellation was not observed.");
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
            }
            long downloadOffset = -1; elapsed.Restart();
            await remote.DownloadAsync(path, destination, stop.Token, new TransferProbe(value => { if (downloadOffset < 0) downloadOffset = value.TransferredBytes; }));
            double downloadSeconds = elapsed.Elapsed.TotalSeconds;
            if (downloadOffset <= 0 || new FileInfo(destination).Length != bytes.Length || await HashFileAsync(destination) != expected) throw new IOException("Resumed download did not preserve its offset and verified bytes.");
            Pass("audit.bulk_resume", "A 32 MiB binary upload and download resume after cancellation and verify exact bytes", new { uploadOffset, downloadOffset, uploadSeconds, downloadSeconds, route = remote.ActiveRoute });
        }
        catch (Exception ex) { Fail("audit.bulk_resume", "Binary transfers preserve offsets and bytes through cancellation", new { error = ex.ToString() }); }

        if (loopbackAgent == null) return;
        using (var cover = new Forms.Form { FormBorderStyle = Forms.FormBorderStyle.None, ShowInTaskbar = false, TopMost = true,
            Bounds = Forms.Screen.PrimaryScreen!.Bounds, BackColor = System.Drawing.Color.DarkSlateBlue })
        {
            cover.Show(); cover.Refresh();
            using var streamStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            streamStop.CancelAfter(TimeSpan.FromSeconds(15));
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int frames = 0;
            string codec = "negotiating";
            var stream = remote.StreamAdaptiveAsync(frame =>
            {
                using (frame)
                {
                    if (Interlocked.Increment(ref frames) == 1) first.TrySetResult();
                    else refreshed.TrySetResult();
                }
                return Task.CompletedTask;
            }, (value, reason) => codec = value + ": " + reason, ct: streamStop.Token);
            try
            {
                await Task.WhenAny(first.Task, stream).WaitAsync(streamStop.Token);
                if (stream.IsCompleted) await stream;
                await first.Task.WaitAsync(streamStop.Token);
                await Task.Delay(1500, streamStop.Token);
                int idleFrames = Volatile.Read(ref frames);
                RemoteClient.Require(await remote.CallAsync("screen.refresh", ct: streamStop.Token));
                await refreshed.Task.WaitAsync(streamStop.Token);
                if (idleFrames != 1) throw new IOException($"Static desktop produced {idleFrames} frames before the refresh request.");
                Pass("audit.static_refresh", "An unchanged adaptive stream suppresses duplicate images and responds to an explicit fresh-frame request", new { idleFrames, refreshedFrames = frames });
            }
            catch (Exception ex) { Fail("audit.static_refresh", "An unchanged adaptive stream recovers without a pixel change", new { frames, codec, streamState = stream.Status.ToString(), error = stream.Exception?.ToString() ?? ex.ToString() }); }
            finally
            {
                streamStop.Cancel();
                try { await stream; } catch (OperationCanceledException) { } catch (IOException) { }
            }
        }

        if (loopbackAgent != null && loopbackController != null)
            await ProbeLoopbackMinimizeRestoreAsync();
    }

    private async Task ProbeLoopbackMinimizeRestoreAsync()
    {
        if (loopbackController == null) return;
        product = loopbackController;
        WindowPattern? window = null;
        try
        {
            Native.FocusWindow(loopbackController.Id);
            var root = Root();
            if (!root.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern) || pattern is not WindowPattern state)
                throw new InvalidOperationException("The loopback controller did not expose WindowPattern.");
            window = state;
            var pauseButton = Element("pauseViewing");

            var before = await WaitForLiveEvidenceAsync(30);
            if (!before.BadgeVisible || !before.TelemetryVisible)
                throw new IOException("Live viewing was not ready before the minimize/restore check.");

            state.SetWindowVisualState(WindowVisualState.Minimized);
            await WaitForUiAsync(() => state.Current.WindowVisualState == WindowVisualState.Minimized, 10);
            await WaitForUiAsync(() => IsResumeViewingButton(pauseButton), 10);
            bool minimizedPaused = state.Current.WindowVisualState == WindowVisualState.Minimized && IsResumeViewingButton(pauseButton);

            state.SetWindowVisualState(WindowVisualState.Normal);
            await WaitForUiAsync(() => state.Current.WindowVisualState == WindowVisualState.Normal, 10);
            var restored = await WaitForLiveEvidenceAsync(30);
            var heartbeat = Data(await CallAsync("session.heartbeat"));
            bool sessionAfterRestore = heartbeat.ValueKind == JsonValueKind.Object;

            Click("pauseViewing");
            await WaitForUiAsync(() => IsResumeViewingButton(pauseButton), 10);
            state.SetWindowVisualState(WindowVisualState.Minimized);
            await WaitForUiAsync(() => state.Current.WindowVisualState == WindowVisualState.Minimized, 10);
            state.SetWindowVisualState(WindowVisualState.Normal);
            await WaitForUiAsync(() => state.Current.WindowVisualState == WindowVisualState.Normal, 10);
            await Task.Delay(500, stop.Token);
            string pauseButtonText = Value(pauseButton), pauseOverlay = TryValue("streamOverlay");
            bool pauseIntentPreserved = IsResumeViewingButton(pauseButton)
                && FindVisibleId(ContractId("liveBadge")) == null;
            var pausedHeartbeat = Data(await CallAsync("session.heartbeat"));
            bool sessionWhilePaused = pausedHeartbeat.ValueKind == JsonValueKind.Object;

            var evidence = new
            {
                minimizedPaused,
                restoredLive = restored.BadgeVisible && restored.TelemetryVisible,
                sessionAfterRestore,
                pauseIntentPreserved,
                sessionWhilePaused,
                pauseButton = pauseButtonText,
                pauseOverlay,
                restoredTelemetry = restored.TelemetryText
            };
            if (minimizedPaused && restored.BadgeVisible && restored.TelemetryVisible && sessionAfterRestore && pauseIntentPreserved && sessionWhilePaused)
                Pass("loopback.normal_minimize_restore", "Ordinary minimize pauses live viewing to save work, restores it for an active stream, and preserves an explicit pause without ending the session", evidence);
            else
                Fail("loopback.normal_minimize_restore", "Ordinary minimize pauses live viewing to save work, restores it for an active stream, and preserves an explicit pause without ending the session", evidence);
        }
        catch (Exception ex)
        {
            Fail("loopback.normal_minimize_restore", "Ordinary minimize pauses live viewing to save work, restores it for an active stream, and preserves an explicit pause without ending the session", new { error = ex.ToString() });
        }
        finally
        {
            try
            {
                if (window != null && window.Current.WindowVisualState == WindowVisualState.Minimized)
                    window.SetWindowVisualState(WindowVisualState.Normal);
            }
            catch (Exception) { }
            Native.FocusWindow(loopbackController.Id);
        }
    }

    private bool IsResumeViewingButton(AutomationElement? button = null)
    {
        string actual;
        try { actual = button == null ? TryValue("pauseViewing") : Value(button); }
        catch (ElementNotAvailableException) { return false; }
        return new[] { "en", "fr", "es" }
            .Select(language => UiText.Get(nameof(UiText.Resume), CultureInfo.GetCultureInfo(language)))
            .Any(expected => actual.Equals(expected, StringComparison.Ordinal));
    }
}
