using System.Text.Json;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

public sealed partial class MainForm
{
    private sealed record FleetDevice(Peer Peer, string Version = "", string Sha256 = "", bool Online = false,
        string State = "unknown", int Percent = 0, string Detail = "");
    private readonly Dictionary<string, FleetDevice> fleet = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UpdateProgressTracker> fleetProgress = new(StringComparer.OrdinalIgnoreCase);
    private readonly Forms.Button updateAllDevices = Button(() => UiText.UpdateAllDevices, "updateAllDevices", 178, primary: true);
    private CancellationTokenSource? fleetLifetime;
    private bool fleetRefreshing;
    private bool isUpdateAdmin;
    private Task<ExecutableSnapshot>? controllerSnapshot;
    private bool FleetBusy => fleetLifetime != null;
    private static string DeviceKey(Peer peer) => peer.Fingerprint.Length > 0 ? peer.Fingerprint : peer.Host;
    private bool NewerDeviceKnown => controllerSnapshot is { IsCompletedSuccessfully: true } &&
        fleet.Values.Any(d => d.Version.Length > 0 && Version.TryParse(d.Version, out var version) &&
            version > UpdatePolicy.ReleaseVersion(controllerSnapshot.Result.FileVersion));

    private void InitializeFleet()
    {
        try
        {
            string path = Path.Combine(root, "devices.dpapi");
            if (File.Exists(path))
                foreach (var device in JsonSerializer.Deserialize<List<FleetDevice>>(Vault.Read(path), Json.Options) ?? [])
                    fleet[DeviceKey(device.Peer)] = device with { Online = false, State = "offline", Percent = 0, Detail = "" };
        }
        catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException or JsonException) { }
        LoadUpdateTimings();
        updateAllDevices.Click += async (_, _) =>
        {
            try { if (FleetBusy) fleetLifetime?.Cancel(); else await UpdateAllDevicesAsync(); }
            catch (Exception) { discoveryState.SetText(() => UiText.UpdateIncomplete); RefreshControllerControls(); }
        };
        peers.OwnerDraw = true;
        peers.DrawColumnHeader += DrawComputerHeader;
        peers.DrawSubItem += DrawComputerCell;
        RenderPeers();
    }

    private void ObserveFleet()
    {
        fleetProgress.Clear();
        foreach (var key in fleet.Keys.ToArray()) fleet[key] = fleet[key] with { Online = false, State = "offline", Detail = "" };
        foreach (var peer in discoveredPeers)
        {
            string key = DeviceKey(peer);
            string state = isUpdateAdmin ? "checking" : "unknown";
            fleet[key] = fleet.TryGetValue(key, out var previous)
                ? previous with { Peer = peer, Online = true, State = state, Detail = "" }
                : new(peer, Online: true, State: state);
        }
    }

    private void RecordDevice(FleetDevice device, AgentUpdateProgress? update = null)
    {
        string key = DeviceKey(device.Peer);
        bool redrawOnly = fleet.TryGetValue(key, out var previous) && previous.State == device.State &&
            previous.Version == device.Version && previous.Peer.Name == device.Peer.Name &&
            UpdateProgressTracker.IsActiveStage(device.State);
        if (!fleetProgress.TryGetValue(key, out var progress)) fleetProgress[key] = progress = CreateUpdateProgress(key);
        progress.Report(device.State, update?.TransferredBytes ?? 0, update?.TotalBytes ?? 0);
        fleet[key] = device;
        if (!IsDisposed)
        {
            if (redrawOnly) peers.Invalidate();
            else RenderPeers();
        }
    }

    private void SaveFleet()
    {
        Vault.Save(Path.Combine(root, "devices.dpapi"), JsonSerializer.SerializeToUtf8Bytes(fleet.Values, Json.Options));
        SaveUpdateTimings();
    }

    private string FleetState(FleetDevice device) => device.State switch
    {
        "current" => UiText.ClientUpToDate, "newer" or "conflict" => UiText.UpdateControllerFirst,
        "available" => UiText.UpdateAvailable, "offline" => UiText.DeviceOffline,
        "legacy" => UiText.InitialUpdateRequired, "busy" => UiText.DeviceInUse,
        "failed" => UiText.UpdateIncomplete, "queued" => UiText.UpdateQueued,
        "preparing" => UiText.PreparingUpdate,
        "hashing" => UiText.PreparingUpdatePackage, "finalizing" => UiText.FinalizingUpdate,
        "transferring" => UiText.UpdatingDevice, "verifying" => UiText.TransferVerifying,
        "restarting" => UiText.TransferRestarting, "checking" => UiText.CheckingVersion,
        _ => UiText.AdminPcRequired
    };

    private RemoteClient FleetClient(Peer peer) => new(InternetSettings.Target(peer, root)) { AdminRoot = root };
    private bool IsActiveDevice(Peer peer) => supportSession && client != null && Safety.Equal(client.Connection.Fingerprint, peer.Fingerprint);

    internal static async Task RunFleetOperationsAsync<T>(IEnumerable<T> devices, Func<T, Task> operation, CancellationToken ct)
    {
        using var slots = new SemaphoreSlim(4);
        await Task.WhenAll(devices.Select(async device =>
        {
            await slots.WaitAsync(ct);
            try { ct.ThrowIfCancellationRequested(); await operation(device); }
            finally { slots.Release(); }
        }));
    }

    private async Task RefreshFleetVersionsAsync(CancellationToken ct)
    {
        if (!PrivateInternet || !isUpdateAdmin || FleetBusy || fleetRefreshing) return;
        fleetRefreshing = true;
        foreach (var device in fleet.Values.Where(device => device.Online).ToArray())
            RecordDevice(device with { State = "checking", Percent = 0, Detail = "" });
        RefreshControllerControls();
        try
        {
            var controller = await (controllerSnapshot ??= SupportPlatform.GetCurrentVersionAsync(CancellationToken.None)).WaitAsync(ct);
            await RunFleetOperationsAsync(fleet.Values.Where(d => d.Online).ToArray(), async device =>
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    JsonElement snapshot;
                    bool busy = false;
                    if (IsActiveDevice(device.Peer)) snapshot = RemoteClient.Require(await client!.CallAsync("update.snapshot", new { versionOnly = true }, ct: ct, seconds: 15));
                    else
                    {
                        // Older clients ignore this optional flag and return their full snapshot.
                        var inspected = await RemoteClient.ForDiscoveredPeer(device.Peer, root).AdminRequestAsync("admin.inspect", ct, new { versionOnly = true });
                        snapshot = inspected.GetProperty("snapshot"); busy = inspected.GetProperty("busy").GetBoolean();
                    }
                    var remote = snapshot.GetProperty("agent").Deserialize<ExecutableSnapshot>(Json.Options)!;
                    remote.Validate();
                    if (snapshot.TryGetProperty("platform", out var platformValue) &&
                        platformValue.Deserialize<SupportPlatformStatus>(Json.Options) is { Available: false } platform)
                    {
                        RecordDevice(device with { Version = remote.FileVersion ?? "", Sha256 = remote.Sha256,
                            State = "failed", Detail = platform.Message });
                        return;
                    }
                    RememberWakeAdapter(device.Peer, snapshot);
                    int comparison = UpdatePolicy.ReleaseVersion(remote.FileVersion).CompareTo(UpdatePolicy.ReleaseVersion(controller.FileVersion));
                    string state = comparison > 0 ? "newer" : busy ? "busy" : Safety.Equal(remote.Sha256, controller.Sha256) ? "current" : comparison < 0 ? "available" : "conflict";
                    RecordDevice(device with { Version = remote.FileVersion ?? "", Sha256 = remote.Sha256, State = state });
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (RemoteOperationException ex) when (ex.Code is "access_denied" or "unknown_operation")
                { RecordDevice(device with { State = "legacy" }); }
                catch (Exception ex) { RecordDevice(device with { State = "failed", Detail = FleetFailureDetail(ex) }); }
            }, ct);
            SaveFleet();
        }
        finally
        {
            foreach (var device in fleet.Values.Where(d => d.State == "checking").ToArray())
                RecordDevice(device with { State = "failed" });
            fleetRefreshing = false; RefreshControllerControls();
        }
    }

    private async Task UpdateAllDevicesAsync()
    {
        if (!PrivateInternet || !isUpdateAdmin || FleetBusy || fleetRefreshing || pairingBusy || clientUpdateBusy || terminating || action != null) return;
        if (NewerDeviceKnown)
        { discoveryState.SetText(() => UiText.UpdateControllerFirst); return; }
        var targets = fleet.Values.Where(d => d.Online && d.State is "available" or "legacy" or "failed").ToArray();
        if (targets.Length == 0) return;
        if (!supportSession && (selectedPeer == null || !targets.Any(d => DeviceKey(d.Peer) == DeviceKey(selectedPeer))))
        {
            var first = peers.Items.Cast<Forms.ListViewItem>().FirstOrDefault(item => item.Tag is Peer peer && DeviceKey(peer) == DeviceKey(targets[0].Peer));
            if (first != null) { peers.SelectedIndices.Clear(); first.Selected = true; SelectPeerFromList(); }
        }
        using var lifetime = new CancellationTokenSource(); fleetLifetime = lifetime;
        foreach (var device in targets) RecordDevice(device with { State = "hashing", Detail = "", Percent = 0 });
        discoveryState.SetText(() => UiText.PreparingUpdate);
        RefreshControllerControls();
        try
        {
            // Hash once for the batch, holding the payload against replacement.
            using var payload = File.Open(Environment.ProcessPath!, FileMode.Open, FileAccess.Read, FileShare.Read);
            var controller = await Task.Run(() => SupportPlatform.CaptureCurrentExecutableAsync(lifetime.Token), lifetime.Token);
            controllerSnapshot = Task.FromResult(controller);
            foreach (var device in targets) RecordDevice(device with { State = "queued", Detail = "", Percent = 0 });
            discoveryState.SetText(() => UiText.UpdatingDevice);
            await RunFleetOperationsAsync(targets, async initial =>
            {
                if (NewerDeviceKnown) return;
                var device = initial with { State = "preparing", Detail = "", Percent = 0 }; RecordDevice(device);
                var target = RemoteClient.ForDiscoveredPeer(device.Peer, root);
                bool acquired = false;
                bool updated = false;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(SupportOperationTimeouts.ControllerSynchronizationSeconds));
                    if (initial.State == "legacy")
                    {
                        var settings = InternetSettings.Load(root)!;
                        string id = device.Peer.SupportId.Length > 0 ? device.Peer.SupportId : device.Peer.Host;
                        await target.PairAsync(settings.AuthenticationSecret(id), timeout.Token);
                    }
                    else await target.AdminRequestAsync("admin.connect", timeout.Token);
                    acquired = true;
                    var snapshot = RemoteClient.Require(await target.CallAsync("update.snapshot", ct: timeout.Token, seconds: 30));
                    var installed = snapshot.GetProperty("agent").Deserialize<ExecutableSnapshot>(Json.Options)!;
                    device = device with { Version = installed.FileVersion ?? "", Sha256 = installed.Sha256 }; RecordDevice(device);
                    if (UpdatePolicy.ReleaseVersion(installed.FileVersion) > UpdatePolicy.ReleaseVersion(controller.FileVersion))
                    { RecordDevice(device with { State = "newer" }); return; }
                    bool receiving = true;
                    var progress = new Progress<AgentUpdateProgress>(value =>
                    {
                        if (!receiving || IsDisposed || value.Stage == "complete") return;
                        device = device with { State = value.Stage, Percent = value.TransferPercent,
                            Detail = value.Stage == "transferring"
                                ? FormatFleetBytes(value.TransferredBytes, value.TotalBytes) : "" };
                        RecordDevice(device, value);
                    });
                    try { await AgentUpdateClient.SynchronizeAgentAsync(target, controller, snapshot, timeout.Token, progress); }
                    finally { receiving = false; }
                    updated = true;
                    RecordDevice(device with { State = "finalizing", Percent = 0, Detail = "" });
                }
                catch (Exception ex)
                {
                    if (!lifetime.IsCancellationRequested) diagnostics.Record("fleet_update_failed",
                        new(device.Peer.Name, "controller", controller.FileVersion ?? "", Route: target.ActiveRoute),
                        (ex as RemoteOperationException)?.Code, ex, incident: true);
                    RecordDevice(device with { State = "failed", Detail = lifetime.IsCancellationRequested ? UiText.TransferPaused : FleetFailureDetail(ex) });
                }
                finally
                {
                    if (acquired)
                    {
                        using var release = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                        try { await target.ReleaseUpdateAsync(release.Token); }
                        catch { try { await target.DisconnectAsync(release.Token); } catch { } }
                    }
                    if (updated) RecordDevice(device with { Version = controller.FileVersion ?? "", Sha256 = controller.Sha256, State = "current", Percent = 100, Detail = "" });
                    SaveFleet();
                }
            }, lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            foreach (var device in fleet.Values.Where(d => d.State is "queued" or "hashing").ToArray())
                RecordDevice(device with { State = "failed", Detail = lifetime.IsCancellationRequested ? UiText.TransferPaused :
                    NewerDeviceKnown ? UiText.UpdateControllerFirst : UiText.UpdateIncomplete });
            fleetLifetime = null;
            if (!IsDisposed) { RefreshControllerControls(); discoveryState.SetText(() => UiText.UpdateBatchFinished); }
        }
    }

    internal static bool NeedsFleetProgressAnimation(string state) => UpdateProgressTracker.IsActiveStage(state);

    internal static string FormatFleetBytes(long transferred, long total) => total >= 1024 * 1024
        ? $"{transferred / 1048576d:F1}/{total / 1048576d:F1} MiB"
        : $"{transferred / 1024d:F0}/{total / 1024d:F0} KiB";

    internal static string FleetFailureDetail(Exception ex)
    {
        string message = ex.GetBaseException().Message.Trim();
        return message.Length == 0 ? UiText.RetryUpdate : message;
    }
}
