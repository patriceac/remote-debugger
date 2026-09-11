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
}
