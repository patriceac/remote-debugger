using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Automation;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private async Task LoopbackWanAsync()
    {
        string root = Path.Combine(output, "wan-settings");
        string fingerprint = new string('c', 64);
        new InternetSettings("https://relay.example", new string('a', 64), new string('b', 64)).Save(root);
        var devices = new[]
        {
            new { Peer = new Peer("WAN fixture A", "RD-0123-4567-89AB-CDEF", 443, fingerprint, "RD-0123-4567-89AB-CDEF") },
            new { Peer = new Peer("WAN fixture B", "RD-9876-5432-10FE-DCBA", 443, new string('d', 64), "RD-9876-5432-10FE-DCBA") }
        };
        Vault.Save(Path.Combine(root, "devices.dpapi"), JsonSerializer.SerializeToUtf8Bytes(devices, Json.Options));
        try
        {
            foreach (string address in new[] { "127.0.0.1", devices[0].Peer.Host })
                ProbeReconnectSelection(root, devices[0].Peer with { Host = address });
            product = loopbackController = LaunchLoopbackProduct(false, root);
            await WaitUiAsync(); WindowState = Forms.FormWindowState.Minimized;
            ResizeProductWindow(1060, 720);
            var firstDevice = await SelectDevice("WAN fixture A");
            await CheckDoubleClickConnect(firstDevice);
            await Save("bedros.hd.free.fr");
            Check("default_port", "bedros.hd.free.fr:45832");
            await Save("bedros.hd.free.fr:65536");
            if (DeviceWanAddress.Load(root, fingerprint)?.Port != 45832) throw new IOException("Invalid input overwrote the saved WAN port.");
            await Save("bedros.hd.free.fr:55001");
            await SelectDevice("WAN fixture B");
            if (Value(Find("wanAddress")!) != "") throw new IOException("WAN configuration leaked to a different device.");
            await SelectDevice("WAN fixture A");
            Check("per_device", "bedros.hd.free.fr:55001");
            CaptureDesktop("wan-configured.png");
            product.Kill(entireProcessTree: true); await product.WaitForExitAsync(stop.Token);
            product.Dispose(); product = loopbackController = LaunchLoopbackProduct(false, root);
            await WaitUiAsync();
            ResizeProductWindow(1060, 720);
            await SelectDevice("WAN fixture A");
            Check("restart_persistence", "bedros.hd.free.fr:55001");
            await Save("");
            Check("optional_clear", "");
            CaptureDesktop("wan-optional-blank.png");
        }
        catch
        {
            CaptureDesktop("wan-failure.png", focusProduct: false);
            throw;
        }
        finally { await CleanupLoopbackProcessesAsync(); }
        await FinishAsync();

        async Task<AutomationElement> SelectDevice(string name)
        {
            await WaitForUiAsync(() => Find("peers", 100) != null, 10);
            var item = Find("peers")!.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
                .First(element => element.Current.Name == name && element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _));
            ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            await Task.Delay(250, stop.Token);
            return item;
        }

        async Task CheckDoubleClickConnect(AutomationElement item)
        {
            await WaitForUiAsync(() => Find("pair")?.Current.IsEnabled == true, 10);
            var bounds = item.Current.BoundingRectangle;
            var point = new System.Drawing.Point((int)(bounds.Left + 16), (int)(bounds.Top + bounds.Height / 2));
            Native.Mouse(product!.Id, point.X, point.Y);
            await Task.Delay(100, stop.Token);
            Native.Mouse(product.Id, point.X, point.Y);
            bool started;
            try { await WaitForUiAsync(() => Find("pair")?.Current.IsEnabled == false, 3); started = true; }
            catch (TimeoutException) { started = false; }
            var evidence = new { started, client = item.Current.Name };
            const string requirement = "Double-clicking a client entry starts the same connection action as Connect";
            if (started) Pass("connection.client_double_click", requirement, evidence);
            else Fail("connection.client_double_click", requirement, evidence);
            await WaitForUiAsync(() => Find("pair")?.Current.IsEnabled == true, 15);
        }

        async Task Save(string value)
        {
            ((ValuePattern)Find("wanAddress")!.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);
            InvokeElement(Find("saveWanAddress")!);
            await Task.Delay(250, stop.Token);
        }

        void Check(string stage, string expected)
        {
            string actual = Value(Find("wanAddress")!);
            string saved = DeviceWanAddress.Format(DeviceWanAddress.Load(root, fingerprint));
            if (actual != expected || saved != expected) throw new IOException($"WAN {stage}: field '{actual}', saved '{saved}', expected '{expected}'.");
            var saveBounds = Find("saveWanAddress")!.Current.BoundingRectangle;
            var addressBounds = Find("wanAddress")!.Current.BoundingRectangle;
            var connectBounds = Find("pair")!.Current.BoundingRectangle;
            if (connectBounds.Top - addressBounds.Bottom > saveBounds.Height * 4) throw new IOException("The WAN editor pushes Connect away from the connection fields.");
            Pass("wan." + stage, "The shipped per-device WAN editor saves, restores and clears an optional address", new { actual, saved, connectGap = connectBounds.Top - addressBounds.Bottom });
        }
    }

    private void ProbeReconnectSelection(string root, Peer peer)
    {
        // Exercise the real teardown without starting a listener or contacting the relay.
        using var form = new MainForm(startAgent: false, dataRoot: root, loopbackOnly: true);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo Field(string name) => typeof(MainForm).GetField(name, flags)!;
        try
        {
            ((Forms.TextBox)Field("host").GetValue(form)!).Text = peer.Host;
            Field("selectedPeer").SetValue(form, peer);
            Field("selectedFingerprint").SetValue(form, peer.Fingerprint);
            Field("client").SetValue(form, new RemoteClient(new Connection(peer.Host, peer.Port, peer.Fingerprint, new string('e', 64))));
            typeof(MainForm).GetMethod("ClearControllerSession", flags)!.Invoke(form, null);
            var retained = (Peer?)Field("selectedPeer").GetValue(form);
            bool selectionRetained = retained == peer && (string?)Field("selectedFingerprint").GetValue(form) == peer.Fingerprint;
            bool grantCleared = Field("client").GetValue(form) == null;
            string invitation = "RD-FEDC-BA98-7654-3210";
            bool wan = InternetSettings.IsSupportId(peer.Host);
            var fresh = peer with { SupportId = invitation, Host = wan ? invitation : peer.Host };
            bool rebound = retained != null && PeerDiscovery.Rebind(retained, [fresh]) == fresh;
            string id = "reconnect.selection_" + (wan ? "wan" : "lan");
            const string requirement = "End support clears the grant but retains the selected PC identity for a fresh invitation on reconnect";
            var evidence = new { selectionRetained, grantCleared, rebound };
            if (selectionRetained && grantCleared && rebound) Pass(id, requirement, evidence);
            else Fail(id, requirement, evidence);
        }
        finally { typeof(MainForm).GetMethod("DisposeResources", flags)!.Invoke(form, null); }
    }
}
