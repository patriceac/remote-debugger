using RemoteDebugger.Core;
using Xunit;

public sealed class StreamAcknowledgementsTests
{
    [Fact]
    public async Task AllowsNextFrameBeforeAckButBoundsOutstandingFramesAndDrainsInOrder()
    {
        using var stream = new GatedStream();
        await Wire.WriteAsync(stream, new { sequence = 0 }, default);
        await Wire.WriteAsync(stream, new { sequence = 1 }, default);
        stream.Position = 0;
        int observed = 0;
        await using var acks = new StreamAcknowledgements(stream, default, () => observed++);
        await acks.AddAsync(0).WaitAsync(TimeSpan.FromSeconds(2));
        var second = acks.AddAsync(1);
        Assert.False(second.IsCompleted);
        stream.Ready.SetResult();
        await second; await acks.DrainAsync();
        Assert.Equal(2, observed);
    }

    private sealed class GatedStream : MemoryStream
    {
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { await Ready.Task.WaitAsync(ct); return await base.ReadAsync(buffer, ct); }
    }
}
