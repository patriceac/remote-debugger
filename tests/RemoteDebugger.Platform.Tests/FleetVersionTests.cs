using System.Collections.Concurrent;
using RemoteDebugger;
using RemoteDebugger.Core;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class FleetVersionTests
{
    [Fact]
    public void UpdatePreflightRejectsUnavailablePlatformAndAcceptsLegacySnapshots()
    {
        var unavailable = Json.Element(new { platform = new { available = false, message = "Repair the installed support service." } });
        Assert.Equal("Repair the installed support service.", Assert.Throws<InvalidOperationException>(() =>
            AgentUpdateClient.RequireUpdatePlatform(unavailable)).Message);
        AgentUpdateClient.RequireUpdatePlatform(Json.Element(new { platform = new { available = true } }));
        AgentUpdateClient.RequireUpdatePlatform(Json.Element(new { }));
    }

    [Fact]
    public async Task SlowClientDoesNotBlockOtherOperationsAndConcurrencyIsBounded()
    {
        var started = new ConcurrentQueue<int>();
        var entered = Enumerable.Range(0, 6).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var release = Enumerable.Range(0, 6).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var batch = MainForm.RunFleetOperationsAsync(Enumerable.Range(0, 6), async i =>
        {
            started.Enqueue(i); entered[i].SetResult();
            await release[i].Task;
        }, CancellationToken.None);
        try
        {
            await entered[3].Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { 0, 1, 2, 3 }, started.ToArray());
            release[1].SetResult();
            await entered[4].Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(release[0].Task.IsCompleted);
            Assert.Equal(5, started.Count);
            Assert.False(batch.IsCompleted);
        }
        finally
        {
            foreach (var gate in release) gate.TrySetResult();
            await batch.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(6, started.Count);
    }

    [Fact]
    public async Task CancellationStopsQueuedOperationsAndReleasesTheBatch()
    {
        using var stop = new CancellationTokenSource();
        int started = 0;
        var batch = MainForm.RunFleetOperationsAsync(Enumerable.Range(0, 8), async _ =>
        {
            Interlocked.Increment(ref started);
            await Task.Delay(Timeout.Infinite, stop.Token);
        }, stop.Token);
        Assert.Equal(4, started);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(4, started);
    }

    [Fact]
    public async Task FailedOperationDoesNotPreventRemainingDevicesFromRunning()
    {
        var completed = new ConcurrentQueue<int>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batch = MainForm.RunFleetOperationsAsync(Enumerable.Range(0, 8), async i =>
        {
            await release.Task;
            if (i == 0) throw new InvalidOperationException("Device update failed.");
            completed.Enqueue(i);
        }, CancellationToken.None);

        release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => batch.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(Enumerable.Range(1, 7), completed.Order());
    }
}
