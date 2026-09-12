using System.Threading.Channels;

namespace RemoteDebugger.Core;

/// <summary>Receive independently of rendering; retain only the newest pending frame.</summary>
public static class LatestFrameStream
{
    public static async Task RunAsync<T>(Func<Action<T>, CancellationToken, Task> receive,
        Func<T, Task> present, CancellationToken ct = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var frames = Channel.CreateBounded<T>(new BoundedChannelOptions(1)
        {
            SingleReader = true, SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false
        });
        // Network reads and acknowledgements must continue while the UI is busy.
        var receiver = Task.Run(async () =>
        {
            Exception? failure = null;
            try { await receive(frame => frames.Writer.TryWrite(frame), lifetime.Token).ConfigureAwait(false); }
            catch (Exception ex) { failure = ex; }
            finally { frames.Writer.TryComplete(failure); }
        });
        try
        {
            while (await frames.Reader.WaitToReadAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                // Resume on the presenter's context BEFORE taking the frame.
                // A blocked UI must not hold an old frame in a posted callback.
                if (frames.Reader.TryRead(out var frame)) await present(frame);
            }
        }
        finally
        {
            lifetime.Cancel();
            await receiver.ConfigureAwait(false);
        }
    }
}
