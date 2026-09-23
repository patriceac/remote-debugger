using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Automation;
using RemoteDebugger;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private async Task LoopbackWakeAsync()
    {
        if (scope != "runtime" || IsUpdateVariant)
            throw new ArgumentException("Loopback wake only accepts the Runtime/None configuration.");

        string root = Path.Combine(output, "wake");
        Directory.CreateDirectory(root);
        string fingerprint = new('a', 64);
        var devices = new[]
        {
            new
            {
                Peer = new Peer("Wake fixture PC", "127.0.0.1", 45832, fingerprint),
                Version = "", Sha256 = "", Online = false, State = "offline", Percent = 0, Detail = ""
            }
        };
        Vault.Save(Path.Combine(root, "devices.dpapi"), JsonSerializer.SerializeToUtf8Bytes(devices, Json.Options));

        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        string expectedMac = "00:11:22:33:44:55";

        try
        {
            product = loopbackController = LaunchLoopbackProduct(false, root, "fr");
            await WaitUiAsync();
            WindowState = Forms.FormWindowState.Minimized;
            ResizeProductWindow(1060, 720);
            await SelectWakeDeviceAsync("Wake fixture PC");
            bool offlineBefore = UiTexts().Any(IsOfflineText);
            CaptureDesktop("wake-offline-fr.png");

            await ClickWakeAsync("configureWake");
            await WaitForUiAsync(() => FindWake("wakeMac") != null, 15);
            CaptureDesktop("wake-modal-minimum-fr.png", focusProduct: false);
            foreach (string id in new[] { "wakeMac", "wakeSender", "wakeDestination", "wakePort", "wakeHelp", "saveWakeSettings", "cancelWakeSettings" })
                if (FindWake(id)?.Current.IsOffscreen != false) throw new IOException("Wake modal control clipped: " + id);
            var bodyBounds = FindWake("wakeSettingsBody")!.Current.BoundingRectangle;
            foreach (string id in new[] { "wakeMac", "wakeSender", "wakeDestination", "wakePort", "wakeHelp" })
                if (!bodyBounds.Contains(FindWake(id)!.Current.BoundingRectangle)) throw new IOException("Wake modal control partially clipped: " + id);
            SetWake("wakeMac", expectedMac);
            SetWake("wakeDestination", "127.0.0.1");
            SetWake("wakePort", port.ToString());
            InvokeWake("saveWakeSettings");
            await WaitForUiAsync(() => FindWake("wakeMac", 100) == null, 15);

            var saved = DeviceWakeSettings.Load(root, fingerprint);
            if (saved == null || saved.MacAddress != expectedMac || saved.Destination != "127.0.0.1" || saved.Port != port)
                throw new IOException("Wake settings did not persist the normalized loopback target.");
            if (receiver.Available != 0) throw new IOException("Saving wake settings must not send a wake packet.");
            Pass("wake.settings_saved", "The offline device wake editor saves a normalized MAC, loopback destination and UDP port", new { saved, port });

            using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            receiveTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            Task<byte[][]> packetsTask = ReceiveWakePacketsAsync(receiver, 3, receiveTimeout.Token);
            await WaitForUiAsync(() => FindWake("wakePc")?.Current.IsEnabled == true, 10);
            InvokeWake("wakePc");
            byte[][] packets = await packetsTask;
            byte[] expectedPacket = WakeOnLan.CreatePacket(expectedMac);
            bool packetShape = packets.Length == 3 && packets.All(packet => packet.SequenceEqual(expectedPacket));
            string status = string.Empty;
            bool feedback = false;
            await WaitForUiAsync(() =>
            {
                status = WakeText("connectionFormState");
                feedback = status.Contains("sent", StringComparison.OrdinalIgnoreCase)
                    || status.Contains("envoy", StringComparison.OrdinalIgnoreCase);
                return feedback;
            }, 10);
            bool offlineAfter = UiTexts().Any(IsOfflineText);
            if (packetShape && feedback && offlineBefore && offlineAfter)
                Pass("wake.loopback_packet", "The offline PC wake action sends three valid 102-byte magic packets and reports packet-sent feedback", new { packetCount = packets.Length, packetLengths = packets.Select(packet => packet.Length).ToArray(), status, offlineBefore, offlineAfter });
            else
                Fail("wake.loopback_packet", "The offline PC wake action sends three valid 102-byte magic packets and reports packet-sent feedback", new { packetCount = packets.Length, packetLengths = packets.Select(packet => packet.Length).ToArray(), packetShape, status, feedback, offlineBefore, offlineAfter });

            ResizeProductWindow(1280, 860);
            await ClickWakeAsync("configureWake");
            await WaitForUiAsync(() => FindWake("wakeMac") != null, 15);
            bool reopened = WakeText("wakeMac") == expectedMac
                && WakeText("wakeDestination") == "127.0.0.1"
                && WakeText("wakePort") == port.ToString();
            CaptureDesktop("wake-settings-reopened-fr.png", focusProduct: false);
            if (reopened)
                Pass("wake.settings_reopen", "The wake settings dialog restores the saved values after sending", new { mac = WakeText("wakeMac"), destination = WakeText("wakeDestination"), port = WakeText("wakePort") });
            else
                Fail("wake.settings_reopen", "The wake settings dialog restores the saved values after sending", new { mac = WakeText("wakeMac"), destination = WakeText("wakeDestination"), port = WakeText("wakePort") });
            SetWake("wakeMac", "00:11:22:33:44:66");
            InvokeWake("cancelWakeSettings");
            await WaitForUiAsync(() => FindWake("wakeMac", 100) == null, 15);
            if (DeviceWakeSettings.Load(root, fingerprint)?.MacAddress != expectedMac)
                throw new IOException("Cancel must preserve the saved wake settings.");
            Pass("wake.settings_cancel", "Cancel discards the edited MAC address");
            await FinishAsync();
        }
        catch
        {
            CaptureDesktop("wake-failure.png", focusProduct: false);
            throw;
        }
        finally { await CleanupLoopbackProcessesAsync(); }
    }

    private async Task SelectWakeDeviceAsync(string name)
    {
        await WaitForUiAsync(() => FindWake("peers") != null, 15);
        var list = FindWake("peers")!;
        var item = list.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
            .First(element => element.Current.Name == name && element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _));
        ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        await Task.Delay(300, stop.Token);
    }

    private AutomationElement? FindWake(string id, int milliseconds = 1200, bool includeOffscreen = false)
    {
        if (product == null || product.HasExited) return null;
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, product.Id),
            new PropertyCondition(AutomationElement.AutomationIdProperty, id));
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < milliseconds)
        {
            try
            {
                var found = AutomationElement.RootElement.FindFirst(TreeScope.Descendants, condition);
                if (found != null && (includeOffscreen || !found.Current.IsOffscreen)) return found;
            }
            catch (Exception) { }
            Thread.Sleep(100);
        }
        return null;
    }

    private string WakeText(string id)
    {
        var element = FindWake(id, 5000) ?? throw new InvalidOperationException("Missing wake control: " + id);
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern value)
            return value.Current.Value;
        if (element.TryGetCurrentPattern(RangeValuePattern.Pattern, out var range) && range is RangeValuePattern numeric)
            return numeric.Current.Value.ToString(CultureInfo.InvariantCulture);
        var childEditor = FindWakeValueEditor(element);
        if (childEditor != null && childEditor.TryGetCurrentPattern(ValuePattern.Pattern, out var childPattern) && childPattern is ValuePattern childValue)
            return childValue.Current.Value;
        return element.Current.Name;
    }

    private void SetWake(string id, string value)
    {
        var element = FindWake(id, 5000) ?? throw new InvalidOperationException("Missing wake control: " + id);
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern editor)
        {
            editor.SetValue(value);
            return;
        }
        if (element.TryGetCurrentPattern(RangeValuePattern.Pattern, out var range) && range is RangeValuePattern numeric &&
            double.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out double amount))
        {
            numeric.SetValue(amount);
            return;
        }
        var childEditor = FindWakeValueEditor(element);
        if (childEditor != null && childEditor.TryGetCurrentPattern(ValuePattern.Pattern, out var childPattern) && childPattern is ValuePattern childValue)
        {
            childValue.SetValue(value);
            return;
        }
        throw new InvalidOperationException("Wake control is not editable: " + id);
    }

    private static AutomationElement? FindWakeValueEditor(AutomationElement container)
    {
        try
        {
            return container.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
                .FirstOrDefault(candidate => candidate.TryGetCurrentPattern(ValuePattern.Pattern, out _));
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    private void InvokeWake(string id)
    {
        var element = FindWake(id, 5000) ?? throw new InvalidOperationException("Missing wake control: " + id);
        InvokeElement(element);
    }

    private async Task ClickWakeAsync(string id)
    {
        var element = FindWake(id, 5000, includeOffscreen: true) ?? throw new InvalidOperationException("Missing wake control: " + id);
        element.SetFocus();
        await Task.Delay(150, stop.Token);
        var point = element.GetClickablePoint();
        Native.Mouse(0, (int)point.X, (int)point.Y);
    }

    private static bool IsOfflineText(string value) =>
        value.Contains("offline", StringComparison.OrdinalIgnoreCase)
        || value.Contains("hors ligne", StringComparison.OrdinalIgnoreCase)
        || value.Contains("sin conexión", StringComparison.OrdinalIgnoreCase);

    private static async Task<byte[][]> ReceiveWakePacketsAsync(UdpClient receiver, int count, CancellationToken ct)
    {
        var packets = new List<byte[]>(count);
        while (packets.Count < count)
            packets.Add((await receiver.ReceiveAsync(ct)).Buffer);
        return packets.ToArray();
    }
}
