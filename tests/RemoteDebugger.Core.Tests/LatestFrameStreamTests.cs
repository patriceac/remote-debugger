using RemoteDebugger.Core;
using Xunit;

namespace RemoteDebugger.Core.Tests;

public sealed class LatestFrameStreamTests
{
    [Fact]
    public async Task BusyUiTakesNewestFrameOnlyWhenItsCallbackRuns()
    {
        var ui = new QueuedContext();
        var beginReceive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBurst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shown = new List<int>();
        var previousContext = SynchronizationContext.Current;
        Task run;
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            run = LatestFrameStream.RunAsync<int>(async (publish, ct) =>
            {
                await beginReceive.Task.WaitAsync(ct);
                publish(0);
                await releaseBurst.Task.WaitAsync(ct);
                for (int i = 1; i <= 1000; i++) publish(i);
                received.SetResult();
            }, frame =>
            {
                Assert.Same(ui, SynchronizationContext.Current);
                shown.Add(frame);
                return Task.CompletedTask;
            });
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        beginReceive.SetResult();
        await ui.Posted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseBurst.SetResult();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(shown);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!run.IsCompleted)
        {
            SynchronizationContext.SetSynchronizationContext(ui);
            try { ui.RunPending(); }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
            if (!run.IsCompleted) await Task.Delay(1, deadline.Token);
        }
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 1000 }, shown);
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State)> callbacks = new();
        public TaskCompletionSource Posted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Post(SendOrPostCallback callback, object? state)
        {
            callbacks.Enqueue((callback, state));
            Posted.TrySetResult();
        }
        public void RunPending()
        {
            while (callbacks.TryDequeue(out var item)) item.Callback(item.State);
        }
    }

    [Fact]
    public async Task SlowPresenterSkipsIntermediateFramesAndKeepsNewest()
    {
        var presenting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var burstReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shown = new List<int>();
        await LatestFrameStream.RunAsync<int>(async (publish, ct) =>
        {
            publish(0);
            await presenting.Task.WaitAsync(ct);
            for (int i = 1; i <= 1000; i++) publish(i);
            burstReceived.SetResult();
        }, async frame =>
        {
            shown.Add(frame);
            if (frame == 0)
            {
                presenting.SetResult();
                await burstReceived.Task;
            }
        }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 0, 1000 }, shown);
    }

    [Fact]
    public async Task CancellationDropsPendingFramesAndStopsReceiver()
    {
        using var stop = new CancellationTokenSource();
        var presenting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shown = new List<int>();
        bool receiverStopped = false;
        var run = LatestFrameStream.RunAsync<int>(async (publish, ct) =>
        {
            try
            {
                publish(0);
                await presenting.Task.WaitAsync(ct);
                publish(1);
                received.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally { receiverStopped = true; }
        }, async frame =>
        {
            shown.Add(frame);
            presenting.SetResult();
            await received.Task;
            stop.Cancel();
        }, stop.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { 0 }, shown);
        Assert.True(receiverStopped);
    }

    [Fact]
    public async Task ReceiveFailureReachesPresenterLoopForReconnect()
    {
        var failure = new IOException("Transport interrupted");
        var actual = await Assert.ThrowsAsync<IOException>(() => LatestFrameStream.RunAsync<int>(
            (_, _) => Task.FromException(failure), _ => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(failure, actual);
    }

    [Fact]
    public async Task PresentationFailureCancelsReceiverWithoutLosingOriginalError()
    {
        bool receiverStopped = false;
        var failure = new InvalidDataException("Invalid image");
        var actual = await Assert.ThrowsAsync<InvalidDataException>(() => LatestFrameStream.RunAsync<int>(async (publish, ct) =>
        {
            try { publish(0); await Task.Delay(Timeout.Infinite, ct); }
            finally { receiverStopped = true; }
        }, _ => Task.FromException(failure)).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(failure, actual);
        Assert.True(receiverStopped);
    }
}
