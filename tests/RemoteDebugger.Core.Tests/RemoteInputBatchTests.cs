using RemoteDebugger.Core;
using Xunit;

namespace RemoteDebugger.Core.Tests;

public sealed class RemoteInputBatchTests
{
    [Fact]
    public void TakePendingClampsBatchSizeAndHonorsEligibility()
    {
        var queue = new RemoteInputQueue<string>();
        for (int i = 0; i < 16; i++) Assert.True(queue.TryWrite("keyDown", "key-" + i));

        IReadOnlyList<string> batch = queue.TakePending(16, value => value != "key-7");

        Assert.Equal(7, batch.Count);
        Assert.Equal(Enumerable.Range(0, 7).Select(i => "key-" + i), batch);
    }

    [Fact]
    public async Task TakePendingStopsBeforeReleaseAndLeavesFollowingInputQueued()
    {
        var queue = new RemoteInputQueue<string>();
        Assert.True(queue.TryWrite("move", "move"));
        Assert.True(queue.TryWrite("release", "release"));
        Assert.True(queue.TryWrite("keyDown", "after-release"));

        Assert.Equal(["move"], queue.TakePending(15, _ => true));

        await using var reader = queue.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("release", reader.Current);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("after-release", reader.Current);
        queue.Complete();
    }
}
