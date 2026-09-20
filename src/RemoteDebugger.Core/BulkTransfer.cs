using System.Threading.Channels;

namespace RemoteDebugger.Core;

/// <summary>Binary payload with a bounded window and acknowledged file offsets.</summary>
public static class BulkTransfer
{
    public const int ChunkSize = 256 * 1024;
    public const int WindowSize = 8 * ChunkSize;
    public const int WindowsInFlight = 8;

    public static async Task SendAsync(Stream source, Stream channel, long offset, long length, Action<long>? progress, CancellationToken ct)
    {
        Validate(offset, length);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var available = new SemaphoreSlim(WindowsInFlight, WindowsInFlight);
        var pending = Channel.CreateUnbounded<long>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        byte[] buffer = new byte[ChunkSize];
        async Task AcknowledgeAsync()
        {
            try
            {
                await foreach (long expected in pending.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
                {
                    if (await Wire.ReadAsync<long>(channel, lifetime.Token).ConfigureAwait(false) != expected) throw new IOException("Unexpected transfer acknowledgement.");
                    progress?.Invoke(expected);
                    available.Release();
                }
            }
            catch { lifetime.Cancel(); throw; }
        }
        Task acknowledgements = AcknowledgeAsync();
        try
        {
            while (offset < length)
            {
                await available.WaitAsync(lifetime.Token).ConfigureAwait(false);
                long end = Math.Min(length, offset + WindowSize);
                while (offset < end)
                {
                    int count = (int)Math.Min(buffer.Length, end - offset);
                    await source.ReadExactlyAsync(buffer.AsMemory(0, count), lifetime.Token).ConfigureAwait(false);
                    await channel.WriteAsync(buffer.AsMemory(0, count), lifetime.Token).ConfigureAwait(false);
                    offset += count;
                }
                await channel.FlushAsync(lifetime.Token).ConfigureAwait(false);
                pending.Writer.TryWrite(offset);
            }
            pending.Writer.TryComplete();
            await acknowledgements.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && lifetime.IsCancellationRequested)
        { await acknowledgements.ConfigureAwait(false); throw; }
        finally
        {
            lifetime.Cancel(); pending.Writer.TryComplete();
            try { await acknowledgements.ConfigureAwait(false); } catch { }
        }
    }

    public static async Task ReceiveAsync(Stream channel, Stream destination, long offset, long length, Action<long>? progress, CancellationToken ct)
    {
        Validate(offset, length);
        byte[] buffer = new byte[ChunkSize];
        while (offset < length)
        {
            long end = Math.Min(length, offset + WindowSize);
            while (offset < end)
            {
                int count = (int)Math.Min(buffer.Length, end - offset);
                await channel.ReadExactlyAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                await destination.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                offset += count;
            }
            await destination.FlushAsync(ct).ConfigureAwait(false);
            await Wire.WriteAsync(channel, offset, ct).ConfigureAwait(false);
            progress?.Invoke(offset);
        }
    }

    private static void Validate(long offset, long length)
    {
        if (offset < 0 || offset > length || length > 16L * 1024 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(offset));
    }
}
