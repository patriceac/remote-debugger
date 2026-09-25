using System.Security.Authentication;
using RemoteDebugger.Core;

namespace RemoteDebugger;

public sealed class SecurityMigrationClient(string root)
{
    private readonly SecurityMigrationStore store = new(root);

    public async Task<SecurityMigrationState> RunAsync(IProgress<SecurityDevice>? progress, CancellationToken ct, string? onlyHost = null)
    {
        var state = store.Load() ?? throw new InvalidOperationException("Create the passphrase-protected setup first.");
        state.Current.Save(root);
        if (!string.IsNullOrEmpty(onlyHost) && state.Devices.Any(d => d.LegacyHost == onlyHost && d.State == "protected")) return state;
        var peers = await state.Previous.FindAsync(ct, root).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(onlyHost)) peers = peers.Where(p => p.Host == onlyHost).ToList();
        foreach (var peer in peers)
            if (!state.Devices.Any(d => d.LegacyHost == peer.Host)) state.Devices.Add(new(peer.Name, peer.Host));
        store.Save(state);
        void Record(int index, SecurityDevice device)
        {
            state.Devices[index] = device;
            if (device.State == "protected" && device.Fingerprint.Length > 0)
                for (int j = 0; j < state.Devices.Count; j++)
                    if (j != index && state.Devices[j].Fingerprint == device.Fingerprint)
                        state.Devices[j] = state.Devices[j] with { State = "protected", Error = "", Connection = null, LegacyConnection = null };
            store.Save(state); progress?.Report(device);
        }
        for (int i = 0; i < state.Devices.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var device = state.Devices[i];
            if (!string.IsNullOrEmpty(onlyHost) && device.LegacyHost != onlyHost) continue;
            if (device.State == "protected") { progress?.Report(device); continue; }
            try
            {
                if (device.Connection != null && device.NewHost.Length > 0)
                {
                    try
                    {
                        var recovered = new RemoteClient(device.Connection);
                        try { await ConfirmAndReleaseAsync(recovered, state.Current.SecurityId, ct).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is IOException or InvalidOperationException or AuthenticationException)
                        {
                            // A lost release reply may already have reset the temporary grant.
                            await recovered.PairAsync(state.Current.AuthenticationSecret(device.NewHost), ct).ConfigureAwait(false);
                            await ConfirmAndReleaseAsync(recovered, state.Current.SecurityId, ct).ConfigureAwait(false);
                        }
                        Record(i, device with { State = "protected", Error = "", Connection = null, LegacyConnection = null }); continue;
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or AuthenticationException) { }
                    // Ordinary agent restarts change routing IDs. Recover by
                    // the previously authenticated certificate, never by name.
                    bool found = false;
                    foreach (var online in await state.Current.FindAsync(ct, root).ConfigureAwait(false))
                    {
                        if (online.Host == device.NewHost || device.Fingerprint.Length == 0) continue;
                        var recovered = new RemoteClient(new(online.Host, 443, device.Fingerprint, "", state.Current.RelayUrl, state.Current.AccessKey));
                        try
                        {
                            await recovered.PairAsync(state.Current.AuthenticationSecret(online.Host), ct).ConfigureAwait(false);
                            device = device with { NewHost = online.Host, Connection = recovered.Connection, State = "verifying" }; Record(i, device);
                            await ConfirmAndReleaseAsync(recovered, state.Current.SecurityId, ct).ConfigureAwait(false);
                            Record(i, device with { State = "protected", Error = "", Connection = null, LegacyConnection = null });
                            found = true; break;
                        }
                        catch (Exception ex) when (ex is IOException or InvalidOperationException or AuthenticationException) { }
                    }
                    if (found) continue;
                }
                if (!peers.Any(p => p.Host == device.LegacyHost))
                {
                    Record(i, device with { State = "pending", Error = "" }); continue;
                }
                Record(i, device with { State = "updating", Error = "" });
                var existing = device.LegacyConnection ?? store.PreviousConnection(state.Previous, device);
                var legacy = new RemoteClient(existing ?? new(device.LegacyHost, 443, device.Fingerprint, "", state.Previous.RelayUrl, state.Previous.AccessKey));
                if (existing == null) await legacy.PairAsync(state.Previous.AuthenticationSecret(device.LegacyHost), ct).ConfigureAwait(false);
                device = device with { Fingerprint = legacy.Connection.Fingerprint, LegacyConnection = legacy.Connection, State = "updating", Error = "" };
                Record(i, device);
                await SupportPlatform.SynchronizeAgentAsync(legacy, ct).ConfigureAwait(false);
                var staged = RemoteClient.Require(await legacy.CallAsync("security.stage", new { settings = state.Current }, ct, seconds: 60).ConfigureAwait(false));
                string host = staged.Str("host");
                if (!InternetSettings.IsSupportId(host) || !Safety.Equal(staged.Str("fingerprint"), device.Fingerprint) || staged.Str("securityId") != state.Current.SecurityId)
                    throw new AuthenticationException("The migrated computer identity changed.");
                device = device with { NewHost = host, State = "verifying" }; Record(i, device);
                var secured = new RemoteClient(new(host, 443, device.Fingerprint, "", state.Current.RelayUrl, state.Current.AccessKey));
                await secured.PairAsync(state.Current.AuthenticationSecret(host), ct).ConfigureAwait(false);
                // Persist recovery authorization BEFORE committing the remote switch.
                device = device with { Connection = secured.Connection }; Record(i, device);
                await ConfirmAndReleaseAsync(secured, state.Current.SecurityId, ct).ConfigureAwait(false);
                Record(i, device with { State = "protected", Error = "", Connection = null, LegacyConnection = null });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // No credentials are included in diagnostic strings or UI rows.
                Record(i, state.Devices[i] with { State = "pending", Error = ex is AuthenticationException ? "Access could not be verified." : "Update incomplete. Retry when support is available." });
            }
        }
        return state;
    }

    private static async Task ConfirmAndReleaseAsync(RemoteClient client, string securityId, CancellationToken ct)
    {
        RemoteClient.Require(await client.CallAsync("security.commit", new { securityId }, ct).ConfigureAwait(false));
        var status = RemoteClient.Require(await client.CallAsync("security.status", ct: ct).ConfigureAwait(false));
        if (status.Str("securityId") != securityId) throw new AuthenticationException("Security migration was not confirmed.");
        RemoteClient.Require(await client.CallAsync("security.release", new { securityId }, ct).ConfigureAwait(false));
    }
}
