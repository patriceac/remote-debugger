using System.Text.Json;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

public sealed partial class MainForm
{
    private sealed record FleetDevice(Peer Peer, string Version = "", string Sha256 = "", bool Online = false,
        string State = "unknown", int Percent = 0, string Detail = "");
    private readonly Dictionary<string, FleetDevice> fleet = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> fleetStageStarted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Forms.Button updateAllDevices = Button(() => UiText.UpdateAllDevices, "updateAllDevices", 178, primary: true);
    private CancellationTokenSource? fleetLifetime;
    private bool fleetRefreshing;
    private bool isUpdateAdmin;
    private Task<ExecutableSnapshot>? controllerSnapshot;
    private DateTimeOffset fleetCheckedAt;
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
        updateAllDevices.Click += async (_, _) =>
        {
            try { if (FleetBusy) fleetLifetime?.Cancel(); else await UpdateAllDevicesAsync(); }
            catch (Exception) { discoveryState.SetText(() => UiText.UpdateIncomplete); RefreshControllerControls(); }
        };
        peers.OwnerDraw = true;
        peers.DrawColumnHeader += (_, e) => e.DrawDefault = true;
        peers.DrawSubItem += (_, e) =>
        {
            if (e.ColumnIndex != 3 || e.Item?.Tag is not Peer peer || !fleet.TryGetValue(DeviceKey(peer), out var device) ||
                device.State is not ("transferring" or "checking" or "verifying" or "restarting"))
            { e.DrawDefault = true; return; }
            using var background = new SolidBrush(e.Item.Selected ? SystemColors.Highlight : peers.BackColor);
            e.Graphics.FillRectangle(background, e.Bounds);
            var track = new Rectangle(e.Bounds.X + 5, e.Bounds.Bottom - 5, Math.Max(0, e.Bounds.Width - 10), 3);
            using var back = new SolidBrush(Divider); using var fill = new SolidBrush(Teal);
            e.Graphics.FillRectangle(back, track);
            if (device.State == "transferring") e.Graphics.FillRectangle(fill, track with { Width = track.Width * device.Percent / 100 });
            else if (track.Width > 0)
            {
                int pulseWidth = Math.Min(track.Width, Math.Max(18, track.Width / 3));
                int pulseLeft = (int)(Environment.TickCount64 / 12 % (track.Width + pulseWidth)) - pulseWidth;
                var pulse = Rectangle.Intersect(track, new Rectangle(track.X + pulseLeft, track.Y, pulseWidth, track.Height));
                if (pulse.Width > 0) e.Graphics.FillRectangle(fill, pulse);
            }
            string detail = device.Detail;
            if (device.State != "transferring" && fleetStageStarted.TryGetValue(DeviceKey(peer), out var started))
            {
                string elapsed = UiText.Format(UiText.ElapsedTime, FormatTransferEta(DateTimeOffset.UtcNow - started));
                detail = detail.Length == 0 ? elapsed : detail + " · " + elapsed;
            }
            Forms.TextRenderer.DrawText(e.Graphics, detail, peers.Font,
                new Rectangle(e.Bounds.X + 5, e.Bounds.Y, e.Bounds.Width - 10, e.Bounds.Height - 5),
                e.Item.Selected ? SystemColors.HighlightText : PrimaryText, Forms.TextFormatFlags.EndEllipsis | Forms.TextFormatFlags.VerticalCenter);
        };
        RenderPeers();
    }

    private void ObserveFleet()
    {
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

    private void RecordDevice(FleetDevice device)
    {
        string key = DeviceKey(device.Peer);
        if (!fleet.TryGetValue(key, out var previous) || previous.State != device.State || !fleetStageStarted.ContainsKey(key))
            fleetStageStarted[key] = DateTimeOffset.UtcNow;
        fleet[key] = device;
        if (!IsDisposed) RenderPeers();
    }

    private void SaveFleet() => Vault.Save(Path.Combine(root, "devices.dpapi"), JsonSerializer.SerializeToUtf8Bytes(fleet.Values, Json.Options));

    private string FleetState(FleetDevice device) => device.State switch
    {
        "current" => UiText.ClientUpToDate, "newer" or "conflict" => UiText.UpdateControllerFirst,
        "available" => UiText.UpdateAvailable, "offline" => UiText.DeviceOffline,
        "legacy" => UiText.InitialUpdateRequired, "busy" => UiText.DeviceInUse,
        "failed" => UiText.UpdateIncomplete, "queued" => UiText.UpdateQueued,
        "transferring" => UiText.UpdatingDevice, "verifying" => UiText.TransferVerifying,
        "restarting" => UiText.TransferRestarting, "checking" => UiText.CheckingVersion,
        _ => UiText.AdminPcRequired
    };

    private RemoteClient FleetClient(Peer peer) => new(InternetSettings.Target(peer, root)) { AdminRoot = root };
    private bool IsActiveDevice(Peer peer) => supportSession && client != null && Safety.Equal(client.Connection.Fingerprint, peer.Fingerprint);

    internal static async Task CheckFleetVersionsAsync<T>(IEnumerable<T> devices, Func<T, Task> inspect, CancellationToken ct)
    {
        using var slots = new SemaphoreSlim(4);
        await Task.WhenAll(devices.Select(async device =>
        {
            await slots.WaitAsync(ct);
            try { ct.ThrowIfCancellationRequested(); await inspect(device); }
            finally { slots.Release(); }
        }));
    }

    private async Task RefreshFleetVersionsAsync(CancellationToken ct, bool reuseRecent = false)
    {
        if (!PrivateInternet || !isUpdateAdmin || FleetBusy || fleetRefreshing) return;
        if (reuseRecent && DateTimeOffset.UtcNow - fleetCheckedAt < TimeSpan.FromSeconds(30)) return;
        fleetCheckedAt = default;
        fleetRefreshing = true;
        foreach (var device in fleet.Values.Where(device => device.Online).ToArray())
            RecordDevice(device with { State = "checking", Percent = 0, Detail = "" });
        RefreshControllerControls();
        try
        {
            var controller = await (controllerSnapshot ??= SupportPlatform.GetCurrentVersionAsync(CancellationToken.None)).WaitAsync(ct);
            await CheckFleetVersionsAsync(fleet.Values.Where(d => d.Online).ToArray(), async device =>
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
            fleetCheckedAt = DateTimeOffset.UtcNow;
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
        // Reuse a just-completed discovery pass; each target is validated again before transfer.
        using var preparation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await RefreshFleetVersionsAsync(preparation.Token, reuseRecent: true);
        var controller = await (controllerSnapshot ??= SupportPlatform.GetCurrentVersionAsync(CancellationToken.None));
        if (NewerDeviceKnown)
        { discoveryState.SetText(() => UiText.UpdateControllerFirst); return; }
        fleetCheckedAt = default;
        using var lifetime = new CancellationTokenSource(); fleetLifetime = lifetime;
        RefreshControllerControls();
        try
        {
            foreach (var initial in fleet.Values.Where(d => d.Online && d.State is "available" or "legacy" or "failed").ToArray())
            {
                if (lifetime.IsCancellationRequested) break;
                var device = initial with { State = "queued", Detail = "" }; RecordDevice(device);
                var target = FleetClient(device.Peer);
                bool acquired = false;
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
                    { RecordDevice(device with { State = "newer" }); break; }
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    long baseline = -1;
                    bool receiving = true;
                    var progress = new Progress<AgentUpdateProgress>(value =>
                    {
                        if (!receiving || IsDisposed) return;
                        if (baseline < 0) baseline = value.TransferredBytes;
                        var metrics = FileTransferMetrics.Calculate(value.TransferredBytes, value.TotalBytes, value.TransferredBytes - baseline, watch.Elapsed);
                        device = device with { State = value.Stage, Percent = value.TransferPercent,
                            Detail = value.Stage == "transferring"
                                ? $"{value.TransferPercent}% · {FormatFleetBytes(value.TransferredBytes, value.TotalBytes)}" +
                                    (metrics.Remaining is { } eta ? " · " + FormatTransferEta(eta) : "")
                                : FormatBytes(value.TotalBytes) };
                        RecordDevice(device);
                    });
                    try { await SupportPlatform.SynchronizeAgentAsync(target, timeout.Token, progress); }
                    finally { receiving = false; }
                    RecordDevice(device with { Version = controller.FileVersion ?? "", Sha256 = controller.Sha256, State = "current", Percent = 100, Detail = "" });
                }
                catch (Exception ex) { RecordDevice(device with { State = "failed", Detail = lifetime.IsCancellationRequested ? UiText.TransferPaused : FleetFailureDetail(ex) }); }
                finally
                {
                    if (acquired)
                    {
                        using var release = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                        try { await target.ReleaseUpdateAsync(release.Token); }
                        catch { try { await target.DisconnectAsync(release.Token); } catch { } }
                    }
                    SaveFleet();
                }
            }
        }
        finally
        {
            fleetLifetime = null;
            if (!IsDisposed) { RefreshControllerControls(); discoveryState.SetText(() => UiText.UpdateBatchFinished); }
        }
    }

    internal static string FormatFleetBytes(long transferred, long total) => total >= 1024 * 1024
        ? $"{transferred / 1048576d:F1}/{total / 1048576d:F1} MiB"
        : $"{transferred / 1024d:F0}/{total / 1024d:F0} KiB";

    internal static string FleetFailureDetail(Exception ex)
    {
        string message = ex.GetBaseException().Message.Trim();
        return message.Length == 0 ? UiText.RetryUpdate : message;
    }
}
