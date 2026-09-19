using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private AutomationElement SecurityControl(string id) => AutomationElement.RootElement.FindFirst(TreeScope.Descendants,
        new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, product!.Id),
            new PropertyCondition(AutomationElement.AutomationIdProperty, id))) ?? throw new IOException("Missing security control: " + id);

    private async Task SecurityReviewAsync()
    {
        var fixture = JsonDocument.Parse(await File.ReadAllBytesAsync(Environment.GetCommandLineArgs().Last(), stop.Token));
        var previous = fixture.RootElement.GetProperty("previous").Deserialize<InternetSettings>(Json.Options)!;
        var preparation = fixture.RootElement.GetProperty("preparation").Deserialize<RelaySecurityPreparation>(Json.Options)!;
        string controllerRoot = Path.Combine(output, "security-controller");
        const string password = "temporary acceptance words for protected setup";
        Process? agentProcess = null, controllerProcess = null;
        try
        {
            previous.Save(productData); previous.Save(controllerRoot);
            Vault.Save(Path.Combine(controllerRoot, "security-relay-prepared.dpapi"), JsonSerializer.SerializeToUtf8Bytes(preparation, Json.Options));
            agentProcess = LaunchInternetProduct(true, productData, enableSupport: true); product = agentProcess;
            await WaitUiAsync(); WindowState = Forms.FormWindowState.Minimized;
            Peer? peer = null;
            for (int n = 0; n < 100 && peer == null; n++)
            {
                peer = (await previous.FindAsync(stop.Token)).FirstOrDefault(p => p.Name == Environment.MachineName);
                if (peer == null) await Task.Delay(500, stop.Token);
            }
            if (peer == null) throw new IOException("The isolated test agent did not register.");
            controllerProcess = LaunchInternetProduct(false, controllerRoot, openSecurity: true); product = controllerProcess;
            await WaitUiAsync(); Native.FocusWindow(product.Id);
            await Task.Delay(700, stop.Token);
            if (!SecurityControl("securityPassphrase").Current.IsPassword) throw new IOException("The passphrase field is not masked.");
            CaptureDesktop("security-setup.png", focusProduct: false);
            foreach (string id in new[] { "securityPassphrase", "securityConfirmation" })
            {
                var field = SecurityControl(id); field.SetFocus();
                if (!field.Current.HasKeyboardFocus) throw new IOException("The masked field did not receive focus.");
                // Keep the modal dialog focused; targeting the process would
                // reactivate its disabled main window before typing.
                Native.TypeText(0, password); await Task.Delay(150, stop.Token);
            }
            InvokeElement(SecurityControl("securityCreate"));
            var store = new SecurityMigrationStore(controllerRoot);
            for (int n = 0; n < 80 && store.Load() == null; n++) await Task.Delay(250, stop.Token);
            var state = store.Load() ?? throw new IOException("Security setup did not complete: " + SecurityControl("securityStatus").Current.Name);
            Pass("security.gui_setup", "The signed Release accepts a masked passphrase once and creates only encrypted portable setup");
            var envelope = ProtectedSetup.Read(await File.ReadAllBytesAsync(store.ProtectedSetupPath, stop.Token));
            if (envelope.ProfileId != state.Current.SecurityId || state.Current.AccessKey == previous.AccessKey || state.Current.PairingKey == previous.PairingKey)
                throw new IOException("Security setup did not rotate both credentials.");
            // Scope the live test to its disposable guest. Never migrate other discovered PCs.
            state.Devices.Clear(); state.Devices.Add(new SecurityDevice(peer.Name, peer.Host));
            state.Devices.Add(new SecurityDevice("Offline test computer", "RD-0000-0000-0000-0001")); store.Save(state);
            string legacyRoot = Path.Combine(output, "legacy-controller"), legacyConnection = Path.Combine(output, "legacy.connection");
            previous.Save(legacyRoot);
            await CliAsync(["pair", "--host", peer.Host, "--data-root", legacyRoot, "--connection", legacyConnection]);
            // An already paired controller must reuse its saved grant: the
            // receiving PC has correctly closed the one-time pairing gate.
            File.Copy(legacyConnection, Path.Combine(controllerRoot, "controller.connection"));
            var migration = await CliAsync(["security-migrate", "--host", peer.Host, "--data-root", controllerRoot]);
            if (!migration.GetProperty("complete").GetBoolean()) throw new IOException("Security migration did not complete.");
            state = store.Load()!;
            if (state.Devices[0].State != "protected" || state.Devices[1].State != "pending") throw new IOException("Migration state is not truthful.");
            if (InternetSettings.Load(productData) != state.Current) throw new IOException("The receiving PC did not persist the new credentials.");
            Pass("security.remote_migration", "The Release verifies new credentials and migrates a receiving PC without local passphrase entry; offline work remains pending");
            if ((await previous.FindAsync(stop.Token)).Any(p => p.Host == peer.Host)) throw new IOException("Old credentials still discover the migrated PC.");
            string statusRequest = Path.Combine(output, "security-status-request.json");
            await File.WriteAllTextAsync(statusRequest, Json.Text(new { operation = "status", args = new { }, timeoutSeconds = 10 }), stop.Token);
            var denied = await CliAsync(["call", "--request", statusRequest, "--connection", legacyConnection], requireSuccess: false);
            if (denied.GetProperty("ok").GetBoolean()) throw new IOException("The old saved session retained access.");
            Pass("security.legacy_rejected", "Both legacy discovery and the previously authenticated session lose access after migration");
            string securedConnection = Path.Combine(output, "secured.connection");
            await CliAsync(["pair", "--host", state.Devices[0].NewHost, "--data-root", controllerRoot, "--connection", securedConnection]);
            await CliAsync(["sync", "--connection", securedConnection]);
            await CliAsync(["screenshot", "--file", Path.Combine(output, "security-protected-desktop.jpg"), "--connection", securedConnection]);
            Pass("security.protected_reconnect", "A fresh authenticated controller reconnects and receives the live desktop using the remembered new credentials");
            // Simulate a controller stopping after remote commit but before it
            // records success: only the saved protected route and grant remain.
            state.Devices[0] = state.Devices[0] with { State = "verifying", Connection = RemoteClient.Load(securedConnection).Connection };
            store.Save(state);
            agentProcess.Kill(true); await agentProcess.WaitForExitAsync(stop.Token);
            agentProcess = LaunchInternetProduct(true, productData, enableSupport: true);
            for (int n = 0; n < 100; n++)
            {
                if ((await state.Current.FindAsync(stop.Token)).Any(p => p.Name == Environment.MachineName && p.Host != state.Devices[0].NewHost)) break;
                await Task.Delay(500, stop.Token);
            }
            await CliAsync(["security-migrate", "--host", peer.Host, "--data-root", controllerRoot]);
            if (store.Load()!.Devices[0].State != "protected") throw new IOException("Unacknowledged migration could not recover.");
            Pass("security.recovery", "A restarted controller recovers an unacknowledged migration after the receiving PC restarts, using its pinned certificate on a new route");
            var again = await CliAsync(["security-migrate", "--host", peer.Host, "--data-root", controllerRoot]);
            if (!again.GetProperty("complete").GetBoolean()) throw new IOException("Completed migration was not idempotent.");
            Pass("security.idempotent", "Repeating completed migration leaves the protected PC intact");
            ((WindowPattern)SecurityControl("securityWindow").GetCurrentPattern(WindowPattern.Pattern)).Close();
            controllerProcess.Kill(true); await controllerProcess.WaitForExitAsync(stop.Token);
            controllerProcess = LaunchInternetProduct(false, controllerRoot, openSecurity: true); product = controllerProcess;
            await WaitUiAsync(); await Task.Delay(500, stop.Token);
            CaptureDesktop("security-migrated.png", focusProduct: false);
            string freshRoot = Path.Combine(output, "fresh-install");
            InternetSettings.Import(store.ProtectedSetupPath, freshRoot);
            if (InternetSettings.Load(freshRoot) != null) throw new IOException("A fresh installer unlocked without its passphrase.");
            try { new SecurityMigrationStore(freshRoot).Unlock("incorrect test passphrase"); throw new IOException("Incorrect passphrase accepted."); }
            catch (System.Security.Cryptography.CryptographicException) { }
            new SecurityMigrationStore(freshRoot).Unlock(password);
            if (InternetSettings.Load(freshRoot) != state.Current) throw new IOException("Remembered unlock failed.");
            InternetSettings.Import(store.ProtectedSetupPath, freshRoot);
            if (File.Exists(new SecurityMigrationStore(freshRoot).PendingSetupPath)) throw new IOException("Reinstall requires the passphrase again.");
            Pass("security.first_install", "A new account requires the passphrase once, rejects an incorrect passphrase, and retains authorization on reinstall");
            await FinishAsync();
        }
        catch
        {
            CaptureDesktop("security-failure.png", focusProduct: false);
            throw;
        }
        finally
        {
            if (controllerProcess is { HasExited: false }) controllerProcess.Kill(true);
            if (agentProcess is { HasExited: false }) agentProcess.Kill(true);
        }
    }
}
