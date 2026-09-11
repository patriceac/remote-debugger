using System.Net;

namespace RemoteDebugger.Core;

public static class PeerDiscoveryPolicy
{
    // Display names are not machine identities: cloned PCs may share a name.
    public static bool IsRemoteAddress(string host, IEnumerable<IPAddress> localAddresses)
    {
        if (!IPAddress.TryParse(host, out var address)) return false;
        address = Normalize(address);
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        return !localAddresses.Any(local => Normalize(local).Equals(address));
    }

    public static bool IsSameEndpoint(string firstHost, int firstPort, string secondHost, int secondPort)
    {
        if (firstPort != secondPort || string.IsNullOrWhiteSpace(firstHost) || string.IsNullOrWhiteSpace(secondHost)) return false;
        if (IPAddress.TryParse(firstHost, out var firstAddress) && IPAddress.TryParse(secondHost, out var secondAddress))
            return Normalize(firstAddress).Equals(Normalize(secondAddress));
        return string.Equals(firstHost.TrimEnd('.'), secondHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
