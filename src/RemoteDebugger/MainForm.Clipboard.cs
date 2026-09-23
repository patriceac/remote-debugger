using System.Text.Json;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

public sealed partial class MainForm
{
    private readonly Forms.CheckBox shareClipboard = new ViewerCheckBox
    { Name = "shareClipboard", Checked = true, AutoSize = true, ForeColor = PrimaryText, Anchor = Forms.AnchorStyles.Left }.WithText(() => UiText.ShareClipboard);
    private DesktopClipboard? localClipboard;
    private CancellationTokenSource? clipboardLifetime;
    private bool ClipboardConnected => supportSession && heartbeatHealthy && !clientUpdateBusy && !powerBusy && !terminating && !IsDisposed;

    private void RefreshClipboardSharing()
    {
        LoadViewerPreferences();
        if (!ClipboardConnected || !shareClipboard.Checked)
        {
            clipboardLifetime?.Cancel();
            localClipboard?.Pause();
            return;
        }
        if (clipboardLifetime == null && client is { } target)
        {
            var lifetime = new CancellationTokenSource(); clipboardLifetime = lifetime;
            _ = ShareClipboardAsync(target, lifetime);
        }
    }

    private async Task ShareClipboardAsync(RemoteClient target, CancellationTokenSource lifetime)
    {
        var clipboard = localClipboard ??= new DesktopClipboard(() => ClipboardConnected && clipboardLifetime is { IsCancellationRequested: false });
        // The native listener is retained, but every transport connection starts a new baseline.
        try
        {
            var data = RemoteClient.Require(await target.CallAsync("clipboard.begin", ct: lifetime.Token, seconds: 10));
            string remoteSession = data.Str("session"), localSession = await clipboard.BeginAsync(lifetime.Token);
            async Task SendAsync()
            {
                long version = 0;
                while (!lifetime.IsCancellationRequested)
                {
                    var change = await clipboard.ReadAsync(localSession, version, lifetime.Token);
                    if (change == null) continue;
                    RemoteClient.Require(await target.CallAsync("clipboard.write", new { session = remoteSession, version = change.Version, text = change.Text }, ct: lifetime.Token, seconds: 10));
                    version = change.Version;
                }
            }
            async Task ReceiveAsync()
            {
                long version = 0;
                while (!lifetime.IsCancellationRequested)
                {
                    var received = RemoteClient.Require(await target.CallAsync("clipboard.read", new { session = remoteSession, after = version }, ct: lifetime.Token, seconds: 30));
                    if (!received.TryGetProperty("change", out var element) || element.ValueKind == JsonValueKind.Null) continue;
                    var change = element.Deserialize<ClipboardChange>(Json.Options)!;
                    await clipboard.ApplyAsync(localSession, change, lifetime.Token);
                    version = change.Version;
                }
            }
            var send = SendAsync(); var receive = ReceiveAsync();
            await Task.WhenAny(send, receive);
            lifetime.Cancel();
            await Task.WhenAll(send, receive);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            RecordIncident("clipboard_interrupted", ex);
            // Capture is suspended on any transport failure, never queued for replay.
            clipboard.Pause();
            try { await Task.Delay(2000, lifetime.Token); } catch (OperationCanceledException) { }
        }
        finally
        {
            lifetime.Cancel(); clipboard.Pause();
            if (ReferenceEquals(clipboardLifetime, lifetime)) clipboardLifetime = null;
            lifetime.Dispose();
        }
    }
}
