namespace RemoteDebugger.Core;

/// <summary>Binary payload with a bounded window and acknowledged file offsets.</summary>
public static class BulkTransfer
{
    public const int ChunkSize = 256 * 1024;
    public const int WindowSize = 8 * ChunkSize;

    public static async Task SendAsync(Stream source, Stream channel, long offset, long length, Action<long>? progress, CancellationToken ct)
    {
        Validate(offset, length);
        byte[] buffer = new byte[ChunkSize];
        while (offset < length)
        {
            long end = Math.Min(length, offset + WindowSize);
            while (offset < end)
            {
                int count = (int)Math.Min(buffer.Length, end - offset);
                await source.ReadExactlyAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                await channel.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                offset += count;
            }
            await channel.FlushAsync(ct).ConfigureAwait(false);
            if (await Wire.ReadAsync<long>(channel, ct).ConfigureAwait(false) != offset) throw new IOException("Unexpected transfer acknowledgement.");
            progress?.Invoke(offset);
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
