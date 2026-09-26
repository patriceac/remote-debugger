using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using RemoteDebugger.Core;

namespace RemoteDebugger;

public sealed partial class RemoteClient
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> failedRoutes = new();
    private readonly SemaphoreSlim routeProbe = new(1, 1);
    private readonly bool discoverRoutes;
    private DateTimeOffset nextRouteProbe;
    private int observedNetworkVersion;
    private static int networkVersion;
    public string ActiveRoute { get; private set; } = "";
    internal bool DirectOnly { get; init; }

    internal static RemoteClient ForDiscoveredPeer(Peer peer, string root, bool directOnly = false) => new(
        directOnly ? InternetSettings.Target(peer, root) with { WanEndpoint = DeviceWanAddress.Load(root, peer.Fingerprint) }
            : InternetSettings.Target(peer, root))
    {
        AdminRoot = root,
        DirectOnly = directOnly,
        // The fleet just ran LAN/relay discovery; do not repeat it for every PC.
        nextRouteProbe = DateTimeOffset.UtcNow.AddMinutes(1),
        observedNetworkVersion = Volatile.Read(ref networkVersion)
    };

    static RemoteClient() => NetworkChange.NetworkAddressChanged += (_, _) => Interlocked.Increment(ref networkVersion);

    internal static bool IsLanAddress(string host)
    {
        if (!IPAddress.TryParse(host, out var address)) return false;
        if (IPAddress.IsLoopback(address)) return true;
        byte[] candidate = address.GetAddressBytes();
        return NetworkInterface.GetAllNetworkInterfaces().Where(nic => nic.OperationalStatus == OperationalStatus.Up)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Any(local =>
            {
                byte[] ip = local.Address.GetAddressBytes();
                if (ip.Length != candidate.Length) return false;
                int bits = local.PrefixLength;
                for (int i = 0; i < ip.Length; i++, bits -= 8)
                {
                    int mask = bits >= 8 ? 255 : bits <= 0 ? 0 : 255 << (8 - bits);
                    if ((ip[i] & mask) != (candidate[i] & mask)) return false;
                }
                return true;
            });
    }

    private bool CoolingDown(DirectEndpoint endpoint) => failedRoutes.TryGetValue($"{endpoint.Host}:{endpoint.Port}", out var until) && until > DateTimeOffset.UtcNow;
    private void CoolDown(string host, int port) => failedRoutes[$"{host}:{port}"] = DateTimeOffset.UtcNow.AddMinutes(2);

    internal static IEnumerable<DirectEndpoint> PreferredDirectEndpoints(Connection route)
    {
        if (route.DirectHost.Length > 0 && IsLanAddress(route.DirectHost))
            yield return new(route.DirectHost, route.DirectPort);
        if (route.WanEndpoint is { } wan && (wan.Host != route.DirectHost || wan.Port != route.DirectPort || !IsLanAddress(route.DirectHost)))
            yield return wan;
    }

    internal Task<Stream> OpenTransportAsync(CancellationToken ct) => OpenTransportAsync(ct, null);

    private async Task<Stream> OpenTransportAsync(CancellationToken ct, Action<string>? selectedRoute)
    {
        RequireController();
        if (DirectOnly)
        {
            var directRoute = Connection;
            var first = directRoute.DirectHost.Length > 0 ? new DirectEndpoint(directRoute.DirectHost, directRoute.DirectPort)
                : directRoute.RelayUrl.Length == 0 && !InternetSettings.IsSupportId(directRoute.Host) ? new DirectEndpoint(directRoute.Host, directRoute.Port) : null;
            Exception? lastError = null;
            foreach (var endpoint in new[] { first, directRoute.WanEndpoint }.OfType<DirectEndpoint>().Distinct())
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    var stream = await transportFactory(directRoute with { Host = endpoint.Host, Port = endpoint.Port,
                        RelayUrl = "", RelayAccessKey = "", DirectHost = "", DirectPort = 0 }, attempt.Token).ConfigureAwait(false);
                    ActiveRoute = IsLanAddress(endpoint.Host) ? "Direct LAN" : "Direct WAN";
                    selectedRoute?.Invoke(ActiveRoute);
                    return stream;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested) { lastError = ex; }
            }
            ct.ThrowIfCancellationRequested();
            throw new IOException("No direct LAN/WAN route is available. The automatic update will retry later.", lastError);
        }
        if (discoverRoutes) await RefreshRoutesIfNeededAsync(ct).ConfigureAwait(false);
        var route = Connection;
        if (route.RelayUrl.Length > 0)
        {
            foreach (var endpoint in PreferredDirectEndpoints(route).Where(endpoint => !CoolingDown(endpoint)))
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    var direct = route with { DirectHost = endpoint.Host, DirectPort = endpoint.Port };
                    var stream = await transportFactory(direct, attempt.Token).ConfigureAwait(false);
                    if (ReferenceEquals(Connection, route)) Connection = direct;
                    string name = IsLanAddress(endpoint.Host) ? "Direct LAN" : "Direct WAN";
                    ActiveRoute = name; selectedRoute?.Invoke(name);
                    return stream;
                }
                catch (Exception) when (!ct.IsCancellationRequested) { CoolDown(endpoint.Host, endpoint.Port); }
            }
            if (ReferenceEquals(Connection, route)) Connection = route with { DirectHost = "", DirectPort = 0 };
            route = route with { DirectHost = "", DirectPort = 0 };
        }
        var transport = await transportFactory(route, ct).ConfigureAwait(false);
        string routeName = route.RelayUrl.Length > 0 ? "Relay" : IsLanAddress(route.Host) ? "Direct LAN" : "Direct WAN";
        ActiveRoute = routeName; selectedRoute?.Invoke(routeName);
        return transport;
    }

    private async Task RefreshRoutesIfNeededAsync(CancellationToken ct)
    {
        if (DirectOnly) return;
        if (DateTimeOffset.UtcNow < nextRouteProbe && observedNetworkVersion == Volatile.Read(ref networkVersion)) return;
        if (!await routeProbe.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            int currentNetworkVersion = Volatile.Read(ref networkVersion);
            bool networkChanged = observedNetworkVersion != currentNetworkVersion;
            observedNetworkVersion = currentNetworkVersion;
            if (networkChanged) failedRoutes.Clear();
            nextRouteProbe = DateTimeOffset.UtcNow.AddMinutes(1);
            if (!networkChanged && Connection.DirectHost.Length > 0 && IsLanAddress(Connection.DirectHost)) return;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var nearby = await Discovery.FindAsync(1000, deadline.Token, AdminRoot).ConfigureAwait(false);
            var peer = nearby.FirstOrDefault(p => string.Equals(p.Fingerprint, Connection.Fingerprint, StringComparison.OrdinalIgnoreCase));
            if (peer != null)
            {
                if (Connection.RelayUrl.Length == 0) Connection = Connection with { Host = peer.Host, Port = peer.Port };
                else if (Connection.Token.Length == 0) { Connection = Connection with { DirectHost = peer.Host, DirectPort = peer.Port }; return; }
                else if (await TryPreferDirectAsync([new(peer.Host, peer.Port)], deadline.Token).ConfigureAwait(false)) return;
            }
            if (Connection.RelayUrl.Length > 0 && Connection.Token.Length > 0)
                await TryPreferDirectAsync(await GetDirectEndpointsAsync(deadline.Token).ConfigureAwait(false), deadline.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { }
        finally { routeProbe.Release(); }
    }
}
