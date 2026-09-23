using System.Text.Json;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

public sealed partial class MainForm
{
    private readonly Forms.Button wakePc = Button(() => UiText.WakePc, "wakePc", 92);
    private readonly Forms.Button configureWake = Button(() => UiText.WakeSettings, "configureWake", 156);
    private bool wakeBusy;
    private string wakeFingerprint = "";
    private WakeSettings? wakeSettings;

    private Forms.Control BuildWakeControls()
    {
        var row = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Top, AutoSize = true, AutoSizeMode = Forms.AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = Forms.Padding.Empty };
        foreach (var control in new[] { wakePc, configureWake, updateClientButton }) { control.Margin = new(0, 0, 8, 0); row.Controls.Add(control); }
        configureWake.Click += (_, _) => ConfigureWake();
        wakePc.Click += async (_, _) => await WakeSelectedPcAsync();
        return row;
    }

    private void RefreshWakeControls()
    {
        string fingerprint = selectedPeer?.Fingerprint ?? "";
        if (wakeFingerprint != fingerprint)
        {
            wakeFingerprint = fingerprint; wakeSettings = DeviceWakeSettings.Load(root, fingerprint);
        }
        configureWake.Enabled = PairingExchange.ValidHash(fingerprint) && !wakeBusy && !pairingBusy && !FleetBusy && !terminating;
        wakePc.Enabled = configureWake.Enabled && wakeSettings != null && !supportSession && !clientUpdateBusy;
    }

    private void ConfigureWake()
    {
        if (selectedPeer is not { } peer || !configureWake.Enabled) return;
        using var form = new WakeSettingsForm(peer.Name, wakeSettings);
        if (form.ShowModal(this) != Forms.DialogResult.OK || form.Settings == null) return;
        try
        {
            DeviceWakeSettings.Save(root, peer.Fingerprint, form.Settings);
            wakeSettings = form.Settings; SaveFleet();
            connectionState.SetText(() => UiText.WakeSettingsSaved); RefreshWakeControls();
        }
        catch (Exception ex) { connectionState.SetText(ex.Message); }
    }

    private void RememberWakeAdapter(Peer peer, JsonElement snapshot)
    {
        if (!snapshot.TryGetProperty("wakeAdapters", out var adapters) || adapters.ValueKind != JsonValueKind.Array ||
            DeviceWakeSettings.Load(root, peer.Fingerprint) != null) return;
        var adapter = adapters.EnumerateArray().FirstOrDefault();
        if (adapter.ValueKind != JsonValueKind.Object) return;
        try { DeviceWakeSettings.Save(root, peer.Fingerprint, new(adapter.Str("macAddress"))); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or JsonException) { return; }
        if (wakeFingerprint == peer.Fingerprint) { wakeFingerprint = ""; RefreshWakeControls(); }
    }

    private async Task RememberConnectedWakeAdapterAsync(RemoteClient target)
    {
        try
        {
            var snapshot = RemoteClient.Require(await target.CallAsync("wake.info", seconds: 10));
            if (!IsDisposed && ReferenceEquals(client, target))
                RememberWakeAdapter(new Peer("", target.Connection.Host, target.Connection.Port, target.Connection.Fingerprint), snapshot);
        }
        catch { /* Older agents remain configurable by entering their MAC address. */ }
    }

    private async Task WakeSelectedPcAsync()
    {
        if (selectedPeer is not { } peer || wakeSettings is not { } settings || !wakePc.Enabled) return;
        wakeBusy = true; RefreshControllerControls();
        connectionState.SetText(() => UiText.WakeSending);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            if (settings.HelperFingerprint.Length == 0)
                await WakeOnLan.SendAsync(settings.MacAddress, settings.Destination, settings.Port, deadline.Token);
            else
            {
                var helper = fleet.Values.FirstOrDefault(d => d.Online && d.Peer.Fingerprint.Equals(settings.HelperFingerprint, StringComparison.OrdinalIgnoreCase));
                if (helper == null || !isUpdateAdmin) throw new InvalidOperationException(UiText.WakeHelperUnavailable);
                await FleetClient(helper.Peer).AdminRequestAsync("admin.wake", deadline.Token,
                    new { macAddress = settings.MacAddress, port = settings.Port });
            }
            if (selectedPeer == peer) connectionState.SetText(() => UiText.WakePacketSent);
        }
        catch (Exception ex) { if (selectedPeer == peer) connectionState.SetText(() => UiText.FailurePrefix + ex.Message); }
        finally { wakeBusy = false; if (!IsDisposed) RefreshControllerControls(); }
    }
}
