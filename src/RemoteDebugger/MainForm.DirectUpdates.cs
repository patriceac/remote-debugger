using Forms = System.Windows.Forms;

namespace RemoteDebugger;

public sealed partial class MainForm
{
    private readonly Forms.Timer directUpdateTimer = new();
    private readonly DirectUpdateSchedule directUpdateSchedule = new();
    private CancellationTokenSource? directUpdateLifetime;

    private FleetDevice[] PendingDirectUpdates() => fleet.Values.Where(device => device.NeedsDirectUpdate(
        ExecutableIdentity.Sha256, DeviceWanAddress.Load(root, device.Peer.Fingerprint))).ToArray();

    private void ScheduleDirectUpdates()
    {
        if (IsDisposed) return;
        directUpdateTimer.Stop();
        if (!shown || !PrivateInternet || !isUpdateAdmin || quitting || terminating || directUpdateLifetime != null ||
            discoveryBusy || fleetRefreshing || FleetBusy) return;
        directUpdateSchedule.Refresh(PendingDirectUpdates().Select(device => DeviceKey(device.Peer) + ":" + ExecutableIdentity.Sha256), DateTimeOffset.UtcNow);
        if (directUpdateSchedule.DueUtc is not { } due) return;
        directUpdateTimer.Interval = (int)Math.Clamp((due - DateTimeOffset.UtcNow).TotalMilliseconds, 1, int.MaxValue);
        directUpdateTimer.Start();
    }

    private async Task RunDirectUpdatesAsync()
    {
        directUpdateTimer.Stop();
        if (directUpdateLifetime != null || discoveryBusy || fleetRefreshing || FleetBusy) return;
        using var lifetime = new CancellationTokenSource();
        directUpdateLifetime = lifetime;
        try
        {
            if (!PrivateInternet || !new UpdateAdminStore(root).IsAdmin || quitting || terminating ||
                FleetBusy || fleetRefreshing || discoveryBusy || pairingBusy || clientUpdateBusy || action != null) return;
            var pending = PendingDirectUpdates();
            if (pending.Length == 0) return;
            if (pending.Any(device => device.KnownDirectAddress != null))
            {
                // Refresh changing LAN addresses, retaining only identities already awaiting an update.
                fleetRefreshing = true;
                RefreshControllerControls();
                try
                {
                    var nearby = await Discovery.FindAsync(1000, lifetime.Token, root);
                    foreach (var device in pending)
                        if (nearby.FirstOrDefault(peer => string.Equals(peer.Fingerprint, device.Peer.Fingerprint, StringComparison.OrdinalIgnoreCase)) is { } peer)
                            fleet[DeviceKey(peer)] = device.Observe(peer);
                    pending = PendingDirectUpdates();
                }
                catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException) { }
                finally { fleetRefreshing = false; }
            }
            await RefreshFleetVersionsAsync(lifetime.Token, pending);
            lifetime.Token.ThrowIfCancellationRequested();
            await UpdateAllDevicesAsync(pending.Select(device => fleet[DeviceKey(device.Peer)]).ToArray());
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            diagnostics.Record("direct_auto_update_failed", DiagnosticContext(), (ex as RemoteOperationException)?.Code, ex, incident: true);
        }
        finally
        {
            directUpdateLifetime = null;
            directUpdateSchedule.Defer(DateTimeOffset.UtcNow);
            ScheduleDirectUpdates();
        }
    }
}
