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

    internal async Task<Stream> OpenTransportAsync(CancellationToken ct)
    {
        if (discoverRoutes) await RefreshRoutesIfNeededAsync(ct).ConfigureAwait(false);
        var route = Connection;
        if (route.DirectHost.Length > 0 && route.RelayUrl.Length > 0)
        {
            if (!CoolingDown(new(route.DirectHost, route.DirectPort)))
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    var stream = await transportFactory(route, attempt.Token).ConfigureAwait(false);
                    ActiveRoute = IsLanAddress(route.DirectHost) ? "Direct LAN" : "Direct WAN";
                    return stream;
                }
                catch (Exception) when (!ct.IsCancellationRequested) { CoolDown(route.DirectHost, route.DirectPort); }
            }
            if (ReferenceEquals(Connection, route)) Connection = route with { DirectHost = "", DirectPort = 0 };
            route = route with { DirectHost = "", DirectPort = 0 };
        }
        var transport = await transportFactory(route, ct).ConfigureAwait(false);
        ActiveRoute = route.RelayUrl.Length > 0 ? "Relay" : IsLanAddress(route.Host) ? "Direct LAN" : "Direct WAN";
        return transport;
    }

    private async Task RefreshRoutesIfNeededAsync(CancellationToken ct)
    {
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
            var nearby = await Discovery.FindAsync(1000, deadline.Token).ConfigureAwait(false);
            var peer = nearby.FirstOrDefault(p => string.Equals(p.Fingerprint, Connection.Fingerprint, StringComparison.OrdinalIgnoreCase));
            if (peer != null)
            {
                if (Connection.RelayUrl.Length == 0) Connection = Connection with { Host = peer.Host, Port = peer.Port };
                else if (await TryPreferDirectAsync([new(peer.Host, peer.Port)], deadline.Token).ConfigureAwait(false)) return;
            }
            if (Connection.RelayUrl.Length > 0)
                await TryPreferDirectAsync(await GetDirectEndpointsAsync(deadline.Token).ConfigureAwait(false), deadline.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { }
        finally { routeProbe.Release(); }
    }
}
