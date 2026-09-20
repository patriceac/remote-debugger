using System.Text.Json;

namespace RemoteDebugger.Core;

/// <summary>Allow one newer frame while the previous frame is being decoded.</summary>
public sealed class StreamAcknowledgements(Stream stream, CancellationToken ct, Action activity) : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
    private readonly Queue<long> sequences = new();
    private Task<JsonElement>? pending;

    public async Task AddAsync(long sequence)
    {
        sequences.Enqueue(sequence);
        pending ??= ReadAsync();
        if (sequences.Count >= 2) await CompleteAsync();
    }

    public async Task ObserveAsync()
    {
        if (pending?.IsCompleted == true) await CompleteAsync();
    }

    public async Task DrainAsync()
    {
        while (pending != null) await CompleteAsync();
    }

    private async Task CompleteAsync()
    {
        var ack = await pending!;
        if (ack.Long("sequence", -1) != sequences.Dequeue()) throw new InvalidDataException("Invalid stream acknowledgement.");
        activity();
        pending = sequences.Count == 0 ? null : ReadAsync();
    }

    private async Task<JsonElement> ReadAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        return await Wire.ReadAsync<JsonElement>(stream, timeout.Token);
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (pending != null) { try { await pending; } catch { } }
        lifetime.Dispose();
    }
}
