namespace RemoteDebugger.Core;

/// <summary>Exclusive leases allow concurrent commands without interleaving their replies.</summary>
public sealed class RpcConnectionPool(Func<CancellationToken, Task<Stream>> connect, int capacity = 4)
{
    private sealed record Idle(Stream Stream, object? Key, DateTimeOffset Returned);
    private readonly Queue<Idle> idle = new();
    private readonly object sync = new();
    private int generation;
    private Timer? idleTimeout;

    public async Task<Reply> CallAsync(Request request, CancellationToken ct, object? key = null)
    {
        Stream? stream = null;
        int leaseGeneration;
        while (true)
        {
            Idle? cached;
            lock (sync)
            {
                leaseGeneration = generation; idle.TryDequeue(out cached);
                if (idle.Count == 0) idleTimeout?.Change(Timeout.Infinite, Timeout.Infinite);
            }
            if (cached == null) break;
            if (Equals(cached.Key, key) && DateTimeOffset.UtcNow - cached.Returned < TimeSpan.FromSeconds(20))
            { stream = cached.Stream; break; }
            await cached.Stream.DisposeAsync().ConfigureAwait(false);
        }
        stream ??= await connect(ct).ConfigureAwait(false);
        bool reusable = false;
        try
        {
            await Wire.WriteAsync(stream, request with { KeepAlive = true }, ct).ConfigureAwait(false);
            var reply = await Wire.ReadAsync<Reply>(stream, ct).ConfigureAwait(false);
            if (reply.Id != request.Id) throw new InvalidDataException("Reply did not match its request.");
            reusable = reply.KeepAlive;
            return reply;
        }
        finally
        {
            if (reusable)
            {
                lock (sync)
                    if (leaseGeneration == generation && idle.Count < capacity)
                    {
                        idle.Enqueue(new(stream, key, DateTimeOffset.UtcNow)); stream = null;
                        idleTimeout ??= new Timer(state => { _ = ClearAsync(); });
                        idleTimeout.Change(TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan);
                    }
            }
            if (stream != null) await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task ClearAsync()
    {
        Idle[] cached;
        lock (sync) { generation++; cached = idle.ToArray(); idle.Clear(); idleTimeout?.Dispose(); idleTimeout = null; }
        foreach (var connection in cached) await connection.Stream.DisposeAsync().ConfigureAwait(false);
    }
}
