using System.Text.Json;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

public sealed partial class MainForm
{
    private sealed record CopyBatch(RemoteClient Target, string[] Sources, string Directory);
    private readonly Queue<CopyBatch> copyQueue = new();
    private CopyBatch? pausedCopy;
    private bool copyPump, draggingFiles, preparingDrag;
    private TransferQueueForm? transferWindow;
    private Point? viewerDragOrigin;
    private Task<TransferEntry[]>? viewerDragCandidate;

    private bool CanDropFiles => supportSession && heartbeatHealthy && !terminating && !powerBusy && !clientUpdateBusy && client != null;

    private void InitializeFileDrop()
    {
        fileList.MultiSelect = true; fileList.AllowDrop = screen.AllowDrop = true;
        fileList.DragEnter += (_, e) => SetDropEffect(e, fileDirectoryLoaded);
        fileList.DragOver += (_, e) => SetDropEffect(e, fileDirectoryLoaded);
        screen.DragEnter += (_, e) => SetDropEffect(e, liveFrameFresh);
        screen.DragOver += (_, e) => SetDropEffect(e, liveFrameFresh);
        fileList.DragDrop += async (_, e) =>
        {
            if (!TryGetDroppedFiles(e, out var files) || !fileDirectoryLoaded) return;
            Point point = fileList.PointToClient(new(e.X, e.Y));
            string destination = fileList.HitTest(point).Item?.Tag is FileSortRow { IsDirectory: true } folder ? folder.Path : currentDirectory;
            await EnqueueCopyAsync(files, destination);
        };
        screen.DragDrop += async (_, e) =>
        {
            if (!TryGetDroppedFiles(e, out var files) || geometry == null || client is not { } target) return;
            Point local = screen.PointToClient(new(e.X, e.Y));
            var point = geometry.MapLetterbox(screen.Width, screen.Height, local.X, local.Y);
            if (point == null) return;
            try
            {
                var data = RemoteClient.Require(await target.CallAsync("shell.dropTarget", new { x = point.Value.X, y = point.Value.Y }, seconds: 15));
                if (ReferenceEquals(client, target) && CanDropFiles) await EnqueueCopyAsync(files, data.Str("directory"));
            }
            catch (Exception ex) { ShowTransferFailure(ex); }
        };
        fileList.ItemDrag += async (_, _) =>
        {
            if (!CanDropFiles || fileTransferLifetime != null || preparingDrag || draggingFiles || client is not { } target) return;
            string[] paths = fileList.SelectedItems.Cast<Forms.ListViewItem>().Select(item => ((FileSortRow)item.Tag!).Path).ToArray();
            preparingDrag = true;
            try { await DragRemoteFilesAsync(target, await RemoteManifestAsync(target, paths)); }
            catch (Exception ex) { ShowTransferFailure(ex); }
            finally { preparingDrag = false; }
        };
    }

    private void SetDropEffect(Forms.DragEventArgs args, bool surfaceReady) => args.Effect =
        CanDropFiles && surfaceReady && args.Data?.GetDataPresent(Forms.DataFormats.FileDrop) == true && (args.AllowedEffect & Forms.DragDropEffects.Copy) != 0
        ? Forms.DragDropEffects.Copy : Forms.DragDropEffects.None;
    private bool TryGetDroppedFiles(Forms.DragEventArgs args, out string[] files)
    {
        files = args.Data?.GetData(Forms.DataFormats.FileDrop) as string[] ?? [];
        return CanDropFiles && files.Length is > 0 and <= 256;
    }

    private void BeginViewerFileGesture(Forms.MouseEventArgs e, Task<bool> downApplied)
    {
        viewerDragOrigin = null; viewerDragCandidate = null;
        if (e.Button != Forms.MouseButtons.Left || !CanSendInput() || geometry == null || fileTransferLifetime != null || client is not { } target) return;
        var point = geometry.MapLetterbox(screen.Width, screen.Height, e.X, e.Y);
        if (point == null) return;
        viewerDragOrigin = e.Location;
        viewerDragCandidate = ReadViewerSelectionAsync(target, point.Value.X, point.Value.Y, downApplied);
    }
    private async Task<TransferEntry[]> ReadViewerSelectionAsync(RemoteClient target, int x, int y, Task<bool> downApplied)
    {
        try
        {
            if (!await downApplied.WaitAsync(TimeSpan.FromSeconds(5)) || !ReferenceEquals(client, target)) return [];
            var data = RemoteClient.Require(await target.CallAsync("shell.selection", new { x, y }, seconds: 10));
            var paths = data.Strings("paths");
            return paths.Length == 0 ? [] : await RemoteManifestAsync(target, paths);
        }
        catch { return []; } // Ordinary drags and non-Explorer surfaces remain normal remote input.
    }
    private async Task<bool> ContinueViewerFileGestureAsync(Forms.MouseEventArgs e)
    {
        if (draggingFiles || preparingDrag) return true;
        if (viewerDragOrigin == null || viewerDragCandidate == null || e.Button != Forms.MouseButtons.Left ||
            screen.ClientRectangle.Contains(e.Location) || client is not { } target) return false;
        var candidate = viewerDragCandidate; viewerDragOrigin = null; viewerDragCandidate = null;
        preparingDrag = true;
        try
        {
            var entries = await candidate;
            if (entries.Length == 0 || !CanDropFiles) return false;
            // Cancel Explorer's in-progress remote drag before releasing its mouse
            // button. Releasing first could move the source on the remote desktop.
            var cancelledDrag = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            inputQueue.Reset(new QueuedInput(target, sessionGeneration, new { kind = "keyDown", virtualKey = 0x1B }, cancelledDrag));
            if (!await cancelledDrag.Task.WaitAsync(TimeSpan.FromSeconds(5))) throw new IOException("Remote drag cancellation was not acknowledged.");
            RemoteClient.Require(await target.SendInputAsync(new { kind = "keyUp", virtualKey = 0x1B }, seconds: 5));
            await ReleaseHeldInputAsync(target);
            await DragRemoteFilesAsync(target, entries);
            return true;
        }
        catch (Exception ex) { ShowTransferFailure(ex); return true; }
        finally { preparingDrag = false; }
    }

    private static async Task<TransferEntry[]> RemoteManifestAsync(RemoteClient target, string[] paths)
    {
        var entries = RemoteClient.Require(await target.CallAsync("files.manifest", new { paths }, seconds: 30)).Deserialize<TransferEntry[]>(Json.Options) ?? [];
        if (entries.Length is < 1 or > TransferTree.MaximumEntries) throw new IOException("Invalid remote file manifest.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            TransferTree.ValidateRelativePath(entry.RelativePath);
            if (entry.Size < 0 || !Path.IsPathFullyQualified(entry.SourcePath) || !names.Add(entry.RelativePath)) throw new IOException("Invalid remote file manifest.");
        }
        return entries;
    }

    private async Task EnqueueCopyAsync(string[] sources, string directory)
    {
        if (!CanDropFiles || client is not { } target) return;
        if (fileTransferLifetime != null && !copyPump) { fileState.SetText(() => UiText.TransferAlreadyRunning); return; }
        copyQueue.Enqueue(new(target, sources, directory));
        EnsureTransferWindow();
        transferWindow!.SetState(UiText.Format(UiText.TransferQueueWaiting, copyQueue.Count), indeterminate: true);
        if (!copyPump && pausedCopy == null) await PumpCopyQueueAsync();
    }
    private async Task PumpCopyQueueAsync()
    {
        if (copyPump || pausedCopy != null) return;
        copyPump = true;
        int generation = sessionGeneration;
        try
        {
            while (generation == sessionGeneration && copyQueue.TryDequeue(out var batch))
            {
                if (!ReferenceEquals(client, batch.Target) || !supportSession) { copyQueue.Clear(); break; }
                using var lifetime = new CancellationTokenSource(); fileTransferLifetime = lifetime;
                try
                {
                    RefreshControllerControls(); EnsureTransferWindow();
                    transferWindow!.SetState(UiText.PreparingFileTransfer, indeterminate: true);
                    var entries = await Task.Run(() => TransferTree.Read(batch.Sources, lifetime.Token), lifetime.Token);
                    var conflicts = RemoteClient.Require(await batch.Target.CallAsync("files.conflicts", new { directory = batch.Directory, entries }, lifetime.Token, seconds: 30))
                        .Deserialize<string[]>(Json.Options) ?? [];
                    if (conflicts.Length > 0 && Forms.MessageBox.Show(this, UiText.Format(UiText.ConfirmReplaceFiles, conflicts.Length, batch.Directory) + "\n\n" +
                        string.Join("\n", conflicts.Take(8)), UiText.FileTransfers, Forms.MessageBoxButtons.OKCancel,
                        Forms.MessageBoxIcon.Warning, Forms.MessageBoxDefaultButton.Button2) != Forms.DialogResult.OK)
                        throw new OperationCanceledException(lifetime.Token);
                    var approved = conflicts.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    _ = RemoteClient.Require(await batch.Target.CallAsync("files.createDirectories", new { directory = batch.Directory, entries }, lifetime.Token, seconds: 30));
                    transferWindow.SetItems(entries.Where(entry => !entry.Directory).Select(entry => entry.RelativePath).ToArray());
                    long total = entries.Sum(entry => entry.Size), done = 0;
                    int index = 0;
                    using var display = new TransferProgressDisplay(this, lifetime);
                    foreach (var entry in entries.Where(entry => !entry.Directory))
                    {
                        display.BeginFile();
                        int row = index; long baseBytes = done;
                        transferWindow.SetItem(row, UiText.Running);
                        bool reporting = true;
                        var progress = new Progress<FileTransferProgress>(value => { if (reporting) display.Report(baseBytes + value.TransferredBytes, total, entry.RelativePath); });
                        try { await batch.Target.UploadAsync(entry.SourcePath, Path.Combine(batch.Directory, entry.RelativePath), lifetime.Token, progress,
                            overwrite: approved.Contains(entry.RelativePath)); }
                        finally { reporting = false; }
                        done += entry.Size; index++; transferWindow.SetItem(row, UiText.UploadVerified);
                        display.Report(done, total, entry.RelativePath);
                    }
                    display.Dispose();
                    transferWindow.SetState(UiText.UploadVerified, complete: true);
                    fileTransferDetails.SetText(() => UiText.UploadVerified);
                    diagnostics.Record("file_copy_verified", DiagnosticContext());
                }
                catch (Exception ex)
                {
                    if (generation != sessionGeneration || !ReferenceEquals(client, batch.Target)) break;
                    pausedCopy = batch;
                    transferWindow?.SetState(ex is OperationCanceledException ? UiText.TransferPaused : ex.Message);
                    transferWindow?.EnableResume(true);
                    if (ex is not OperationCanceledException) RecordIncident("file_transfer_failed", ex);
                    break;
                }
                finally { if (ReferenceEquals(fileTransferLifetime, lifetime)) fileTransferLifetime = null; RefreshControllerControls(); }
            }
        }
        finally { copyPump = false; }
        if (CanDropFiles && pausedCopy == null)
        { if (copyQueue.Count > 0) await PumpCopyQueueAsync(); else await BrowseFilesAsync(); }
    }

    private void EnsureTransferWindow()
    {
        if (transferWindow is { IsDisposed: false }) { if (!transferWindow.Visible) transferWindow.Show(this); return; }
        transferWindow = new TransferQueueForm(() => fileTransferLifetime?.Cancel(), async () =>
        {
            if (copyPump || pausedCopy is not { } batch) return;
            pausedCopy = null;
            var rest = copyQueue.ToArray(); copyQueue.Clear(); copyQueue.Enqueue(batch); foreach (var item in rest) copyQueue.Enqueue(item);
            transferWindow!.EnableResume(false); await PumpCopyQueueAsync();
        });
        transferWindow.Show(this);
    }
    private void RenderTransferProgress(TransferRateTracker rate, long bytes, long total, string name)
    {
        var metrics = rate.Snapshot();
        string details = bytes >= total ? UiText.VerifyingFileTransfer : UiText.Format(UiText.FileTransferNumbers,
            metrics.Percent, FormatBytes(bytes), FormatBytes(total), metrics.BytesPerSecond > 0 ? FormatBytes((long)metrics.BytesPerSecond) + "/s" : "—",
            metrics.Remaining is { } eta ? FormatTransferEta(eta) : UiText.CalculatingTransferEta);
        fileTransferStatus.SetText(name); fileTransferDetails.SetText(details);
        fileTransferProgress.Style = Forms.ProgressBarStyle.Continuous; fileTransferProgress.Value = metrics.Percent;
        transferWindow?.SetProgress(metrics.Percent, details);
    }

    private sealed class TransferProgressDisplay : IDisposable
    {
        private readonly MainForm owner;
        private readonly CancellationTokenSource lifetime;
        private readonly int generation;
        private readonly Forms.Timer timer = new() { Interval = 500 };
        private TransferRateTracker rate = new();
        private long bytes, total;
        private string? name;
        private bool disposed;
        internal TransferProgressDisplay(MainForm owner, CancellationTokenSource lifetime)
        {
            this.owner = owner; this.lifetime = lifetime; generation = owner.sessionGeneration;
            timer.Tick += (_, _) => Render(); timer.Start();
        }
        internal void BeginFile() { rate = new(); name = null; }
        internal void Report(long bytes, long total, string name)
        { this.bytes = bytes; this.total = total; this.name = name; rate.Report(bytes, total); Render(); }
        private void Render()
        {
            if (name != null && !lifetime.IsCancellationRequested && generation == owner.sessionGeneration && ReferenceEquals(owner.fileTransferLifetime, lifetime))
                owner.RenderTransferProgress(rate, bytes, total, name);
        }
        public void Dispose() { if (disposed) return; disposed = true; timer.Stop(); timer.Dispose(); }
    }

    private async Task DragRemoteFilesAsync(RemoteClient target, TransferEntry[] entries)
    {
        if (entries.Length == 0 || !ReferenceEquals(client, target) || !CanDropFiles || fileTransferLifetime != null) return;
        if ((Forms.Control.MouseButtons & Forms.MouseButtons.Left) == 0) { fileState.SetText(() => UiText.DragAgainToResume); return; }
        using var lifetime = new CancellationTokenSource(); fileTransferLifetime = lifetime; draggingFiles = true;
        EnsureTransferWindow(); transferWindow!.EnableResume(false);
        transferWindow.SetItems(entries.Where(entry => !entry.Directory).Select(entry => entry.RelativePath).ToArray());
        transferWindow.SetState(UiText.DropInExplorer, indeterminate: true); RefreshControllerControls();
        string cacheRoot = Path.Combine(root, "drag-cache"); Directory.CreateDirectory(cacheRoot);
        var tasks = new Dictionary<int, Task<string>>(); long done = 0, total = entries.Sum(entry => entry.Size);
        using var display = new TransferProgressDisplay(this, lifetime);
        using var fetchGate = new SemaphoreSlim(1, 1);
        async Task<string> FetchAsync(int index)
        {
            await fetchGate.WaitAsync(lifetime.Token);
            try
            {
            display.BeginFile();
            var entry = entries[index];
            int row = entries.Take(index).Count(item => !item.Directory);
            string file = Safety.UnderRoot(cacheRoot, Safety.Hash(target.Connection.Fingerprint + "|" + entry.SourcePath) + ".bin");
            transferWindow.SetItem(row, UiText.Running);
            long baseBytes = done;
            bool reporting = true;
            var progress = new Progress<FileTransferProgress>(value => { if (reporting) display.Report(baseBytes + value.TransferredBytes, total, entry.RelativePath); });
            try { await target.DownloadAsync(entry.SourcePath, file, lifetime.Token, progress); }
            finally { reporting = false; }
            if (new FileInfo(file).Length != entry.Size) throw new IOException("The source file changed during the drag; select it again.");
            done += entry.Size; display.Report(done, total, entry.RelativePath);
            transferWindow.SetItem(row, UiText.DownloadVerified); return file;
            }
            finally { fetchGate.Release(); }
        }
        Task<string> Materialize(int index)
        {
            if (InvokeRequired)
            {
                var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                BeginInvoke(async () => { try { completion.TrySetResult(await Materialize(index)); } catch (Exception ex) { completion.TrySetException(ex); } });
                return completion.Task;
            }
            if (!tasks.TryGetValue(index, out var task)) tasks[index] = task = FetchAsync(index);
            return task;
        }
        try
        {
            var data = new VirtualFileDrop(entries, Materialize, lifetime.Token);
            bool copied = await data.DragAsync();
            display.Dispose();
            int skipped = 0, row = 0;
            for (int index = 0; copied && index < entries.Length; index++)
            {
                if (entries[index].Directory) continue;
                if (!data.RequestedFiles.Contains(index)) { transferWindow.SetItem(row, UiText.TransferSkipped); skipped++; }
                row++;
            }
            string outcome = copied ? skipped == 0 ? UiText.DownloadVerified : UiText.Format(UiText.TransferSkippedFiles, skipped) : UiText.DragAgainToResume;
            transferWindow.SetState(outcome, complete: copied && skipped == 0); fileTransferDetails.SetText(outcome);
            if (copied)
            {
                foreach (var task in tasks.Values.Where(task => task.IsCompletedSuccessfully))
                    try { File.Delete(task.Result); } catch (IOException) { }
                diagnostics.Record(skipped == 0 ? "file_copy_verified" : "file_copy_finished_with_skips", DiagnosticContext());
            }
        }
        catch (Exception ex) { string outcome = ex is OperationCanceledException ? UiText.DragAgainToResume : ex.Message; transferWindow.SetState(outcome); fileTransferDetails.SetText(outcome); if (ex is not OperationCanceledException) RecordIncident("file_transfer_failed", ex); }
        finally
        {
            lifetime.Cancel();
            try { await Task.WhenAll(tasks.Values); } catch { }
            if (ReferenceEquals(fileTransferLifetime, lifetime)) fileTransferLifetime = null;
            draggingFiles = false; RefreshControllerControls();
        }
    }
    private void ShowTransferFailure(Exception ex)
    { fileState.SetText(ex.Message); EnsureTransferWindow(); transferWindow!.SetState(ex.Message); RecordIncident("file_transfer_failed", ex); }
    private void ClearFileCopyQueue()
    {
        copyQueue.Clear(); pausedCopy = null; viewerDragOrigin = null; viewerDragCandidate = null;
        transferWindow?.EnableResume(false);
    }
}

internal sealed class TransferQueueForm : Forms.Form
{
    protected override bool ShowWithoutActivation => true;
    private readonly Forms.ListView items = new() { Name = "transferQueue", Dock = Forms.DockStyle.Fill, View = Forms.View.Details, FullRowSelect = true };
    private readonly Forms.Label state = new() { Name = "transferQueueState", Dock = Forms.DockStyle.Bottom, Height = 60, Padding = new(0, 8, 0, 0) };
    private readonly Forms.ProgressBar progress = new() { Name = "transferQueueProgress", Dock = Forms.DockStyle.Bottom, Height = 12 };
    private readonly Forms.Button resume = new() { Name = "resumeTransfers", Text = UiText.ResumeTransfers, AutoSize = true, Enabled = false };
    internal TransferQueueForm(Action cancel, Func<Task> retry)
    {
        Text = UiText.FileTransfers; Name = "transferQueueWindow"; StartPosition = Forms.FormStartPosition.CenterParent;
        ClientSize = new(620, 340); MinimumSize = new(510, 300); Padding = new(16); Font = new("Segoe UI", 10);
        items.Columns.Add(UiText.Name, 365); items.Columns.Add(UiText.State, 180);
        var buttons = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Bottom, Height = 40 };
        var stop = new Forms.Button { Name = "pauseTransfers", Text = UiText.Cancel, AutoSize = true };
        stop.Click += (_, _) => cancel(); resume.Click += async (_, _) => await retry();
        buttons.Controls.Add(stop); buttons.Controls.Add(resume);
        Controls.Add(items); Controls.Add(progress); Controls.Add(state); Controls.Add(buttons);
        FormClosing += (_, e) => { cancel(); if (e.CloseReason == Forms.CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }
    internal void SetItems(string[] names) { progress.Value = 0; items.Items.Clear(); foreach (string name in names) { var row = items.Items.Add(name); row.SubItems.Add(UiText.Waiting); } }
    internal void SetItem(int index, string value) { if (index >= 0 && index < items.Items.Count) items.Items[index].SubItems[1].Text = value; }
    internal void SetState(string value, bool indeterminate = false, bool complete = false)
    { state.Text = value; progress.Style = indeterminate ? Forms.ProgressBarStyle.Marquee : Forms.ProgressBarStyle.Continuous; if (complete) progress.Value = 100; }
    internal void SetProgress(int percent, string text) { progress.Style = Forms.ProgressBarStyle.Continuous; progress.Value = Math.Clamp(percent, 0, 100); state.Text = text; }
    internal void EnableResume(bool enabled) => resume.Enabled = enabled;
}
