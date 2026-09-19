using System.Text.Json;
using RemoteDebugger;
using RemoteDebugger.Core;
using Xunit;

public sealed class DeviceWanAddressTests
{
    [Theory]
    [InlineData("", null, 0)]
    [InlineData("   ", null, 0)]
    [InlineData(" Bedros.hd.free.fr ", "bedros.hd.free.fr", 45832)]
    [InlineData("bedros.hd.free.fr:55001", "bedros.hd.free.fr", 55001)]
    [InlineData("192.168.1.20:1", "192.168.1.20", 1)]
    public void AddressIsOptionalAndPortDefaultsWhenOmitted(string value, string? host, int port)
    {
        Assert.Equal(host == null ? null : new DirectEndpoint(host, port), DeviceWanAddress.Parse(value));
    }

    [Theory]
    [InlineData("bedros.hd.free.fr:0")]
    [InlineData("bedros.hd.free.fr:65536")]
    [InlineData("bedros.hd.free.fr:")]
    [InlineData("bedros.hd.free.fr:abc")]
    [InlineData("https://bedros.hd.free.fr:45832")]
    [InlineData("bedros.hd.free.fr/path")]
    public void MalformedAddressIsRejected(string value) => Assert.Throws<ArgumentException>(() => DeviceWanAddress.Parse(value));

    [Fact]
    public void SettingsBelongToDeviceIdentityAndSurviveAddressChanges()
    {
        string root = Path.Combine(Path.GetTempPath(), "RemoteDebugger-Wan-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new InternetSettings("https://relay.example", new string('a', 64), new string('b', 64));
            settings.Save(root);
            string fingerprint = new string('c', 64);
            var wan = DeviceWanAddress.Parse("bedros.hd.free.fr:55001");
            DeviceWanAddress.Save(root, fingerprint, wan);
            var peer = new Peer("Renamed PC", "192.168.1.42", 45832, fingerprint.ToUpperInvariant(), "RD-0123-4567-89AB-CDEF");
            var connection = InternetSettings.Target(peer, root);
            Assert.Equal(wan, connection.WanEndpoint);
            Assert.Null(DeviceWanAddress.Load(root, new string('d', 64)));
            Assert.Equal(connection, JsonSerializer.Deserialize<Connection>(JsonSerializer.Serialize(connection, Json.Options), Json.Options));
            DeviceWanAddress.Save(root, fingerprint, null);
            Assert.Null(InternetSettings.Target(peer, root).WanEndpoint);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true, true, true, "127.0.0.1")]
    [InlineData(false, true, true, "127.0.0.1,bedros.hd.free.fr")]
    [InlineData(false, true, false, "127.0.0.1,bedros.hd.free.fr,relay")]
    [InlineData(false, false, false, "127.0.0.1,relay")]
    public async Task RoutingPrefersLanThenConfiguredWanThenRelay(bool lanWorks, bool configureWan, bool wanWorks, string expected)
    {
        var wan = configureWan ? new DirectEndpoint("bedros.hd.free.fr", 55001) : null;
        var target = new Connection("RD-0123-4567-89AB-CDEF", 443, new string('a', 64), "token", "https://relay.example", new string('b', 64), "127.0.0.1", 45832, wan);
        var attempts = new List<string>();
        var client = new RemoteClient(target, (route, _) =>
        {
            attempts.Add(route.DirectHost.Length > 0 ? route.DirectHost : "relay");
            Assert.Equal(target.Fingerprint, route.Fingerprint);
            if (route.DirectHost == "127.0.0.1" && !lanWorks || route.DirectHost == wan?.Host && !wanWorks)
                throw new IOException("Unreachable endpoint");
            return Task.FromResult<Stream>(new MemoryStream());
        });
        using var stream = await client.OpenTransportAsync(default);
        Assert.Equal(expected, string.Join(',', attempts));
        Assert.Equal(wan, client.Connection.WanEndpoint);
        if (!lanWorks && !wanWorks)
        {
            attempts.Clear();
            using var retry = await client.OpenTransportAsync(default);
            Assert.Equal(new[] { "relay" }, attempts);
        }
    }

    [Fact]
    public async Task UnconfiguredLegacyWanRouteIsSkipped()
    {
        var target = new Connection("RD-0123-4567-89AB-CDEF", 443, new string('a', 64), "token", "https://relay.example", new string('b', 64), "203.0.113.10", 45832);
        var client = new RemoteClient(target, (route, _) =>
        {
            Assert.Empty(route.DirectHost);
            return Task.FromResult<Stream>(new MemoryStream());
        });
        using var stream = await client.OpenTransportAsync(default);
        Assert.Equal("Relay", client.ActiveRoute);
    }
}
