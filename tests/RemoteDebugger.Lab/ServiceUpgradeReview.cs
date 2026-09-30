using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private object ReadUpgradeState()
    {
        if (product == null || product.HasExited)
        {
            int session = Process.GetCurrentProcess().SessionId;
            foreach (var candidate in Process.GetProcessesByName("RemoteDebugger"))
            {
                if (candidate.SessionId == session && string.Equals(candidate.MainModule?.FileName, application, StringComparison.OrdinalIgnoreCase))
                { product?.Dispose(); product = candidate; break; }
                candidate.Dispose();
            }
        }
        if (product == null || product.HasExited) throw new IOException("The upgraded desktop process is not running yet.");
        var identity = ProvisioningEvidence.InspectProduct(product, application, brokerProvisioning!.RegisteredUserSid);
        using var service = new ServiceController(SupportPlatformPaths.ServiceName);
        string Hash(string path, bool protectedFile = false)
        {
            if (protectedFile) return Convert.ToHexString(SHA256.HashData(Vault.Read(path)));
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(file));
        }
        var retained = new Dictionary<string, string>();
        foreach (string name in new[] { "internet.dpapi", "identity.pfx.dpapi", "internet-invitation.dpapi", "language.json", "theme.txt" })
            retained[name] = Hash(Path.Combine(productData, name), name.EndsWith(".dpapi", StringComparison.Ordinal));
        return new
        {
            appVersion = FileVersionInfo.GetVersionInfo(application).FileVersion,
            appHash = Hash(application), serviceHash = Hash(SupportPlatformPaths.ServiceExecutable),
            serviceVersion = FileVersionInfo.GetVersionInfo(SupportPlatformPaths.ServiceExecutable).FileVersion,
            serviceStart = service.StartType.ToString(), serviceState = service.Status.ToString(),
            identity, retained, privateProfile = InternetSettings.Load(productData) == UnattendedFixtureSettings,
            pendingUnlock = File.Exists(Path.Combine(productData, "internet.pending.rdrelay")),
            isAdmin = new UpdateAdminStore(productData).IsAdmin
        };
    }

    private async Task UpgradeAgentFromControllerAsync(Peer peer, string expectedHash)
    {
        var before = await PowerMessageAsync("POWER_UPGRADE_STATE");
        if (before.Str("appVersion") != "0.5.40.0" || before.Str("serviceVersion") != "0.5.40.0" ||
            before.Str("appHash") != before.Str("serviceHash") || before.Str("serviceStart") != "Manual")
            throw new IOException("The upgrade did not start from the real 0.5.40 app and demand-start service.");
        await File.WriteAllTextAsync(Path.Combine(output, "upgrade-before.json"), before.GetRawText(), stop.Token);
        Testing.UpdateAcceptanceAuthority.Enroll(productData);
        UnattendedFixtureSettings.Save(productData);
        AdminMaintenancePreference.Save(productData, false);
        product = LaunchProduct(false); await WaitUiAsync();
        var elapsed = Stopwatch.StartNew();
        JsonElement after = default;
        await WaitWorkflowAsync(async () =>
        {
            try
            {
                after = await PowerMessageAsync("POWER_UPGRADE_STATE", 4);
                return after.Str("appHash") == expectedHash && after.Str("serviceHash") == expectedHash &&
                    after.Str("serviceStart") == "Automatic" && after.Str("serviceState") == "Running";
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                await File.WriteAllTextAsync(Path.Combine(output, "upgrade-pending.txt"), ex.Message, stop.Token);
                return false;
            }
        }, 600);
        // Observe the controller's own completion, not merely the replacement on disk.
        await WaitWorkflowAsync(() =>
        {
            string path = Path.Combine(productData, "devices.dpapi");
            if (!File.Exists(path)) return Task.FromResult(false);
            var devices = JsonSerializer.Deserialize<JsonElement>(Vault.Read(path));
            return Task.FromResult(devices.EnumerateArray().Any(d => d.GetProperty("peer").Str("fingerprint") == peer.Fingerprint &&
                d.Str("state") == "current" && d.Str("verifiedUpdateSha256") == expectedHash));
        }, 90);
        await File.WriteAllTextAsync(Path.Combine(output, "upgrade-after.json"), after.GetRawText(), stop.Token);
        foreach (var item in before.GetProperty("retained").EnumerateObject())
            if (after.GetProperty("retained").Str(item.Name) != item.Value.GetString())
                throw new IOException("Upgrade changed saved agent data: " + item.Name);
        if (!after.GetProperty("privateProfile").GetBoolean() || after.GetProperty("pendingUnlock").GetBoolean() ||
            after.GetProperty("isAdmin").GetBoolean() || !after.GetProperty("identity").GetProperty("accepted").GetBoolean())
            throw new IOException("Upgrade lost private setup or changed the agent's user/privilege/role.");
        var rediscovered = (await Discovery.FindAsync(2000, stop.Token)).Single(p => p.Fingerprint == peer.Fingerprint);
        if (rediscovered.SupportId != peer.SupportId) throw new IOException("Upgrade changed the saved private support identity.");
        CaptureDesktop("upgrade-controller-complete.jpg");
        Pass("upgrade.automatic_fleet", "The real 0.5.40 agent upgrades through the controller's automatic direct fleet path without agent interaction",
            new { from = before.Str("appVersion"), to = after.Str("appVersion"), elapsedSeconds = elapsed.Elapsed.TotalSeconds, expectedHash });
        Pass("upgrade.retained_state", "Identity, unlocked private setup, language, theme and unelevated agent-only role survive the upgrade");
    }
}
