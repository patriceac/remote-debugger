using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace RemoteDebugger;

/// <summary>One desktop workspace per Windows user/session, across executable paths.</summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly string pipeName;
    private readonly CancellationTokenSource lifetime = new();
    private Task? listener;
    private bool ownsMutex;

    private SingleInstance(string identity)
    {
        mutex = new Mutex(false, @"Local\" + identity);
        pipeName = identity + ".activate";
    }

    public static SingleInstance ForCurrentSession(bool loopbackOnly, string? dataRoot)
    {
        using var user = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        return new SingleInstance(IdentityFor(user.User!.Value, process.SessionId, loopbackOnly, dataRoot));
    }

    internal static string IdentityFor(string sid, int sessionId, bool loopbackOnly, string? dataRoot)
    {
        // The existing disconnected Lab hosts two PCs in one guest. Only its
        // loopback-only, explicitly separate data roots may have separate GUIs.
        string scope = loopbackOnly && !string.IsNullOrWhiteSpace(dataRoot)
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)).ToUpperInvariant()
            : "desktop";
        return "RemoteDebugger.Gui." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{sid}\n{sessionId}\n{scope}")));
    }

    public bool TryAcquire()
    {
        if (ownsMutex) return true;
        try { ownsMutex = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { ownsMutex = true; }
        return ownsMutex;
    }

    public async Task<bool> ActivateExistingAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            var pid = new byte[4];
            await pipe.ReadExactlyAsync(pid, deadline.Token).ConfigureAwait(false);
            // A foreground second launch grants activation to the existing GUI.
            // The authenticated local pipe identifies the actual owning process.
            AllowSetForegroundWindow(BitConverter.ToInt32(pid));
            await pipe.WriteAsync(new byte[] { 1 }, deadline.Token).ConfigureAwait(false);
            var acknowledgement = new byte[1];
            await pipe.ReadExactlyAsync(acknowledgement, deadline.Token).ConfigureAwait(false);
            return acknowledgement[0] == 1;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException) { return false; }
    }

    public void StartListening(Action activate)
    {
        if (!ownsMutex || listener != null) throw new InvalidOperationException("Only the primary desktop instance may listen once.");
        listener = ListenAsync(activate);
    }

    private async Task ListenAsync(Action activate)
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                await pipe.WriteAsync(BitConverter.GetBytes(Environment.ProcessId), deadline.Token).ConfigureAwait(false);
                var command = new byte[1];
                await pipe.ReadExactlyAsync(command, deadline.Token).ConfigureAwait(false);
                if (command[0] == 1) activate();
                await pipe.WriteAsync(command, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                if (lifetime.IsCancellationRequested) return;
                // A disconnected or stalled duplicate must not disable later activation.
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        lifetime.Cancel();
        listener?.GetAwaiter().GetResult();
        lifetime.Dispose();
        if (ownsMutex) mutex.ReleaseMutex();
        mutex.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
