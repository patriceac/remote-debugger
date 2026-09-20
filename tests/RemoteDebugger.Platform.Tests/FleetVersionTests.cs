using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using RemoteDebugger;
using RemoteDebugger.Core;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class FleetVersionTests
{
    [Theory]
    [InlineData(true, "checking")]
    [InlineData(false, "offline")]
    public void DiscoveryDiscardsElapsedTimeFromThePreviousAttempt(bool online, string expectedState)
    {
        // Exercise discovery state without constructing a window or starting network services.
        var form = (MainForm)RuntimeHelpers.GetUninitializedObject(typeof(MainForm));
        GC.SuppressFinalize(form);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo Field(string name) => typeof(MainForm).GetField(name, flags)!;
        var devices = (System.Collections.IDictionary)Activator.CreateInstance(Field("fleet").FieldType)!;
        var started = new Dictionary<string, DateTimeOffset>();
        var peer = new Peer("Booting PC", "192.0.2.1", 45832, new string('a', 64));
        var discovered = new List<Peer> { peer };
        Field("fleet").SetValue(form, devices);
        Field("fleetStageStarted").SetValue(form, started);
        Field("discoveredPeers").SetValue(form, discovered);
        Field("isUpdateAdmin").SetValue(form, true);
        void Observe() => typeof(MainForm).GetMethod("ObserveFleet", flags)!.Invoke(form, null);

        Observe();
        started[peer.Fingerprint] = DateTimeOffset.UtcNow.AddMinutes(-6);
        if (!online) discovered.Clear();
        Observe();

        Assert.Empty(started);
        var device = devices[peer.Fingerprint]!;
        Assert.Equal(expectedState, device.GetType().GetProperty("State")!.GetValue(device));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task UpdateSnapshotUsesCachedIdentityAndOnlyChecksUpdateReadiness(bool versionOnly, bool serviceReady)
    {
        string root = Path.Combine(Path.GetTempPath(), "RemoteDebugger-preflight-" + Guid.NewGuid().ToString("N"));
        var identity = new ExecutableSnapshot(Path.Combine(root, "agent.exe"), 128, new string('a', 64), "0.4.30", new string('b', 64));
        var operations = new List<string>();
        try
        {
            using var service = new AgentUpdateService(root, (_, _) => throw new InvalidOperationException("No installation expected."), null,
                _ => Task.FromResult(identity), (operation, _, _) =>
                {
                    operations.Add(operation);
                    return serviceReady ? Task.FromResult(Json.Element(new { active = false })) :
                        Task.FromException<System.Text.Json.JsonElement>(new IOException("Update service unavailable."));
                });
            var snapshot = Json.Element(await service.SnapshotAsync(CancellationToken.None, versionOnly));
            Assert.Equal(identity.Sha256, snapshot.GetProperty("agent").Str("sha256"));
            if (versionOnly)
            {
                Assert.Empty(operations);
                Assert.False(snapshot.TryGetProperty("platform", out _));
            }
            else
            {
                Assert.Equal(new[] { "update.status" }, operations);
                Assert.Equal(serviceReady, snapshot.GetProperty("platform").GetProperty("available").GetBoolean());
                if (!serviceReady)
                    Assert.Equal("Update service unavailable.", Assert.Throws<InvalidOperationException>(() =>
                        AgentUpdateClient.RequireUpdatePlatform(snapshot)).Message);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

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
