using System.Text.Json;
using RemoteDebugger;
using RemoteDebugger.Core;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class DirectUpdateTests
{
    [Fact]
    public void PendingDevicesRetryEveryFiveMinutesAndCurrentFleetSleepsUntilNewWork()
    {
        var now = DateTimeOffset.UtcNow;
        var schedule = new DirectUpdateSchedule();
        schedule.Refresh(["pc:release1"], now);
        Assert.Equal(now, schedule.DueUtc);
        schedule.Defer(now); // Offline, busy, or failed: leave it pending.
        schedule.Refresh(["pc:release1"], now.AddMinutes(1));
        Assert.Equal(now.AddMinutes(5), schedule.DueUtc);
        schedule.Refresh([], now.AddMinutes(5));
        Assert.Null(schedule.DueUtc);
        schedule.Refresh([], now.AddDays(1));
        Assert.Null(schedule.DueUtc);
        schedule.Refresh(["pc:release2"], now.AddDays(1));
        Assert.Equal(now.AddDays(1), schedule.DueUtc);
        schedule.Defer(now.AddDays(1));
        schedule.Refresh(["pc:release2", "new-pc:release2"], now.AddDays(1).AddSeconds(1));
        Assert.Equal(now.AddDays(1).AddSeconds(1), schedule.DueUtc);
    }

    [Fact]
    public void SavedDirectIdentityAndVerifiedReleaseSurviveRelayDiscoveryAndRestart()
    {
        string hash = new('b', 64);
        var lan = new Peer("PC", "192.0.2.1", 45832, new string('a', 64), "RD-0123-4567-89AB-CDEF");
        var relay = lan with { Host = lan.SupportId, Port = 443 };
        var device = new MainForm.FleetDevice(lan).Observe(relay) with { VerifiedUpdateSha256 = hash };
        var restored = JsonSerializer.Deserialize<MainForm.FleetDevice>(JsonSerializer.Serialize(device, Json.Options), Json.Options)!;
        Assert.Equal(lan.Host, restored.DirectPeer.Host);
        Assert.False(restored.NeedsDirectUpdate(hash, null));
        Assert.True(restored.NeedsDirectUpdate(new string('c', 64), null));
        var relayOnly = new MainForm.FleetDevice(relay);
        Assert.False(relayOnly.NeedsDirectUpdate(hash, null));
        Assert.True(relayOnly.NeedsDirectUpdate(hash, new("wan.example", 45832)));
        Assert.False((relayOnly with { Peer = relay with { Fingerprint = "" } }).NeedsDirectUpdate(hash, new("wan.example", 45832)));
    }

    [Fact]
    public async Task DirectOnlyFallsBackToConfiguredWanOnEveryNewConnection()
    {
        var routes = new List<Connection>();
        var connection = new Connection("RD-0123-4567-89AB-CDEF", 443, new string('a', 64), "token",
            "https://relay.example", "relay-key", "127.0.0.1", 45832, new("wan.example", 55001));
        var client = new RemoteClient(connection, (route, _) =>
        {
            routes.Add(route);
            return route.Host == "127.0.0.1" ? throw new IOException("LAN offline") : Task.FromResult<Stream>(new MemoryStream());
        }) { DirectOnly = true };
        using var initial = await client.OpenTransportAsync(default);
        using var reconnect = await client.OpenTransportAsync(default);
        Assert.Equal(new[] { "127.0.0.1", "wan.example", "127.0.0.1", "wan.example" }, routes.Select(route => route.Host));
        Assert.All(routes, route => { Assert.Empty(route.RelayUrl); Assert.Empty(route.RelayAccessKey); Assert.Equal(connection.Fingerprint, route.Fingerprint); });
        Assert.Equal("Direct WAN", client.ActiveRoute);
    }

    [Theory]
    [InlineData("inspect")]
    [InlineData("begin")]
    [InlineData("transfer")]
    [InlineData("resume")]
    [InlineData("health")]
    [InlineData("release")]
    public async Task AutomaticUpdateNeverUsesRelayWhenDirectRoutesFail(string operation)
    {
        var routes = new List<Connection>();
        var client = new RemoteClient(new("RD-0123-4567-89AB-CDEF", 443, new string('a', 64), "token",
            "https://relay.example", "relay-key", "127.0.0.1", 45832, new("wan.example", 55001)), (route, _) =>
        {
            routes.Add(route);
            throw new IOException("Direct endpoint offline");
        }) { DirectOnly = true };
        using var input = new MemoryStream([1]);
        await Assert.ThrowsAnyAsync<IOException>(async () =>
        {
            switch (operation)
            {
                case "inspect": await client.AdminRequestAsync("admin.inspect", default); break;
                case "begin": await client.SendUpdateAsync("update.begin"); break;
                case "transfer": await client.TransferUpdateAsync("transaction", input, 1, null, default); break;
                case "release": await client.ReleaseUpdateAsync(default); break;
                default: await client.CallAsync("update." + operation); break;
            }
        });
        Assert.NotEmpty(routes);
        Assert.Contains(routes, route => route.Host == "wan.example");
        Assert.All(routes, route =>
        {
            Assert.Contains(route.Host, new[] { "127.0.0.1", "wan.example" });
            Assert.Empty(route.RelayUrl);
            Assert.Empty(route.RelayAccessKey);
        });
    }
}
