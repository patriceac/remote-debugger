using System.Net;
using RemoteDebugger.Core;
using Xunit;

namespace RemoteDebugger.Core.Tests;

public sealed class PeerDiscoveryPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("not-an-address")]
    public void NonRemoteAddressesAreExcluded(string host) =>
        Assert.False(PeerDiscoveryPolicy.IsRemoteAddress(host, []));

    [Theory]
    [InlineData("10.254.0.2", "::ffff:10.254.0.2")]
    [InlineData("::ffff:10.254.0.2", "10.254.0.2")]
    public void LocalAdapterIdentityHandlesMappedAddresses(string host, string local) =>
        Assert.False(PeerDiscoveryPolicy.IsRemoteAddress(host, [IPAddress.Parse(local)]));

    [Fact]
    public void AnotherPcOnTheSameSubnetRemainsDiscoverable() =>
        Assert.True(PeerDiscoveryPolicy.IsRemoteAddress("10.254.0.3", [IPAddress.Parse("10.254.0.2")]));

    [Theory]
    [InlineData("10.254.0.3", "10.254.0.3")]
    [InlineData("10.254.0.3", "::ffff:10.254.0.3")]
    [InlineData("Agent.Example", "agent.example.")]
    public void RediscoveryOfSameEndpointDoesNotChangeTarget(string activeHost, string discoveredHost) =>
        Assert.True(PeerDiscoveryPolicy.IsSameEndpoint(activeHost, 45832, discoveredHost, 45832));

    [Theory]
    [InlineData("10.254.0.3", 45832, "10.254.0.4", 45832)]
    [InlineData("10.254.0.3", 45832, "10.254.0.3", 45831)]
    [InlineData("agent-a", 45832, "agent-b", 45832)]
    public void DifferentEndpointChangesTarget(string activeHost, int activePort, string discoveredHost, int discoveredPort) =>
        Assert.False(PeerDiscoveryPolicy.IsSameEndpoint(activeHost, activePort, discoveredHost, discoveredPort));
}
