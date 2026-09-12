using RemoteDebugger.Core;
using Xunit;

namespace RemoteDebugger.Core.Tests;

public sealed class RemoteInputQueueTests
{
    [Fact]
    public async Task PointerBurstKeepsLatestPositionAndPreservesClickOrdering()
    {
        var queue = new RemoteInputQueue<string>(4);
        for (int i = 0; i < 1000; i++) Assert.True(queue.TryWrite("move", i.ToString()));
        Assert.True(queue.TryWrite("down", "down"));
        Assert.True(queue.TryWrite("move", "drag"));
        Assert.True(queue.TryWrite("up", "up"));
        await using var reader = queue.ReadAllAsync().GetAsyncEnumerator();
        foreach (string expected in new[] { "999", "down", "drag", "up" })
        {
            Assert.True(await reader.MoveNextAsync());
            Assert.Equal(expected, reader.Current);
        }
        queue.Complete();
        Assert.False(await reader.MoveNextAsync());
    }

    [Fact]
    public async Task FocusLossDiscardsQueuedKeysAndReleasesBeforeNewInput()
    {
        var queue = new RemoteInputQueue<string>(2);
        queue.TryWrite("keyDown", "old key");
        queue.TryWrite("up", "old click");
        Assert.False(queue.TryWrite("keyDown", "overflow"));
        queue.Reset("release");
        Assert.True(queue.TryWrite("keyDown", "new key"));
        await using var reader = queue.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("release", reader.Current);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("new key", reader.Current);
        queue.Complete();
    }

    [Fact]
    public void TransportFailureKeepsPreferenceAndRequiresReleaseBeforeRecovery()
    {
        var state = new RemoteInputState();
        state.Suspend();
        Assert.True(state.Enabled);
        Assert.False(state.CanSend(true, true, true));
        state.Released();
        Assert.True(state.CanSend(true, true, true));
        Assert.False(state.CanSend(false, true, true));
        Assert.False(state.CanSend(true, false, true));
        Assert.False(state.CanSend(true, true, false));
        state.Enabled = false;
        state.Suspend();
        state.Released();
        Assert.False(state.Enabled);
        Assert.False(state.CanSend(true, true, true));
    }
}
