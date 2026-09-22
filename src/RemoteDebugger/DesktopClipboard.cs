using System.ComponentModel;
using System.Runtime.InteropServices;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

/// <summary>Event-driven STA clipboard access; no startup reads, history or persisted payloads.</summary>
internal sealed class DesktopClipboard(Func<bool> authorized) : IDisposable
{
    private readonly object gate = new();
    private readonly SessionClipboard state = new();
    private Task<ClipboardWindow>? window;
    private TaskCompletionSource pulse = NewPulse();
    private string session = "";
    private long appliedVersion;
    private bool disposed, applying;
    private static TaskCompletionSource NewPulse() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<string> BeginAsync(CancellationToken ct)
    {
        return await InvokeAsync(() =>
        {
            lock (gate)
            {
                if (!authorized()) throw new InvalidOperationException("Clipboard sharing requires a connected support session.");
                state.Begin(GetClipboardSequenceNumber()); appliedVersion = 0;
                session = Guid.NewGuid().ToString("N"); Signal(); return session;
            }
        }, ct);
    }

    public void Pause()
    {
        lock (gate) { state.Pause(); session = ""; Signal(); }
    }

    public async Task<ClipboardChange?> ReadAsync(string id, long after, CancellationToken ct)
    {
        Task changed;
        lock (gate)
        {
            RequireSession(id);
            if (state.Latest is { } latest && latest.Version > after) return latest;
            changed = pulse.Task;
        }
        try { await changed.WaitAsync(TimeSpan.FromSeconds(20), ct); }
        catch (TimeoutException) { }
        lock (gate)
        {
            RequireSession(id);
            return state.Latest is { } latest && latest.Version > after ? latest : null;
        }
    }

    public async Task ApplyAsync(string id, ClipboardChange change, CancellationToken ct)
    {
        if (change.Text.Length > SessionClipboard.MaximumTextLength) throw new ArgumentException("Clipboard text exceeds 1 MiB.");
        await InvokeAsync(() =>
        {
            lock (gate)
            {
                RequireSession(id);
                if (change.Version <= appliedVersion) return false;
                applying = true;
                try
                {
                    if (change.Text.Length == 0) Forms.Clipboard.Clear();
                    else Forms.Clipboard.SetText(change.Text, Forms.TextDataFormat.UnicodeText);
                    state.Suppress(GetClipboardSequenceNumber());
                    appliedVersion = change.Version;
                }
                finally { applying = false; }
                return true;
            }
        }, ct);
    }

    private void Changed()
    {
        lock (gate)
        {
            if (disposed || applying || !state.Connected) return;
            if (!authorized()) { Pause(); return; }
            uint sequence = GetClipboardSequenceNumber();
            if (!state.HasNewSequence(sequence)) return;
            try
            {
                // File drag/drop is independent of text clipboard synchronization.
                if (!Forms.Clipboard.ContainsText(Forms.TextDataFormat.UnicodeText)) { state.Suppress(sequence); return; }
                string text = Forms.Clipboard.GetText(Forms.TextDataFormat.UnicodeText);
                uint readSequence = GetClipboardSequenceNumber();
                if (readSequence != sequence) return;
                if (state.Capture(sequence, text) != null) Signal();
            }
            catch (ExternalException) { /* Busy clipboard: a later change is still independently eligible. */ }
        }
    }

    private void RequireSession(string id)
    {
        if (!authorized()) Pause();
        if (disposed || session.Length == 0 || id != session || !state.Connected)
            throw new InvalidOperationException("Clipboard connection ended; establish a fresh baseline.");
    }
    private void Signal() { var previous = pulse; pulse = NewPulse(); previous.TrySetResult(); }

    private async Task<T> InvokeAsync<T>(Func<T> action, CancellationToken ct)
    {
        Task<ClipboardWindow> ready;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ready = window ??= StartWindow();
        }
        var control = await ready.WaitAsync(ct);
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        control.BeginInvoke(() =>
        {
            try { ct.ThrowIfCancellationRequested(); result.TrySetResult(action()); }
            catch (Exception ex) { result.TrySetException(ex); }
        });
        return await result.Task.WaitAsync(ct);
    }

    private Task<ClipboardWindow> StartWindow()
    {
        var ready = new TaskCompletionSource<ClipboardWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var control = new ClipboardWindow(Changed);
                _ = control.Handle;
                ready.TrySetResult(control);
                Forms.Application.Run();
            }
            catch (Exception ex) { ready.TrySetException(ex); }
        }) { IsBackground = true, Name = "Session clipboard" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return ready.Task;
    }

    public void Dispose()
    {
        Task<ClipboardWindow>? ready;
        lock (gate) { if (disposed) return; disposed = true; Pause(); ready = window; }
        if (ready != null) _ = CloseAsync(ready);
    }
    private static async Task CloseAsync(Task<ClipboardWindow> ready)
    {
        try { var control = await ready; control.BeginInvoke(Forms.Application.ExitThread); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
    }

    private sealed class ClipboardWindow(Action changed) : Forms.Control
    {
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!AddClipboardFormatListener(Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        protected override void OnHandleDestroyed(EventArgs e) { RemoveClipboardFormatListener(Handle); base.OnHandleDestroyed(e); }
        protected override void WndProc(ref Forms.Message m)
        {
            if (m.Msg == 0x031D) { changed(); return; }
            base.WndProc(ref m);
        }
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
}
